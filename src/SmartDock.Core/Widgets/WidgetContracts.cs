using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;

namespace SmartDock.Core.Widgets;

public enum WidgetCategory
{
    Time,
    System,
    Network,
    Media,
    Productivity,
    Developer,
    AI,
    Other,
}

public enum WidgetState
{
    /// <summary>Waiting for the first successful refresh.</summary>
    Loading,

    /// <summary>Showing current data.</summary>
    Ready,

    /// <summary>The data source does not exist on this machine (e.g. no battery). Not an error.</summary>
    Unavailable,

    /// <summary>An external service could not be reached; cached data may still be displayed.</summary>
    Disconnected,

    /// <summary>The widget failed; the host is retrying with backoff.</summary>
    Error,
}

/// <summary>Static metadata used by the registry and the widget manager.</summary>
public sealed record WidgetDescriptor(
    string TypeId,
    string Name,
    string Description,
    WidgetCategory Category,
    string IconGlyph,
    DockRegion DefaultRegion,
    Func<IServiceProvider, IDockWidget> Factory)
{
    /// <summary>When false the manager only allows one instance in the dock.</summary>
    public bool AllowMultiple { get; init; }
}

/// <summary>A context-menu entry contributed by a widget.</summary>
public sealed record WidgetMenuItem(string Header, ICommand Command, bool IsChecked = false, bool IsCheckable = false);

/// <summary>Per-instance services handed to a widget at initialisation.</summary>
public sealed class WidgetContext(
    string instanceId,
    WidgetDescriptor descriptor,
    WidgetSettings settings,
    IEventBus events,
    INotificationService notifications,
    ILogger logger)
{
    public string InstanceId { get; } = instanceId;

    public WidgetDescriptor Descriptor { get; } = descriptor;

    public WidgetSettings Settings { get; } = settings;

    public IEventBus Events { get; } = events;

    public INotificationService Notifications { get; } = notifications;

    public ILogger Logger { get; } = logger;

    /// <summary>Set by the host so a widget can ask for an immediate refresh (e.g. after a settings change).</summary>
    public Action? RequestRefresh { get; set; }
}

/// <summary>
/// Common contract for every dock widget. Widgets are view models: they never create top-level
/// windows; the app supplies a DataTemplate per widget type.
/// </summary>
public interface IDockWidget : IAsyncDisposable
{
    string InstanceId { get; }

    WidgetDescriptor Descriptor { get; }

    WidgetState State { get; }

    string? StatusMessage { get; }

    DateTimeOffset? LastUpdated { get; }

    /// <summary>How often the shared scheduler should call <see cref="RefreshAsync"/>. <see cref="Timeout.InfiniteTimeSpan"/> = event driven only.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>Optional fixed width in DIPs; null = size to content.</summary>
    double? Width { get; set; }

    IReadOnlyList<WidgetMenuItem> MenuItems { get; }

    Task InitializeAsync(WidgetContext context, CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Called by the host when a refresh throws, so the UI can show an error state.</summary>
    void ReportFailure(string message);
}
