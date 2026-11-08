using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartDock.App.Services;
using SmartDock.App.ViewModels;
using SmartDock.App.Views;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;

namespace SmartDock.App.Controls;

public enum DropPosition
{
    None,
    Before,
    After,
}

public static class DragFormats
{
    /// <summary>Payload: instance id of a widget already in the dock.</summary>
    public const string WidgetInstance = "SmartDock.WidgetInstance";

    /// <summary>Payload: type id of a widget dragged from the widget manager.</summary>
    public const string WidgetType = "SmartDock.WidgetType";

    /// <summary>Payload: pin id of a pinned application.</summary>
    public const string Pin = "SmartDock.Pin";
}

/// <summary>
/// Chrome around every widget: card background, hover, loading/error overlay, context menu,
/// drag-to-reorder, drop target with insertion indicator, and insert/remove animations.
/// The widget's own content comes from the implicit DataTemplate for its type.
/// </summary>
public sealed class WidgetCard : ContentControl
{
    public static readonly DependencyProperty DropPositionProperty = DependencyProperty.Register(
        nameof(DropPosition), typeof(DropPosition), typeof(WidgetCard), new PropertyMetadata(DropPosition.None));

    private Point? _pressPoint;
    private bool _animatedIn;
    private static readonly HashSet<WidgetCard> CardsWithOpenPopup = [];
    private Popup? _popup;
    private DateTime _popupClosedAt;

    /// <summary>
    /// Closes every open widget popup. Called when SmartDock opens one of its windows (search, explorer, notes…):
    /// the dock never takes focus, so the popup's own "close on outside click" doesn't fire in that case.
    /// </summary>
    public static void CloseAllPopups()
    {
        foreach (var card in CardsWithOpenPopup.ToList())
        {
            if (card._popup is { IsOpen: true } popup)
            {
                popup.IsOpen = false;
            }
        }

        CardsWithOpenPopup.Clear();
    }

    static WidgetCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(WidgetCard), new FrameworkPropertyMetadata(typeof(WidgetCard)));
    }

    public WidgetCard()
    {
        AllowDrop = true;
        Loaded += OnLoaded;
        ContextMenuOpening += OnContextMenuOpening;

        // A placeholder menu is required for ContextMenuOpening to fire; it is rebuilt every time.
        ContextMenu = new ContextMenu();
    }

    public DropPosition DropPosition
    {
        get => (DropPosition)GetValue(DropPositionProperty);
        set => SetValue(DropPositionProperty, value);
    }

    private IDockWidget? Widget => Content as IDockWidget;

    private DockViewModel? Dock => Window.GetWindow(this)?.DataContext as DockViewModel;

    public Task AnimateOutAsync()
    {
        if (!AnimationSettings.Enabled)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        var duration = AnimationSettings.Duration(160);
        var scale = EnsureLayoutScale();
        var fade = new DoubleAnimation(0, duration) { EasingFunction = AnimationSettings.Ease };
        var shrink = new DoubleAnimation(0.4, duration) { EasingFunction = AnimationSettings.Ease };
        fade.Completed += (_, _) => tcs.TrySetResult();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        BeginAnimation(OpacityProperty, fade);
        return tcs.Task;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _pressPoint = e.GetPosition(this);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        var wasClick = _pressPoint is not null;
        _pressPoint = null;

        // A click (no drag) on the card itself — inner buttons keep their own behaviour.
        if (!wasClick || IsInsideControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        switch (Widget)
        {
            case IClickableWidget clickable:
                clickable.OnClick();
                e.Handled = true;
                break;
            case IPopupWidget popupWidget:
                TogglePopup(popupWidget);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (Widget is IWheelWidget wheel)
        {
            wheel.OnWheel(e.Delta);
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || Widget is not { } widget)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressPoint = null;
        Opacity = 0.45;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(DragFormats.WidgetInstance, widget.InstanceId), DragDropEffects.Move);
        }
        finally
        {
            Opacity = 1;
        }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (DroppedFiles(e) is { } files)
        {
            e.Effects = Widget is IFileDropWidget fileTarget && fileTarget.AcceptsFiles(files) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!IsWidgetDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        DropPosition = e.GetPosition(this).X < ActualWidth / 2 ? DropPosition.Before : DropPosition.After;
        e.Effects = e.Data.GetDataPresent(DragFormats.WidgetType) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DropPosition = DropPosition.None;
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (DroppedFiles(e) is { } files)
        {
            e.Handled = true;
            if (Widget is IFileDropWidget fileTarget && fileTarget.AcceptsFiles(files))
            {
                fileTarget.OnFilesDropped(files);
            }

            return;
        }

        var position = DropPosition;
        DropPosition = DropPosition.None;
        if (Dock is not { } dock || Widget is not { } target || dock.Find(target.InstanceId, out var region) is null)
        {
            return;
        }

        e.Handled = true;
        var index = dock.Collection(region).IndexOf(target) + (position == DropPosition.After ? 1 : 0);
        await HandleDropAsync(dock, e.Data, region, index);
    }

    internal static async Task HandleDropAsync(DockViewModel dock, IDataObject data, DockRegion region, int index)
    {
        if (data.GetData(DragFormats.WidgetInstance) is string instanceId)
        {
            dock.MoveWidget(instanceId, region, index);
        }
        else if (data.GetData(DragFormats.WidgetType) is string typeId)
        {
            await dock.AddWidgetAsync(typeId, region, index);
        }
    }

    /// <summary>Files/folders dragged from Explorer, or null for any other drag.</summary>
    private static IReadOnlyList<string>? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths : null;

    internal static bool IsWidgetDrag(DragEventArgs e) =>
        e.Data.GetDataPresent(DragFormats.WidgetInstance) || e.Data.GetDataPresent(DragFormats.WidgetType);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_animatedIn || !AnimationSettings.Enabled)
        {
            return;
        }

        _animatedIn = true;
        var duration = AnimationSettings.Duration(220);
        var scale = EnsureLayoutScale();
        // FillBehavior.Stop hands the properties back to their base values (1) so later changes
        // (e.g. dimming while dragging) are not masked by a held animation value.
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, duration) { EasingFunction = AnimationSettings.Ease, FillBehavior = FillBehavior.Stop });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }

    private void TogglePopup(IPopupWidget popupWidget)
    {
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
            return;
        }

        // Clicking the card while its popup is open first closes it (outside click); don't reopen at once.
        if (DateTime.UtcNow - _popupClosedAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        var key = Content.GetType().Name + ".Popup";
        if (TryFindResource(key) is not DataTemplate template)
        {
            return;
        }

        popupWidget.OnPopupOpened();
        var top = Window.GetWindow(this) is DockWindow { IsTopEdge: true };
        _popup ??= new Popup
        {
            PlacementTarget = this,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = AnimationSettings.Enabled ? PopupAnimation.Fade : PopupAnimation.None,
            Child = new Border
            {
                Margin = new Thickness(14),
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                MinWidth = 240,
                MaxWidth = 420,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
                Child = new ContentControl(),
            },
        };

        _popup.Closed -= OnPopupClosed;
        _popup.Closed += OnPopupClosed;
        var border = (Border)_popup.Child;
        border.SetResourceReference(Border.BackgroundProperty, "PopupBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "DockBorderBrush");
        var presenter = (ContentControl)border.Child;
        presenter.Content = Content;
        presenter.ContentTemplate = template;
        presenter.SetResourceReference(Control.FontFamilyProperty, "UiFont");

        if (Window.GetWindow(this) is DockWindow dockWindow)
        {
            dockWindow.PlacePopupOutsideDock(_popup, this, border.Margin.Bottom);
        }
        else
        {
            _popup.Placement = top ? PlacementMode.Bottom : PlacementMode.Top;
            _popup.VerticalOffset = top ? 4 : -4;
        }

        _popup.IsOpen = true;
        CardsWithOpenPopup.Add(this);
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        CardsWithOpenPopup.Remove(this);
        _popupClosedAt = DateTime.UtcNow;
        (Content as IPopupWidget)?.OnPopupClosed();
    }

    private bool IsInsideControl(DependencyObject? d)
    {
        while (d is not null && d != this)
        {
            if (d is ButtonBase or RangeBase or TextBoxBase)
            {
                return true;
            }

            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private ScaleTransform EnsureLayoutScale()
    {
        if (LayoutTransform is ScaleTransform { IsFrozen: false } existing)
        {
            return existing;
        }

        var scale = new ScaleTransform(1, 1);
        LayoutTransform = scale;
        return scale;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Widget is not { } widget || Dock is not { } dock)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        foreach (var item in widget.MenuItems)
        {
            menu.Items.Add(new MenuItem
            {
                Header = item.Header,
                Command = item.Command,
                IsCheckable = item.IsCheckable,
                IsChecked = item.IsChecked,
            });
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        if (widget is IConfigurableWidget)
        {
            var configure = new MenuItem { Header = "Configure…" };
            configure.Click += (_, _) => AppCommands.ConfigureWidget(widget.InstanceId);
            menu.Items.Add(configure);
        }

        if (widget is IPopupWidget popupWidget)
        {
            var details = new MenuItem { Header = "Show details" };
            details.Click += (_, _) => TogglePopup(popupWidget);
            menu.Items.Add(details);
        }

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => dock.RefreshWidget(widget.InstanceId);
        menu.Items.Add(refresh);

        var remove = new MenuItem { Header = "Remove from dock" };
        remove.Click += async (_, _) =>
        {
            await AnimateOutAsync();
            await dock.RemoveWidgetAsync(widget.InstanceId);
        };
        menu.Items.Add(remove);

        menu.Items.Add(new Separator());
        var manager = new MenuItem { Header = "Widget manager…" };
        manager.Click += (_, _) => AppCommands.ShowWidgetManager();
        menu.Items.Add(manager);
        var settings = new MenuItem { Header = "Dock settings…" };
        settings.Click += (_, _) => AppCommands.ShowSettings();
        menu.Items.Add(settings);

        if (Window.GetWindow(this) is DockWindow dockWindow)
        {
            dockWindow.PlaceMenuOutsideDock(menu, this);
        }

        ContextMenu = menu;
    }
}
