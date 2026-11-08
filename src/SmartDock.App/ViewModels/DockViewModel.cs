using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.App.ViewModels;

/// <summary>
/// Owns the widget instances shown in the dock and keeps them in sync with layout.json.
/// All public members must be called on the UI thread (they mutate bound collections).
/// </summary>
public sealed class DockViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IWidgetRegistry _registry;
    private readonly ILayoutService _layout;
    private readonly WidgetHost _host;
    private readonly IEventBus _events;
    private readonly INotificationService _notifications;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DockViewModel> _logger;
    private readonly Dictionary<string, WidgetSettings> _settingsByInstance = new(StringComparer.Ordinal);
    private int _notificationCount;

    public DockViewModel(
        IServiceProvider services,
        IWidgetRegistry registry,
        ILayoutService layout,
        WidgetHost host,
        IEventBus events,
        INotificationService notifications,
        ILoggerFactory loggerFactory,
        AppsViewModel apps)
    {
        _services = services;
        _registry = registry;
        _layout = layout;
        _host = host;
        _events = events;
        _notifications = notifications;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DockViewModel>();
        Apps = apps;
    }

    public AppsViewModel Apps { get; }

    public ObservableCollection<IDockWidget> CenterWidgets { get; } = [];

    public ObservableCollection<IDockWidget> RightWidgets { get; } = [];

    public ObservableCollection<DockNotification> Notifications { get; } = [];

    public int NotificationCount
    {
        get => _notificationCount;
        private set => SetProperty(ref _notificationCount, value);
    }

    public INotificationService NotificationService => _notifications;

    public async Task LoadAsync()
    {
        var layout = _layout.Current;
        foreach (var config in layout.CenterWidgets.ToList())
        {
            await CreateAsync(config, DockRegion.Center, CenterWidgets.Count).ConfigureAwait(true);
        }

        foreach (var config in layout.RightWidgets.ToList())
        {
            await CreateAsync(config, DockRegion.Right, RightWidgets.Count).ConfigureAwait(true);
        }

        SyncNotifications();
    }

    public int CountOf(string typeId) =>
        CenterWidgets.Concat(RightWidgets).Count(w => string.Equals(w.Descriptor.TypeId, typeId, StringComparison.OrdinalIgnoreCase));

    public bool CanAdd(string typeId) =>
        _registry.TryGet(typeId, out var d) && (d.AllowMultiple || CountOf(typeId) == 0);

    public async Task<IDockWidget?> AddWidgetAsync(string typeId, DockRegion? region = null, int? index = null)
    {
        if (!_registry.TryGet(typeId, out var descriptor) || !CanAdd(typeId))
        {
            return null;
        }

        var target = region ?? descriptor.DefaultRegion;
        var list = Collection(target);
        var at = Math.Clamp(index ?? list.Count, 0, list.Count);
        var config = new WidgetInstanceConfig { InstanceId = DockLayout.NewInstanceId(descriptor.TypeId), TypeId = descriptor.TypeId };
        _layout.Mutate(l => l.GetRegion(target).Insert(Math.Clamp(at, 0, l.GetRegion(target).Count), config));
        return await CreateAsync(config, target, at).ConfigureAwait(true);
    }

    public async Task RemoveWidgetAsync(string instanceId)
    {
        var widget = Find(instanceId, out var region);
        if (widget is null)
        {
            return;
        }

        Collection(region).Remove(widget);
        _settingsByInstance.Remove(instanceId);
        _layout.Mutate(l => l.Remove(instanceId));
        await _host.RemoveAsync(instanceId).ConfigureAwait(true);
    }

    public void MoveWidget(string instanceId, DockRegion region, int index)
    {
        var widget = Find(instanceId, out var fromRegion);
        if (widget is null)
        {
            return;
        }

        var source = Collection(fromRegion);
        var target = Collection(region);
        var from = source.IndexOf(widget);

        if (ReferenceEquals(source, target))
        {
            // Index is computed against the list before removal; adjust when moving right.
            var to = Math.Clamp(index > from ? index - 1 : index, 0, target.Count - 1);
            if (to == from)
            {
                return;
            }

            target.Move(from, to);
            _layout.Mutate(l => l.Move(instanceId, region, to));
            return;
        }

        source.RemoveAt(from);
        var at = Math.Clamp(index, 0, target.Count);
        target.Insert(at, widget);
        _layout.Mutate(l => l.Move(instanceId, region, at));
    }

    public void RefreshWidget(string instanceId) => _host.RequestRefresh(instanceId);

    /// <summary>The live settings bag of a widget instance (used by the configuration dialog).</summary>
    public WidgetSettings? GetSettings(string instanceId) => _settingsByInstance.GetValueOrDefault(instanceId);

    public async Task ResetLayoutAsync()
    {
        foreach (var w in CenterWidgets.Concat(RightWidgets).ToList())
        {
            await _host.RemoveAsync(w.InstanceId).ConfigureAwait(true);
        }

        CenterWidgets.Clear();
        RightWidgets.Clear();
        _settingsByInstance.Clear();
        _layout.Reset();
        await LoadAsync().ConfigureAwait(true);
        Apps.Refresh();
    }

    /// <summary>Called on the UI thread when the notification history changes.</summary>
    public void SyncNotifications()
    {
        Notifications.Clear();
        foreach (var n in _notifications.History)
        {
            Notifications.Add(n);
        }

        NotificationCount = Notifications.Count;
    }

    public IDockWidget? Find(string instanceId, out DockRegion region)
    {
        region = DockRegion.Center;
        var w = CenterWidgets.FirstOrDefault(x => x.InstanceId == instanceId);
        if (w is not null)
        {
            return w;
        }

        region = DockRegion.Right;
        return RightWidgets.FirstOrDefault(x => x.InstanceId == instanceId);
    }

    public ObservableCollection<IDockWidget> Collection(DockRegion region) =>
        region == DockRegion.Center ? CenterWidgets : RightWidgets;

    private async Task<IDockWidget?> CreateAsync(WidgetInstanceConfig config, DockRegion region, int index)
    {
        if (!_registry.TryGet(config.TypeId, out var descriptor))
        {
            return null;
        }

        IDockWidget widget;
        try
        {
            widget = descriptor.Factory(_services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create widget {TypeId}", config.TypeId);
            _notifications.Post("Widget unavailable", $"{descriptor.Name} could not be created: {ex.Message}", NotificationSeverity.Error, dedupeKey: "create:" + config.TypeId);
            return null;
        }

        var settings = new WidgetSettings(config.Settings);
        settings.Changed += (_, _) => _layout.ScheduleSave();
        _settingsByInstance[config.InstanceId] = settings;
        var context = new WidgetContext(
            config.InstanceId,
            descriptor,
            settings,
            _events,
            _notifications,
            _loggerFactory.CreateLogger("Widget." + descriptor.TypeId));

        widget.Width = config.Width;

        // Initialise before binding so templates never see a widget without its context.
        // Initialisation errors are isolated by the host; the card then shows the error state.
        await _host.AddAsync(widget, context).ConfigureAwait(true);

        var list = Collection(region);
        list.Insert(Math.Clamp(index, 0, list.Count), widget);
        return widget;
    }
}
