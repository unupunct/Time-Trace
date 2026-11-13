using System.Windows.Controls;
using System.Windows.Input;
using TimeTrace.App.ViewModels;

namespace TimeTrace.App.Views;

public partial class ApplicationsView : UserControl
{
    public ApplicationsView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject d && FindParent<ComboBox>(d) != null) return;
        if (Grid.SelectedItem is AppRowVm row && DataContext is ApplicationsViewModel vm) vm.OpenDetailCommand.Execute(row);
    }

    private static T? FindParent<T>(System.Windows.DependencyObject? d) where T : System.Windows.DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = System.Windows.Media.VisualTreeHelper.GetParent(d); }
        return null;
    }
}
