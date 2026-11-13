using System.Windows.Controls;
using System.Windows.Data;
using TimeTrace.App.ViewModels;

namespace TimeTrace.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnSourceUpdated(object? sender, DataTransferEventArgs e) => (DataContext as SettingsViewModel)?.Changed();
}
