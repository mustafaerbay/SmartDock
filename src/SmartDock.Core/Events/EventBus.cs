using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartDock.Core.Events;

/// <summary>Lightweight in-process pub/sub so widgets never reference each other directly.</summary>
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);

    void Publish<TEvent>(TEvent evt);
}

public sealed class EventBus(ILogger<EventBus>? logger = null) : IEventBus
{
    private readonly ILogger _logger = logger ?? NullLogger<EventBus>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    public void Publish<TEvent>(TEvent evt)
    {
        Delegate[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<TEvent>)handler)(evt);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not break the publisher or other subscribers.
                _logger.LogError(ex, "Event handler for {EventType} failed", typeof(TEvent).Name);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Published by the media service wrapper when playback state changes.</summary>
public sealed record MediaPlaybackChanged(bool IsPlaying, string? Title, string? Artist);

/// <summary>Published by the Pomodoro widget when a phase completes.</summary>
public sealed record PomodoroPhaseCompleted(string InstanceId, string CompletedPhase);

/// <summary>Published when the user changes the taskbar mode from any surface (tray, settings).</summary>
public sealed record TaskbarModeChanged(Models.TaskbarMode Mode);

/// <summary>A calendar event as published to other components (automation, notifications).</summary>
public sealed record CalendarEventInfo(string Uid, string Title, DateTimeOffset Start, DateTimeOffset End, string? Location, string? JoinUrl);

/// <summary>Published by calendar widgets after each refresh with the upcoming events (next 24 h).</summary>
public sealed record CalendarEventsUpdated(string SourceInstanceId, IReadOnlyList<CalendarEventInfo> Events);

/// <summary>
/// A numeric metric observed by a widget, e.g. "github.stars:owner/repo". Milestone automations watch
/// these and fire once per threshold crossing.
/// </summary>
public sealed record MetricObserved(string MetricId, string DisplayName, double Value, string SourceInstanceId);

/// <summary>Asks the app to open the notes window (widgets cannot create windows themselves).</summary>
public sealed record OpenNotesRequested(string? NoteId);

/// <summary>Asks the app to open its file-search window (Everything widget).</summary>
public sealed record OpenFileSearchRequested(string? Query, int MaxResults, bool MatchPath);

/// <summary>A web link offered by the Bookmarks widget's search window.</summary>
public sealed record BookmarkLink(string Name, string Url, string Folder, string Source);

/// <summary>Asks the app to open the bookmark search window over <paramref name="Links"/>.</summary>
public sealed record OpenBookmarkSearchRequested(string Title, IReadOnlyList<BookmarkLink> Links, string? BrowserPath);
