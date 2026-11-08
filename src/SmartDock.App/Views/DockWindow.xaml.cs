using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SmartDock.App.Controls;
using SmartDock.App.Services;
using SmartDock.App.ViewModels;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Windows.Interop;
using SmartDock.Windows.SystemIntegration;
using SmartDock.Windows.WindowManager;

namespace SmartDock.App.Views;

/// <summary>
/// The floating dock. Responsibilities kept here (view concerns only): native window styles, placement
/// on the configured monitor/edge, shell-hook messages, auto-hide, fullscreen avoidance, toasts,
/// and app-icon interactions. Business logic lives in the view models.
/// </summary>
public partial class DockWindow : Window
{
    private readonly DockViewModel _vm;
    private readonly ILogger<DockWindow> _logger;
    private readonly DispatcherTimer _appsDebounce;
    private readonly DispatcherTimer _watchTimer;
    private readonly DispatcherTimer _hideDelay;
    private readonly DispatcherTimer _toastTimer;
    private DockSettings _settings;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private uint _shellHookMessage;
    private uint _taskbarCreatedMessage;
    private IntPtr _winEventHook;
    private NativeMethods.WinEventDelegate? _winEventProc;
    private bool _slidOut;
    private bool _hiddenForFullscreen;
    private DockNotification? _currentToast;
    private Point? _appPressPoint;
    private AppBarRegistration? _appBar;
    private int? _lastReservedThickness;
    private RECT? _lastBand;
    private DispatcherTimer? _refitTimer;
    private DispatcherTimer? _reserveTimer;
    private bool _applyingDeferredReserve;
    private WindowPreviewWindow? _preview;
    private readonly DispatcherTimer _previewShowTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _previewHideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Button? _hoverButton;

    public DockWindow(DockViewModel vm, ISettingsService settings, ILogger<DockWindow> logger)
    {
        _vm = vm;
        _logger = logger;
        _settings = settings.Current;
        InitializeComponent();
        DataContext = vm;

        _appsDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _appsDebounce.Tick += (_, _) =>
        {
            _appsDebounce.Stop();
            _vm.Apps.Refresh();
        };

        // One cheap periodic check covers fullscreen detection, taskbar enforcement and missed shell events.
        _watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _watchTimer.Tick += (_, _) => OnWatchTick();

        _hideDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hideDelay.Tick += (_, _) =>
        {
            _hideDelay.Stop();
            TrySlideOut();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => CloseToast();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        Closed += OnClosed;
        InitWindowPreviews();
        _vm.Notifications.CollectionChanged += OnNotificationsChanged;
        UpdateBell();
    }

    /// <summary>Raised when Explorer restarts (the taskbar was recreated).</summary>
    public event EventHandler? ExplorerRestarted;

    /// <summary>Raised from the shared watch timer; the app uses it to enforce replacement mode.</summary>
    public event EventHandler? WatchTick;

    public IntPtr Handle => _hwnd;

    private static readonly ItemsPanelTemplate SingleRowPanel = CreatePanel(wrap: false);
    private static readonly ItemsPanelTemplate WrapRowsPanel = CreatePanel(wrap: true);

    private static ItemsPanelTemplate CreatePanel(bool wrap)
    {
        var factory = new FrameworkElementFactory(wrap ? typeof(WrapPanel) : typeof(StackPanel));
        if (wrap)
        {
            factory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
        }
        else
        {
            factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        }

        var template = new ItemsPanelTemplate(factory);
        template.Seal();
        return template;
    }

    /// <summary>Popups open below the dock when it sits at the top of the screen.</summary>
    public bool IsTopEdge => _settings.Behavior.Edge == DockEdge.Top;

    /// <summary>
    /// The dock window extends past the visible dock by its shadow padding, and it re-asserts "topmost"
    /// periodically. A menu or popup overlapping that window can end up under it, so they are anchored to
    /// the dock border and pushed fully outside the window, opening above (or below, for a top dock) it.
    /// </summary>
    public (Rect Area, PlacementMode Placement, double Offset) OutsidePlacement(double x, double width)
    {
        var pad = IsTopEdge ? DockHost.Margin.Bottom : DockHost.Margin.Top;
        var gap = pad + 4;
        return (new Rect(x, 0, width, DockBorder.ActualHeight),
            IsTopEdge ? PlacementMode.Bottom : PlacementMode.Top,
            IsTopEdge ? gap : -gap);
    }

    /// <summary>Opens <paramref name="menu"/> (owned by <paramref name="owner"/>) at the mouse x position, outside the dock window.</summary>
    public void PlaceMenuOutsideDock(ContextMenu menu, UIElement owner)
    {
        var (area, placement, offset) = OutsidePlacement(Mouse.GetPosition(DockBorder).X, 0);

        // ContextMenuService values on the owner take precedence when the menu opens, so set both.
        ContextMenuService.SetPlacementTarget(owner, DockBorder);
        ContextMenuService.SetPlacement(owner, placement);
        ContextMenuService.SetPlacementRectangle(owner, area);
        ContextMenuService.SetHorizontalOffset(owner, 0);
        ContextMenuService.SetVerticalOffset(owner, offset);
        menu.PlacementTarget = DockBorder;
        menu.Placement = placement;
        menu.PlacementRectangle = area;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = offset;
    }

    /// <summary>Anchors a widget popup over <paramref name="anchor"/>'s x range, outside the dock window.</summary>
    public void PlacePopupOutsideDock(Popup popup, FrameworkElement anchor, double transparentMargin)
    {
        var left = anchor.TranslatePoint(new Point(0, 0), DockBorder).X;
        var (area, placement, offset) = OutsidePlacement(left, anchor.ActualWidth);
        popup.PlacementTarget = DockBorder;
        popup.PlacementRectangle = area;
        popup.Placement = placement;

        // The popup's own transparent margin (room for its shadow) may overlap the dock window; its visible part may not.
        popup.VerticalOffset = IsTopEdge ? offset - transparentMargin : offset + transparentMargin;
        popup.HorizontalOffset = -transparentMargin;
    }

    public void ApplySettings(DockSettings settings)
    {
        _settings = settings;
        var edgeTop = settings.Behavior.Edge == DockEdge.Top;

        // Single row (StackPanel) or wrap onto more rows (WrapPanel) when the dock is full.
        var panel = settings.Behavior.WrapWhenFull ? WrapRowsPanel : SingleRowPanel;
        foreach (var region in new ItemsControl[] { AppsList, CenterList, RightList })
        {
            if (!ReferenceEquals(region.ItemsPanel, panel))
            {
                region.ItemsPanel = panel;
            }
        }

        var intensity = settings.Appearance.ShadowIntensity;
        DockBorder.Effect = intensity <= 0.01
            ? null
            : new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = edgeTop ? 90 : 270,
                BlurRadius = 10 + (16 * intensity),
                ShadowDepth = 1 + (3 * intensity),
                Opacity = 0.15 + (0.45 * intensity),
                RenderingBias = RenderingBias.Performance,
            };

        // Dock size: one uniform scale for the whole bar (height, icons, text, widgets).
        var dockScale = settings.Appearance.DockScale;
        DockBorder.LayoutTransform = Math.Abs(dockScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(dockScale, dockScale);

        var placement = edgeTop ? PlacementMode.Bottom : PlacementMode.Top;
        ToastPopup.Placement = placement;
        NotificationsPopup.Placement = placement;
        ToastPopup.VerticalOffset = edgeTop ? 8 : -8;
        NotificationsPopup.VerticalOffset = edgeTop ? 14 : -14;
        HotStrip.VerticalAlignment = edgeTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        HotStrip.Visibility = settings.Behavior.AutoHide ? Visibility.Visible : Visibility.Collapsed;

        if (!settings.Behavior.AutoHide && _slidOut)
        {
            SlideIn();
        }

        Reposition();
        if (settings.Behavior.AutoHide && !RootGrid.IsMouseOver)
        {
            _hideDelay.Start();
        }
    }

    /// <summary>Re-reserves the screen band once the dock's height has stopped changing.</summary>
    private void ScheduleDeferredReserve()
    {
        if (_reserveTimer is null)
        {
            _reserveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _reserveTimer.Tick += (_, _) =>
            {
                _reserveTimer.Stop();
                _applyingDeferredReserve = true;
                try
                {
                    Reposition(forceAppBar: false);
                }
                finally
                {
                    _applyingDeferredReserve = false;
                }
            };
        }

        _reserveTimer.Stop();
        _reserveTimer.Start(); // restart: wait until the size is stable
    }

    /// <summary>
    /// After the dock changed height (e.g. 3 rows → 2), give the shell a moment to apply the new work area, then
    /// fit maximised windows that Windows didn't resize back.
    /// </summary>
    private void ScheduleRefit(IntPtr monitorHandle, RECT workBefore)
    {
        _refitTimer?.Stop();
        _refitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refitTimer.Tick += (_, _) =>
        {
            _refitTimer?.Stop();
            if (AppBarRegistration.WorkAreaOf(monitorHandle) is { } workAfter)
            {
                var moved = AppBarRegistration.RefitMaximizedWindows(monitorHandle, workBefore, workAfter, _hwnd);
                if (moved > 0)
                {
                    _logger.LogDebug("Re-fitted {Count} maximised window(s) to the new work area", moved);
                }
            }
        };
        _refitTimer.Start();
    }

    /// <summary>Gives the reserved screen band back to Windows (safe to call repeatedly, from any exit path).</summary>
    public void ReleaseScreenSpace()
    {
        try
        {
            _lastReservedThickness = null;
            _lastBand = null;
            _reserveTimer?.Stop();
            _appBar?.Release();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Releasing the AppBar reservation failed");
        }
    }

    /// <summary>Places the window centred on the configured monitor edge, in physical pixels (per-monitor DPI safe).</summary>
    public void Reposition() => Reposition(forceAppBar: false);

    private void Reposition(bool forceAppBar)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorService.Resolve(_settings.Behavior.MonitorDeviceName);
        if (monitor is null || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = monitor.Scale;

        // DockBorder sizes are in its own (pre-"Dock size") units, so divide the available DIPs by that scale.
        var dockScale = _settings.Appearance.DockScale;
        // Full width: the visible bar spans the screen minus the edge gap on each side (the window's transparent
        // shadow padding may hang off-screen, which is harmless). Floating: keep the padding on-screen too.
        var available = _settings.Behavior.FullWidth
            ? (monitor.WorkArea.Width / scale) - (2 * _settings.Behavior.EdgeMargin)
            : (monitor.WorkArea.Width / scale) - (2 * _settings.Behavior.EdgeMargin) - DockHost.Margin.Left - DockHost.Margin.Right;
        var maxDockWidth = Math.Max(200, available / dockScale);
        if (Math.Abs(DockBorder.MaxWidth - maxDockWidth) > 0.5)
        {
            DockBorder.MaxWidth = maxDockWidth;
        }

        // Wrapping needs a width limit per group: the app list and the right-hand widgets are in horizontal stacks
        // that would otherwise give them unlimited width. The centre region simply fills what's left and wraps.
        var wrap = _settings.Behavior.WrapWhenFull;
        AppsList.MaxWidth = wrap ? Math.Max(120, maxDockWidth * 0.38) : double.PositiveInfinity;
        RightList.MaxWidth = wrap ? Math.Max(120, maxDockWidth * 0.38) : double.PositiveInfinity;

        // Full width: a fixed width filling the screen (minus the edge gap); otherwise size to content.
        var targetWidth = _settings.Behavior.FullWidth ? maxDockWidth : double.NaN;
        // NaN means "size to content". Any arithmetic with NaN is false, so the NaN cases are compared explicitly:
        // switching between auto and a fixed width must always apply.
        var current = DockBorder.Width;
        var widthChanged = double.IsNaN(targetWidth) != double.IsNaN(current)
            || (!double.IsNaN(targetWidth) && Math.Abs(current - targetWidth) > 0.5);
        if (widthChanged)
        {
            DockBorder.Width = targetWidth;
        }

        var work = monitor.WorkArea;

        // Use the size WPF just laid out, not GetWindowRect: when the content shrinks (3 rows → 2) SizeChanged
        // fires before the window handle has its new size, and positioning with the old height left a gap under
        // the dock (and reserved too much space).
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = ActualWidth > 0 ? (int)Math.Round(ActualWidth * dpi.DpiScaleX) : rect.Width;
        var height = ActualHeight > 0 ? (int)Math.Round(ActualHeight * dpi.DpiScaleY) : rect.Height;
        var shadowPad = (int)Math.Round(DockHost.Margin.Bottom * scale);
        var margin = (int)Math.Round(_settings.Behavior.EdgeMargin * scale);
        var top = _settings.Behavior.Edge == DockEdge.Top;

        // "Stick to the screen edge": reserve the dock's band in the work area so maximised windows stop at it.
        // Band = gap to the screen edge + visible dock + gap to windows. The shell may shift it to sit next to
        // other app bars (e.g. the Windows taskbar in companion mode).
        if (_settings.Behavior.ReserveSpace && !_settings.Behavior.AutoHide && _appBar is not null && height > 2 * shadowPad)
        {
            var windowGap = (int)Math.Round(_settings.Behavior.WindowGap * scale);
            var thickness = height - (2 * shadowPad) + margin + windowGap;
            var workBefore = monitor.WorkArea;

            // Height changes come in bursts (rows wrapping, widgets animating in). Re-reserving the band makes the
            // shell move every maximised window, so do that once the size has settled (~150 ms) and meanwhile
            // place the dock against the band we already hold: its screen-edge side doesn't move.
            var settled = forceAppBar || _applyingDeferredReserve || _lastBand is null || _lastReservedThickness == thickness;
            if (!settled && _lastBand is { } held)
            {
                work = new RECT { Left = work.Left, Right = work.Right, Top = held.Top, Bottom = held.Bottom };
                ScheduleDeferredReserve();
            }
            else if (_appBar.Reserve(monitor.Bounds, top, thickness, forceAppBar) is { } band)
            {
                work = new RECT { Left = work.Left, Right = work.Right, Top = band.Top, Bottom = band.Bottom };
                if (_lastReservedThickness is { } previous && previous != thickness)
                {
                    ScheduleRefit(monitor.Handle, workBefore);
                }

                _lastReservedThickness = thickness;
                _lastBand = band;
            }
            else
            {
                _logger.LogWarning("Could not reserve screen space (AppBar registration failed); the dock will float");
            }
        }
        else
        {
            ReleaseScreenSpace();
        }

        // With auto-hide the window touches the edge so the hot strip catches the pointer.
        var gap = _settings.Behavior.AutoHide ? 0 : margin - shadowPad;
        var x = work.Left + ((work.Width - width) / 2);
        var y = top ? work.Top + gap : work.Bottom - height - gap;

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void ShowToast(DockNotification notification)
    {
        _currentToast = notification;
        ToastTitle.Text = notification.Title;
        ToastMessage.Text = notification.Message;
        ToastAccent.Background = (Brush)FindResource(notification.Severity switch
        {
            NotificationSeverity.Success => "SuccessBrush",
            NotificationSeverity.Warning => "WarningBrush",
            NotificationSeverity.Error => "DangerBrush",
            _ => "AccentBrush",
        });

        if (_slidOut)
        {
            SlideIn();
            _hideDelay.Stop();
            _hideDelay.Start();
        }

        ToastPopup.IsOpen = true;
        if (AnimationSettings.Enabled)
        {
            var from = _settings.Behavior.Edge == DockEdge.Top ? -12 : 12;
            ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, AnimationSettings.Duration(220)) { EasingFunction = AnimationSettings.Ease });
            ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, AnimationSettings.Duration(180)));
        }

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _appBar = new AppBarRegistration(_hwnd);

        // Tool window: no Alt-Tab entry. No-activate: clicking the dock never steals focus from the user's app.
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        _shellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (!NativeMethods.RegisterShellHookWindow(_hwnd))
        {
            _logger.LogWarning("RegisterShellHookWindow failed; relying on periodic window refresh");
        }

        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        ApplySettings(_settings);
        _vm.Apps.Refresh();
        _watchTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _preview?.Close();
        _preview = null;
        _watchTimer.Stop();
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DeregisterShellHookWindow(_hwnd);
        }

        ReleaseScreenSpace();

        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var message = (uint)msg;
        if (message == _shellHookMessage && _shellHookMessage != 0 && wParam.ToInt32() == NativeMethods.HSHELL_FLASH)
        {
            // An app called FlashWindowEx (new message, finished download, ...). HSHELL_FLASH is
            // HSHELL_REDRAW with the high bit set, so it must be checked before masking below.
            OnAppFlash(lParam);
        }
        else if (message == _shellHookMessage && _shellHookMessage != 0)
        {
            switch (wParam.ToInt32() & 0x7FFF)
            {
                case NativeMethods.HSHELL_WINDOWACTIVATED:
                    _vm.Apps.UpdateActive(lParam, _hwnd);
                    DebounceAppsRefresh();
                    CheckFullscreen();
                    break;
                case NativeMethods.HSHELL_WINDOWCREATED:
                case NativeMethods.HSHELL_WINDOWDESTROYED:
                case NativeMethods.HSHELL_REDRAW:
                    DebounceAppsRefresh();
                    break;
            }
        }
        else if (_appBar is not null && message == _appBar.CallbackMessage && _appBar.CallbackMessage != 0)
        {
            // Another app bar (e.g. the taskbar) moved or resized: recompute our band.
            if (wParam.ToInt32() == NativeMethods.ABN_POSCHANGED)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(forceAppBar: true)));
            }
        }
        else if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer forgot every app bar when it restarted: register again.
            _appBar?.Release();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(forceAppBar: true)));
            _logger.LogInformation("Explorer restarted (TaskbarCreated)");
            NativeMethods.RegisterShellHookWindow(_hwnd);
            ExplorerRestarted?.Invoke(this, EventArgs.Empty);
            DebounceAppsRefresh();
        }
        else if (message is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED or NativeMethods.WM_SETTINGCHANGE)
        {
            // Resolution, DPI, monitor (dis)connection or work-area change: re-place after WPF has re-laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));
        }

        return IntPtr.Zero;
    }

    private void OnAppFlash(IntPtr hwnd)
    {
        if (!_settings.Behavior.ShowAttention)
        {
            return;
        }

        if (_vm.Apps.MarkAttention(hwnd) && _slidOut)
        {
            // Auto-hidden: show the dock briefly so the blinking icon is visible (the taskbar does the same).
            SlideIn();
            _hideDelay.Stop();
            _hideDelay.Start();
        }

        // The window may be new (not in the list yet); the refresh re-applies the attention state.
        DebounceAppsRefresh();
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Out-of-context WinEvents arrive on this (UI) thread's message loop.
        _vm.Apps.UpdateActive(hwnd, _hwnd);
        CheckFullscreen();
    }

    private void DebounceAppsRefresh()
    {
        _appsDebounce.Stop();
        _appsDebounce.Start();
    }

    private void OnWatchTick()
    {
        CheckFullscreen();
        WatchTick?.Invoke(this, EventArgs.Empty);

        // Keep topmost: some apps (and Explorer restarts) push other topmost windows above the dock.
        if (!_hiddenForFullscreen && _hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void CheckFullscreen()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var shouldHide = false;
        if (_settings.Behavior.Fullscreen == FullscreenBehavior.Hide)
        {
            var monitor = MonitorService.Resolve(_settings.Behavior.MonitorDeviceName);
            shouldHide = monitor is not null && FullscreenDetector.IsFullscreenOn(monitor.Handle, _hwnd);
        }

        if (shouldHide == _hiddenForFullscreen)
        {
            return;
        }

        _hiddenForFullscreen = shouldHide;
        NativeMethods.ShowWindow(_hwnd, shouldHide ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNA);
        if (shouldHide)
        {
            ToastPopup.IsOpen = false;
            NotificationsPopup.IsOpen = false;
        }
    }

    // ---------- auto-hide ----------
    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _hideDelay.Stop();
        if (_slidOut)
        {
            SlideIn();
        }
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (_settings.Behavior.AutoHide)
        {
            _hideDelay.Stop();
            _hideDelay.Start();
        }
    }

    private void TrySlideOut()
    {
        if (!_settings.Behavior.AutoHide || _slidOut)
        {
            return;
        }

        // Never hide under the pointer, during a drag, or while a menu/popup is open.
        if (RootGrid.IsMouseOver || Mouse.Captured is not null || NotificationsPopup.IsOpen || ToastPopup.IsOpen || _preview is { IsVisible: true })
        {
            _hideDelay.Start();
            return;
        }

        _slidOut = true;
        var distance = DockHost.ActualHeight + DockHost.Margin.Top + DockHost.Margin.Bottom;
        AnimateSlide(_settings.Behavior.Edge == DockEdge.Top ? -distance : distance);
    }

    private void SlideIn()
    {
        _slidOut = false;
        AnimateSlide(0);
    }

    private void AnimateSlide(double to)
    {
        var animation = new DoubleAnimation(to, AnimationSettings.Duration(200)) { EasingFunction = AnimationSettings.Ease };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    // ---------- notifications ----------
    private void OnNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateBell();

    private void UpdateBell()
    {
        var any = _vm.Notifications.Count > 0;
        BellBadge.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoNotifications.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseToast()
    {
        _toastTimer.Stop();
        ToastPopup.IsOpen = false;
        _currentToast = null;
        if (_settings.Behavior.AutoHide && !RootGrid.IsMouseOver)
        {
            _hideDelay.Start();
        }
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentToast?.ActionUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }

        CloseToast();
    }

    private void OnBellClick(object sender, RoutedEventArgs e)
    {
        CloseToast();
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
    }

    private void OnClearNotifications(object sender, RoutedEventArgs e) => _vm.NotificationService.Clear();

    private void OnDismissNotification(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            _vm.NotificationService.Dismiss(id);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => AppCommands.ShowSettings();

    private void OnStartClick(object sender, RoutedEventArgs e) => WindowTracker.OpenStartMenu();

    private void OnDockContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Widget cards and app icons have their own menus.
        if (e.OriginalSource is DependencyObject d)
        {
            if (FindAncestor<WidgetCard>(d) is not null)
            {
                return;
            }

            if (FindAncestor<Button>(d) is { } button)
            {
                // App icons build their own menu; other buttons (bell, settings, Start) have none.
                if (button.ContextMenu is null)
                {
                    e.Handled = true;
                }

                return;
            }
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Widget manager…", AppCommands.ShowWidgetManager));
        menu.Items.Add(Item("Dock settings…", AppCommands.ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Refresh applications", _vm.Apps.Refresh));
        PlaceMenuOutsideDock(menu, DockBorder);
        DockBorder.ContextMenu = menu;
    }

    // ---------- widget regions (drop on empty space appends) ----------
    private void OnRegionDragOver(object sender, DragEventArgs e)
    {
        e.Effects = WidgetCard.IsWidgetDrag(e)
            ? (e.Data.GetDataPresent(DragFormats.WidgetType) ? DragDropEffects.Copy : DragDropEffects.Move)
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRegionDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl { Tag: string tag } || !Enum.TryParse<DockRegion>(tag, out var region))
        {
            return;
        }

        e.Handled = true;
        await WidgetCard.HandleDropAsync(_vm, e.Data, region, _vm.Collection(region).Count);
    }

    // ---------- applications ----------
    private void OnAppClick(object sender, RoutedEventArgs e)
    {
        HideWindowPreview();
        if (sender is FrameworkElement { DataContext: DockAppItem item })
        {
            _vm.Apps.Activate(item);
        }
    }

    // ---------- window previews ----------
    private void OnAppMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_settings.Behavior.ShowWindowPreviews || sender is not Button { DataContext: DockAppItem { IsRunning: true } } button
            || e.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _hoverButton = button;
        _previewHideTimer.Stop();
        if (_preview is { IsVisible: true })
        {
            ShowWindowPreview(); // Moving between icons: switch immediately.
        }
        else
        {
            _previewShowTimer.Stop();
            _previewShowTimer.Start();
        }
    }

    private void OnAppMouseLeave(object sender, MouseEventArgs e)
    {
        _previewShowTimer.Stop();
        if (_preview is { IsVisible: true })
        {
            _previewHideTimer.Stop();
            _previewHideTimer.Start();
        }
    }

    private void InitWindowPreviews()
    {
        _previewShowTimer.Tick += (_, _) =>
        {
            _previewShowTimer.Stop();
            if (_hoverButton is { IsMouseOver: true })
            {
                ShowWindowPreview();
            }
        };
        _previewHideTimer.Tick += (_, _) =>
        {
            _previewHideTimer.Stop();
            if (_preview is { IsMouseOver: false } && _hoverButton is not { IsMouseOver: true })
            {
                HideWindowPreview();
            }
        };
    }

    private void ShowWindowPreview()
    {
        if (_hoverButton is not { DataContext: DockAppItem { IsRunning: true } item } button || !button.IsVisible)
        {
            return;
        }

        if (_preview is null)
        {
            _preview = new WindowPreviewWindow();
            _preview.MouseEnter += (_, _) => _previewHideTimer.Stop();
            _preview.MouseLeave += (_, _) =>
            {
                _previewHideTimer.Stop();
                _previewHideTimer.Start();
            };
            _preview.WindowChosen += (_, activated) =>
            {
                if (activated)
                {
                    HideWindowPreview();
                }
                else
                {
                    // A window was closed: refresh the list, then rebuild the preview from it.
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                    {
                        await Task.Delay(350);
                        _vm.Apps.Refresh();
                        if (_preview?.Item is { } shown)
                        {
                            var current = _vm.Apps.Items.FirstOrDefault(i => i.Key == shown.Key);
                            if (current is { IsRunning: true } && _hoverButton is not null)
                            {
                                ShowWindowPreview();
                            }
                            else
                            {
                                HideWindowPreview();
                            }
                        }
                    }));
                }
            };
        }

        // Icon rectangle in physical screen pixels.
        var topLeft = button.PointToScreen(new Point(0, 0));
        var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        var anchor = new RECT { Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)bottomRight.X, Bottom = (int)bottomRight.Y };

        // Keep the preview clear of the dock window itself (it re-asserts topmost and would cover it).
        if (NativeMethods.GetWindowRect(_hwnd, out var dockRect))
        {
            anchor.Top = Math.Min(anchor.Top, dockRect.Top);
            anchor.Bottom = Math.Max(anchor.Bottom, dockRect.Bottom);
        }

        if (MonitorService.Resolve(_settings.Behavior.MonitorDeviceName) is not { } monitor)
        {
            return;
        }

        _preview.ShowFor(item, anchor, monitor.Bounds, IsTopEdge);
    }

    private void HideWindowPreview()
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _preview?.HidePreview();
    }

    private void OnAppContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        HideWindowPreview();
        if (sender is not Button { DataContext: DockAppItem item } button)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var window in item.Windows)
        {
            var title = string.IsNullOrWhiteSpace(window.Title) ? item.DisplayName : window.Title;
            var handle = window.Handle;
            menu.Items.Add(Item(title.Length > 60 ? title[..57] + "…" : title, () => WindowTracker.Activate(handle)));
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        if (item.ExecutablePath is not null)
        {
            menu.Items.Add(Item(item.IsRunning ? "Open new window" : "Open", () => _vm.Apps.Launch(item)));
        }

        menu.Items.Add(item.IsPinned
            ? Item("Unpin from dock", () => _vm.Apps.Unpin(item))
            : Item("Pin to dock", () => _vm.Apps.Pin(item)));

        if (item.IsRunning)
        {
            menu.Items.Add(Item(item.HasMultipleWindows ? "Close all windows" : "Close window", () => _vm.Apps.CloseAll(item)));
        }

        PlaceMenuOutsideDock(menu, button);
        button.ContextMenu = menu;
    }

    private void OnAppPreviewMouseDown(object sender, MouseButtonEventArgs e) => _appPressPoint = e.GetPosition(this);

    private void OnAppPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_appPressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed
            || sender is not Button { DataContext: DockAppItem { Pin: { } pin } } button)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        _appPressPoint = null;
        button.Opacity = 0.45;
        try
        {
            DragDrop.DoDragDrop(button, new DataObject(DragFormats.Pin, pin.Id), DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = 1;
        }
    }

    private void OnAppDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DragFormats.Pin) && sender is FrameworkElement { DataContext: DockAppItem { IsPinned: true } };
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAppDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormats.Pin) is not string pinId
            || sender is not FrameworkElement { DataContext: DockAppItem { Pin: { } target } } element)
        {
            return;
        }

        e.Handled = true;
        var pins = _vm.Apps.Items.Where(i => i.IsPinned).Select(i => i.Pin!.Id).ToList();
        var index = pins.IndexOf(target.Id) + (e.GetPosition(element).X > element.ActualWidth / 2 ? 1 : 0);
        _vm.Apps.MovePin(pinId, index);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static T? FindAncestor<T>(DependencyObject? d)
        where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T match)
            {
                return match;
            }

            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return null;
    }
}
