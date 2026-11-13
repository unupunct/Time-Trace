using CommunityToolkit.Mvvm.ComponentModel;
using TimeTrace.App.Services;
using TimeTrace.Core.Models;

namespace TimeTrace.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    /// <summary>When true the shell re-runs <see cref="RefreshAsync"/> on the dashboard refresh interval while the page is visible.</summary>
    public virtual bool AutoRefresh => false;
    public virtual Task ActivateAsync() => RefreshAsync();
    public virtual Task RefreshAsync() => Task.CompletedTask;

    private bool _busy, _again;
    /// <summary>Runs a load exclusively; a request arriving meanwhile is queued (never dropped) so the latest selection always wins.</summary>
    protected async Task RunExclusiveAsync(Func<Task> work)
    {
        if (_busy) { _again = true; return; }
        _busy = true;
        try { do { _again = false; await work(); } while (_again); }
        finally { _busy = false; }
    }
}

public sealed record PresetOption(RangePreset Preset, string Label)
{
    public static readonly IReadOnlyList<PresetOption> All = new[]
    {
        new PresetOption(RangePreset.Today, "Today"), new PresetOption(RangePreset.Yesterday, "Yesterday"),
        new PresetOption(RangePreset.Last7Days, "Last 7 days"), new PresetOption(RangePreset.Last30Days, "Last 30 days"),
        new PresetOption(RangePreset.Custom, "Custom range…"),
    };
}

public sealed record CategoryFilter(int? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A session prepared for display (list rows, details, recent-activity timeline).</summary>
public sealed class ActivityItem
{
    public long Id { get; init; }
    public int? AppId { get; init; }
    public string AppName { get; init; } = "";
    public string Executable { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? IconPath { get; init; }
    public string Title { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public int? CategoryId { get; init; }
    public string ColorHex { get; init; } = "#6B7280";
    public bool IsIdle { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
    public double DurationSeconds { get; init; }
    public bool IsOpen { get; init; }

    public string StartText => Fmt.TimeOfDay(StartUtc);
    public string TimeRange => $"{Fmt.TimeOfDay(StartUtc)} – {Fmt.TimeOfDay(EndUtc)}";
    public string DateText => Fmt.Date(StartUtc);
    public string DurationText => Fmt.Duration(DurationSeconds);
    public string DisplayName => IsIdle ? "Idle" : AppName;
    public string DisplayTitle => IsIdle ? "No keyboard or mouse input" : Title;

    public static ActivityItem From(ActivitySession s, Lookups lk)
    {
        AppRecord? app = null;
        if (s.ApplicationId is int id) lk.AppById.TryGetValue(id, out app);
        Category? cat = null;
        if (app?.CategoryId is int cid) lk.CategoryById.TryGetValue(cid, out cat);
        cat ??= lk.Fallback;
        return new ActivityItem
        {
            Id = s.Id, AppId = s.ApplicationId, AppName = app?.Name ?? (s.SessionType == SessionType.Idle ? "Idle" : "(deleted application)"),
            Executable = app?.ExecutableName ?? "", ExecutablePath = app?.ExecutablePath, IconPath = app?.IconCachePath,
            Title = s.WindowTitle ?? "", CategoryName = s.SessionType == SessionType.Idle ? "" : cat.Name, CategoryId = cat.Id, ColorHex = cat.Color,
            IsIdle = s.SessionType == SessionType.Idle, StartUtc = s.StartTimeUtc, EndUtc = s.EndTimeUtc, DurationSeconds = s.DurationSeconds,
            IsOpen = s.IsOpen,
        };
    }
}
