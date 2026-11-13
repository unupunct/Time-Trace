using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Services;
using TimeTrace.Core.Reporting;
using TimeTrace.Core.Settings;

namespace TimeTrace.App.ViewModels;

public sealed record Choice(int Value, string Label);
public sealed record TextChoice(string Value, string Label);

public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Title => "Settings";

    public AppSettings Draft { get; private set; }

    public IReadOnlyList<Choice> PollChoices { get; } = new[] { new Choice(1, "1 second (default)"), new Choice(2, "2 seconds"), new Choice(5, "5 seconds"), new Choice(10, "10 seconds"), new Choice(30, "30 seconds") };
    public IReadOnlyList<Choice> IdleChoices { get; } = new[]
    {
        new Choice(1, "1 minute"), new Choice(3, "3 minutes"), new Choice(5, "5 minutes (default)"), new Choice(10, "10 minutes"),
        new Choice(15, "15 minutes"), new Choice(30, "30 minutes"), new Choice(-1, "Custom…"),
    };
    public IReadOnlyList<Choice> RetentionChoices { get; } = new[] { new Choice(0, "Keep forever"), new Choice(30, "30 days"), new Choice(90, "90 days"), new Choice(180, "180 days"), new Choice(365, "1 year") };
    public IReadOnlyList<Choice> RefreshChoices { get; } = new[] { new Choice(5, "5 seconds"), new Choice(10, "10 seconds"), new Choice(30, "30 seconds"), new Choice(60, "1 minute"), new Choice(300, "5 minutes") };
    public IReadOnlyList<TextChoice> Themes { get; } = new[] { new TextChoice("Dark", "Dark"), new TextChoice("Light", "Light") };
    public IReadOnlyList<TextChoice> TimeFormats { get; } = new[] { new TextChoice("24h", "24-hour (14:30)"), new TextChoice("12h", "12-hour (2:30 PM)") };
    public IReadOnlyList<TextChoice> DateFormats { get; } = new[]
    {
        new TextChoice("yyyy-MM-dd", "2026-03-31"), new TextChoice("dd.MM.yyyy", "31.03.2026"), new TextChoice("dd/MM/yyyy", "31/03/2026"), new TextChoice("MM/dd/yyyy", "03/31/2026"),
    };
    public IReadOnlyList<TextChoice> Languages { get; } = new[] { new TextChoice("en", "English") };
    public IReadOnlyList<string> AccentPalette { get; } = new[] { "#3DDC84", "#4F9DFF", "#B07CFF", "#FF7A9C", "#FF9F4A", "#2FC8D9", "#E6C34A" };

    public ObservableCollection<string> ExcludedExecutables { get; } = new();
    public ObservableCollection<string> ExcludedPaths { get; } = new();
    public ObservableCollection<string> ExcludedTitleRules { get; } = new();

    [ObservableProperty] private int _idleChoice = 5;
    [ObservableProperty] private int _customIdleMinutes = 20;
    [ObservableProperty] private string _newExecutable = "", _newPath = "", _newTitleRule = "";
    [ObservableProperty] private string _databasePath = "", _statusMessage = "", _accentHex = "#3DDC84";
    public bool IsCustomIdle => IdleChoice == -1;
    public bool TrackingAvailable => Draft.TrackingEnabled;

    private CancellationTokenSource? _debounce;

    public SettingsViewModel()
    {
        Draft = AppServices.Instance.Settings.Current.Clone();
        Load();
    }

    private void Load()
    {
        var svc = AppServices.Instance;
        Draft = svc.Settings.Current.Clone();
        Draft.StartWithWindows = svc.Startup.IsEnabled();       // the registry is the source of truth
        IdleChoice = IdleChoices.Any(c => c.Value == Draft.IdleThresholdMinutes) ? Draft.IdleThresholdMinutes : -1;
        if (IdleChoice == -1) CustomIdleMinutes = Draft.IdleThresholdMinutes;
        AccentHex = Draft.AccentColor;
        Fill(ExcludedExecutables, Draft.ExcludedExecutables); Fill(ExcludedPaths, Draft.ExcludedPaths); Fill(ExcludedTitleRules, Draft.ExcludedTitleRules);
        DatabasePath = svc.Db.Path;
        OnPropertyChanged(nameof(Draft)); OnPropertyChanged(nameof(IsCustomIdle)); OnPropertyChanged(nameof(TrackingAvailable));
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items) { target.Clear(); foreach (var i in items) target.Add(i); }

    public override Task ActivateAsync() { Load(); return Task.CompletedTask; }

    partial void OnIdleChoiceChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomIdle));
        if (value > 0) { Draft.IdleThresholdMinutes = value; Changed(); }
        else { Draft.IdleThresholdMinutes = Math.Clamp(CustomIdleMinutes, 1, 480); Changed(); }
    }
    partial void OnCustomIdleMinutesChanged(int value) { if (IsCustomIdle) { Draft.IdleThresholdMinutes = Math.Clamp(value, 1, 480); Changed(); } }
    partial void OnAccentHexChanged(string value) { if (AppSettings.IsValidHexColor(value) && !string.Equals(Draft.AccentColor, value, StringComparison.OrdinalIgnoreCase)) { Draft.AccentColor = value.ToUpperInvariant(); Changed(); } }

    /// <summary>Called by the view whenever a bound control writes to the draft; saves after a short debounce.</summary>
    public void Changed()
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(350, cts.Token); } catch (OperationCanceledException) { return; }
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => await PersistAsync());
        });
    }

    private async Task PersistAsync()
    {
        try
        {
            var svc = AppServices.Instance;
            Draft.ExcludedExecutables = ExcludedExecutables.ToList(); Draft.ExcludedPaths = ExcludedPaths.ToList(); Draft.ExcludedTitleRules = ExcludedTitleRules.ToList();
            var wasTracking = svc.Settings.Current.TrackingEnabled;
            var toSave = Draft.Clone();
            toSave.FirstRunCompleted = true;
            await svc.Settings.SaveAsync(toSave);

            // side effects
            svc.Theme.Apply(toSave.Theme, toSave.AccentColor);
            if (toSave.StartWithWindows) svc.Startup.Enable(AppServices.ExePath, toSave.StartMinimized); else svc.Startup.Disable();
            if (wasTracking && !toSave.TrackingEnabled) await svc.Engine.StopAsync();
            OnPropertyChanged(nameof(TrackingAvailable));
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Saving settings failed"); StatusMessage = "Could not save settings: " + ex.Message; }
    }

    // ---------------------------------------------------------------- exclusion lists
    private void Add(ObservableCollection<string> list, ref string input)
    {
        var v = input.Trim();
        if (v.Length == 0 || v.Length > 260 || list.Contains(v, StringComparer.OrdinalIgnoreCase)) { input = ""; return; }
        list.Add(v); input = ""; Changed();
    }
    [RelayCommand] private void AddExecutable() { var s = NewExecutable; Add(ExcludedExecutables, ref s); NewExecutable = s; AppServices.Instance.InvalidateAppCache(); }
    [RelayCommand] private void AddPath() { var s = NewPath; Add(ExcludedPaths, ref s); NewPath = s; }
    [RelayCommand] private void AddTitleRule() { var s = NewTitleRule; Add(ExcludedTitleRules, ref s); NewTitleRule = s; }
    [RelayCommand] private void RemoveExecutable(string? v) { if (v != null && ExcludedExecutables.Remove(v)) Changed(); }
    [RelayCommand] private void RemovePath(string? v) { if (v != null && ExcludedPaths.Remove(v)) Changed(); }
    [RelayCommand] private void RemoveTitleRule(string? v) { if (v != null && ExcludedTitleRules.Remove(v)) Changed(); }

    [RelayCommand] private void PickAccent(string? hex) { if (hex != null) AccentHex = hex; }

    // ---------------------------------------------------------------- database & privacy actions
    [RelayCommand]
    private async Task BackupAsync()
    {
        var path = Dialogs.SaveFile("Back up database", $"timetrace-backup-{DateTime.Now:yyyyMMdd-HHmm}.db", "TimeTrace database (*.db)|*.db");
        if (path == null) return;
        try { await Task.Run(() => AppServices.Instance.Db.BackupTo(path)); StatusMessage = "Backup saved to " + path; }
        catch (Exception ex) { Dialogs.Error("Backup failed", ex.Message); }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var path = Dialogs.OpenFile("Restore database", "TimeTrace database (*.db)|*.db|All files (*.*)|*.*");
        if (path == null) return;
        if (!Dialogs.Confirm("Restore database", "Replace ALL current data with this backup?\nA copy of the current database is kept next to it as .before-restore.")) return;
        try
        {
            await AppServices.Instance.RestoreDatabaseAsync(path);
            Load();
            AppServices.Instance.Theme.Apply(AppServices.Instance.Settings.Current.Theme, AppServices.Instance.Settings.Current.AccentColor);
            StatusMessage = "Database restored.";
        }
        catch (Exception ex) { Dialogs.Error("Restore failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        var folder = Dialogs.PickFolder("Choose a folder for your exported data");
        if (folder == null) return;
        try
        {
            var svc = AppServices.Instance;
            var lk = await svc.Data.GetLookupsAsync();
            var sessions = await svc.Repo.GetSessionsAsync(DateTime.UnixEpoch, DateTime.UtcNow.AddDays(2));
            var events = await svc.Repo.GetEventsAsync(10000);
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            await File.WriteAllTextAsync(Path.Combine(folder, $"TimeTrace_all_data_{stamp}.json"), DataExporter.ToJson(lk.Apps, lk.Categories, sessions, events, DateTime.UtcNow));
            await File.WriteAllTextAsync(Path.Combine(folder, $"TimeTrace_sessions_{stamp}.csv"), DataExporter.SessionsToCsv(sessions, lk.AppById, Fmt.Tz), new System.Text.UTF8Encoding(true));
            StatusMessage = $"Exported {sessions.Count} sessions to {folder}";
        }
        catch (Exception ex) { Dialogs.Error("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task DeleteHistoryAsync()
    {
        if (!Dialogs.Confirm("Delete all activity history", "Permanently delete ALL recorded sessions, tracking events and detected applications?\n\nCategories, rules and settings are kept. This cannot be undone.")) return;
        await DeleteAllAsync();
    }

    private async Task DeleteAllAsync()
    {
        try { await AppServices.Instance.DeleteAllActivityAsync(); StatusMessage = "All activity history was deleted."; }
        catch (Exception ex) { Dialogs.Error("Delete failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ApplyRetentionNowAsync()
    {
        if (Draft.RetentionDays <= 0) { StatusMessage = "Choose a retention period first (it is applied automatically, hourly)."; return; }
        var n = await AppServices.Instance.ApplyRetentionAsync();
        StatusMessage = $"Removed {n} session(s) older than {Draft.RetentionDays} days.";
    }

    [RelayCommand]
    private async Task ChangeDatabaseLocationAsync()
    {
        var folder = Dialogs.PickFolder("Choose the new database folder");
        if (folder == null) return;
        try
        {
            var svc = AppServices.Instance;
            var target = Path.Combine(folder, "timetrace.db");
            if (File.Exists(target) && !Dialogs.Confirm("Database exists", "A TimeTrace database already exists there. Use it instead of copying the current one?")) return;
            if (!File.Exists(target)) await Task.Run(() => svc.Db.BackupTo(target));
            svc.Paths.SetDatabasePath(target);
            Dialogs.Info("Restart required", "The database location was changed. Please restart TimeTrace to start using it.\nThe previous database file was not deleted.");
            StatusMessage = "New location will be used after restart: " + target;
        }
        catch (Exception ex) { Dialogs.Error("Could not change location", ex.Message); }
    }

    [RelayCommand] private void OpenDataFolder() => Dialogs.OpenInExplorer(AppServices.Instance.Paths.DataDirectory);
    [RelayCommand] private void OpenLogFolder() => Dialogs.OpenInExplorer(AppServices.Instance.Paths.LogDirectory);

    [RelayCommand]
    private void ExportSettings()
    {
        var path = Dialogs.SaveFile("Export settings", "timetrace-settings.json", "JSON (*.json)|*.json");
        if (path == null) return;
        File.WriteAllText(path, Draft.ToJson());
        StatusMessage = "Settings exported.";
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var path = Dialogs.OpenFile("Import settings", "JSON (*.json)|*.json");
        if (path == null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1_000_000) throw new InvalidDataException("File is too large to be a TimeTrace settings file.");
            var imported = AppSettings.FromJson(File.ReadAllText(path));      // validated and clamped
            imported.FirstRunCompleted = true;
            await AppServices.Instance.Settings.SaveAsync(imported);
            AppServices.Instance.Theme.Apply(imported.Theme, imported.AccentColor);
            Load();
            StatusMessage = "Settings imported.";
        }
        catch (Exception ex) { Dialogs.Error("Import failed", "The file is not a valid settings file: " + ex.Message); }
    }
}
