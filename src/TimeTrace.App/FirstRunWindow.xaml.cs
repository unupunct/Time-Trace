using System.Windows;

namespace TimeTrace.App;

/// <summary>Shown once: explains what is recorded and asks for consent before any tracking or startup entry is enabled.</summary>
public partial class FirstRunWindow : Window
{
    public bool StartTracking { get; private set; }
    public bool StartWithWindows => StartupBox.IsChecked == true;

    public FirstRunWindow() => InitializeComponent();

    private void OnAccept(object sender, RoutedEventArgs e) { StartTracking = true; DialogResult = true; }
    private void OnDecline(object sender, RoutedEventArgs e) { StartTracking = false; DialogResult = false; }
}
