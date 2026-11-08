using System.Windows;
using SmartDock.App.Services;
using SmartDock.App.ViewModels;

namespace SmartDock.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm, string settingsPath)
    {
        _vm = vm;
        WindowStyling.ApplyFluent(this);
        InitializeComponent();
        DataContext = vm;
        PathRun.Text = settingsPath;
        Activated += (_, _) => vm.Reload();
    }

    private void OnAccentPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            _vm.AccentColor = hex;
        }
    }

    private void OnOpenWidgetManager(object sender, RoutedEventArgs e) => AppCommands.ShowWidgetManager();
}
