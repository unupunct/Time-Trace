using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;
using TimeTrace.Core.Tracking;
using TimeTrace.Data;

namespace TimeTrace.App.Services;

public enum RangePreset { Today, Yesterday, Last7Days, Last30Days, Custom }

public sealed record Lookups(IReadOnlyList<AppRecord> Apps, IReadOnlyList<Category> Categories)
{
    public Dictionary<int, AppRecord> AppById => Apps.ToDictionary(a => a.Id);
    public Dictionary<int, Category> CategoryById => Categories.ToDictionary(c => c.Id);
    public Category Fallback => Categories.FirstOrDefault(c => c.Name == DefaultCategories.FallbackName) ?? Categories.Last();
}

/// <summary>Read side used by the UI: loads sessions (merged with the in-memory live tail) and runs analytics.</summary>
public sealed class DataService
{
    private readonly SqliteRepository _repo;
    private readonly TrackingEngine _engine;
    private readonly Func<bool> _includeIdle;

    public DataService(SqliteRepository repo, TrackingEngine engine, Func<bool> includeIdle)
    { _repo = repo; _engine = engine; _includeIdle = includeIdle; }

    public async Task<Lookups> GetLookupsAsync() => new(await _repo.GetApplicationsAsync(), await _repo.GetCategoriesAsync());

    public static DateRange RangeFor(RangePreset p, DateOnly? from = null, DateOnly? to = null)
    {
        var tz = Fmt.Tz;
        var today = Periods.LocalToday(DateTime.UtcNow, tz);
        return p switch
        {
            RangePreset.Today => Periods.Days(today, today, tz),
            RangePreset.Yesterday => Periods.Days(today.AddDays(-1), today.AddDays(-1), tz),
            RangePreset.Last7Days => Periods.Days(today.AddDays(-6), today, tz),
            RangePreset.Last30Days => Periods.Days(today.AddDays(-29), today, tz),
            _ => Periods.Days(from ?? today, to ?? from ?? today, tz) is var r && r.ToUtc > r.FromUtc ? r : Periods.Days(today, today, tz),
        };
    }

    public async Task<IReadOnlyList<ActivitySession>> GetSessionsAsync(DateRange range, int? appId = null)
    {
        var list = (await _repo.GetSessionsAsync(range.FromUtc, range.ToUtc, appId)).ToList();
        foreach (var live in _engine.GetLiveSessions())
        {
            if (live.EndTimeUtc <= range.FromUtc || live.StartTimeUtc >= range.ToUtc) continue;
            if (appId.HasValue && live.ApplicationId != appId) continue;
            var i = list.FindIndex(s => s.Id == live.Id);
            if (i >= 0) list[i] = live; else list.Add(live);
        }
        return list;
    }

    public async Task<UsageSummary> SummarizeAsync(DateRange range, Lookups? lookups = null, IEnumerable<ActivitySession>? sessions = null)
    {
        lookups ??= await GetLookupsAsync();
        sessions ??= await GetSessionsAsync(range);
        return AnalyticsEngine.Summarize(sessions, lookups.Apps.ToList(), lookups.Categories.ToList(), range, Fmt.Tz, _includeIdle(), DateTime.UtcNow);
    }
}
