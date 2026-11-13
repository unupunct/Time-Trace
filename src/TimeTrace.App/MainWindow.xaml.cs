using System.Windows;
using TimeTrace.App.Services;
using TimeTrace.App.ViewModels;

namespace TimeTrace.App;

public partial class MainWindow : Window
{
    public MainViewModel Vm { get; }

    public MainWindow()
    {
        InitializeComponent();
        Vm = new MainViewModel();
        DataContext = Vm;
        SourceInitialized += (_, _) => AppServices.Instance.Theme.ApplyTitleBar(this);
        Loaded += async (_, _) => await Vm.NavigateAsync("Dashboard");
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && AppServices.Instance.Settings.Current.MinimizeToTray) Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            if (AppServices.Instance.Settings.Current.CloseToTray) { e.Cancel = true; Hide(); return; }
            e.Cancel = true;
            _ = ((App)Application.Current).ExitApplicationAsync();
            return;
        }
        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false; Focus();
    }
}
