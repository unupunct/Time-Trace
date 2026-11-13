using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Controls;
using TimeTrace.App.Services;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Reporting;

namespace TimeTrace.App.ViewModels;

public sealed record ReportKindOption(ReportKind Kind, string Label, bool UsesAnchor);
public sealed record FormatOption(ReportFormat Format, string Label);

public sealed class ComparisonVm
{
    public string Label { get; init; } = "";
    public string CurrentText { get; init; } = "";
    public string PreviousText { get; init; } = "";
    public string DeltaText { get; init; } = "";
    public bool IsUp { get; init; }
    public bool HasDelta { get; init; }
    public string Detail { get; init; } = "";
}

public sealed partial class ReportsViewModel : PageViewModel
{
    public override string Title => "Reports";

    public IReadOnlyList<ReportKindOption> Kinds { get; } = new[]
    {
        new ReportKindOption(ReportKind.Daily, "Daily report", true), new ReportKindOption(ReportKind.Weekly, "Weekly report", true),
        new ReportKindOption(ReportKind.Monthly, "Monthly report", true), new ReportKindOption(ReportKind.Custom, "Custom date range report", false),
        new ReportKindOption(ReportKind.ApplicationUsage, "Application usage report", false), new ReportKindOption(ReportKind.CategoryUsage, "Category usage report", false),
    };
    public IReadOnlyList<FormatOption> Formats { get; } = new[]
    {
        new FormatOption(ReportFormat.Html, "HTML (with charts)"), new FormatOption(ReportFormat.Csv, "CSV (spreadsheet)"), new FormatOption(ReportFormat.Json, "JSON (data)"),
    };

    [ObservableProperty] private ReportKindOption _selectedKind;
    [ObservableProperty] private FormatOption _selectedFormat;
    [ObservableProperty] private DateTime _anchorDate = DateTime.Today;
    [ObservableProperty] private DateTime _fromDate = DateTime.Today.AddDays(-13);
    [ObservableProperty] private DateTime _toDate = DateTime.Today;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _status = "", _lastFile = "";
    [ObservableProperty] private string _periodText = "", _trackedText = "", _activeText = "", _idleText = "", _unobservedText = "", _switchText = "", _avgText = "", _appsText = "";
    [ObservableProperty] private IReadOnlyList<BarItem>? _bars;
    [ObservableProperty] private IReadOnlyList<SliceItem>? _slices;
    [ObservableProperty] private double[,]? _heatmap;
    [ObservableProperty] private string _barTitle = "Daily breakdown";
    public ObservableCollection<AppCardVm> TopApps { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<ComparisonVm> Comparisons { get; } = new();
    public bool UsesAnchor => SelectedKind.UsesAnchor;
    public bool UsesRange => !SelectedKind.UsesAnchor;
    public bool HasFile => !string.IsNullOrEmpty(LastFile);


    public ReportsViewModel()
    {
        _selectedKind = Kinds[1]; _selectedFormat = Formats[0];
        var saved = AppServices.Instance?.Settings.Current.ReportFolder;
        _folder = string.IsNullOrWhiteSpace(saved)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TimeTrace Reports") : saved;
    }

    partial void OnSelectedKindChanged(ReportKindOption value) { OnPropertyChanged(nameof(UsesAnchor)); OnPropertyChanged(nameof(UsesRange)); _ = RefreshAsync(); }
    partial void OnAnchorDateChanged(DateTime value) => _ = RefreshAsync();
    partial void OnFromDateChanged(DateTime value) => _ = RefreshAsync();
    partial void OnToDateChanged(DateTime value) => _ = RefreshAsync();
    partial void OnLastFileChanged(string value) => OnPropertyChanged(nameof(HasFile));

    private DateRange CurrentRange()
    {
        var tz = Fmt.Tz; var a = DateOnly.FromDateTime(AnchorDate);
        return SelectedKind.Kind switch
        {
            ReportKind.Daily => Periods.Days(a, a, tz),
            ReportKind.Weekly => Periods.Days(Periods.StartOfWeek(a), Periods.StartOfWeek(a).AddDays(6), tz),
            ReportKind.Monthly => Periods.Days(Periods.StartOfMonth(a), Periods.StartOfMonth(a).AddMonths(1).AddDays(-1), tz),
            _ => Periods.Days(DateOnly.FromDateTime(FromDate), DateOnly.FromDateTime(ToDate < FromDate ? FromDate : ToDate), tz),
        };
    }

    private async Task<ReportData> BuildAsync()
    {
        var svc = AppServices.Instance;
        var summary = await svc.Data.SummarizeAsync(CurrentRange());
        return ReportBuilder.Build(SelectedKind.Kind, summary, Fmt.Tz, DateTime.UtcNow);
    }

    public override Task RefreshAsync() => RunExclusiveAsync(LoadAsync);

    private async Task LoadAsync()
    {
        try
        {
            var svc = AppServices.Instance;
            var lk = await svc.Data.GetLookupsAsync();
            var range = CurrentRange();
            var s = await svc.Data.SummarizeAsync(range, lk);
            PeriodText = $"{Fmt.Date(DateOnly.FromDateTime(Fmt.Local(range.FromUtc)))} – {Fmt.Date(DateOnly.FromDateTime(Fmt.Local(range.ToUtc.AddTicks(-1))))}";
            TrackedText = Fmt.Duration(s.TrackedSeconds); ActiveText = Fmt.Duration(s.ActiveSeconds); IdleText = Fmt.Duration(s.IdleSeconds);
            UnobservedText = Fmt.Duration(s.UnobservedSeconds); SwitchText = s.SwitchCount.ToString(); AppsText = s.ApplicationCount.ToString();
            AvgText = s.ActiveSessionCount == 0 ? "–" : Fmt.Duration(s.AverageSessionSeconds);
            var idle = svc.Settings.Current.IncludeIdleInReports;
            BarTitle = s.Daily.Count <= 1 ? "Activity by hour" : "Daily breakdown";
            Bars = s.Daily.Count <= 1
                ? Enumerable.Range(0, 24).Select(h => new BarItem(Fmt.Hour(h), s.ByHour[h], 0, $"{h:00}:00 – {Fmt.Duration(s.ByHour[h])}")).ToList()
                : s.Daily.Select(d => new BarItem(s.Daily.Count > 10 ? d.Date.ToString("d/M") : Fmt.DayShort(d.Date), d.ActiveSeconds, idle ? d.IdleSeconds : 0,
                    $"{Fmt.Date(d.Date)}\nActive {Fmt.Duration(d.ActiveSeconds)}" + (idle ? $"\nIdle {Fmt.Duration(d.IdleSeconds)}" : ""))).ToList();
            Slices = s.Categories.Select(c => new SliceItem(c.Name, c.Seconds, c.Color)).ToList();
            Legend.Clear();
            foreach (var c in s.Categories) Legend.Add(new LegendItem { Label = c.Name, ColorHex = c.Color, ValueText = $"{Fmt.Duration(c.Seconds)} · {Fmt.Percent(c.Seconds / Math.Max(s.ActiveSeconds, 1) * 100)}" });
            Heatmap = s.Heatmap;

            TopApps.Clear();
            int rank = 1;
            foreach (var a in s.Apps.Take(10))
            {
                lk.AppById.TryGetValue(a.ApplicationId, out var app);
                TopApps.Add(new AppCardVm { Rank = rank++, AppId = a.ApplicationId, Name = a.Name, IconPath = app?.IconCachePath, TimeText = Fmt.Duration(a.Seconds),
                    Percent = a.Seconds / Math.Max(s.TrackedSeconds, 1) * 100, PercentText = Fmt.Percent(a.Seconds / Math.Max(s.TrackedSeconds, 1) * 100), Sessions = a.SessionCount });
            }
            await LoadComparisonsAsync(lk);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Reports refresh failed"); }
    }

    private async Task LoadComparisonsAsync(Lookups lk)
    {
        var svc = AppServices.Instance; var tz = Fmt.Tz;
        var today = Periods.LocalToday(DateTime.UtcNow, tz);
        async Task<ComparisonVm> One(string label, (DateRange cur, DateRange prev) r)
        {
            var c = await svc.Data.SummarizeAsync(r.cur, lk); var p = await svc.Data.SummarizeAsync(r.prev, lk);
            var cmp = AnalyticsEngine.Compare(label, c, p);
            string delta = !p.HasData ? "no data for the previous period"
                : $"{(cmp.DeltaSeconds >= 0 ? "+" : "−")}{Fmt.Duration(Math.Abs(cmp.DeltaSeconds))}" + (cmp.DeltaPercent is double pc ? $" ({(pc >= 0 ? "+" : "−")}{Math.Abs(pc):0}%)" : "");
            return new ComparisonVm { Label = label, CurrentText = Fmt.Duration(c.TrackedSeconds), PreviousText = Fmt.Duration(p.TrackedSeconds),
                DeltaText = delta, IsUp = cmp.DeltaSeconds >= 0, HasDelta = p.HasData };
        }
        var list = new[]
        {
            await One("Today vs yesterday", AnalyticsEngine.TodayVsYesterday(today, tz)),
            await One("This week vs last week", AnalyticsEngine.ThisWeekVsLast(today, tz)),
            await One("This month vs last month", AnalyticsEngine.ThisMonthVsLast(today, tz)),
        };
        Comparisons.Clear();
        foreach (var c in list) Comparisons.Add(c);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var f = Dialogs.PickFolder("Choose where reports are saved", Folder);
        if (f != null) { Folder = f; var s = AppServices.Instance.Settings; s.Current.ReportFolder = f; _ = s.SaveAsync(s.Current); }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        try
        {
            var report = await BuildAsync();
            var path = await ReportExporter.ExportToFolderAsync(report, SelectedFormat.Format, Folder);
            LastFile = path;
            Status = "Report saved: " + path;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Report export failed");
            Status = "Could not save the report: " + ex.Message;
        }
    }

    [RelayCommand] private void OpenFolder() { if (HasFile) Dialogs.OpenInExplorer(LastFile); else Dialogs.OpenInExplorer(Folder); }
    [RelayCommand] private void OpenFile()
    {
        if (!HasFile) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LastFile) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Could not open the file: " + ex.Message; }
    }
}
