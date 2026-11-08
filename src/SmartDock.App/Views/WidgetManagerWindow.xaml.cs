using System.Windows;
using System.Windows.Input;
using SmartDock.App.Controls;
using SmartDock.App.Services;
using SmartDock.App.ViewModels;

namespace SmartDock.App.Views;

public partial class WidgetManagerWindow : Window
{
    private readonly WidgetManagerViewModel _vm;
    private Point? _pressPoint;

    public WidgetManagerWindow(WidgetManagerViewModel vm)
    {
        _vm = vm;
        WindowStyling.ApplyFluent(this);
        InitializeComponent();
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e) => _pressPoint = e.GetPosition(this);

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: string typeId } card)
        {
            return;
        }

        // Do not start a drag from the Add/Remove buttons.
        if (e.OriginalSource is DependencyObject d && IsInsideButton(d, card))
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressPoint = null;
        DragDrop.DoDragDrop(card, new DataObject(DragFormats.WidgetType, typeId), DragDropEffects.Copy);
    }

    private static bool IsInsideButton(DependencyObject d, DependencyObject stop)
    {
        while (d is not null && d != stop)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private async void OnResetLayout(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Reset the dock to the default widgets and pinned apps?", "SmartDock", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            await _vm.ResetLayoutCommand.ExecuteAsync(null);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
