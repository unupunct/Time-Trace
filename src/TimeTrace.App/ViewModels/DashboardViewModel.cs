using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimeTrace.App.Controls;
using TimeTrace.App.Services;
using TimeTrace.Core.Analytics;

namespace TimeTrace.App.ViewModels;

public sealed class AppCardVm
{
    public int AppId { get; init; }
    public int Rank { get; init; }
    public string Name { get; init; } = "";
    public string? IconPath { get; init; }
    public string TimeText { get; init; } = "";
    public string PercentText { get; init; } = "";
    public double Percent { get; init; }
    public int Sessions { get; init; }
    public string SessionsText => Sessions == 1 ? "1 session" : $"{Sessions} sessions";
    public string ColorHex { get; init; } = "#6B7280";
    public string CategoryName { get; init; } = "";
}

public sealed class LegendItem
{
    public string Label { get; init; } = "";
    public string ColorHex { get; init; } = "#6B7280";
    public string ValueText { get; init; } = "";
}

public sealed partial class DashboardViewModel : PageViewModel
{
    public override string Title => "Dashboard";
    public override bool AutoRefresh => true;

    public IReadOnlyList<PresetOption> Presets => PresetOption.All;
    [ObservableProperty] private PresetOption _selectedPreset = PresetOption.All[0];
    [ObservableProperty] private DateTime _customFrom = DateTime.Today.AddDays(-6);
    [ObservableProperty] private DateTime _customTo = DateTime.Today;

    [ObservableProperty] private string _totalTracked = "–", _activeTime = "–", _idleTime = "–", _appsUsed = "–", _mostUsed = "–", _avgSession = "–";
    [ObservableProperty] private string _mostUsedDetail = "", _rangeLabel = "", _trackedNote = "", _chartTitle = "Daily activity";
    [ObservableProperty] private string _idleLabel = "Idle time", _trackedLabel = "Total tracked time";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private IReadOnlyList<BarItem>? _bars;
    [ObservableProperty] private IReadOnlyList<SliceItem>? _appSlices, _categorySlices;
    [ObservableProperty] private double[,]? _heatmap;
    [ObservableProperty] private string _donutTitle = "", _donutSubtitle = "", _categoryDonutTitle = "";

    public bool IsCustom => SelectedPreset.Preset == RangePreset.Custom;
    public ObservableCollection<AppCardVm> TopApps { get; } = new();
    public ObservableCollection<LegendItem> AppLegend { get; } = new();
    public ObservableCollection<LegendItem> CategoryLegend { get; } = new();
    public ObservableCollection<ActivityItem> Recent { get; } = new();


    partial void OnSelectedPresetChanged(PresetOption value) { OnPropertyChanged(nameof(IsCustom)); _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) { if (IsCustom) _ = RefreshAsync(); }
    partial void OnCustomToChanged(DateTime value) { if (IsCustom) _ = RefreshAsync(); }

    public void SelectToday() => SelectedPreset = PresetOption.All[0];

    public override Task RefreshAsync() => RunExclusiveAsync(LoadAsync);

    private async Task LoadAsync()
    {
        try
        {
            var svc = AppServices.Instance;
            var range = DataService.RangeFor(SelectedPreset.Preset, DateOnly.FromDateTime(CustomFrom), DateOnly.FromDateTime(CustomTo));
            var lk = await svc.Data.GetLookupsAsync();
            var sessions = await svc.Data.GetSessionsAsync(range);
            var s = await svc.Data.SummarizeAsync(range, lk, sessions);
            var idleIncluded = svc.Settings.Current.IncludeIdleInReports;

            HasData = s.HasData;
            TrackedLabel = idleIncluded ? "Total tracked time" : "Total tracked time (active)";
            TotalTracked = Fmt.Duration(s.TrackedSeconds);
            ActiveTime = Fmt.Duration(s.ActiveSeconds);
            IdleTime = Fmt.Duration(s.IdleSeconds);
            AppsUsed = s.ApplicationCount.ToString();
            MostUsed = s.MostUsedApp?.Name ?? "–";
            MostUsedDetail = s.MostUsedApp == null ? "" : Fmt.Duration(s.MostUsedApp.Seconds);
            AvgSession = s.ActiveSessionCount == 0 ? "–" : Fmt.Duration(s.AverageSessionSeconds);
            var first = DateOnly.FromDateTime(Fmt.Local(range.FromUtc)); var last = DateOnly.FromDateTime(Fmt.Local(range.ToUtc.AddTicks(-1)));
            RangeLabel = first == last ? Fmt.Date(first) : $"{Fmt.Date(first)} – {Fmt.Date(last)}";
            TrackedNote = s.UnobservedSeconds > 60
                ? $"Not observed: {Fmt.Duration(s.UnobservedSeconds)} (TimeTrace was stopped, paused, locked or the PC was asleep)"
                : "";

            // Top 10 application cards
            var total = Math.Max(s.TrackedSeconds, 1);
            TopApps.Clear();
            int rank = 1;
            foreach (var a in s.Apps.Take(10))
            {
                lk.AppById.TryGetValue(a.ApplicationId, out var app);
                var cat = a.CategoryId is int cid && lk.CategoryById.TryGetValue(cid, out var c) ? c : lk.Fallback;
                TopApps.Add(new AppCardVm
                {
                    AppId = a.ApplicationId, Rank = rank++, Name = a.Name, IconPath = app?.IconCachePath, TimeText = Fmt.Duration(a.Seconds),
                    Percent = a.Seconds / total * 100, PercentText = Fmt.Percent(a.Seconds / total * 100), Sessions = a.SessionCount,
                    ColorHex = cat.Color, CategoryName = cat.Name,
                });
            }

            // Daily activity chart: hourly for a single day, daily otherwise
            if (s.Daily.Count <= 1)
            {
                ChartTitle = "Activity by hour";
                Bars = Enumerable.Range(0, 24).Select(h => new BarItem(Fmt.Hour(h), s.ByHour[h], 0, $"{h:00}:00 – {Fmt.Duration(s.ByHour[h])}")).ToList();
            }
            else
            {
                ChartTitle = "Daily activity";
                Bars = s.Daily.Select(d => new BarItem(s.Daily.Count > 10 ? d.Date.ToString("d/M") : Fmt.DayShort(d.Date), d.ActiveSeconds, idleIncluded ? d.IdleSeconds : 0,
                    $"{Fmt.Date(d.Date)}\nActive {Fmt.Duration(d.ActiveSeconds)}" + (idleIncluded ? $"\nIdle {Fmt.Duration(d.IdleSeconds)}" : ""))).ToList();
            }

            // Application distribution: top 8 + "Other apps"
            var palette = new[] { "#3DDC84", "#4F9DFF", "#B07CFF", "#FF9F4A", "#2FC8D9", "#FF7A9C", "#E6C34A", "#7BD389" };
            var top = s.Apps.Take(8).ToList();
            var slices = top.Select((a, i) => new SliceItem(a.Name, a.Seconds, palette[i % palette.Length])).ToList();
            var rest = s.Apps.Skip(8).Sum(a => a.Seconds);
            if (rest > 0) slices.Add(new SliceItem("Other applications", rest, "#6B7280"));
            AppSlices = slices;
            DonutTitle = Fmt.Duration(s.ActiveSeconds); DonutSubtitle = "active";
            AppLegend.Clear();
            foreach (var sl in slices) AppLegend.Add(new LegendItem { Label = sl.Label, ColorHex = sl.ColorHex, ValueText = Fmt.Percent(sl.Value / Math.Max(s.ActiveSeconds, 1) * 100) });

            CategorySlices = s.Categories.Select(c => new SliceItem(c.Name, c.Seconds, c.Color)).ToList();
            CategoryLegend.Clear();
            foreach (var c in s.Categories) CategoryLegend.Add(new LegendItem { Label = c.Name, ColorHex = c.Color, ValueText = Fmt.Duration(c.Seconds) });
            CategoryDonutTitle = s.MostUsedCategory?.Name ?? "";

            Heatmap = s.Heatmap;

            Recent.Clear();
            foreach (var x in sessions.OrderByDescending(x => x.StartTimeUtc).Take(14)) Recent.Add(ActivityItem.From(x, lk));
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Dashboard refresh failed"); }
    }
}
