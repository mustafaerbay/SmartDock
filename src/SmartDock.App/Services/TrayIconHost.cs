using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Models;
using SmartDock.Windows.Taskbar;

namespace SmartDock.App.Services;

/// <summary>
/// System-tray icon and menu (main UI thread — H.NotifyIcon's WPF control requires it).
/// If the UI thread ever hangs, the watchdog process restores the taskbar instead (see ADR 0002).
/// </summary>
public sealed class TrayIconHost(TaskbarController taskbar, ILogger<TrayIconHost> logger) : IDisposable
{
    private TaskbarIcon? _icon;
    private MenuItem? _companionItem;
    private MenuItem? _replacementItem;

    public event Action? ShowDockRequested;

    public event Action? WidgetManagerRequested;

    public event Action? SettingsRequested;

    public event Action<TaskbarMode>? ModeRequested;

    public event Action? RestartRequested;

    public event Action? ExitRequested;

    public event Action? CheckUpdatesRequested;

    public event Action? InstallUpdateRequested;

    private MenuItem? _installUpdateItem;

    /// <summary>Shows "Install update vX" in the menu when <paramref name="version"/> is set.</summary>
    public void SetUpdateAvailable(string? version)
    {
        if (_installUpdateItem is null)
        {
            return;
        }

        _installUpdateItem.Header = version is null ? "Install update" : $"Install update v{version}";
        _installUpdateItem.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
        if (_icon is not null)
        {
            _icon.ToolTipText = "SmartDock " + SmartDock.Core.AppInfo.DisplayVersion + (version is null ? string.Empty : $" · update v{version} available");
        }
    }

    public void Start(TaskbarMode initialMode)
    {
        try
        {
            _icon = new TaskbarIcon
            {
                ToolTipText = "SmartDock " + SmartDock.Core.AppInfo.DisplayVersion,

                // A pack-URI image lets H.NotifyIcon resolve the .ico through Application.GetResourceStream.
                IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/smartdock.ico", UriKind.Absolute)),
                ContextMenu = BuildMenu(initialMode),
            };
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // The dock still works without a tray icon; the dock's own menus and settings remain available.
            logger.LogError(ex, "Could not create the tray icon");
        }
    }

    public void SetMode(TaskbarMode mode)
    {
        if (_companionItem is not null && _replacementItem is not null)
        {
            _companionItem.IsChecked = mode == TaskbarMode.Companion;
            _replacementItem.IsChecked = mode == TaskbarMode.Replacement;
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private ContextMenu BuildMenu(TaskbarMode mode)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "SmartDock " + SmartDock.Core.AppInfo.DisplayVersion, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show Dock", () => ShowDockRequested?.Invoke()));
        menu.Items.Add(Item("Widget Manager", () => WidgetManagerRequested?.Invoke()));
        menu.Items.Add(Item("Settings", () => SettingsRequested?.Invoke()));
        menu.Items.Add(new Separator());

        _companionItem = Item("Companion Mode", () => ModeRequested?.Invoke(TaskbarMode.Companion));
        _companionItem.IsChecked = mode == TaskbarMode.Companion;
        _replacementItem = Item("Replacement Mode", () => ModeRequested?.Invoke(TaskbarMode.Replacement));
        _replacementItem.IsChecked = mode == TaskbarMode.Replacement;
        menu.Items.Add(_companionItem);
        menu.Items.Add(_replacementItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Restart Dock", () => RestartRequested?.Invoke()));
        menu.Items.Add(Item("Restore Windows Taskbar", () =>
        {
            taskbar.Restore();
            ModeRequested?.Invoke(TaskbarMode.Companion);
        }));
        _installUpdateItem = Item("Install update", () => InstallUpdateRequested?.Invoke());
        _installUpdateItem.Visibility = Visibility.Collapsed;
        _installUpdateItem.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(Item("Check for updates", () => CheckUpdatesRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", () => ExitRequested?.Invoke()));
        return menu;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }
}
