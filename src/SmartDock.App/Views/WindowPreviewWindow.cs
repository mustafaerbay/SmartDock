using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SmartDock.App.ViewModels;
using SmartDock.Windows.Interop;
using SmartDock.Windows.WindowManager;

namespace SmartDock.App.Views;

/// <summary>
/// Hover preview for a dock app: one live DWM thumbnail per window, like the Windows taskbar.
/// Click a thumbnail to switch to that window, or its × to close it. Never takes focus.
/// It is a separate, non-layered window because DWM can't draw thumbnails into a transparent one.
/// </summary>
public sealed class WindowPreviewWindow : Window
{
    private const double MaxThumbWidth = 220;
    private const double MaxThumbHeight = 132;
    private const double MinThumbWidth = 110;
    private const double CardChrome = 16; // card padding + margin, horizontally

    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal };
    private readonly List<(Border Placeholder, DwmThumbnail Thumbnail)> _thumbnails = [];
    private IntPtr _hwnd;
    private RECT _anchor;
    private RECT _monitor;
    private bool _below;

    public WindowPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "DockBorderBrush");
        Content = new Border { Padding = new Thickness(6), Child = _cards };
        LayoutUpdated += (_, _) => PlaceThumbnails();
        SizeChanged += (_, _) =>
        {
            if (Item is not null)
            {
                Reposition();
            }
        };
    }

    /// <summary>The app whose windows are shown, or null when hidden.</summary>
    public DockAppItem? Item { get; private set; }

    /// <summary>Raised after the user picked or closed a window, so the dock can hide/refresh.</summary>
    public event Action<IntPtr, bool>? WindowChosen;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Windows 11 rounded corners (ignored on Windows 10).
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
    }

    /// <summary>
    /// Shows previews for <paramref name="item"/> next to <paramref name="anchor"/> (the icon's screen rect, physical
    /// pixels), kept inside <paramref name="monitor"/>; above the anchor unless <paramref name="below"/>.
    /// </summary>
    public void ShowFor(DockAppItem item, RECT anchor, RECT monitor, bool below)
    {
        Item = item;
        _anchor = anchor;
        _monitor = monitor;
        _below = below;
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        Build(item);
        if (_thumbnails.Count == 0 && _cards.Children.Count == 0)
        {
            HidePreview();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        Reposition();
        PlaceThumbnails();
    }

    public void HidePreview()
    {
        ClearThumbnails();
        _cards.Children.Clear();
        Item = null;
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbnails();
        base.OnClosed(e);
    }

    private void Build(DockAppItem item)
    {
        ClearThumbnails();
        _cards.Children.Clear();

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var available = Math.Max(300, (_monitor.Right - _monitor.Left) / scale - 40);
        var count = Math.Max(1, item.Windows.Count);
        var boxWidth = Math.Clamp((available / count) - CardChrome, MinThumbWidth, MaxThumbWidth);
        var boxHeight = boxWidth * (MaxThumbHeight / MaxThumbWidth);

        foreach (var window in item.Windows)
        {
            var thumbnail = DwmThumbnail.Create(_hwnd, window.Handle);
            var (w, h) = thumbnail?.SourceSize ?? (0, 0);
            double width = boxWidth, height = boxHeight;
            if (w > 0 && h > 0)
            {
                var fit = Math.Min(boxWidth / w, boxHeight / h);
                width = Math.Max(40, w * fit);
                height = Math.Max(30, h * fit);
            }

            var placeholder = new Border
            {
                Width = width,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            if (thumbnail is null)
            {
                // DWM couldn't provide a thumbnail: show the app icon instead of an empty box.
                placeholder.Child = new Image { Source = item.Icon, Width = 32, Height = 32 };
            }
            else
            {
                _thumbnails.Add((placeholder, thumbnail));
            }

            _cards.Children.Add(BuildCard(window, item, placeholder, boxWidth));
        }
    }

    private FrameworkElement BuildCard(AppWindowInfo window, DockAppItem item, Border placeholder, double width)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(window.Title) ? item.DisplayName : window.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        title.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 22,
            Height = 22,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Close window",
            Visibility = Visibility.Hidden,
            Focusable = false,
        };
        close.SetResourceReference(StyleProperty, "DockIconButton");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            WindowTracker.RequestClose(window.Handle);
            WindowChosen?.Invoke(window.Handle, false);
        };

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(placeholder);

        var card = new Border
        {
            Width = width + 12,
            Padding = new Thickness(6),
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = title.Text,
        };
        card.MouseEnter += (_, _) =>
        {
            card.SetResourceReference(Border.BackgroundProperty, "WidgetHoverBrush");
            close.Visibility = Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Brushes.Transparent;
            close.Visibility = Visibility.Hidden;
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, close))
            {
                return;
            }

            WindowTracker.Activate(window.Handle);
            WindowChosen?.Invoke(window.Handle, true);
        };
        return card;
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Centres the window over the icon (physical pixels) and keeps it on the monitor.</summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        const int gap = 10;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = ((_anchor.Left + _anchor.Right) / 2) - (width / 2);
        x = Math.Clamp(x, _monitor.Left + 4, Math.Max(_monitor.Left + 4, _monitor.Right - width - 4));
        var y = _below ? _anchor.Bottom + gap : _anchor.Top - height - gap;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Points each DWM thumbnail at its placeholder (client-area physical pixels).</summary>
    private void PlaceThumbnails()
    {
        if (_thumbnails.Count == 0 || !IsVisible)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var (placeholder, thumbnail) in _thumbnails)
        {
            if (!placeholder.IsVisible || PresentationSource.FromVisual(placeholder) is null)
            {
                continue;
            }

            var topLeft = placeholder.TranslatePoint(new Point(0, 0), this);
            var left = (int)Math.Round(topLeft.X * dpi.DpiScaleX);
            var top = (int)Math.Round(topLeft.Y * dpi.DpiScaleY);
            var right = left + (int)Math.Round(placeholder.ActualWidth * dpi.DpiScaleX);
            var bottom = top + (int)Math.Round(placeholder.ActualHeight * dpi.DpiScaleY);
            thumbnail.Update(left, top, right, bottom);
        }
    }

    private void ClearThumbnails()
    {
        foreach (var (_, thumbnail) in _thumbnails)
        {
            thumbnail.Dispose();
        }

        _thumbnails.Clear();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
