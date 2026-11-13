using TimeTrace.Core.Models;
using TimeTrace.Core.Tracking;

namespace TimeTrace.Tests;

[Trait("Category", "Unit")]
public class TrackingEngineTests
{
    private static async Task<EngineHarness> StartedAsync(Action<EngineHarness>? configure = null)
    {
        var h = new EngineHarness();
        configure?.Invoke(h);
        await h.Engine.StartAsync();
        return h;
    }

    // 1. Application detection
    [Fact]
    public async Task Detects_application_and_stores_metadata()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("code.exe", "main.cs - Visual Studio Code", pid: 4242, path: @"C:\Tools\code.exe");
        await h.RunAsync(2);

        var app = Assert.Single(h.Repo.Apps);
        Assert.Equal("code.exe", app.ExecutableName);
        Assert.Equal(@"C:\Tools\code.exe", app.ExecutablePath);
        Assert.Equal("code", app.Name);
        var s = Assert.Single(h.Repo.Sessions);
        Assert.Equal(4242, s.ProcessId);
        Assert.Equal("main.cs - Visual Studio Code", s.WindowTitle);
    }

    [Fact]
    public async Task Applies_classification_rule_to_new_application()
    {
        var h = await StartedAsync(x => x.Classifier.Map["code.exe"] = 7);
        h.Windows.Current = FakeWindows.Win("code.exe");
        await h.RunAsync(2);
        Assert.Equal(7, h.Repo.Apps.Single().CategoryId);
    }

    // 2 + 3. Active window changes, session start and end
    [Fact]
    public async Task Window_change_closes_previous_session_and_starts_new_one()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        var startA = h.Repo.Sessions[0].StartTimeUtc;
        h.Windows.Current = FakeWindows.Win("b.exe");
        await h.RunAsync(5);

        Assert.Equal(2, h.Repo.Sessions.Count);
        var a = h.Repo.Sessions[0]; var b = h.Repo.Sessions[1];
        Assert.False(a.IsOpen);
        Assert.True(b.IsOpen);                                    // still running
        Assert.Equal(10, a.DurationSeconds, 0.01);
        Assert.Equal(startA + TimeSpan.FromSeconds(10), a.EndTimeUtc);
        Assert.Equal(a.EndTimeUtc, b.StartTimeUtc);               // contiguous, no gap and no overlap
    }

    [Fact]
    public async Task Multiple_windows_of_same_process_form_one_session()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("notepad.exe", "doc1", pid: 10);
        await h.RunAsync(5);
        h.Windows.Current = FakeWindows.Win("notepad.exe", "doc2", pid: 11);   // second instance/window
        await h.RunAsync(5);
        Assert.Single(h.Repo.Sessions);
        Assert.Single(h.Repo.Apps);
    }

    [Fact]
    public async Task Closing_the_engine_persists_final_session()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(8);
        await h.Engine.StopAsync();
        var s = Assert.Single(h.Repo.Sessions);
        Assert.False(s.IsOpen);
        Assert.Equal(7, s.DurationSeconds, 0.01);
    }

    // 4. Duration calculation is monotonic
    [Fact]
    public async Task Duration_ignores_wall_clock_changes()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(5);
        h.Clock.JumpWallClock(TimeSpan.FromHours(3));     // user changes system time
        await h.RunAsync(5);
        h.Clock.JumpWallClock(TimeSpan.FromHours(-10));
        await h.Engine.StopAsync();
        var s = Assert.Single(h.Repo.Sessions);
        Assert.Equal(9, s.DurationSeconds, 0.01);
        Assert.Equal(s.StartTimeUtc + TimeSpan.FromSeconds(9), s.EndTimeUtc);
    }

    [Fact]
    public async Task Does_not_write_a_row_per_poll()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(25);
        Assert.Equal(1, h.Repo.InsertCount);
        Assert.Equal(0, h.Repo.UpdateCount);
        await h.RunAsync(40);                    // crosses the 30 s checkpoint once
        Assert.Equal(1, h.Repo.InsertCount);
        Assert.InRange(h.Repo.UpdateCount, 1, 3);
        Assert.True(h.Repo.Sessions[0].IsOpen);
        Assert.InRange(h.Repo.Sessions[0].DurationSeconds, 30, 65);   // checkpoint progress is visible in the database
    }

    [Fact]
    public async Task Very_short_sessions_are_discarded()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await h.Engine.TickAsync();                              // flicker: foreground for only 0.4 s
        h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        h.Windows.Current = FakeWindows.Win("b.exe");
        await h.Engine.TickAsync();
        await h.RunAsync(5);
        Assert.DoesNotContain(h.Repo.Sessions, s => s.ApplicationId == 1);
        Assert.Contains(h.Repo.Sessions, s => s.ApplicationId == 2);
    }

    // 5 + 6. Idle detection and exclusion of idle time
    [Fact]
    public async Task Idle_period_is_recorded_separately_and_not_attributed_to_the_application()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with { IdleThreshold = TimeSpan.FromSeconds(60) });
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(30);                                  // active, user is typing
        for (int i = 1; i <= 70; i++)                          // user stops: idle grows 1..70 s
        {
            h.Idle.Idle = TimeSpan.FromSeconds(i);
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await h.Engine.TickAsync();
        }
        Assert.True(h.Engine.Snapshot.IsIdle);

        h.Idle.Idle = TimeSpan.Zero;                           // input resumes
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Engine.TickAsync();
        Assert.False(h.Engine.Snapshot.IsIdle);

        var active = h.Repo.Sessions.First(s => s.SessionType == SessionType.Active);
        var idle = h.Repo.Sessions.Single(s => s.SessionType == SessionType.Idle);
        Assert.Equal(29, active.DurationSeconds, 1.5);         // only the time before the last input
        Assert.Equal(active.EndTimeUtc, idle.StartTimeUtc);    // idle begins when the user stopped
        Assert.Equal(71, idle.DurationSeconds, 1.5);
        Assert.False(idle.IsOpen);
        Assert.Null(idle.ApplicationId);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.IdleStart);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.IdleEnd);
        // tracking resumed with a fresh session after activity returned
        Assert.Contains(h.Repo.Sessions, s => s.SessionType == SessionType.Active && s.IsOpen);
    }

    [Fact]
    public async Task Idle_threshold_is_configurable()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with { IdleThreshold = TimeSpan.FromMinutes(10) });
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(5);
        h.Idle.Idle = TimeSpan.FromMinutes(6);                 // above 5, below the configured 10 minutes
        await h.RunAsync(2);
        Assert.False(h.Engine.Snapshot.IsIdle);
        h.Idle.Idle = TimeSpan.FromMinutes(11);
        await h.RunAsync(1);
        Assert.True(h.Engine.Snapshot.IsIdle);
    }

    // 8. Application exclusions
    [Fact]
    public async Task Excluded_application_flag_prevents_tracking()
    {
        var h = new EngineHarness();
        h.Repo.Apps.Add(new AppRecord { Id = 1, Name = "Secret", ExecutableName = "secret.exe", IsExcluded = true });
        await h.Engine.StartAsync();
        h.Windows.Current = FakeWindows.Win("secret.exe");
        await h.RunAsync(10);
        Assert.Empty(h.Repo.Sessions);
    }

    [Fact]
    public async Task Excluded_by_executable_and_path_rules()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with
        {
            ExcludedExecutables = new[] { "KeePass.exe" }, ExcludedPaths = new[] { @"\PrivateTools\" },
        });
        h.Windows.Current = FakeWindows.Win("keepass.exe");
        await h.RunAsync(5);
        h.Windows.Current = FakeWindows.Win("tool.exe", path: @"D:\PrivateTools\tool.exe");
        await h.RunAsync(5);
        Assert.Empty(h.Repo.Sessions);
        Assert.Empty(h.Repo.Apps);                      // excluded apps are not even stored
    }

    [Fact]
    public async Task Exclusion_closes_current_session_when_excluded_window_appears()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe", "Open");
        await h.RunAsync(5);
        h.Options = h.Options with { ExcludedExecutables = new[] { "a.exe" } };
        await h.RunAsync(5);
        var s = Assert.Single(h.Repo.Sessions);
        Assert.False(s.IsOpen);
    }

    // 9. Window title exclusions
    [Fact]
    public async Task Window_title_rules_exclude_matching_windows_only()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with { ExcludedTitleRules = new[] { "incognito", "re:^bank\\b" } });
        h.Windows.Current = FakeWindows.Win("chrome.exe", "Reddit - Incognito");
        await h.RunAsync(5);
        h.Windows.Current = FakeWindows.Win("chrome.exe", "Bank of Somewhere");
        await h.RunAsync(5);
        Assert.Empty(h.Repo.Sessions);

        h.Windows.Current = FakeWindows.Win("chrome.exe", "Documentation");
        await h.RunAsync(5);
        Assert.Single(h.Repo.Sessions);
    }

    [Fact]
    public void Invalid_regex_rules_do_not_throw()
    {
        Assert.False(ExclusionMatcher.TitleMatches("re:([unclosed", "anything"));
    }

    [Fact]
    public async Task Window_titles_are_not_stored_when_disabled_but_still_used_for_exclusion()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with { TrackWindowTitles = false, ExcludedTitleRules = new[] { "secret" } });
        h.Windows.Current = FakeWindows.Win("a.exe", "My document");
        await h.RunAsync(3);
        Assert.Null(h.Repo.Sessions.Single().WindowTitle);

        h.Windows.Current = FakeWindows.Win("b.exe", "secret plans");
        await h.RunAsync(3);
        Assert.DoesNotContain(h.Repo.Apps, a => a.ExecutableName == "b.exe");
    }

    // 10. Pause / resume
    [Fact]
    public async Task Pause_stops_attribution_and_resume_continues_with_new_session()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        await h.Engine.PauseAsync();
        Assert.Equal(TrackingState.Paused, h.Engine.State);
        await h.RunAsync(30);                                  // ticks while paused do nothing
        await h.Engine.ResumeAsync();
        await h.RunAsync(10);
        await h.Engine.StopAsync();

        Assert.Equal(2, h.Repo.Sessions.Count);
        Assert.All(h.Repo.Sessions, s => Assert.InRange(s.DurationSeconds, 8, 10));
        var gap = h.Repo.Sessions[1].StartTimeUtc - h.Repo.Sessions[0].EndTimeUtc;
        Assert.True(gap >= TimeSpan.FromSeconds(30));          // paused time is unobserved
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Paused);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Resumed);
    }

    [Fact]
    public async Task Stopped_engine_ignores_ticks()
    {
        var h = new EngineHarness();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        Assert.Empty(h.Repo.Sessions);
        Assert.Equal(TrackingState.Stopped, h.Engine.State);
    }

    // 12. Interrupted-session recovery
    [Fact]
    public async Task Start_recovers_sessions_left_open_by_a_crash()
    {
        var h = new EngineHarness();
        h.Repo.Sessions.Add(new ActivitySession { Id = 99, ApplicationId = 1, StartTimeUtc = h.Clock.UtcNow.AddHours(-2), EndTimeUtc = h.Clock.UtcNow.AddHours(-1), DurationSeconds = 3600, IsOpen = true });
        int recovered = 0;
        h.Engine.SessionsRecovered += n => recovered = n;
        await h.Engine.StartAsync();
        Assert.Equal(1, recovered);
        Assert.False(h.Repo.Sessions.Single().IsOpen);
        Assert.Equal(3600, h.Repo.Sessions.Single().DurationSeconds);   // last checkpoint is preserved
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Recovered);
    }

    // 22. Sleep and resume
    [Fact]
    public async Task Sleep_closes_session_and_does_not_count_sleep_time()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        await h.Engine.OnSystemSuspendAsync();
        h.Clock.Advance(TimeSpan.FromHours(8));                // laptop lid closed overnight
        await h.Engine.OnSystemResumeAsync();
        await h.RunAsync(5);

        Assert.Equal(2, h.Repo.Sessions.Count);
        Assert.Equal(9, h.Repo.Sessions[0].DurationSeconds, 0.01);
        Assert.InRange(h.Repo.Sessions[1].DurationSeconds, 0, 5);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Suspend);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Resume);
    }

    [Fact]
    public async Task Unannounced_time_gap_ends_session_at_last_observation()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        h.Clock.Advance(TimeSpan.FromMinutes(30));             // process was frozen, no suspend notification arrived
        await h.Engine.TickAsync();
        var first = h.Repo.Sessions[0];
        Assert.Equal(9, first.DurationSeconds, 0.01);
        Assert.False(first.IsOpen);
    }

    [Fact]
    public async Task Lock_pauses_tracking_when_configured()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(10);
        await h.Engine.OnSessionLockedAsync();
        await h.RunAsync(20);
        await h.Engine.OnSessionUnlockedAsync();
        await h.RunAsync(5);
        Assert.Equal(2, h.Repo.Sessions.Count);
        Assert.Equal(9, h.Repo.Sessions[0].DurationSeconds, 0.01);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Locked);
    }

    [Fact]
    public async Task Lock_is_ignored_when_option_disabled()
    {
        var h = await StartedAsync(x => x.Options = TrackingOptions.Default with { PauseOnLock = false });
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(5);
        await h.Engine.OnSessionLockedAsync();
        await h.RunAsync(5);
        Assert.Single(h.Repo.Sessions);
    }

    [Fact]
    public async Task Logoff_persists_everything_immediately()
    {
        var h = await StartedAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(5);
        await h.Engine.OnSessionEndingAsync();
        Assert.False(h.Repo.Sessions.Single().IsOpen);
        Assert.Equal(TrackingState.Stopped, h.Engine.State);
        Assert.Contains(h.Repo.Events, e => e.EventType == TrackingEventTypes.Shutdown);
    }

    // Robustness
    [Fact]
    public async Task Missing_window_for_one_tick_does_not_split_session_but_longer_does()
    {
        var h = await StartedAsync();
        var win = FakeWindows.Win("a.exe");
        h.Windows.Current = win;
        await h.RunAsync(5);
        h.Windows.Current = null;
        await h.RunAsync(1);                                   // transient (e.g. alt-tab)
        h.Windows.Current = win;
        await h.RunAsync(5);
        Assert.Single(h.Repo.Sessions);

        h.Windows.Current = null;
        await h.RunAsync(5);
        Assert.False(h.Repo.Sessions[0].IsOpen);
    }

    [Fact]
    public async Task Elevated_process_with_inaccessible_path_and_empty_title_is_tracked()
    {
        var h = await StartedAsync();
        h.Windows.Current = new TimeTrace.Core.Abstractions.ForegroundWindowInfo(1, "elevated.exe", null, "elevated", null);
        await h.RunAsync(5);
        Assert.Null(h.Repo.Apps.Single().ExecutablePath);
        Assert.Null(h.Repo.Sessions.Single().WindowTitle);
    }

    [Fact]
    public async Task Database_failures_do_not_crash_tracking()
    {
        var failing = new ThrowingRepository();
        var h = new EngineHarness(failing);
        string? error = null;
        h.Engine.ErrorOccurred += e => error = e;
        await h.Engine.StartAsync();
        h.Windows.Current = FakeWindows.Win("a.exe");
        await h.RunAsync(5);
        Assert.NotNull(error);
        Assert.Equal(TrackingState.Running, h.Engine.State);
    }

    private sealed class ThrowingRepository : TimeTrace.Core.Abstractions.IActivityRepository
    {
        private static T Boom<T>() => throw new InvalidOperationException("disk full");
        public Task<AppRecord?> GetApplicationByExecutableAsync(string e, CancellationToken ct = default) => Boom<Task<AppRecord?>>();
        public Task<AppRecord?> GetApplicationAsync(int id, CancellationToken ct = default) => Boom<Task<AppRecord?>>();
        public Task<AppRecord> AddApplicationAsync(AppRecord a, CancellationToken ct = default) => Boom<Task<AppRecord>>();
        public Task UpdateApplicationAsync(AppRecord a, CancellationToken ct = default) => Boom<Task>();
        public Task<IReadOnlyList<AppRecord>> GetApplicationsAsync(CancellationToken ct = default) => Boom<Task<IReadOnlyList<AppRecord>>>();
        public Task DeleteApplicationAsync(int id, CancellationToken ct = default) => Boom<Task>();
        public Task<long> InsertSessionAsync(ActivitySession s, CancellationToken ct = default) => Boom<Task<long>>();
        public Task UpdateSessionAsync(ActivitySession s, CancellationToken ct = default) => Boom<Task>();
        public Task<IReadOnlyList<ActivitySession>> GetSessionsAsync(DateTime f, DateTime t, int? a = null, CancellationToken ct = default) => Boom<Task<IReadOnlyList<ActivitySession>>>();
        public Task DeleteSessionsAsync(IEnumerable<long> ids, CancellationToken ct = default) => Boom<Task>();
        public Task DeleteAllActivityAsync(CancellationToken ct = default) => Boom<Task>();
        public Task<int> DeleteSessionsOlderThanAsync(DateTime c, CancellationToken ct = default) => Boom<Task<int>>();
        public Task<int> RecoverOpenSessionsAsync(CancellationToken ct = default) => Boom<Task<int>>();
        public Task AddEventAsync(string t, DateTime ts, string? d, CancellationToken ct = default) => Boom<Task>();
        public Task<IReadOnlyList<TrackingEvent>> GetEventsAsync(int limit = 200, CancellationToken ct = default) => Boom<Task<IReadOnlyList<TrackingEvent>>>();
    }
}
