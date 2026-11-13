using System.Text.Json;
using Microsoft.Data.Sqlite;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;
using TimeTrace.Core.Reporting;
using TimeTrace.Core.Settings;
using TimeTrace.Core.Tracking;
using TimeTrace.Data;

namespace TimeTrace.Tests;

/// <summary>Creates an isolated temp SQLite database per test (integration tests; never touches real user data).</summary>
public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "TimeTraceTests_" + Guid.NewGuid().ToString("N"));
    public SqliteDatabase Db { get; }
    public SqliteRepository Repo { get; }
    public TempDb()
    {
        Directory.CreateDirectory(Dir);
        Db = new SqliteDatabase(Path.Combine(Dir, "test.db"));
        Db.Migrate();
        Repo = new SqliteRepository(Db);
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

[Trait("Category", "Integration")]
public class DatabaseTests
{
    private static readonly DateTime T0 = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    // 11. Persistence
    [Fact]
    public async Task Sessions_round_trip_through_sqlite_with_utc_timestamps()
    {
        using var t = new TempDb();
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "Code", ExecutableName = "code.exe", FirstSeen = T0, LastSeen = T0 });
        var id = await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = app.Id, ProcessId = 7, WindowTitle = "x", StartTimeUtc = T0, EndTimeUtc = T0.AddMinutes(5), DurationSeconds = 300 });
        var list = await t.Repo.GetSessionsAsync(T0.AddMinutes(-1), T0.AddHours(1));
        var s = Assert.Single(list);
        Assert.Equal(id, s.Id);
        Assert.Equal(DateTimeKind.Utc, s.StartTimeUtc.Kind);
        Assert.Equal(T0, s.StartTimeUtc);
        Assert.Equal(300, s.DurationSeconds);
    }

    [Fact]
    public async Task Range_query_returns_overlapping_sessions_only()
    {
        using var t = new TempDb();
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        Task<long> Add(DateTime s, int mins) => t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = app.Id, StartTimeUtc = s, EndTimeUtc = s.AddMinutes(mins), DurationSeconds = mins * 60 });
        await Add(T0.AddHours(-3), 10);          // before
        await Add(T0.AddMinutes(-5), 10);        // straddles start
        await Add(T0.AddMinutes(30), 10);        // inside
        await Add(T0.AddHours(2), 10);           // after
        var r = await t.Repo.GetSessionsAsync(T0, T0.AddHours(1));
        Assert.Equal(2, r.Count);
    }

    // 7. Duplicate application handling
    [Fact]
    public async Task Duplicate_executables_do_not_create_duplicate_application_rows()
    {
        using var t = new TempDb();
        var a = await t.Repo.AddApplicationAsync(new AppRecord { Name = "Chrome", ExecutableName = "chrome.exe", FirstSeen = T0, LastSeen = T0 });
        var b = await t.Repo.AddApplicationAsync(new AppRecord { Name = "Google Chrome", ExecutableName = "CHROME.EXE", FirstSeen = T0, LastSeen = T0 });
        Assert.Equal(a.Id, b.Id);
        Assert.Single(await t.Repo.GetApplicationsAsync());
    }

    [Fact]
    public async Task Migrations_create_schema_indexes_and_default_data_idempotently()
    {
        using var t = new TempDb();
        t.Db.Migrate();      // second run must be a no-op
        var cats = await t.Repo.GetCategoriesAsync();
        Assert.Equal(8, cats.Count);
        Assert.Contains(cats, c => c.Name == "Development");
        Assert.NotEmpty(await t.Repo.GetRulesAsync());
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_%'";
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 5);
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    // 12. Recovery (real database)
    [Fact]
    public async Task Open_sessions_are_recovered_after_simulated_crash()
    {
        using var t = new TempDb();
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = app.Id, StartTimeUtc = T0, EndTimeUtc = T0.AddMinutes(20), DurationSeconds = 1200, IsOpen = true });
        Assert.Equal(1, await t.Repo.RecoverOpenSessionsAsync());
        Assert.Equal(0, await t.Repo.RecoverOpenSessionsAsync());
        var s = (await t.Repo.GetSessionsAsync(T0, T0.AddDays(1))).Single();
        Assert.False(s.IsOpen);
        Assert.Equal(1200, s.DurationSeconds);
    }

    [Fact]
    public async Task Engine_end_to_end_against_sqlite_persists_sessions()
    {
        using var t = new TempDb();
        var h = new EngineHarness(t.Repo);
        await h.Engine.StartAsync();
        h.Windows.Current = FakeWindows.Win("a.exe", "w1");
        await h.RunAsync(10);
        h.Windows.Current = FakeWindows.Win("b.exe", "w2");
        await h.RunAsync(10);
        await h.Engine.StopAsync();
        var sessions = await t.Repo.GetSessionsAsync(h.Clock.UtcNow.AddHours(-1), h.Clock.UtcNow.AddHours(1));
        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, (await t.Repo.GetApplicationsAsync()).Count);
        Assert.All(sessions, s => Assert.False(s.IsOpen));
    }

    // 19. Backup
    [Fact]
    public async Task Backup_produces_a_valid_copy_that_can_be_restored()
    {
        using var t = new TempDb();
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = app.Id, StartTimeUtc = T0, EndTimeUtc = T0.AddMinutes(1), DurationSeconds = 60 });
        var backup = Path.Combine(t.Dir, "backups", "b.db");
        t.Db.BackupTo(backup);
        SqliteDatabase.ValidateBackup(backup);

        await t.Repo.DeleteAllActivityAsync();
        Assert.Empty(await t.Repo.GetSessionsAsync(T0.AddDays(-1), T0.AddDays(1)));

        t.Db.RestoreFrom(backup);
        Assert.Single(await t.Repo.GetSessionsAsync(T0.AddDays(-1), T0.AddDays(1)));
    }

    [Fact]
    public void Restore_rejects_files_that_are_not_timetrace_databases()
    {
        using var t = new TempDb();
        var junk = Path.Combine(t.Dir, "junk.db");
        File.WriteAllText(junk, "this is not sqlite");
        Assert.ThrowsAny<Exception>(() => t.Db.RestoreFrom(junk));
        // live database must be untouched
        Assert.Equal(8, t.Repo.GetCategoriesAsync().Result.Count);
    }

    // 20. Data deletion
    [Fact]
    public async Task Selected_sessions_can_be_deleted()
    {
        using var t = new TempDb();
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        var ids = new List<long>();
        for (int i = 0; i < 3; i++) ids.Add(await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = app.Id, StartTimeUtc = T0.AddHours(i), EndTimeUtc = T0.AddHours(i).AddMinutes(5), DurationSeconds = 300 }));
        await t.Repo.DeleteSessionsAsync(new[] { ids[1] });
        var left = await t.Repo.GetSessionsAsync(T0.AddDays(-1), T0.AddDays(1));
        Assert.Equal(new[] { ids[0], ids[2] }, left.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task Delete_all_removes_activity_but_keeps_categories_and_excluded_apps()
    {
        using var t = new TempDb();
        var a = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        var x = await t.Repo.AddApplicationAsync(new AppRecord { Name = "X", ExecutableName = "x.exe", IsExcluded = true, FirstSeen = T0, LastSeen = T0 });
        await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = a.Id, StartTimeUtc = T0, EndTimeUtc = T0.AddMinutes(1), DurationSeconds = 60 });
        await t.Repo.AddEventAsync("Started", T0, null);
        await t.Repo.DeleteAllActivityAsync();
        Assert.Empty(await t.Repo.GetSessionsAsync(T0.AddDays(-1), T0.AddDays(1)));
        Assert.Empty(await t.Repo.GetEventsAsync());
        Assert.Equal("x.exe", (await t.Repo.GetApplicationsAsync()).Single().ExecutableName);
        Assert.Equal(8, (await t.Repo.GetCategoriesAsync()).Count);
    }

    [Fact]
    public async Task Retention_deletes_only_sessions_older_than_cutoff()
    {
        using var t = new TempDb();
        var a = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", FirstSeen = T0, LastSeen = T0 });
        await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = a.Id, StartTimeUtc = T0.AddDays(-100), EndTimeUtc = T0.AddDays(-100).AddMinutes(1), DurationSeconds = 60 });
        await t.Repo.InsertSessionAsync(new ActivitySession { ApplicationId = a.Id, StartTimeUtc = T0, EndTimeUtc = T0.AddMinutes(1), DurationSeconds = 60 });
        Assert.Equal(1, await t.Repo.DeleteSessionsOlderThanAsync(T0.AddDays(-30)));
        Assert.Single(await t.Repo.GetSessionsAsync(T0.AddYears(-1), T0.AddYears(1)));
    }

    // 16. Categories
    [Fact]
    public async Task Deleting_category_moves_applications_to_fallback()
    {
        using var t = new TempDb();
        var cats = await t.Repo.GetCategoriesAsync();
        var custom = await t.Repo.AddCategoryAsync(new Category { Name = "Study", Color = "#112233" });
        var other = cats.Single(c => c.Name == "Other");
        var app = await t.Repo.AddApplicationAsync(new AppRecord { Name = "A", ExecutableName = "a.exe", CategoryId = custom.Id, FirstSeen = T0, LastSeen = T0 });
        await t.Repo.DeleteCategoryAsync(custom.Id, other.Id);
        Assert.Equal(other.Id, (await t.Repo.GetApplicationAsync(app.Id))!.CategoryId);
        Assert.DoesNotContain(await t.Repo.GetCategoriesAsync(), c => c.Name == "Study");
    }

    [Fact]
    public async Task Classifier_uses_default_and_custom_rules()
    {
        using var t = new TempDb();
        var classifier = new RuleClassifier(t.Repo);
        var cats = await t.Repo.GetCategoriesAsync();
        Assert.Equal(cats.Single(c => c.Name == "Browsing").Id, await classifier.ClassifyAsync("Chrome.exe", null));
        Assert.Null(await classifier.ClassifyAsync("unknown.exe", null));
        var study = await t.Repo.AddCategoryAsync(new Category { Name = "Study", Color = "#000000" });
        await t.Repo.AddRuleAsync(new ClassificationRule { MatchType = RuleMatchType.PathContains, Pattern = @"\School\", CategoryId = study.Id });
        Assert.Equal(study.Id, await classifier.ClassifyAsync("reader.exe", @"C:\School\reader.exe"));
    }

    [Fact]
    public async Task Settings_persist_and_are_validated()
    {
        using var t = new TempDb();
        var svc = new SettingsService(t.Repo);
        await svc.LoadAsync();
        Assert.Equal(1, svc.Current.PollIntervalSeconds);
        svc.Current.IdleThresholdMinutes = 15;
        svc.Current.ExcludedExecutables.Add("secret.exe");
        svc.Current.PollIntervalSeconds = 9999;      // invalid -> clamped
        await svc.SaveAsync(svc.Current);

        var again = new SettingsService(t.Repo);
        await again.LoadAsync();
        Assert.Equal(15, again.Current.IdleThresholdMinutes);
        Assert.Equal(60, again.Current.PollIntervalSeconds);
        Assert.Contains("secret.exe", again.Current.ExcludedExecutables);
    }
}

[Trait("Category", "Unit")]
public class AnalyticsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Now = new(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AppRecord Code = new() { Id = 1, Name = "Code", ExecutableName = "code.exe", CategoryId = 2 };
    private static readonly AppRecord Chrome = new() { Id = 2, Name = "Chrome", ExecutableName = "chrome.exe", CategoryId = 6 };
    private static readonly Category[] Cats =
    {
        new() { Id = 2, Name = "Development", Color = "#00ff00" }, new() { Id = 6, Name = "Browsing", Color = "#0000ff" }, new() { Id = 8, Name = "Other", Color = "#888888" },
    };

    private static ActivitySession S(AppRecord? a, DateTime start, double minutes, SessionType t = SessionType.Active) => new()
    {
        ApplicationId = a?.Id, StartTimeUtc = start, EndTimeUtc = start.AddMinutes(minutes), DurationSeconds = minutes * 60, SessionType = t,
    };

    private static UsageSummary Sum(IEnumerable<ActivitySession> s, DateRange r, bool idle = false, TimeZoneInfo? tz = null) =>
        AnalyticsEngine.Summarize(s, new[] { Code, Chrome }, Cats, r, tz ?? Utc, idle, Now);

    // 13. Daily statistics
    [Fact]
    public void Daily_statistics()
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), Utc);
        var sessions = new[]
        {
            S(Code, day.AddHours(9), 60), S(Chrome, day.AddHours(10), 30), S(null, day.AddHours(10.5), 15, SessionType.Idle), S(Code, day.AddHours(11), 30),
        };
        var s = Sum(sessions, range);
        Assert.Equal(120 * 60, s.ActiveSeconds);
        Assert.Equal(15 * 60, s.IdleSeconds);
        Assert.Equal(120 * 60, s.TrackedSeconds);                         // idle excluded by default
        Assert.Equal(2, s.ApplicationCount);
        Assert.Equal("Code", s.MostUsedApp!.Name);
        Assert.Equal(90 * 60, s.MostUsedApp.Seconds);
        Assert.Equal(2, s.MostUsedApp.SessionCount);
        Assert.Equal(40 * 60, s.AverageSessionSeconds);                    // 120 min / 3 sessions
        Assert.Equal(2, s.SwitchCount);                                    // code -> chrome -> code
        Assert.Equal(60 * 60, s.LongestSessions[0].Seconds);
        Assert.Equal(60 * 60, s.ByHour[9]);
        Assert.Equal(30 * 60, s.ByHour[10]);
        Assert.Equal(120 * 60, s.ByDayOfWeek[(int)DayOfWeek.Tuesday]);
    }

    // 6. Idle exclusion in reports
    [Fact]
    public void Idle_time_can_be_included_or_excluded_from_tracked_total()
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), Utc);
        var sessions = new[] { S(Code, day.AddHours(9), 60), S(null, day.AddHours(10), 30, SessionType.Idle) };
        Assert.Equal(3600, Sum(sessions, range, idle: false).TrackedSeconds);
        Assert.Equal(5400, Sum(sessions, range, idle: true).TrackedSeconds);
        Assert.Equal(1800, Sum(sessions, range).IdleSeconds);
    }

    [Fact]
    public void Sessions_crossing_midnight_are_split_between_days()
    {
        var start = new DateTime(2026, 3, 10, 23, 30, 0, DateTimeKind.Utc);
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11), Utc);
        var s = Sum(new[] { S(Code, start, 60) }, range);
        Assert.Equal(2, s.Daily.Count);
        Assert.Equal(1800, s.Daily[0].ActiveSeconds);
        Assert.Equal(1800, s.Daily[1].ActiveSeconds);
        Assert.Equal(1800, s.ByHour[23]);
        Assert.Equal(1800, s.ByHour[0]);
    }

    [Fact]
    public void Sessions_are_clipped_to_the_requested_range()
    {
        var range = Periods.Days(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 11), Utc);
        var s = Sum(new[] { S(Code, new DateTime(2026, 3, 10, 23, 0, 0, DateTimeKind.Utc), 120) }, range);
        Assert.Equal(3600, s.ActiveSeconds);
    }

    // 14. Weekly statistics
    [Fact]
    public void Weekly_statistics_and_averages_only_count_days_with_data()
    {
        var monday = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var range = Periods.Days(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 15), Utc);
        var sessions = new[] { S(Code, monday.AddHours(9), 120), S(Code, monday.AddDays(2).AddHours(9), 60), S(Chrome, monday.AddDays(4).AddHours(9), 60) };
        var s = Sum(sessions, range);
        Assert.Equal(7, s.Daily.Count);
        Assert.Equal(4 * 3600, s.ActiveSeconds);
        Assert.Equal(4 * 3600 / 3.0, s.DailyAverageSeconds, 1);     // 3 days with data, not 7: missing data is not fabricated
        Assert.Equal(4 * 3600, s.WeeklyAverageSeconds, 1);
        Assert.Equal(3 * 3600, s.ByDayOfWeek[(int)DayOfWeek.Monday] + s.ByDayOfWeek[(int)DayOfWeek.Wednesday]);
        Assert.Equal(3600, s.Heatmap[(int)DayOfWeek.Friday, 9]);
    }

    // 15. Monthly statistics + comparison
    [Fact]
    public void Monthly_statistics_and_comparison_with_previous_month()
    {
        var (cur, prev) = AnalyticsEngine.ThisMonthVsLast(new DateOnly(2026, 3, 20), Utc);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), cur.FromUtc);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), cur.ToUtc);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), prev.FromUtc);

        var sessions = new[] { S(Code, new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc), 180), S(Code, new DateTime(2026, 2, 5, 9, 0, 0, DateTimeKind.Utc), 120) };
        var cmp = AnalyticsEngine.Compare("Month", Sum(sessions, cur), Sum(sessions, prev));
        Assert.Equal(3600, cmp.DeltaSeconds);
        Assert.Equal(50.0, cmp.DeltaPercent!.Value, 3);
        Assert.Equal(31, Sum(sessions, cur).Daily.Count);
        Assert.Equal(28, Sum(sessions, prev).Daily.Count);
    }

    [Fact]
    public void Comparison_has_no_percentage_when_previous_period_has_no_data()
    {
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), Utc);
        var cmp = AnalyticsEngine.Compare("x", Sum(new[] { S(Code, new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), 60) }, range), Sum(Array.Empty<ActivitySession>(), Periods.Days(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 9), Utc)));
        Assert.Null(cmp.DeltaPercent);
    }

    [Fact]
    public void This_week_vs_last_week_and_today_vs_yesterday_ranges()
    {
        var (w, pw) = AnalyticsEngine.ThisWeekVsLast(new DateOnly(2026, 3, 18), Utc);   // Wednesday
        Assert.Equal(new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), w.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc), pw.FromUtc);
        var (t, y) = AnalyticsEngine.TodayVsYesterday(new DateOnly(2026, 3, 18), Utc);
        Assert.Equal(t.FromUtc, y.ToUtc);
    }

    // 16. Category calculations
    [Fact]
    public void Category_totals_and_fallback_to_other()
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), Utc);
        var uncategorised = new AppRecord { Id = 3, Name = "Mystery", ExecutableName = "m.exe", CategoryId = null };
        var s = AnalyticsEngine.Summarize(new[] { S(Code, day.AddHours(9), 60), S(Chrome, day.AddHours(10), 30), S(uncategorised, day.AddHours(11), 10) },
            new[] { Code, Chrome, uncategorised }, Cats, range, Utc, false, Now);
        Assert.Equal("Development", s.MostUsedCategory!.Name);
        Assert.Equal(3600, s.Categories.Single(c => c.Name == "Development").Seconds);
        Assert.Equal(1800, s.Categories.Single(c => c.Name == "Browsing").Seconds);
        Assert.Equal(600, s.Categories.Single(c => c.Name == "Other").Seconds);
        Assert.Equal(s.ActiveSeconds, s.Categories.Sum(c => c.Seconds));
    }

    // Tracked vs unobserved
    [Fact]
    public void Unobserved_time_is_reported_separately_from_tracked_time()
    {
        var day = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);          // "now" is 12:00
        var range = Periods.Days(new DateOnly(2026, 3, 20), new DateOnly(2026, 3, 20), Utc);
        var s = Sum(new[] { S(Code, day.AddHours(9), 60) }, range);
        Assert.Equal(12 * 3600 - 3600, s.UnobservedSeconds);                       // only elapsed time counts
    }

    // 21. Timezone conversion
    [Fact]
    public void Local_day_boundaries_and_hours_follow_the_timezone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(5.5), "t", "t");   // UTC+05:30
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), tz);
        Assert.Equal(new DateTime(2026, 3, 9, 18, 30, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(TimeSpan.FromHours(24), range.Length);

        // 20:00 UTC on the 9th is 01:30 local on the 10th
        var sessions = new[] { S(Code, new DateTime(2026, 3, 9, 20, 0, 0, DateTimeKind.Utc), 60) };
        var s = Sum(sessions, range, tz: tz);
        Assert.Equal(1800, s.ByHour[1]);
        Assert.Equal(1800, s.ByHour[2]);
        Assert.Equal(new DateOnly(2026, 3, 10), s.Daily.Single().Date);
    }

    [Fact]
    public void Daylight_saving_day_has_23_hours()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var range = Periods.Days(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29), tz);    // spring forward
        Assert.Equal(TimeSpan.FromHours(23), range.Length);
    }

    [Fact]
    public void Utc_timestamps_convert_to_local_for_display()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-8), "t", "t");
        var utc = new DateTime(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 3, 9, 22, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
    }
}

[Trait("Category", "Unit")]
public class ExportTests
{
    private static ReportData SampleReport(string appName = "Code")
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var app = new AppRecord { Id = 1, Name = appName, ExecutableName = "code.exe", CategoryId = 2 };
        var cats = new[] { new Category { Id = 2, Name = "Development", Color = "#00ff00" } };
        var sessions = new[] { new ActivitySession { ApplicationId = 1, StartTimeUtc = day.AddHours(9), EndTimeUtc = day.AddHours(10), DurationSeconds = 3600 } };
        var range = Periods.Days(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), TimeZoneInfo.Utc);
        var s = AnalyticsEngine.Summarize(sessions, new[] { app }, cats, range, TimeZoneInfo.Utc, false, day.AddDays(1));
        return ReportBuilder.Build(ReportKind.Daily, s, TimeZoneInfo.Utc, day.AddDays(1));
    }

    // 17. CSV
    [Fact]
    public void Csv_export_contains_sections_and_values()
    {
        var csv = ReportExporter.Export(SampleReport(), ReportFormat.Csv);
        Assert.Contains("# Daily report", csv);
        Assert.Contains("Total tracked time,3600", csv);
        Assert.Contains("Code,code.exe,Development,3600,100.0,1,3600", csv);
        Assert.Contains("2026-03-10,3600,0", csv);
    }

    [Fact]
    public void Csv_cells_are_quoted_and_formula_injection_is_neutralised()
    {
        Assert.Equal("\"a,b\"", ReportExporter.CsvCell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReportExporter.CsvCell("say \"hi\""));
        Assert.Equal("'=cmd|' /C calc'!A0", ReportExporter.CsvCell("=cmd|' /C calc'!A0"));
        Assert.StartsWith("'", ReportExporter.CsvCell("+1"));
        Assert.StartsWith("'", ReportExporter.CsvCell("@SUM(A1)"));
        Assert.Equal("normal", ReportExporter.CsvCell("normal"));
    }

    // 18. JSON
    [Fact]
    public void Json_export_is_valid_and_complete()
    {
        var json = ReportExporter.Export(SampleReport(), ReportFormat.Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(3600, root.GetProperty("TrackedSeconds").GetDouble());
        Assert.Equal("Code", root.GetProperty("Applications")[0].GetProperty("Name").GetString());
        Assert.Equal("2026-03-10", root.GetProperty("PeriodStart").GetString());
        Assert.Equal(1, root.GetProperty("Daily").GetArrayLength());
    }

    [Fact]
    public void Html_export_encodes_untrusted_names()
    {
        var html = ReportExporter.Export(SampleReport("<script>alert(1)</script>"), ReportFormat.Html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Daily report", html);
    }

    [Fact]
    public void Application_report_omits_category_section_and_category_report_omits_applications()
    {
        var r = SampleReport();
        var appReport = new ReportData { Kind = ReportKind.ApplicationUsage, Applications = r.Applications, Categories = r.Categories, Daily = r.Daily };
        var catReport = new ReportData { Kind = ReportKind.CategoryUsage, Applications = r.Applications, Categories = r.Categories, Daily = r.Daily };
        Assert.DoesNotContain("# Categories", ReportExporter.ToCsv(appReport));
        Assert.DoesNotContain("# Applications", ReportExporter.ToCsv(catReport));
    }

    [Fact]
    public async Task Export_writes_file_to_chosen_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TimeTraceExport_" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in Enum.GetValues<ReportFormat>())
            {
                var path = await ReportExporter.ExportToFolderAsync(SampleReport(), f, dir);
                Assert.True(File.Exists(path));
                Assert.StartsWith(dir, path);
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Personal_data_export_contains_sessions_with_titles()
    {
        var day = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        var app = new AppRecord { Id = 1, Name = "Code", ExecutableName = "code.exe" };
        var sess = new[] { new ActivitySession { Id = 1, ApplicationId = 1, WindowTitle = "=evil()", StartTimeUtc = day, EndTimeUtc = day.AddMinutes(5), DurationSeconds = 300 } };
        var json = DataExporter.ToJson(new[] { app }, Array.Empty<Category>(), sess, Array.Empty<TrackingEvent>(), day);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("sessions").GetArrayLength());
        var csv = DataExporter.SessionsToCsv(sess, new Dictionary<int, AppRecord> { [1] = app }, TimeZoneInfo.Utc);
        Assert.Contains("'=evil()", csv);
    }
}

[Trait("Category", "Unit")]
public class SettingsTests
{
    [Fact]
    public void Imported_settings_are_validated()
    {
        var s = AppSettings.FromJson("""{"PollIntervalSeconds":-5,"IdleThresholdMinutes":100000,"Theme":"Purple","AccentColor":"javascript:alert(1)","DateFormat":"'; DROP TABLE","ExcludedExecutables":["  a.exe ","a.exe",""]}""");
        Assert.Equal(1, s.PollIntervalSeconds);
        Assert.Equal(480, s.IdleThresholdMinutes);
        Assert.Equal("Dark", s.Theme);
        Assert.Equal("#3DDC84", s.AccentColor);
        Assert.Equal("yyyy-MM-dd", s.DateFormat);
        Assert.Equal(new[] { "a.exe" }, s.ExcludedExecutables);
    }

    [Fact]
    public void Defaults_match_specification()
    {
        var s = new AppSettings();
        Assert.Equal(1, s.PollIntervalSeconds);
        Assert.Equal(5, s.IdleThresholdMinutes);
        Assert.False(s.StartWithWindows);       // never enabled without consent
        Assert.True(s.TrackingEnabled);
    }

    [Fact]
    public void Key_value_round_trip()
    {
        var s = new AppSettings { IdleThresholdMinutes = 30, Theme = "Light" };
        s.ExcludedTitleRules.Add("re:^secret");
        var back = AppSettings.FromKeyValues(s.ToKeyValues());
        Assert.Equal(30, back.IdleThresholdMinutes);
        Assert.Equal("Light", back.Theme);
        Assert.Equal(new[] { "re:^secret" }, back.ExcludedTitleRules);
    }
}

[Trait("Category", "Performance")]
public class PerformanceTests
{
    [Fact]
    public async Task Large_history_loads_and_summarises_quickly()
    {
        using var t = new TempDb();
        var start = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var apps = new List<AppRecord>();
        for (int i = 0; i < 40; i++)
            apps.Add(await t.Repo.AddApplicationAsync(new AppRecord { Name = "App" + i, ExecutableName = $"app{i}.exe", FirstSeen = start, LastSeen = start }));

        // ~60,000 sessions (about 8 months of heavy use) written in one transaction-sized batch through the repository
        using (var c = t.Db.Open())
        using (var tx = c.BeginTransaction())
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ActivitySessions(ApplicationId,ProcessId,WindowTitle,StartTimeUtc,EndTimeUtc,DurationSeconds,SessionType,IsOpen) VALUES($a,1,'t',$s,$e,300,0,0)";
            var pa = cmd.Parameters.Add("$a", SqliteType.Integer); var ps = cmd.Parameters.Add("$s", SqliteType.Text); var pe = cmd.Parameters.Add("$e", SqliteType.Text);
            for (int i = 0; i < 60_000; i++)
            {
                var s = start.AddMinutes(i * 5);
                pa.Value = apps[i % apps.Count].Id; ps.Value = s.ToString("o"); pe.Value = s.AddMinutes(5).ToString("o");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var range = new DateRange(start.Date, start.Date.AddDays(90));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sessions = await t.Repo.GetSessionsAsync(range.FromUtc, range.ToUtc);
        var loadMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var summary = AnalyticsEngine.Summarize(sessions, await t.Repo.GetApplicationsAsync(), await t.Repo.GetCategoriesAsync(), range, TimeZoneInfo.Utc, false, range.ToUtc);
        var analyseMs = sw.ElapsedMilliseconds;

        Assert.True(sessions.Count > 20_000);
        Assert.Equal(90, summary.Daily.Count);
        Assert.True(loadMs < 5000, $"loading took {loadMs} ms");
        Assert.True(analyseMs < 5000, $"analytics took {analyseMs} ms");
    }
}
