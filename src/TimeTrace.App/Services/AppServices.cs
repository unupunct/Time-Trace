using System.Diagnostics;
using Serilog;
using TimeTrace.Core;
using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;
using TimeTrace.Core.Settings;
using TimeTrace.Core.Tracking;
using TimeTrace.Data;
using TimeTrace.Windows;

namespace TimeTrace.App.Services;

/// <summary>Composition root: owns every long-lived service and coordinates tracking control.</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    public AppPaths Paths { get; }
    public SqliteDatabase Db { get; }
    public SqliteRepository Repo { get; }
    public SettingsService Settings { get; }
    public TrackingEngine Engine { get; }
    public DataService Data { get; }
    public IconCache Icons { get; }
    public StartupManager Startup { get; } = new();
    public ThemeService Theme { get; } = new();
    public SystemStateMonitor? SystemState { get; private set; }
    public TrayService? Tray { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop, _maintenance;
    private TrackingState _lastNotifiedState = TrackingState.Stopped;
    private DateOnly _lastSummaryDay;

    public event Action<string>? ErrorRaised;

    private AppServices(AppPaths paths)
    {
        Paths = paths;
        Icons = new IconCache(paths.IconCacheDirectory);
        Db = new SqliteDatabase(paths.DatabasePath);
        Db.Migrate();
        Repo = new SqliteRepository(Db);
        Settings = new SettingsService(Repo);
        Engine = new TrackingEngine(new WindowsForegroundProvider(), new WindowsIdleProvider(), Repo, new RuleClassifier(Repo),
            new SystemClock(), () => TrackingOptions.From(Settings.Current), Log.ForContext<TrackingEngine>());
        Data = new DataService(Repo, Engine, () => Settings.Current.IncludeIdleInReports);
    }

    public static async Task<AppServices> CreateAsync(AppPaths paths)
    {
        var s = new AppServices(paths);
        Instance = s;
        await s.Settings.LoadAsync();
        s.Engine.ErrorOccurred += msg => s.RaiseError(msg);
        s.Engine.SnapshotChanged += s.OnSnapshot;
        return s;
    }

    public void RaiseError(string message)
    {
        Log.Warning("Error surfaced: {Message}", message);
        ErrorRaised?.Invoke(message);
        if (Settings.Current.NotifyErrors) Tray?.ShowBalloon("TimeTrace error", message, isError: true);
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task StartAsync(bool beginTracking)
    {
        SystemState = new SystemStateMonitor();
        SystemState.Suspending += () => _ = Engine.OnSystemSuspendAsync();
        SystemState.Resumed += () => _ = Engine.OnSystemResumeAsync();
        SystemState.Locked += () => _ = Engine.OnSessionLockedAsync();
        SystemState.Unlocked += () => _ = Engine.OnSessionUnlockedAsync();
        SystemState.SessionEnding += () => Engine.OnSessionEndingAsync().GetAwaiter().GetResult();

        _loop = Task.Run(() => Engine.RunAsync(_cts.Token));
        _maintenance = Task.Run(() => MaintenanceAsync(_cts.Token));
        if (beginTracking && Settings.Current.TrackingEnabled) await Engine.StartAsync();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await Engine.StopAsync(TrackingEventTypes.Shutdown);
            _cts.Cancel();
            SystemState?.Dispose();
            if (_loop != null) await Task.WhenAny(_loop, Task.Delay(1500));
        }
        catch (Exception ex) { Log.Error(ex, "Shutdown failure"); }
    }

    // ---------------------------------------------------------------- tracking control (UI + tray)

    public async Task StartTrackingAsync()
    {
        if (!Settings.Current.TrackingEnabled)
        {
            Settings.Current.TrackingEnabled = true;
            await Settings.SaveAsync(Settings.Current);
        }
        if (Engine.State == TrackingState.Paused) await Engine.ResumeAsync(); else await Engine.StartAsync();
    }

    public Task PauseTrackingAsync() => Engine.PauseAsync();
    public Task ResumeTrackingAsync() => Engine.ResumeAsync();
    public Task StopTrackingAsync() => Engine.StopAsync();

    public async Task SetTrackingEnabledAsync(bool enabled)
    {
        Settings.Current.TrackingEnabled = enabled;
        await Settings.SaveAsync(Settings.Current);
        if (!enabled) await Engine.StopAsync();
    }

    public void InvalidateAppCache() => Engine.InvalidateAppCache();

    // ---------------------------------------------------------------- notifications

    private void OnSnapshot(TrackingSnapshot snap)
    {
        if (snap.State == _lastNotifiedState) return;
        var prev = _lastNotifiedState;
        _lastNotifiedState = snap.State;
        var s = Settings.Current;
        if (snap.State == TrackingState.Running && s.NotifyTrackingStarted) Tray?.ShowBalloon("Tracking started", prev == TrackingState.Paused ? "Tracking resumed." : "TimeTrace is now tracking application usage.");
        if (snap.State == TrackingState.Paused && s.NotifyTrackingPaused) Tray?.ShowBalloon("Tracking paused", "TimeTrace is not recording while paused.");
    }

    // ---------------------------------------------------------------- background maintenance

    private async Task MaintenanceAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FillIconsAsync();
                if (tick % 360 == 0) await ApplyRetentionAsync();            // roughly hourly (10 s ticks)
                await DailySummaryAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Maintenance task failed"); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task FillIconsAsync()
    {
        foreach (var app in await Repo.GetApplicationsAsync())
        {
            if (app.IconCachePath != null && File.Exists(app.IconCachePath)) continue;
            var icon = Icons.GetOrCreate(app.ExecutablePath);
            if (icon == null) continue;
            app.IconCachePath = icon;
            await Repo.UpdateApplicationAsync(app);
        }
    }

    public async Task<int> ApplyRetentionAsync()
    {
        var days = Settings.Current.RetentionDays;
        if (days <= 0) return 0;
        var n = await Repo.DeleteSessionsOlderThanAsync(DateTime.UtcNow.AddDays(-days));
        if (n > 0) Log.Information("Retention removed {Count} sessions older than {Days} days", n, days);
        return n;
    }

    private async Task DailySummaryAsync()
    {
        if (!Settings.Current.NotifyDailySummary) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (now.Hour < 18 || _lastSummaryDay == today) return;
        _lastSummaryDay = today;
        var s = await Data.SummarizeAsync(DataService.RangeFor(RangePreset.Today));
        var top = s.MostUsedApp?.Name ?? "no applications";
        Tray?.ShowBalloon("Today with TimeTrace", $"Active time {Fmt.Duration(s.ActiveSeconds)} · most used: {top}");
    }

    // ---------------------------------------------------------------- database operations (stop tracking while the file is replaced)

    public async Task RestoreDatabaseAsync(string backupFile)
    {
        var wasRunning = Engine.State == TrackingState.Running;
        await Engine.StopAsync();
        Db.RestoreFrom(backupFile);
        await Settings.LoadAsync();
        InvalidateAppCache();
        if (wasRunning && Settings.Current.TrackingEnabled) await Engine.StartAsync();
    }

    /// <summary>Stops tracking while history is wiped (live sessions must not reference deleted rows), then resumes if it was running.</summary>
    public async Task DeleteAllActivityAsync()
    {
        var wasRunning = Engine.State == TrackingState.Running;
        await Engine.StopAsync();
        await Repo.DeleteAllActivityAsync();
        InvalidateAppCache();
        if (wasRunning && Settings.Current.TrackingEnabled) await Engine.StartAsync();
    }

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
}
