using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Serilog;
using TimeTrace.App.Services;
using TimeTrace.Core;

namespace TimeTrace.App;

public partial class App : Application
{
    public static bool IsExiting { get; private set; }

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent, _exitEvent;
    private MainWindow? _window;
    private AppServices? _services;
    private string? _dataDirOverride;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool demo = args.Contains("--demo-data", StringComparer.OrdinalIgnoreCase);
        int di = Array.FindIndex(args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        if (di >= 0 && di + 1 < args.Length) _dataDirOverride = args[di + 1];

        // single instance (per data folder, so a portable/--data-dir copy can run next to the normal one)
        var suffix = _dataDirOverride == null ? "" : "." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_dataDirOverride).ToLowerInvariant())))[..8];
        _mutex = new Mutex(true, @"Local\TimeTrace.SingleInstance" + suffix, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TimeTrace.Show" + suffix);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TimeTrace.Exit" + suffix);
        if (!first)
        {
            // "--exit" is used by the installer/uninstaller to close a running instance cleanly; otherwise just show the window
            if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) _exitEvent.Set(); else _showEvent.Set();
            Shutdown();
            return;
        }
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) { Shutdown(); return; }

        try
        {
            var paths = new AppPaths(_dataDirOverride);
            ConfigureLogging(paths);
            Log.Information("TimeTrace {Version} starting", typeof(App).Assembly.GetName().Version);
            DispatcherUnhandledException += (_, ex) => { Log.Error(ex.Exception, "Unhandled UI exception"); ex.Handled = true; Dialogs.Error("TimeTrace", "An unexpected error occurred:\n" + ex.Exception.Message + "\n\nDetails were written to the log file."); };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal(ex.ExceptionObject as Exception, "Fatal exception");
            TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error(ex.Exception, "Unobserved task exception"); ex.SetObserved(); };

            _services = await AppServices.CreateAsync(paths);
            var settings = _services.Settings;

            if (demo && _dataDirOverride != null)
            {
                // synthetic data for screenshots/demos: tracking stays off and the consent dialog is skipped
                await DemoData.GenerateAsync(_services.Repo, _services.Repo, DateTime.UtcNow);
                _services.InvalidateAppCache();
                settings.Current.FirstRunCompleted = true; settings.Current.AutoStartTracking = false; settings.Current.TrackingEnabled = false;
                await settings.SaveAsync(settings.Current);
            }

            if (args.Contains("--auto-consent", StringComparer.OrdinalIgnoreCase) && _dataDirOverride != null && !settings.Current.FirstRunCompleted)
            {
                // developer/test option (only with --data-dir): skip the consent dialog and track
                settings.Current.FirstRunCompleted = true; settings.Current.AutoStartTracking = true; settings.Current.TrackingEnabled = true;
                await settings.SaveAsync(settings.Current);
            }

            int ti = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < args.Length && _dataDirOverride != null) { settings.Current.Theme = args[ti + 1]; settings.Current.Validate(); }
            _services.Theme.Apply(settings.Current.Theme, settings.Current.AccentColor);

            // first run: explicit consent before anything is tracked or added to startup
            bool consentToTrack = settings.Current.AutoStartTracking && settings.Current.FirstRunCompleted;
            if (!settings.Current.FirstRunCompleted)
            {
                var dlg = new FirstRunWindow();
                dlg.ShowDialog();
                settings.Current.FirstRunCompleted = true;
                settings.Current.AutoStartTracking = dlg.StartTracking;
                settings.Current.TrackingEnabled = dlg.StartTracking;
                settings.Current.StartWithWindows = dlg.StartTracking && dlg.StartWithWindows;
                if (settings.Current.StartWithWindows) _services.Startup.Enable(AppServices.ExePath, settings.Current.StartMinimized);
                await settings.SaveAsync(settings.Current);
                consentToTrack = dlg.StartTracking;
            }

            _window = new MainWindow();
            MainWindow = _window;
            _services.Tray = new TrayService(
                open: () => Dispatcher.Invoke(() => _window.ShowAndActivate()),
                stats: () => Dispatcher.Invoke(async () => { _window.ShowAndActivate(); _window.Vm.Dashboard.SelectToday(); await _window.Vm.NavigateAsync("Dashboard"); }),
                timeline: () => Dispatcher.Invoke(async () => { _window.ShowAndActivate(); _window.Vm.Timeline.GoToToday(); await _window.Vm.NavigateAsync("Timeline"); }),
                exit: () => Dispatcher.Invoke(() => _ = ExitApplicationAsync()));

            if (!background) _window.Show();
            else _ = _window.Vm.NavigateAsync("Dashboard");

            await _services.StartAsync(consentToTrack);
            ListenForShowRequests();

            // developer option: --page <Name> --screenshot <file.png> renders a page to an image and exits (used for documentation)
            int pi = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
            int si = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
            int ri = Array.FindIndex(args, a => a.Equals("--preset", StringComparison.OrdinalIgnoreCase));
            if (ri >= 0 && ri + 1 < args.Length && int.TryParse(args[ri + 1], out var presetIndex) && presetIndex is >= 0 and < 5)
            { _window.Vm.Dashboard.SelectedPreset = TimeTrace.App.ViewModels.PresetOption.All[presetIndex]; _window.Vm.Applications.SelectedPreset = TimeTrace.App.ViewModels.PresetOption.All[presetIndex]; }
            if (ri >= 0 && ri + 1 < args.Length && int.TryParse(args[ri + 1], out var dayBack) && dayBack is >= 0 and <= 1) _window.Vm.Timeline.SelectedDate = DateTime.Today.AddDays(-dayBack);
            if (pi >= 0 && pi + 1 < args.Length) await _window.Vm.NavigateAsync(args[pi + 1]);
            if (si >= 0 && si + 1 < args.Length) await CaptureAndExitAsync(args[si + 1]);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show("TimeTrace could not start:\n\n" + ex.Message + "\n\nIf the problem persists, see the log folder in %LOCALAPPDATA%\\TimeTrace\\logs.", "TimeTrace", MessageBoxButton.OK, MessageBoxImage.Error);
            IsExiting = true;
            Shutdown(1);
        }
    }

    private async Task CaptureAndExitAsync(string file)
    {
        var args = Environment.GetCommandLineArgs();
        int di = Array.FindIndex(args, a => a.Equals("--delay", StringComparison.OrdinalIgnoreCase));
        await Task.Delay(di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out var secs) ? secs * 1000 : 2500);
        var w = _window!;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var content = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(file)) enc.Save(fs);
        await ExitApplicationAsync();
    }

    private void ListenForShowRequests()
    {
        var t = new Thread(() =>
        {
            while (_showEvent != null && _exitEvent != null && !IsExiting)
            {
                int i = WaitHandle.WaitAny(new WaitHandle[] { _showEvent, _exitEvent }, 500);
                if (i == 0) Dispatcher.BeginInvoke(() => _window?.ShowAndActivate());
                else if (i == 1) Dispatcher.BeginInvoke(() => _ = ExitApplicationAsync());
            }
        }) { IsBackground = true, Name = "ShowRequestListener" };
        t.Start();
    }

    public async Task ExitApplicationAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { if (_services != null) await _services.ShutdownAsync(); }
        finally
        {
            _services?.Tray?.Dispose();
            Log.Information("TimeTrace exiting");
            Log.CloseAndFlush();
            try { _mutex?.ReleaseMutex(); } catch (Exception) { }
            Shutdown();
        }
    }

    private static void ConfigureLogging(AppPaths paths)
    {
        Directory.CreateDirectory(paths.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "timetrace-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
