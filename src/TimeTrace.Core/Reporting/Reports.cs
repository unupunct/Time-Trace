using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;

namespace TimeTrace.Core.Reporting;

public enum ReportKind { Daily, Weekly, Monthly, Custom, ApplicationUsage, CategoryUsage }
public enum ReportFormat { Csv, Json, Html }

public sealed record ReportApp(string Name, string Executable, string Category, double Seconds, double Percent, int Sessions, double AverageSessionSeconds);
public sealed record ReportCategory(string Name, string Color, double Seconds, double Percent);
public sealed record ReportDay(string Date, double ActiveSeconds, double IdleSeconds);

public sealed class ReportData
{
    public string Title { get; init; } = "";
    public ReportKind Kind { get; init; }
    public string Tagline { get; init; } = "Understand where your time goes.";
    public DateTime GeneratedUtc { get; init; }
    public string PeriodStart { get; init; } = "";
    public string PeriodEnd { get; init; } = "";
    public bool IdleIncludedInTotals { get; init; }
    public double TrackedSeconds { get; init; }
    public double ActiveSeconds { get; init; }
    public double IdleSeconds { get; init; }
    public double UnobservedSeconds { get; init; }
    public int ApplicationCount { get; init; }
    public int SwitchCount { get; init; }
    public double AverageSessionSeconds { get; init; }
    public List<ReportApp> Applications { get; init; } = new();
    public List<ReportCategory> Categories { get; init; } = new();
    public List<ReportDay> Daily { get; init; } = new();
    public double[] ActiveSecondsByHour { get; init; } = new double[24];
    public string Note { get; init; } = "Categories are user-defined classifications, not objective measures of productivity. " +
        "Unobserved time is time when TimeTrace was not tracking (stopped, paused, locked or asleep).";
}

public static class ReportBuilder
{
    public static ReportData Build(ReportKind kind, UsageSummary s, TimeZoneInfo tz, DateTime nowUtc)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s.Range.FromUtc, tz));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s.Range.ToUtc.AddTicks(-1), tz));
        var title = kind switch
        {
            ReportKind.Daily => "Daily report",
            ReportKind.Weekly => "Weekly report",
            ReportKind.Monthly => "Monthly report",
            ReportKind.ApplicationUsage => "Application usage report",
            ReportKind.CategoryUsage => "Category usage report",
            _ => "Custom range report",
        };
        var total = Math.Max(s.TrackedSeconds, 0.0001);
        int topN = kind == ReportKind.ApplicationUsage ? int.MaxValue : 20;
        return new ReportData
        {
            Title = title, Kind = kind, GeneratedUtc = nowUtc,
            PeriodStart = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), PeriodEnd = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IdleIncludedInTotals = s.IdleIncluded, TrackedSeconds = s.TrackedSeconds, ActiveSeconds = s.ActiveSeconds, IdleSeconds = s.IdleSeconds,
            UnobservedSeconds = s.UnobservedSeconds, ApplicationCount = s.ApplicationCount, SwitchCount = s.SwitchCount, AverageSessionSeconds = s.AverageSessionSeconds,
            Applications = s.Apps.Take(topN).Select(a => new ReportApp(a.Name, a.ExecutableName,
                s.Categories.FirstOrDefault(c => c.CategoryId == a.CategoryId)?.Name ?? "", a.Seconds, a.Seconds / total * 100, a.SessionCount, a.AverageSessionSeconds)).ToList(),
            Categories = s.Categories.Select(c => new ReportCategory(c.Name, c.Color, c.Seconds, c.Seconds / total * 100)).ToList(),
            Daily = s.Daily.Select(d => new ReportDay(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.ActiveSeconds, d.IdleSeconds)).ToList(),
            ActiveSecondsByHour = s.ByHour,
        };
    }
}

public static class ReportExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Export(ReportData r, ReportFormat format) => format switch
    {
        ReportFormat.Csv => ToCsv(r),
        ReportFormat.Json => JsonSerializer.Serialize(r, JsonOpts),
        _ => ToHtml(r),
    };

    public static string Extension(ReportFormat f) => f switch { ReportFormat.Csv => ".csv", ReportFormat.Json => ".json", _ => ".html" };

    public static string SuggestedFileName(ReportData r, ReportFormat f) =>
        $"TimeTrace_{r.Kind}_{r.PeriodStart}{(r.PeriodStart == r.PeriodEnd ? "" : "_to_" + r.PeriodEnd)}{Extension(f)}";

    public static async Task<string> ExportToFolderAsync(ReportData r, ReportFormat f, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SuggestedFileName(r, f));
        await File.WriteAllTextAsync(path, Export(r, f), new UTF8Encoding(f == ReportFormat.Csv));
        return path;
    }

    // ----------------------------------------------------------------- CSV
    /// <summary>Quotes a CSV cell and neutralises spreadsheet formula injection (cells that start with = + - @).</summary>
    public static string CsvCell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) v = "'" + v;
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static string N(double d) => Math.Round(d, 0).ToString(CultureInfo.InvariantCulture);
    private static string P(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);

    public static string ToCsv(ReportData r)
    {
        var sb = new StringBuilder();
        void Row(params string?[] cells) => sb.AppendLine(string.Join(",", cells.Select(CsvCell)));
        Row("# " + r.Title); Row("Period", r.PeriodStart, r.PeriodEnd);
        Row("Generated (UTC)", r.GeneratedUtc.ToString("o", CultureInfo.InvariantCulture));
        sb.AppendLine();
        Row("# Summary"); Row("Metric", "Seconds");
        Row("Total tracked time", N(r.TrackedSeconds)); Row("Active time", N(r.ActiveSeconds)); Row("Idle time", N(r.IdleSeconds));
        Row("Unobserved time", N(r.UnobservedSeconds)); Row("Applications used", r.ApplicationCount.ToString(CultureInfo.InvariantCulture));
        Row("Application switches", r.SwitchCount.ToString(CultureInfo.InvariantCulture)); Row("Average session", N(r.AverageSessionSeconds));
        if (r.Kind != ReportKind.CategoryUsage)
        {
            sb.AppendLine(); Row("# Applications"); Row("Application", "Executable", "Category", "Seconds", "Percent of tracked", "Sessions", "Average session seconds");
            foreach (var a in r.Applications) Row(a.Name, a.Executable, a.Category, N(a.Seconds), P(a.Percent), a.Sessions.ToString(CultureInfo.InvariantCulture), N(a.AverageSessionSeconds));
        }
        if (r.Kind != ReportKind.ApplicationUsage)
        {
            sb.AppendLine(); Row("# Categories"); Row("Category", "Seconds", "Percent of tracked");
            foreach (var c in r.Categories) Row(c.Name, N(c.Seconds), P(c.Percent));
        }
        if (r.Kind is not (ReportKind.ApplicationUsage or ReportKind.CategoryUsage))
        {
            sb.AppendLine(); Row("# Daily breakdown"); Row("Date", "Active seconds", "Idle seconds");
            foreach (var d in r.Daily) Row(d.Date, N(d.ActiveSeconds), N(d.IdleSeconds));
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------------- HTML
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Dur(double s) => AnalyticsEngine.FormatDuration(s);

    public static string ToHtml(ReportData r)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>TimeTrace – {H(r.Title)}</title><style>");
        sb.Append("body{background:#15171a;color:#e8eaed;font:14px/1.5 Segoe UI,system-ui,sans-serif;margin:0;padding:32px}main{max-width:960px;margin:auto}");
        sb.Append("h1{margin:0;font-size:26px}h2{font-size:16px;margin:32px 0 10px;color:#3ddc84}.sub{color:#9aa0a6}.cards{display:flex;flex-wrap:wrap;gap:12px;margin-top:20px}");
        sb.Append(".card{background:#1f2226;border-radius:12px;padding:14px 18px;min-width:150px}.card b{display:block;font-size:22px}.card span{color:#9aa0a6;font-size:12px}");
        sb.Append("table{width:100%;border-collapse:collapse;background:#1f2226;border-radius:12px;overflow:hidden}th,td{padding:8px 12px;text-align:left}th{color:#9aa0a6;font-weight:600;font-size:12px;background:#262a2f}");
        sb.Append("tr+tr td{border-top:1px solid #2c3036}.bar{height:8px;border-radius:4px;background:#3ddc84}.track{background:#2c3036;border-radius:4px;width:140px}.note{color:#9aa0a6;font-size:12px;margin-top:32px}");
        sb.Append("svg{background:#1f2226;border-radius:12px;width:100%}</style></head><body><main>");
        sb.Append($"<h1>{H(r.Title)}</h1><div class=\"sub\">TimeTrace · {H(r.PeriodStart)}{(r.PeriodStart == r.PeriodEnd ? "" : " – " + H(r.PeriodEnd))} · generated {H(r.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}</div>");
        sb.Append("<div class=\"cards\">");
        void Card(string label, string value) => sb.Append($"<div class=\"card\"><b>{H(value)}</b><span>{H(label)}</span></div>");
        Card("Total tracked time", Dur(r.TrackedSeconds)); Card("Active time", Dur(r.ActiveSeconds)); Card("Idle time", Dur(r.IdleSeconds));
        Card("Unobserved time", Dur(r.UnobservedSeconds)); Card("Applications", r.ApplicationCount.ToString(CultureInfo.InvariantCulture));
        Card("App switches", r.SwitchCount.ToString(CultureInfo.InvariantCulture)); Card("Avg session", Dur(r.AverageSessionSeconds));
        sb.Append("</div>");

        if (r.Kind != ReportKind.CategoryUsage && r.Applications.Count > 0)
        {
            sb.Append("<h2>Top applications</h2><table><tr><th>Application</th><th>Category</th><th>Time</th><th>Share</th><th>Sessions</th></tr>");
            foreach (var a in r.Applications)
                sb.Append($"<tr><td>{H(a.Name)}<br><span class=\"sub\">{H(a.Executable)}</span></td><td>{H(a.Category)}</td><td>{H(Dur(a.Seconds))}</td>" +
                          $"<td><div class=\"track\"><div class=\"bar\" style=\"width:{Math.Clamp(a.Percent, 0, 100).ToString("0.#", CultureInfo.InvariantCulture)}%\"></div></div>{P(a.Percent)}%</td><td>{a.Sessions}</td></tr>");
            sb.Append("</table>");
        }
        if (r.Kind != ReportKind.ApplicationUsage && r.Categories.Count > 0)
        {
            sb.Append("<h2>Category distribution</h2><table><tr><th>Category</th><th>Time</th><th>Share</th></tr>");
            foreach (var c in r.Categories)
            {
                var color = Settings.AppSettings.IsValidHexColor(c.Color) ? c.Color : "#6B7280";
                sb.Append($"<tr><td>{H(c.Name)}</td><td>{H(Dur(c.Seconds))}</td><td><div class=\"track\"><div class=\"bar\" style=\"background:{color};width:{Math.Clamp(c.Percent, 0, 100).ToString("0.#", CultureInfo.InvariantCulture)}%\"></div></div>{P(c.Percent)}%</td></tr>");
            }
            sb.Append("</table>");
        }
        if (r.Kind is not (ReportKind.ApplicationUsage or ReportKind.CategoryUsage) && r.Daily.Count > 0)
        {
            sb.Append("<h2>Daily breakdown</h2>");
            var max = Math.Max(1, r.Daily.Max(d => d.ActiveSeconds + (r.IdleIncludedInTotals ? d.IdleSeconds : 0)));
            int n = r.Daily.Count; double w = 900.0 / n, bw = Math.Min(40, w * 0.7);
            sb.Append("<svg viewBox=\"0 0 920 200\" role=\"img\" aria-label=\"Daily activity chart\">");
            for (int i = 0; i < n; i++)
            {
                var d = r.Daily[i];
                var h = (d.ActiveSeconds + (r.IdleIncludedInTotals ? d.IdleSeconds : 0)) / max * 150;
                var x = 10 + i * w + (w - bw) / 2;
                sb.Append($"<rect x=\"{x.ToString("0.#", CultureInfo.InvariantCulture)}\" y=\"{(170 - h).ToString("0.#", CultureInfo.InvariantCulture)}\" width=\"{bw.ToString("0.#", CultureInfo.InvariantCulture)}\" height=\"{h.ToString("0.#", CultureInfo.InvariantCulture)}\" rx=\"3\" fill=\"#3ddc84\"><title>{H(d.Date)}: {H(Dur(d.ActiveSeconds))}</title></rect>");
                if (n <= 31 && (n <= 10 || i % Math.Ceiling(n / 10.0) == 0))
                    sb.Append($"<text x=\"{(x + bw / 2).ToString("0.#", CultureInfo.InvariantCulture)}\" y=\"190\" fill=\"#9aa0a6\" font-size=\"10\" text-anchor=\"middle\">{H(d.Date[5..])}</text>");
            }
            sb.Append("</svg>");
            sb.Append("<table style=\"margin-top:12px\"><tr><th>Date</th><th>Active</th><th>Idle</th></tr>");
            foreach (var d in r.Daily) sb.Append($"<tr><td>{H(d.Date)}</td><td>{H(Dur(d.ActiveSeconds))}</td><td>{H(Dur(d.IdleSeconds))}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<p class=\"note\">{H(r.Note)}<br>{H(r.Tagline)} All data stays on your computer.</p></main></body></html>");
        return sb.ToString();
    }
}

/// <summary>Complete export of the user's personal data (for portability / GDPR-style access).</summary>
public static class DataExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ToJson(IEnumerable<AppRecord> apps, IEnumerable<Category> cats, IEnumerable<ActivitySession> sessions,
        IEnumerable<TrackingEvent> events, DateTime nowUtc)
        => JsonSerializer.Serialize(new
        {
            exportedUtc = nowUtc, application = "TimeTrace",
            categories = cats, applications = apps, sessions = sessions.Select(s => new
            {
                s.Id, s.ApplicationId, s.ProcessId, s.WindowTitle, s.StartTimeUtc, s.EndTimeUtc, s.DurationSeconds, type = s.SessionType.ToString(),
            }),
            trackingEvents = events,
        }, JsonOpts);

    public static string SessionsToCsv(IEnumerable<ActivitySession> sessions, IReadOnlyDictionary<int, AppRecord> apps, TimeZoneInfo tz)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Application,Executable,Type,WindowTitle,StartUtc,EndUtc,StartLocal,EndLocal,DurationSeconds");
        foreach (var s in sessions)
        {
            apps.TryGetValue(s.ApplicationId ?? -1, out var a);
            var cells = new[]
            {
                s.Id.ToString(CultureInfo.InvariantCulture), a?.Name, a?.ExecutableName, s.SessionType.ToString(), s.WindowTitle,
                s.StartTimeUtc.ToString("o", CultureInfo.InvariantCulture), s.EndTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTimeFromUtc(s.StartTimeUtc, tz).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTimeFromUtc(s.EndTimeUtc, tz).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                s.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            };
            sb.AppendLine(string.Join(",", cells.Select(ReportExporter.CsvCell)));
        }
        return sb.ToString();
    }
}
