using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Models;
using TimeTrace.Core.Tracking;

namespace TimeTrace.Tests;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);
    public long MonotonicMilliseconds { get; set; } = 1_000_000;

    /// <summary>Advance both clocks (normal passage of time).</summary>
    public void Advance(TimeSpan t) { UtcNow += t; MonotonicMilliseconds += (long)t.TotalMilliseconds; }
    /// <summary>Change only the wall clock (user changes system time / NTP correction).</summary>
    public void JumpWallClock(TimeSpan t) => UtcNow += t;
}

public sealed class FakeWindows : IForegroundWindowProvider
{
    public ForegroundWindowInfo? Current { get; set; }
    public ForegroundWindowInfo? GetForegroundWindow() => Current;

    public static ForegroundWindowInfo Win(string exe, string? title = "Title", int pid = 100, string? path = null) =>
        new(pid, exe, path ?? $@"C:\Apps\{exe}", System.IO.Path.GetFileNameWithoutExtension(exe), title);
}

public sealed class FakeIdle : IIdleTimeProvider
{
    public TimeSpan Idle { get; set; }
    public TimeSpan GetIdleTime() => Idle;
}

public sealed class FakeClassifier : IAppClassifier
{
    public Dictionary<string, int> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<int?> ClassifyAsync(string exe, string? path, CancellationToken ct = default) =>
        Task.FromResult(Map.TryGetValue(exe, out var id) ? (int?)id : null);
}

/// <summary>In-memory repository used by engine unit tests (no database involved).</summary>
public sealed class FakeRepository : IActivityRepository
{
    public List<AppRecord> Apps { get; } = new();
    public List<ActivitySession> Sessions { get; } = new();
    public List<TrackingEvent> Events { get; } = new();
    public int InsertCount, UpdateCount;
    private int _appId; private long _sessId;

    public Task<AppRecord?> GetApplicationByExecutableAsync(string exe, CancellationToken ct = default) =>
        Task.FromResult(Apps.FirstOrDefault(a => string.Equals(a.ExecutableName, exe, StringComparison.OrdinalIgnoreCase)));
    public Task<AppRecord?> GetApplicationAsync(int id, CancellationToken ct = default) => Task.FromResult(Apps.FirstOrDefault(a => a.Id == id));
    public Task<AppRecord> AddApplicationAsync(AppRecord app, CancellationToken ct = default)
    {
        var existing = Apps.FirstOrDefault(a => string.Equals(a.ExecutableName, app.ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return Task.FromResult(existing);
        app.Id = ++_appId; Apps.Add(app); return Task.FromResult(app);
    }
    public Task UpdateApplicationAsync(AppRecord app, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AppRecord>> GetApplicationsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRecord>>(Apps.ToList());
    public Task DeleteApplicationAsync(int id, CancellationToken ct = default) { Apps.RemoveAll(a => a.Id == id); return Task.CompletedTask; }

    public Task<long> InsertSessionAsync(ActivitySession s, CancellationToken ct = default)
    {
        InsertCount++;
        var copy = Clone(s); copy.Id = ++_sessId; Sessions.Add(copy); return Task.FromResult(copy.Id);
    }
    public Task UpdateSessionAsync(ActivitySession s, CancellationToken ct = default)
    {
        UpdateCount++;
        var i = Sessions.FindIndex(x => x.Id == s.Id);
        if (i >= 0) Sessions[i] = Clone(s);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ActivitySession>> GetSessionsAsync(DateTime from, DateTime to, int? appId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActivitySession>>(Sessions.Where(s => s.EndTimeUtc > from && s.StartTimeUtc < to && (appId == null || s.ApplicationId == appId)).ToList());
    public Task DeleteSessionsAsync(IEnumerable<long> ids, CancellationToken ct = default) { var set = ids.ToHashSet(); Sessions.RemoveAll(s => set.Contains(s.Id)); return Task.CompletedTask; }
    public Task DeleteAllActivityAsync(CancellationToken ct = default) { Sessions.Clear(); Events.Clear(); return Task.CompletedTask; }
    public Task<int> DeleteSessionsOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(Sessions.RemoveAll(s => s.EndTimeUtc < cutoff));
    public Task<int> RecoverOpenSessionsAsync(CancellationToken ct = default)
    {
        int n = 0; foreach (var s in Sessions.Where(s => s.IsOpen)) { s.IsOpen = false; n++; }
        return Task.FromResult(n);
    }
    public Task AddEventAsync(string type, DateTime ts, string? details, CancellationToken ct = default)
    { Events.Add(new TrackingEvent { EventType = type, TimestampUtc = ts, Details = details }); return Task.CompletedTask; }
    public Task<IReadOnlyList<TrackingEvent>> GetEventsAsync(int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TrackingEvent>>(Events);

    private static ActivitySession Clone(ActivitySession s) => new()
    {
        Id = s.Id, ApplicationId = s.ApplicationId, ProcessId = s.ProcessId, WindowTitle = s.WindowTitle, StartTimeUtc = s.StartTimeUtc,
        EndTimeUtc = s.EndTimeUtc, DurationSeconds = s.DurationSeconds, SessionType = s.SessionType, IsOpen = s.IsOpen,
    };
}

/// <summary>Wires a TrackingEngine to fakes so tests can script a scenario second by second.</summary>
public sealed class EngineHarness
{
    public FakeClock Clock { get; } = new();
    public FakeWindows Windows { get; } = new();
    public FakeIdle Idle { get; } = new();
    public FakeRepository Repo { get; } = new();
    public FakeClassifier Classifier { get; } = new();
    public TrackingOptions Options { get; set; } = TrackingOptions.Default;
    public TrackingEngine Engine { get; }

    public EngineHarness(IActivityRepository? repo = null)
    {
        Engine = new TrackingEngine(Windows, Idle, repo ?? Repo, Classifier, Clock, () => Options, Serilog.Core.Logger.None);
    }

    /// <summary>Advance time in 1 s steps, ticking the engine each second.</summary>
    public async Task RunAsync(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Engine.TickAsync();
        }
    }
}
