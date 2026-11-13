using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimeTrace.App.Controls;
using TimeTrace.App.ViewModels;

namespace TimeTrace.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();

    private TimelineViewModel? Vm => DataContext as TimelineViewModel;

    private void OnDaily(object sender, RoutedEventArgs e) { if (Vm != null) Vm.IsWeekly = false; }
    private void OnWeekly(object sender, RoutedEventArgs e) { if (Vm != null) Vm.IsWeekly = true; }

    private void OnBlockClicked(TimelineBlock block) => Vm?.SelectSession(block.SessionId);

    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0) Vm.ZoomInCommand.Execute(null); else Vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }
        else if (sender is ScrollViewer sv && Keyboard.Modifiers == ModifierKeys.None && Vm.Rows?.Count > 1)
        {
            // let the page scroll vertically when the weekly view is shown
            e.Handled = false;
        }
    }
}
