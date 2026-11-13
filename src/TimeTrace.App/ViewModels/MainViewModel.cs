using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Services;
using TimeTrace.Core.Tracking;

namespace TimeTrace.App.ViewModels;

public sealed partial class AboutViewModel : PageViewModel
{
    public override string Title => "About";
    public string Version { get; } = typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    public string Tagline => "Understand where your time goes.";
    public string LicenseText { get; } = ReadLicense();
    public string DataFolder => AppServices.Instance.Paths.DataDirectory;
    public string DatabaseFile => AppServices.Instance.Db.Path;

    private static string ReadLicense()
    {
        try
        {
            using var s = typeof(AboutViewModel).Assembly.GetManifestResourceStream("LICENSE.txt");
            if (s == null) return "MIT License";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (Exception) { return "MIT License"; }
    }

    public IReadOnlyList<string> ThirdParty { get; } = new[]
    {
        "CommunityToolkit.Mvvm – MIT License, .NET Foundation",
        "Microsoft.Data.Sqlite and SQLitePCLRaw – MIT / Apache-2.0",
        "SQLite – public domain",
        "Serilog and Serilog.Sinks.File – Apache-2.0",
        "System.Drawing.Common – MIT License, .NET Foundation",
        ".NET and WPF – MIT License, Microsoft",
    };

    [RelayCommand] private void OpenDataFolder() => Dialogs.OpenInExplorer(AppServices.Instance.Paths.DataDirectory);
    [RelayCommand] private void OpenLogs() => Dialogs.OpenInExplorer(AppServices.Instance.Paths.LogDirectory);
}

public sealed partial class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _pages;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public IReadOnlyList<NavItem> NavItems { get; }
    public DashboardViewModel Dashboard { get; }
    public TimelineViewModel Timeline { get; }
    public ApplicationsViewModel Applications { get; }

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private string _statusText = "Tracking stopped";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _statusBrushKey = "FaintBrush";
    [ObservableProperty] private string _pauseText = "Pause";
    [ObservableProperty] private string _startStopText = "Start";
    [ObservableProperty] private bool _canPause, _canStartStop = true;
    [ObservableProperty] private bool _showIndicator = true;
    [ObservableProperty] private string _banner = "";

    public MainViewModel()
    {
        Dashboard = new DashboardViewModel(); Timeline = new TimelineViewModel(); Applications = new ApplicationsViewModel();
        _pages = new Dictionary<string, PageViewModel>
        {
            ["Dashboard"] = Dashboard, ["Timeline"] = Timeline, ["Applications"] = Applications,
            ["Reports"] = new ReportsViewModel(), ["Categories"] = new CategoriesViewModel(), ["Settings"] = new SettingsViewModel(), ["About"] = new AboutViewModel(),
        };
        NavItems = new[]
        {
            new NavItem { Key = "Dashboard", Label = "Dashboard", Glyph = "" }, new NavItem { Key = "Timeline", Label = "Timeline", Glyph = "" },
            new NavItem { Key = "Applications", Label = "Applications", Glyph = "" }, new NavItem { Key = "Reports", Label = "Reports", Glyph = "" },
            new NavItem { Key = "Categories", Label = "Categories", Glyph = "" }, new NavItem { Key = "Settings", Label = "Settings", Glyph = "" },
            new NavItem { Key = "About", Label = "About", Glyph = "" },
        };
        _currentPage = Dashboard;
        NavItems[0].IsSelected = true;

        var svc = AppServices.Instance;
        svc.Engine.SnapshotChanged += s => _dispatcher.BeginInvoke(() => ApplySnapshot(s));
        svc.ErrorRaised += m => _dispatcher.BeginInvoke(() => Banner = "⚠ " + m);
        svc.Settings.Changed += s => _dispatcher.BeginInvoke(() => { ShowIndicator = s.ShowTrackingIndicator; ApplySnapshot(svc.Engine.Snapshot); });
        ShowIndicator = svc.Settings.Current.ShowTrackingIndicator;
        ApplySnapshot(svc.Engine.Snapshot);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(svc.Settings.Current.DashboardRefreshSeconds) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(AppServices.Instance.Settings.Current.DashboardRefreshSeconds);
            if (CurrentPage.AutoRefresh && Application.Current.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized }) await CurrentPage.RefreshAsync();
        };
        _timer.Start();
    }

    public async Task NavigateAsync(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var n in NavItems) n.IsSelected = n.Key == key;
        CurrentPage = page;
        await page.ActivateAsync();
    }

    [RelayCommand] private Task NavigateToAsync(string? key) => key == null ? Task.CompletedTask : NavigateAsync(key);

    [RelayCommand] private void DismissBanner() => Banner = "";

    private void ApplySnapshot(TrackingSnapshot s)
    {
        var svc = AppServices.Instance;
        bool enabled = svc.Settings.Current.TrackingEnabled;
        StatusText = !enabled ? "Tracking disabled" : s.IsIdle ? "Idle" : s.StatusText;
        StatusDetail = !enabled ? "Turn it on in Settings → Tracking"
            : s.State == TrackingState.Running && !s.IsIdle && s.AppName != null ? s.AppName : "";
        StatusBrushKey = !enabled ? "FaintBrush" : s.State switch
        {
            TrackingState.Running => s.IsIdle ? "WarnBrush" : "AccentBrush",
            TrackingState.Paused => "WarnBrush",
            _ => "FaintBrush",
        };
        PauseText = s.State == TrackingState.Paused ? "Resume" : "Pause";
        CanPause = enabled && s.State != TrackingState.Stopped;
        StartStopText = s.State == TrackingState.Stopped ? "Start" : "Stop";
        CanStartStop = enabled || s.State == TrackingState.Stopped;
        svc.Tray?.UpdateStatus(StatusText + (StatusDetail.Length > 0 && enabled ? " · " + StatusDetail : ""));
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        var svc = AppServices.Instance;
        if (svc.Engine.State == TrackingState.Paused) await svc.ResumeTrackingAsync(); else await svc.PauseTrackingAsync();
    }

    [RelayCommand]
    private async Task ToggleStartStopAsync()
    {
        var svc = AppServices.Instance;
        if (svc.Engine.State == TrackingState.Stopped) await svc.StartTrackingAsync(); else await svc.StopTrackingAsync();
    }
}
