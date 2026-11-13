using TimeTrace.Core.Models;

namespace TimeTrace.Core.Analytics;

/// <summary>Half-open UTC interval [FromUtc, ToUtc).</summary>
public readonly record struct DateRange(DateTime FromUtc, DateTime ToUtc)
{
    public TimeSpan Length => ToUtc - FromUtc;
}

public static class Periods
{
    /// <summary>Start of a local calendar day expressed in UTC (DST-safe even when midnight does not exist).</summary>
    public static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public static DateRange Days(DateOnly firstDay, DateOnly lastDayInclusive, TimeZoneInfo tz) =>
        new(LocalMidnightUtc(firstDay, tz), LocalMidnightUtc(lastDayInclusive.AddDays(1), tz));

    public static DateOnly LocalToday(DateTime nowUtc, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));

    public static DateOnly StartOfWeek(DateOnly d, DayOfWeek first = DayOfWeek.Monday)
    {
        int diff = (7 + (d.DayOfWeek - first)) % 7;
        return d.AddDays(-diff);
    }

    public static DateOnly StartOfMonth(DateOnly d) => new(d.Year, d.Month, 1);
}

public sealed record AppUsage(
    int ApplicationId, string Name, string ExecutableName, int? CategoryId,
    double Seconds, int SessionCount, double LongestSessionSeconds, DateTime? FirstUtc, DateTime? LastUtc)
{
    public double AverageSessionSeconds => SessionCount == 0 ? 0 : Seconds / SessionCount;
}

public sealed record CategoryUsage(int? CategoryId, string Name, string Color, double Seconds);
public sealed record DailyUsage(DateOnly Date, double ActiveSeconds, double IdleSeconds);

public sealed record SessionSlice(ActivitySession Session, DateTime ClippedStartUtc, DateTime ClippedEndUtc)
{
    public double Seconds => (ClippedEndUtc - ClippedStartUtc).TotalSeconds;
}

public sealed class UsageSummary
{
    public DateRange Range { get; init; }
    public bool IdleIncluded { get; init; }
    public double ActiveSeconds { get; init; }
    public double IdleSeconds { get; init; }
    /// <summary>Time the tracker actually observed (active, plus idle when included).</summary>
    public double TrackedSeconds => ActiveSeconds + (IdleIncluded ? IdleSeconds : 0);
    /// <summary>Part of the elapsed period (up to "now") that was not observed: tracker off, paused, locked, asleep.</summary>
    public double UnobservedSeconds { get; init; }
    public int ApplicationCount => Apps.Count;
    public AppUsage? MostUsedApp => Apps.FirstOrDefault();
    public CategoryUsage? MostUsedCategory => Categories.FirstOrDefault();
    public int ActiveSessionCount { get; init; }
    public double AverageSessionSeconds => ActiveSessionCount == 0 ? 0 : ActiveSeconds / ActiveSessionCount;
    public int SwitchCount { get; init; }
    public double DailyAverageSeconds { get; init; }
    public double WeeklyAverageSeconds { get; init; }
    public IReadOnlyList<AppUsage> Apps { get; init; } = Array.Empty<AppUsage>();
    public IReadOnlyList<CategoryUsage> Categories { get; init; } = Array.Empty<CategoryUsage>();
    public IReadOnlyList<SessionSlice> LongestSessions { get; init; } = Array.Empty<SessionSlice>();
    public IReadOnlyList<DailyUsage> Daily { get; init; } = Array.Empty<DailyUsage>();
    /// <summary>Active seconds per local hour of day (0..23).</summary>
    public double[] ByHour { get; init; } = new double[24];
    /// <summary>Active seconds per day of week (index = (int)DayOfWeek).</summary>
    public double[] ByDayOfWeek { get; init; } = new double[7];
    /// <summary>Heatmap [dayOfWeek, hour] in seconds.</summary>
    public double[,] Heatmap { get; init; } = new double[7, 24];
    public bool HasData => ActiveSeconds > 0 || IdleSeconds > 0;
}

public sealed record PeriodComparison(string Label, UsageSummary Current, UsageSummary Previous)
{
    public double DeltaSeconds => Current.TrackedSeconds - Previous.TrackedSeconds;
    /// <summary>Null when the previous period has no data (a percentage would be meaningless).</summary>
    public double? DeltaPercent => Previous.TrackedSeconds <= 0 ? null : DeltaSeconds / Previous.TrackedSeconds * 100.0;
}

public static class AnalyticsEngine
{
    public static UsageSummary Summarize(
        IEnumerable<ActivitySession> sessions, IReadOnlyCollection<AppRecord> apps, IReadOnlyCollection<Category> categories,
        DateRange range, TimeZoneInfo tz, bool includeIdle, DateTime nowUtc)
    {
        var appById = apps.ToDictionary(a => a.Id);
        var catById = categories.ToDictionary(c => c.Id);
        var other = categories.FirstOrDefault(c => string.Equals(c.Name, DefaultCategories_Fallback, StringComparison.OrdinalIgnoreCase));

        var slices = new List<SessionSlice>();
        foreach (var s in sessions)
        {
            var a = s.StartTimeUtc > range.FromUtc ? s.StartTimeUtc : range.FromUtc;
            var b = s.EndTimeUtc < range.ToUtc ? s.EndTimeUtc : range.ToUtc;
            if (b > a) slices.Add(new SessionSlice(s, a, b));
        }
        slices.Sort((x, y) => x.ClippedStartUtc.CompareTo(y.ClippedStartUtc));

        double active = 0, idle = 0;
        var byHour = new double[24];
        var byDow = new double[7];
        var heat = new double[7, 24];
        var daily = new SortedDictionary<DateOnly, (double Active, double Idle)>();
        var perApp = new Dictionary<int, (double Sec, int Cnt, double Longest, DateTime First, DateTime Last)>();
        var perCat = new Dictionary<int, double>();
        int activeCount = 0, switches = 0;
        int? lastApp = null;

        foreach (var sl in slices)
        {
            var secs = sl.Seconds;
            bool isIdle = sl.Session.SessionType == SessionType.Idle;
            if (isIdle)
            {
                idle += secs;
            }
            else
            {
                active += secs; activeCount++;
                if (sl.Session.ApplicationId is int id)
                {
                    perApp.TryGetValue(id, out var cur);
                    perApp[id] = (cur.Sec + secs, cur.Cnt + 1, Math.Max(cur.Longest, secs),
                        cur.Cnt == 0 || sl.ClippedStartUtc < cur.First ? sl.ClippedStartUtc : cur.First,
                        cur.Cnt == 0 || sl.ClippedEndUtc > cur.Last ? sl.ClippedEndUtc : cur.Last);
                    int? catId = appById.TryGetValue(id, out var ar) ? ar.CategoryId ?? other?.Id : other?.Id;
                    perCat[catId ?? 0] = perCat.GetValueOrDefault(catId ?? 0) + secs;
                    if (lastApp.HasValue && lastApp.Value != id) switches++;
                    lastApp = id;
                }
            }

            // distribute across local hours / days
            var cur2 = sl.ClippedStartUtc;
            while (cur2 < sl.ClippedEndUtc)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(cur2, tz);
                var toBoundary = TimeSpan.FromSeconds(3600 - (local.Minute * 60 + local.Second) - local.Millisecond / 1000.0);
                var next = cur2 + toBoundary;
                if (next > sl.ClippedEndUtc) next = sl.ClippedEndUtc;
                var chunk = (next - cur2).TotalSeconds;
                if (chunk <= 0) break;
                var day = DateOnly.FromDateTime(local);
                daily.TryGetValue(day, out var d);
                daily[day] = isIdle ? (d.Active, d.Idle + chunk) : (d.Active + chunk, d.Idle);
                if (!isIdle)
                {
                    byHour[local.Hour] += chunk;
                    byDow[(int)local.DayOfWeek] += chunk;
                    heat[(int)local.DayOfWeek, local.Hour] += chunk;
                }
                cur2 = next;
            }
        }

        // Make sure every local day inside the range appears (so charts show gaps honestly as zero-observed days).
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(range.FromUtc, tz));
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(range.ToUtc.AddTicks(-1), tz));
        var dailyList = new List<DailyUsage>();
        for (var d = firstDay; d <= lastDay && (lastDay.DayNumber - firstDay.DayNumber) < 3700; d = d.AddDays(1))
        {
            daily.TryGetValue(d, out var v);
            dailyList.Add(new DailyUsage(d, v.Active, v.Idle));
        }

        var appUsages = perApp
            .Select(kv =>
            {
                appById.TryGetValue(kv.Key, out var a);
                return new AppUsage(kv.Key, a?.Name ?? "(deleted application)", a?.ExecutableName ?? "", a?.CategoryId,
                    kv.Value.Sec, kv.Value.Cnt, kv.Value.Longest, kv.Value.First, kv.Value.Last);
            })
            .OrderByDescending(a => a.Seconds).ToList();

        var catUsages = perCat.Select(kv =>
            {
                catById.TryGetValue(kv.Key, out var c);
                return new CategoryUsage(kv.Key == 0 ? null : kv.Key, c?.Name ?? "Uncategorized", c?.Color ?? "#6B7280", kv.Value);
            })
            .OrderByDescending(c => c.Seconds).ToList();

        var tracked = active + (includeIdle ? idle : 0);
        var end = nowUtc < range.ToUtc ? nowUtc : range.ToUtc;
        var elapsed = Math.Max(0, (end - range.FromUtc).TotalSeconds);
        var unobserved = Math.Max(0, elapsed - (active + idle));

        var daysWithData = dailyList.Count(d => d.ActiveSeconds + (includeIdle ? d.IdleSeconds : 0) > 0);
        var weeksWithData = dailyList.Where(d => d.ActiveSeconds + (includeIdle ? d.IdleSeconds : 0) > 0)
            .Select(d => Periods.StartOfWeek(d.Date)).Distinct().Count();

        return new UsageSummary
        {
            Range = range, IdleIncluded = includeIdle, ActiveSeconds = active, IdleSeconds = idle, UnobservedSeconds = unobserved,
            ActiveSessionCount = activeCount, SwitchCount = switches,
            DailyAverageSeconds = daysWithData == 0 ? 0 : tracked / daysWithData,
            WeeklyAverageSeconds = weeksWithData == 0 ? 0 : tracked / weeksWithData,
            Apps = appUsages, Categories = catUsages, Daily = dailyList,
            LongestSessions = slices.Where(s => s.Session.SessionType == SessionType.Active).OrderByDescending(s => s.Seconds).Take(10).ToList(),
            ByHour = byHour, ByDayOfWeek = byDow, Heatmap = heat,
        };
    }

    private const string DefaultCategories_Fallback = "Other";

    public static PeriodComparison Compare(string label, UsageSummary current, UsageSummary previous) => new(label, current, previous);

    /// <summary>Same-length period immediately before <paramref name="current"/> (calendar-aware for whole days).</summary>
    public static DateRange PreviousRange(DateRange current, TimeZoneInfo tz)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(current.FromUtc, tz));
        var lastExcl = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(current.ToUtc, tz));
        int days = Math.Max(1, lastExcl.DayNumber - first.DayNumber);
        return new DateRange(Periods.LocalMidnightUtc(first.AddDays(-days), tz), current.FromUtc);
    }

    public static (DateRange Current, DateRange Previous) ThisMonthVsLast(DateOnly today, TimeZoneInfo tz)
    {
        var start = Periods.StartOfMonth(today);
        var prevStart = start.AddMonths(-1);
        return (new DateRange(Periods.LocalMidnightUtc(start, tz), Periods.LocalMidnightUtc(start.AddMonths(1), tz)),
                new DateRange(Periods.LocalMidnightUtc(prevStart, tz), Periods.LocalMidnightUtc(start, tz)));
    }

    public static (DateRange Current, DateRange Previous) ThisWeekVsLast(DateOnly today, TimeZoneInfo tz)
    {
        var start = Periods.StartOfWeek(today);
        return (new DateRange(Periods.LocalMidnightUtc(start, tz), Periods.LocalMidnightUtc(start.AddDays(7), tz)),
                new DateRange(Periods.LocalMidnightUtc(start.AddDays(-7), tz), Periods.LocalMidnightUtc(start, tz)));
    }

    public static (DateRange Current, DateRange Previous) TodayVsYesterday(DateOnly today, TimeZoneInfo tz) =>
        (Periods.Days(today, today, tz), Periods.Days(today.AddDays(-1), today.AddDays(-1), tz));

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}
