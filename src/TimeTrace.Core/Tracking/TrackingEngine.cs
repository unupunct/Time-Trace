using Serilog;
using TimeTrace.Core.Abstractions;
using TimeTrace.Core.Models;

namespace TimeTrace.Core.Tracking;

public enum TrackingState { Stopped, Running, Paused }

public sealed record TrackingSnapshot(
    TrackingState State,
    string StatusText,
    string? AppName,
    string? WindowTitle,
    bool IsIdle,
    DateTime? SessionStartUtc);

/// <summary>
/// Core tracking engine. It is driven by <see cref="TickAsync"/> (called by <see cref="RunAsync"/> or directly by tests),
/// keeps the active session in memory, persists a checkpoint periodically (never one row per poll),
/// and uses a monotonic clock for durations so wall-clock changes cannot corrupt them.
/// </summary>
public sealed class TrackingEngine
{
    private const double MinSessionSeconds = 1.0;
    private const int NullWindowGraceTicks = 2;

    private sealed class Live
    {
        public required string Key { get; init; }
        public AppRecord? App { get; init; }              // null for idle
        public int Pid { get; init; }
        public SessionType Type { get; init; }
        public DateTime StartUtc { get; init; }
        public long StartMono { get; init; }
        public long LastCheckpointMono { get; set; }
        public long DbId { get; set; }
        public Dictionary<string, long> TitleMs { get; } = new(StringComparer.Ordinal);
        public string? CurrentTitle { get; set; }
    }

    private readonly IForegroundWindowProvider _windows;
    private readonly IIdleTimeProvider _idle;
    private readonly IActivityRepository _repo;
    private readonly IAppClassifier _classifier;
    private readonly IClock _clock;
    private readonly Func<TrackingOptions> _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AppRecord?> _appCache = new(StringComparer.OrdinalIgnoreCase);

    private Live? _active;
    private Live? _idleSession;
    private long? _lastTickMono;
    private long _runStartMono;
    private int _nullTicks;
    private long _firstNullMono;
    private bool _suspended, _locked, _recovered;

    public TrackingState State { get; private set; } = TrackingState.Stopped;
    public TrackingSnapshot Snapshot { get; private set; } = new(TrackingState.Stopped, "Tracking stopped", null, null, false, null);

    public event Action<TrackingSnapshot>? SnapshotChanged;
    public event Action? SessionPersisted;
    public event Action<string>? ErrorOccurred;
    public event Action<int>? SessionsRecovered;

    public TrackingEngine(IForegroundWindowProvider windows, IIdleTimeProvider idle, IActivityRepository repo,
        IAppClassifier classifier, IClock clock, Func<TrackingOptions> options, ILogger? log = null)
    {
        _windows = windows; _idle = idle; _repo = repo; _classifier = classifier; _clock = clock; _options = options;
        _log = log ?? Log.ForContext<TrackingEngine>();
    }

    // ------------------------------------------------------------ lifecycle

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == TrackingState.Running) return;
            if (!_recovered)
            {
                _recovered = true;
                var n = await SafeAsync(() => _repo.RecoverOpenSessionsAsync(), 0);
                if (n > 0)
                {
                    await EventAsync(TrackingEventTypes.Recovered, $"{n} interrupted session(s) recovered");
                    SessionsRecovered?.Invoke(n);
                }
            }
            BeginObservation();
            State = TrackingState.Running;
            await EventAsync(TrackingEventTypes.Started, null);
            Publish("Tracking");
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != TrackingState.Running) return;
            await CloseAllAsync(_clock.MonotonicMilliseconds);
            State = TrackingState.Paused;
            await EventAsync(TrackingEventTypes.Paused, null);
            Publish("Tracking paused");
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != TrackingState.Paused) return;
            BeginObservation();
            State = TrackingState.Running;
            await EventAsync(TrackingEventTypes.Resumed, null);
            Publish("Tracking");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(string eventType = TrackingEventTypes.Stopped)
    {
        await _gate.WaitAsync();
        try
        {
            if (State == TrackingState.Stopped) return;
            await CloseAllAsync(_clock.MonotonicMilliseconds);
            State = TrackingState.Stopped;
            await EventAsync(eventType, null);
            Publish("Tracking stopped");
        }
        finally { _gate.Release(); }
    }

    public void InvalidateAppCache() { lock (_appCache) _appCache.Clear(); }

    // ------------------------------------------------------------ system events

    public async Task OnSystemSuspendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _suspended = true;
            if (State == TrackingState.Running)
            {
                await CloseAllAsync(_clock.MonotonicMilliseconds);
                await EventAsync(TrackingEventTypes.Suspend, null);
                Publish("System sleeping");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OnSystemResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _suspended = false;
            BeginObservation();
            if (State == TrackingState.Running)
            {
                await EventAsync(TrackingEventTypes.Resume, null);
                Publish("Tracking");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OnSessionLockedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_options().PauseOnLock) return;
            _locked = true;
            if (State == TrackingState.Running)
            {
                await CloseAllAsync(_clock.MonotonicMilliseconds);
                await EventAsync(TrackingEventTypes.Locked, null);
                Publish("Windows locked");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OnSessionUnlockedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_locked) return;
            _locked = false;
            BeginObservation();
            if (State == TrackingState.Running)
            {
                await EventAsync(TrackingEventTypes.Unlocked, null);
                Publish("Tracking");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>User logoff / shutdown: persist everything immediately.</summary>
    public Task OnSessionEndingAsync() => StopAsync(TrackingEventTypes.Shutdown);

    // ------------------------------------------------------------ loop

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TickAsync();
            try { await Task.Delay(_options().PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != TrackingState.Running || _suspended || _locked) return;
            var opt = _options();
            var nowMono = _clock.MonotonicMilliseconds;

            // The process was not scheduled for a long time (e.g. sleep without notification): end sessions at the last observation.
            var gapMs = Math.Max(10_000, (long)(opt.PollInterval.TotalMilliseconds * 5));
            if (_lastTickMono is long last && nowMono - last > gapMs)
            {
                await CloseAllAsync(last);
                _runStartMono = nowMono;
                _nullTicks = 0;
            }
            _lastTickMono = nowMono;

            var idle = _idle.GetIdleTime();
            if (idle >= opt.IdleThreshold) await HandleIdleAsync(idle, nowMono);
            else await HandleActiveAsync(idle, nowMono, opt);

            await CheckpointIfDueAsync(nowMono, opt);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Tracking tick failed");
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------ idle / active handling

    private async Task HandleIdleAsync(TimeSpan idle, long nowMono)
    {
        if (_idleSession == null)
        {
            var idleStartMono = Math.Max(nowMono - (long)idle.TotalMilliseconds, _runStartMono);
            DateTime startUtc;
            if (_active != null)
            {
                idleStartMono = Math.Max(idleStartMono, _active.StartMono);
                startUtc = UtcAt(_active, idleStartMono);
                await CloseLiveAsync(_active, idleStartMono);
                _active = null;
            }
            else startUtc = _clock.UtcNow - TimeSpan.FromMilliseconds(nowMono - idleStartMono);

            _idleSession = new Live { Key = "<idle>", Type = SessionType.Idle, StartUtc = startUtc, StartMono = idleStartMono, LastCheckpointMono = nowMono };
            await InsertLiveAsync(_idleSession, nowMono);
            await EventAsync(TrackingEventTypes.IdleStart, null);
        }
        _nullTicks = 0;
        Publish("Idle");
    }

    private async Task HandleActiveAsync(TimeSpan idle, long nowMono, TrackingOptions opt)
    {
        long? activeStartOverride = null;
        if (_idleSession != null)
        {
            // Input resumed `idle` ago: the idle period ends then.
            var endMono = Math.Max(nowMono - (long)idle.TotalMilliseconds, _idleSession.StartMono);
            await CloseLiveAsync(_idleSession, endMono);
            _idleSession = null;
            activeStartOverride = endMono;
            await EventAsync(TrackingEventTypes.IdleEnd, null);
        }

        var fg = _windows.GetForegroundWindow();
        if (fg == null || fg.IsMinimized)
        {
            if (_nullTicks == 0) _firstNullMono = nowMono;
            _nullTicks++;
            if (_nullTicks >= NullWindowGraceTicks && _active != null)
            {
                await CloseLiveAsync(_active, Math.Max(_firstNullMono, _active.StartMono));
                _active = null;
            }
            if (_active == null) Publish("No active window");
            return;
        }
        _nullTicks = 0;

        var key = fg.ExecutableName.ToLowerInvariant();
        var cached = await GetCachedAppAsync(key);
        bool excluded = cached?.IsExcluded == true || ExclusionMatcher.IsExcluded(opt, fg.ExecutableName, fg.ExecutablePath, fg.WindowTitle);
        if (excluded)
        {
            if (_active != null) { await CloseLiveAsync(_active, nowMono); _active = null; }
            Publish("Excluded window (not recorded)");
            return;
        }

        var title = opt.TrackWindowTitles ? fg.WindowTitle : null;

        if (_active != null && _active.Key == key)
        {
            AccumulateTitle(_active, title, (long)opt.PollInterval.TotalMilliseconds);
            Publish("Tracking");
            return;
        }

        if (_active != null) { await CloseLiveAsync(_active, activeStartOverride ?? nowMono); _active = null; }

        var app = cached ?? await CreateAppAsync(fg);
        var startMono = activeStartOverride ?? nowMono;
        var live = new Live
        {
            Key = key, App = app, Pid = fg.ProcessId, Type = SessionType.Active,
            StartUtc = _clock.UtcNow - TimeSpan.FromMilliseconds(nowMono - startMono),
            StartMono = startMono, LastCheckpointMono = nowMono,
        };
        live.CurrentTitle = title;
        if (!string.IsNullOrEmpty(title)) live.TitleMs[title] = 0;
        _active = live;
        await InsertLiveAsync(live, nowMono);
        await RefreshAppMetadataAsync(app, fg);
        Publish("Tracking");
    }

    private static void AccumulateTitle(Live s, string? title, long weightMs)
    {
        // Each observation of a title adds the poll interval to its weight; the heaviest title represents the session.
        if (string.IsNullOrEmpty(title)) return;
        s.TitleMs.TryGetValue(title, out var v);
        s.TitleMs[title] = v + weightMs;
        if (s.TitleMs.Count > 25)
        {
            var drop = s.TitleMs.OrderBy(kv => kv.Value).First().Key;
            if (drop != title) s.TitleMs.Remove(drop);
        }
        s.CurrentTitle = title;
    }

    // ------------------------------------------------------------ persistence helpers

    private async Task<AppRecord?> GetCachedAppAsync(string key)
    {
        lock (_appCache) { if (_appCache.TryGetValue(key, out var hit)) return hit; }
        var app = await SafeAsync(() => _repo.GetApplicationByExecutableAsync(key), null);
        lock (_appCache) _appCache[key] = app;
        return app;
    }

    private async Task<AppRecord> CreateAppAsync(ForegroundWindowInfo fg)
    {
        var now = _clock.UtcNow;
        int? cat = await SafeAsync(async () => await _classifier.ClassifyAsync(fg.ExecutableName, fg.ExecutablePath), (int?)null);
        var rec = new AppRecord
        {
            Name = string.IsNullOrWhiteSpace(fg.AppName) ? fg.ExecutableName : fg.AppName,
            ExecutableName = fg.ExecutableName, ExecutablePath = fg.ExecutablePath,
            CategoryId = cat, FirstSeen = now, LastSeen = now,
        };
        var saved = await SafeAsync(() => _repo.AddApplicationAsync(rec), rec);
        lock (_appCache) _appCache[fg.ExecutableName.ToLowerInvariant()] = saved;
        return saved;
    }

    private async Task RefreshAppMetadataAsync(AppRecord app, ForegroundWindowInfo fg)
    {
        bool dirty = false;
        if (fg.ExecutablePath != null && !string.Equals(app.ExecutablePath, fg.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        { app.ExecutablePath = fg.ExecutablePath; dirty = true; }
        app.LastSeen = _clock.UtcNow;
        if (dirty) await SafeAsync(async () => { await _repo.UpdateApplicationAsync(app); return 0; }, 0);
    }

    private ActivitySession ToRow(Live s, long endMono, bool open)
    {
        var dur = Math.Max(0, endMono - s.StartMono) / 1000.0;
        return new ActivitySession
        {
            Id = s.DbId, ApplicationId = s.App?.Id, ProcessId = s.Pid,
            WindowTitle = BestTitle(s), StartTimeUtc = s.StartUtc,
            EndTimeUtc = s.StartUtc + TimeSpan.FromSeconds(dur), DurationSeconds = dur,
            SessionType = s.Type, IsOpen = open,
        };
    }

    private static string? BestTitle(Live s) =>
        s.TitleMs.Count == 0 ? null : s.TitleMs.OrderByDescending(kv => kv.Value).First().Key;

    private async Task InsertLiveAsync(Live s, long nowMono)
    {
        var row = ToRow(s, nowMono, open: true);
        s.DbId = await SafeAsync(() => _repo.InsertSessionAsync(row), 0L);
        s.LastCheckpointMono = nowMono;
    }

    private async Task CheckpointIfDueAsync(long nowMono, TrackingOptions opt)
    {
        foreach (var s in new[] { _active, _idleSession })
        {
            if (s == null || nowMono - s.LastCheckpointMono < (long)opt.CheckpointInterval.TotalMilliseconds) continue;
            if (s.DbId == 0) await InsertLiveAsync(s, nowMono);
            else
            {
                var row = ToRow(s, nowMono, open: true);
                await SafeAsync(async () => { await _repo.UpdateSessionAsync(row); return 0; }, 0);
                s.LastCheckpointMono = nowMono;
            }
            SessionPersisted?.Invoke();
        }
    }

    private async Task CloseLiveAsync(Live s, long endMono)
    {
        endMono = Math.Max(endMono, s.StartMono);
        var row = ToRow(s, endMono, open: false);
        if (row.DurationSeconds < MinSessionSeconds)
        {
            if (s.DbId != 0)
            {
                var id = s.DbId;
                await SafeAsync(async () => { await _repo.DeleteSessionsAsync(new[] { id }); return 0; }, 0);
            }
        }
        else if (s.DbId != 0)
        {
            await SafeAsync(async () => { await _repo.UpdateSessionAsync(row); return 0; }, 0);
        }
        else
        {
            await SafeAsync(() => _repo.InsertSessionAsync(row), 0L);
        }
        if (s.App != null)
        {
            s.App.LastSeen = row.EndTimeUtc;
            var app = s.App;
            await SafeAsync(async () => { await _repo.UpdateApplicationAsync(app); return 0; }, 0);
        }
        SessionPersisted?.Invoke();
    }

    private async Task CloseAllAsync(long endMono)
    {
        if (_active != null) { var a = _active; _active = null; await CloseLiveAsync(a, endMono); }
        if (_idleSession != null) { var i = _idleSession; _idleSession = null; await CloseLiveAsync(i, endMono); }
        _nullTicks = 0;
    }

    private void BeginObservation()
    {
        _runStartMono = _clock.MonotonicMilliseconds;
        _lastTickMono = null;
        _nullTicks = 0;
    }

    private static DateTime UtcAt(Live s, long mono) => s.StartUtc + TimeSpan.FromMilliseconds(mono - s.StartMono);

    private Task EventAsync(string type, string? details) =>
        SafeAsync(async () => { await _repo.AddEventAsync(type, _clock.UtcNow, details); return 0; }, 0);

    private async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try { return await action(); }
        catch (Exception ex)
        {
            _log.Error(ex, "Tracking persistence failure");
            ErrorOccurred?.Invoke("Database error: " + ex.Message);
            return fallback;
        }
    }

    private void Publish(string status)
    {
        var a = _active;
        var snap = new TrackingSnapshot(State, status, a?.App?.Name, a?.CurrentTitle, _idleSession != null, a?.StartUtc);
        Snapshot = snap;
        SnapshotChanged?.Invoke(snap);
    }
}
