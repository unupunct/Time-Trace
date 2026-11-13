using System.Globalization;
using System.Text.Json;
using TimeTrace.Core.Abstractions;

namespace TimeTrace.Core.Settings;

public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStartTracking { get; set; } = true;
    public string Language { get; set; } = "en";

    // Tracking
    public bool TrackingEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 1;
    public int IdleThresholdMinutes { get; set; } = 5;
    public bool TrackWindowTitles { get; set; } = true;
    public List<string> ExcludedExecutables { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    public List<string> ExcludedTitleRules { get; set; } = new();
    public bool PauseOnLock { get; set; } = true;
    public bool IncludeIdleInReports { get; set; }

    // Display
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#3DDC84";
    public string TimeFormat { get; set; } = "24h";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public int DashboardRefreshSeconds { get; set; } = 10;

    // Privacy
    public bool ShowTrackingIndicator { get; set; } = true;
    public int RetentionDays { get; set; }          // 0 = keep forever

    // Notifications
    public bool NotifyTrackingStarted { get; set; }
    public bool NotifyTrackingPaused { get; set; }
    public bool NotifyDailySummary { get; set; }
    public bool NotifyErrors { get; set; } = true;

    // Reports
    public string ReportFolder { get; set; } = "";

    public static readonly string[] ValidDateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "MM/dd/yyyy", "dd/MM/yyyy" };
    public static readonly int[] IdlePresetMinutes = { 1, 3, 5, 10, 15, 30 };

    /// <summary>Clamps and sanitises every value so untrusted/imported data can never produce an invalid state.</summary>
    public void Validate()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 1, 60);
        IdleThresholdMinutes = Math.Clamp(IdleThresholdMinutes, 1, 480);
        DashboardRefreshSeconds = Math.Clamp(DashboardRefreshSeconds, 2, 600);
        RetentionDays = Math.Clamp(RetentionDays, 0, 3650);
        Theme = string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        TimeFormat = TimeFormat == "12h" ? "12h" : "24h";
        if (!ValidDateFormats.Contains(DateFormat)) DateFormat = "yyyy-MM-dd";
        if (!IsValidHexColor(AccentColor)) AccentColor = "#3DDC84";
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim();
        ExcludedExecutables = Clean(ExcludedExecutables);
        ExcludedPaths = Clean(ExcludedPaths);
        ExcludedTitleRules = Clean(ExcludedTitleRules);
        ReportFolder ??= "";
    }

    public static bool IsValidHexColor(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim()).Where(s => s.Length is > 0 and <= 260)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Clone() => FromJson(ToJson());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static AppSettings FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        s.Validate();
        return s;
    }

    // ---- key/value mapping for the Settings table ----
    public Dictionary<string, string> ToKeyValues()
    {
        var d = new Dictionary<string, string>();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            var v = p.GetValue(this);
            d[p.Name] = v is List<string> l ? JsonSerializer.Serialize(l)
                : v is bool b ? (b ? "true" : "false")
                : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
        return d;
    }

    public static AppSettings FromKeyValues(IReadOnlyDictionary<string, string> kv)
    {
        var s = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!kv.TryGetValue(p.Name, out var raw)) continue;
            try
            {
                if (p.PropertyType == typeof(bool)) p.SetValue(s, bool.Parse(raw));
                else if (p.PropertyType == typeof(int)) p.SetValue(s, int.Parse(raw, CultureInfo.InvariantCulture));
                else if (p.PropertyType == typeof(string)) p.SetValue(s, raw);
                else if (p.PropertyType == typeof(List<string>)) p.SetValue(s, JsonSerializer.Deserialize<List<string>>(raw) ?? new());
            }
            catch (Exception) { /* malformed stored value: keep the default */ }
        }
        s.Validate();
        return s;
    }
}

/// <summary>Loads/saves settings via the repository and notifies listeners.</summary>
public sealed class SettingsService
{
    private readonly ISettingsRepository _repo;
    public AppSettings Current { get; private set; } = new();
    public event Action<AppSettings>? Changed;

    public SettingsService(ISettingsRepository repo) => _repo = repo;

    public async Task LoadAsync() => Current = AppSettings.FromKeyValues(await _repo.GetAllSettingsAsync());

    public async Task SaveAsync(AppSettings settings)
    {
        settings.Validate();
        await _repo.SetSettingsAsync(settings.ToKeyValues());
        Current = settings;
        Changed?.Invoke(settings);
    }
}
