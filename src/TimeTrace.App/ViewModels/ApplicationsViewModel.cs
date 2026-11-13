using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Controls;
using TimeTrace.App.Services;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;

namespace TimeTrace.App.ViewModels;

public sealed partial class AppRowVm : ObservableObject
{
    private readonly ApplicationsViewModel _owner;
    private bool _loading = true;
    public AppRecord Record { get; }
    public int Id => Record.Id;
    public string Name => Record.Name;
    public string Executable => Record.ExecutableName;
    public string? Path => Record.ExecutablePath;
    public string? IconPath => Record.IconCachePath;
    public double TotalSeconds { get; init; }
    public int SessionCount { get; init; }
    public double AverageSeconds { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string TotalText => TotalSeconds <= 0 ? "–" : Fmt.Duration(TotalSeconds);
    public string AverageText => SessionCount == 0 ? "–" : Fmt.Duration(AverageSeconds);
    public string LastUsedText => LastUsedUtc is { } d ? Fmt.DateTimeShort(d) : "–";
    public IReadOnlyList<Category> Categories { get; }

    [ObservableProperty] private Category? _category;
    [ObservableProperty] private bool _isExcluded;

    public AppRowVm(ApplicationsViewModel owner, AppRecord rec, IReadOnlyList<Category> cats, Category? current)
    {
        _owner = owner; Record = rec; Categories = cats; _category = current; _isExcluded = rec.IsExcluded;
        _loading = false;
    }

    partial void OnCategoryChanged(Category? value) { if (!_loading && value != null) _ = _owner.SaveRowAsync(this); }
    partial void OnIsExcludedChanged(bool value) { if (!_loading) _ = _owner.SaveRowAsync(this); }
}

public sealed partial class ApplicationsViewModel : PageViewModel
{
    public override string Title => "Applications";

    public IReadOnlyList<PresetOption> Presets => PresetOption.All;
    [ObservableProperty] private PresetOption _selectedPreset = PresetOption.All[3];
    [ObservableProperty] private DateTime _customFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime _customTo = DateTime.Today;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private IReadOnlyList<CategoryFilter> _categoryFilters = new[] { new CategoryFilter(null, "All categories") };
    [ObservableProperty] private CategoryFilter _selectedCategoryFilter = new(null, "All categories");
    [ObservableProperty] private bool _showExcludedOnly;
    [ObservableProperty] private string _countText = "";
    public bool IsCustom => SelectedPreset.Preset == RangePreset.Custom;

    public ObservableCollection<AppRowVm> Rows { get; } = new();
    private List<AppRowVm> _all = new();
    private bool _suppress;

    // ---- detail
    [ObservableProperty] private AppRowVm? _detail;
    [ObservableProperty] private string _todayText = "", _weekText = "", _monthText = "", _rangeTotalText = "", _avgText = "", _longestText = "", _firstText = "", _lastText = "", _pathText = "";
    [ObservableProperty] private IReadOnlyList<BarItem>? _detailBars;
    public ObservableCollection<ActivityItem> History { get; } = new();
    public bool IsDetailOpen => Detail != null;

    partial void OnDetailChanged(AppRowVm? value) => OnPropertyChanged(nameof(IsDetailOpen));
    partial void OnSelectedPresetChanged(PresetOption value) { OnPropertyChanged(nameof(IsCustom)); _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) { if (IsCustom) _ = RefreshAsync(); }
    partial void OnCustomToChanged(DateTime value) { if (IsCustom) _ = RefreshAsync(); }
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryFilterChanged(CategoryFilter value) { if (!_suppress) ApplyFilter(); }
    partial void OnShowExcludedOnlyChanged(bool value) => ApplyFilter();

    private DateRange Range => DataService.RangeFor(SelectedPreset.Preset, DateOnly.FromDateTime(CustomFrom), DateOnly.FromDateTime(CustomTo));

    public async Task OpenAppAsync(int appId)
    {
        await RefreshAsync();
        var row = _all.FirstOrDefault(r => r.Id == appId);
        if (row != null) { Detail = row; await LoadDetailAsync(); }
    }

    public override async Task RefreshAsync()
    {
        try
        {
            var svc = AppServices.Instance;
            var lk = await svc.Data.GetLookupsAsync();
            var sessions = await svc.Data.GetSessionsAsync(Range);
            var summary = await svc.Data.SummarizeAsync(Range, lk, sessions);
            var usage = summary.Apps.ToDictionary(a => a.ApplicationId);

            _suppress = true;
            var filters = new List<CategoryFilter> { new(null, "All categories") };
            filters.AddRange(lk.Categories.Select(c => new CategoryFilter(c.Id, c.Name)));
            CategoryFilters = filters;
            SelectedCategoryFilter = filters.FirstOrDefault(f => f.Id == SelectedCategoryFilter.Id) ?? filters[0];
            _suppress = false;

            _all = lk.Apps.Select(a =>
            {
                usage.TryGetValue(a.Id, out var u);
                var cat = a.CategoryId is int cid && lk.CategoryById.TryGetValue(cid, out var c) ? c : lk.Fallback;
                return new AppRowVm(this, a, lk.Categories, cat)
                {
                    TotalSeconds = u?.Seconds ?? 0, SessionCount = u?.SessionCount ?? 0, AverageSeconds = u?.AverageSessionSeconds ?? 0,
                    LastUsedUtc = u?.LastUtc ?? (a.LastSeen > DateTime.MinValue ? a.LastSeen : null),
                };
            }).OrderByDescending(r => r.TotalSeconds).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilter();
            if (Detail != null) { Detail = _all.FirstOrDefault(r => r.Id == Detail.Id); if (Detail != null) await LoadDetailAsync(); }
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Applications refresh failed"); }
    }

    private void ApplyFilter()
    {
        var q = Search?.Trim() ?? "";
        var cat = SelectedCategoryFilter?.Id;
        var rows = _all.Where(r =>
            (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Executable.Contains(q, StringComparison.OrdinalIgnoreCase)) &&
            (cat == null || r.Category?.Id == cat) && (!ShowExcludedOnly || r.IsExcluded)).ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        CountText = $"{rows.Count} of {_all.Count} applications";
    }

    public async Task SaveRowAsync(AppRowVm row)
    {
        try
        {
            var svc = AppServices.Instance;
            var rec = await svc.Repo.GetApplicationAsync(row.Id);
            if (rec == null) return;
            rec.CategoryId = row.Category?.Id; rec.IsExcluded = row.IsExcluded;
            await svc.Repo.UpdateApplicationAsync(rec);
            svc.InvalidateAppCache();
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Saving application failed"); Dialogs.Error("TimeTrace", "Could not save the change: " + ex.Message); }
    }

    // ---------------------------------------------------------------- detail
    [RelayCommand] private async Task OpenDetailAsync(AppRowVm? row) { if (row == null) return; Detail = row; await LoadDetailAsync(); }
    [RelayCommand] private void CloseDetail() { Detail = null; }

    private async Task LoadDetailAsync()
    {
        if (Detail == null) return;
        var svc = AppServices.Instance; var tz = Fmt.Tz;
        var id = Detail.Id;
        var lk = await svc.Data.GetLookupsAsync();
        var today = Periods.LocalToday(DateTime.UtcNow, tz);

        async Task<double> Total(DateOnly a, DateOnly b)
        {
            var r = Periods.Days(a, b, tz);
            var s = await svc.Data.GetSessionsAsync(r, id);
            return AnalyticsEngine.Summarize(s, lk.Apps.ToList(), lk.Categories.ToList(), r, tz, false, DateTime.UtcNow).ActiveSeconds;
        }
        TodayText = Fmt.Duration(await Total(today, today));
        WeekText = Fmt.Duration(await Total(Periods.StartOfWeek(today), today));
        MonthText = Fmt.Duration(await Total(Periods.StartOfMonth(today), today));

        var range = Range;
        var sessions = (await svc.Data.GetSessionsAsync(range, id)).Where(s => s.SessionType == SessionType.Active).ToList();
        var sum = AnalyticsEngine.Summarize(sessions, lk.Apps.ToList(), lk.Categories.ToList(), range, tz, false, DateTime.UtcNow);
        RangeTotalText = Fmt.Duration(sum.ActiveSeconds);
        AvgText = sum.ActiveSessionCount == 0 ? "–" : Fmt.Duration(sum.AverageSessionSeconds);
        LongestText = sum.LongestSessions.Count == 0 ? "–" : Fmt.Duration(sum.LongestSessions[0].Seconds);
        var u = sum.Apps.FirstOrDefault();
        FirstText = u?.FirstUtc is { } f ? Fmt.DateTimeShort(f) : "–";
        LastText = u?.LastUtc is { } l ? Fmt.DateTimeShort(l) : "–";
        PathText = Detail.Path ?? "(path not accessible – the process may be elevated)";
        DetailBars = sum.Daily.Count <= 1
            ? Enumerable.Range(0, 24).Select(h => new BarItem(Fmt.Hour(h), sum.ByHour[h], 0, $"{h:00}:00 – {Fmt.Duration(sum.ByHour[h])}")).ToList()
            : sum.Daily.Select(d => new BarItem(sum.Daily.Count > 10 ? d.Date.ToString("d/M") : Fmt.DayShort(d.Date), d.ActiveSeconds, 0, $"{Fmt.Date(d.Date)} – {Fmt.Duration(d.ActiveSeconds)}")).ToList();
        History.Clear();
        foreach (var s in sessions.OrderByDescending(s => s.StartTimeUtc).Take(500)) History.Add(ActivityItem.From(s, lk));
    }

    [RelayCommand]
    private async Task DeleteHistoryItemAsync(ActivityItem? item)
    {
        if (item == null) return;
        if (!Dialogs.Confirm("Delete session", $"Delete the {item.DurationText} session starting {item.DateText} {item.StartText}?")) return;
        await AppServices.Instance.Repo.DeleteSessionsAsync(new[] { item.Id });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAllForAppAsync()
    {
        if (Detail == null) return;
        if (!Dialogs.Confirm("Delete application history", $"Delete ALL recorded sessions and the entry for {Detail.Name}?\nThis cannot be undone.")) return;
        await AppServices.Instance.Repo.DeleteApplicationAsync(Detail.Id);
        AppServices.Instance.InvalidateAppCache();
        Detail = null;
        await RefreshAsync();
    }
}
