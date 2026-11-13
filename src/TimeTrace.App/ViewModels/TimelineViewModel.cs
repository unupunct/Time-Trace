using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Controls;
using TimeTrace.App.Services;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Models;

namespace TimeTrace.App.ViewModels;

public sealed partial class TimelineViewModel : PageViewModel
{
    public override string Title => "Timeline";
    public override bool AutoRefresh => true;

    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private bool _isWeekly;
    [ObservableProperty] private double _pixelsPerHour = 52;
    [ObservableProperty] private IReadOnlyList<TimelineRow>? _rows;
    [ObservableProperty] private string _dateLabel = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private ActivityItem? _selected;
    [ObservableProperty] private long _selectedId;
    [ObservableProperty] private IReadOnlyList<CategoryFilter> _appFilters = new[] { new CategoryFilter(null, "All applications") };
    [ObservableProperty] private IReadOnlyList<CategoryFilter> _categoryFilters = new[] { new CategoryFilter(null, "All categories") };
    [ObservableProperty] private CategoryFilter _selectedApp = new(null, "All applications");
    [ObservableProperty] private CategoryFilter _selectedCategory = new(null, "All categories");

    public ObservableCollection<ActivityItem> Activities { get; } = new();
    private bool _suppress;

    partial void OnSelectedDateChanged(DateTime value) => _ = RefreshAsync();
    partial void OnIsWeeklyChanged(bool value) => _ = RefreshAsync();
    partial void OnSelectedAppChanged(CategoryFilter value) { if (!_suppress) _ = RefreshAsync(); }
    partial void OnSelectedCategoryChanged(CategoryFilter value) { if (!_suppress) _ = RefreshAsync(); }
    partial void OnSelectedChanged(ActivityItem? value) => SelectedId = value?.Id ?? 0;

    public void GoToToday() { SelectedDate = DateTime.Today; }

    [RelayCommand] private void Previous() => SelectedDate = SelectedDate.AddDays(IsWeekly ? -7 : -1);
    [RelayCommand] private void Next() => SelectedDate = SelectedDate.AddDays(IsWeekly ? 7 : 1);
    [RelayCommand] private void Today() => SelectedDate = DateTime.Today;
    [RelayCommand] private void ZoomIn() => PixelsPerHour = Math.Min(240, PixelsPerHour * 1.35);
    [RelayCommand] private void ZoomOut() => PixelsPerHour = Math.Max(18, PixelsPerHour / 1.35);
    [RelayCommand] private void ResetZoom() => PixelsPerHour = 52;
    [RelayCommand] private void ClearSelection() => Selected = null;

    public void SelectSession(long id) => Selected = Activities.FirstOrDefault(a => a.Id == id);

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (Selected is not { } s) return;
        if (!Dialogs.Confirm("Delete session", $"Delete this {s.DurationText} session of {s.DisplayName}?\nThis cannot be undone.")) return;
        await AppServices.Instance.Repo.DeleteSessionsAsync(new[] { s.Id });
        Selected = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ExcludeAppAsync()
    {
        if (Selected is not { AppId: int id } s || s.IsIdle) return;
        if (!Dialogs.Confirm("Exclude application", $"Stop tracking {s.AppName}?\nExisting sessions are kept; delete them from the Applications page if you want them removed.")) return;
        var svc = AppServices.Instance;
        var app = await svc.Repo.GetApplicationAsync(id);
        if (app == null) return;
        app.IsExcluded = true;
        await svc.Repo.UpdateApplicationAsync(app);
        svc.InvalidateAppCache();
        Selected = null;
    }

    public override Task RefreshAsync() => RunExclusiveAsync(LoadAsync);

    private async Task LoadAsync()
    {
        try
        {
            var svc = AppServices.Instance;
            var tz = Fmt.Tz;
            var day = DateOnly.FromDateTime(SelectedDate);
            var first = IsWeekly ? Periods.StartOfWeek(day) : day;
            var last = IsWeekly ? first.AddDays(6) : day;
            var range = Periods.Days(first, last, tz);
            DateLabel = IsWeekly ? $"{Fmt.Date(first)} – {Fmt.Date(last)}" : day.ToString("dddd, ", System.Globalization.CultureInfo.CurrentCulture) + Fmt.Date(day);

            var lk = await svc.Data.GetLookupsAsync();
            UpdateFilters(lk);
            var all = await svc.Data.GetSessionsAsync(range);

            var appId = SelectedApp.Id; var catId = SelectedCategory.Id;
            var items = all.Select(s => ActivityItem.From(s, lk))
                .Where(i => appId == null || i.AppId == appId)
                .Where(i => catId == null || (!i.IsIdle && i.CategoryId == catId))
                .OrderBy(i => i.StartUtc).ToList();

            var today = Periods.LocalToday(DateTime.UtcNow, tz);
            var rows = new List<TimelineRow>();
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var r = Periods.Days(d, d, tz);
                var blocks = items.Where(i => i.EndUtc > r.FromUtc && i.StartUtc < r.ToUtc)
                    .Select(i => new TimelineBlock(i.Id, i.StartUtc, i.EndUtc, i.DisplayName, i.ColorHex, i.IsIdle, i.Title, i.AppId)).ToList();
                rows.Add(new TimelineRow(IsWeekly ? d.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture) : "24 h", r.FromUtc, blocks, d == today));
            }
            Rows = rows;

            Activities.Clear();
            foreach (var i in items) Activities.Add(i);
            if (Selected != null) Selected = Activities.FirstOrDefault(a => a.Id == Selected.Id);

            var active = items.Where(i => !i.IsIdle).Sum(i => (Clip(i, range)));
            var idle = items.Where(i => i.IsIdle).Sum(i => Clip(i, range));
            SummaryText = $"{items.Count(i => !i.IsIdle)} sessions · active {Fmt.Duration(active)} · idle {Fmt.Duration(idle)}";
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Timeline refresh failed"); }
    }

    private static double Clip(ActivityItem i, DateRange r)
    {
        var a = i.StartUtc > r.FromUtc ? i.StartUtc : r.FromUtc; var b = i.EndUtc < r.ToUtc ? i.EndUtc : r.ToUtc;
        return b > a ? (b - a).TotalSeconds : 0;
    }

    private void UpdateFilters(Lookups lk)
    {
        _suppress = true;
        try
        {
            var apps = new List<CategoryFilter> { new(null, "All applications") };
            apps.AddRange(lk.Apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new CategoryFilter(a.Id, a.Name)));
            var cats = new List<CategoryFilter> { new(null, "All categories") };
            cats.AddRange(lk.Categories.Select(c => new CategoryFilter(c.Id, c.Name)));
            if (AppFilters.Count != apps.Count) { AppFilters = apps; SelectedApp = apps.FirstOrDefault(a => a.Id == SelectedApp.Id) ?? apps[0]; }
            if (CategoryFilters.Count != cats.Count) { CategoryFilters = cats; SelectedCategory = cats.FirstOrDefault(c => c.Id == SelectedCategory.Id) ?? cats[0]; }
        }
        finally { _suppress = false; }
    }
}
