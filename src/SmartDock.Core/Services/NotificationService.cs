namespace SmartDock.Core.Services;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record DockNotification(
    string Id,
    string Title,
    string Message,
    NotificationSeverity Severity,
    DateTimeOffset Timestamp,
    string? SourceId = null,
    string? ActionUrl = null);

public interface INotificationService
{
    event EventHandler<DockNotification>? Posted;

    event EventHandler? HistoryChanged;

    IReadOnlyList<DockNotification> History { get; }

    /// <summary>
    /// Posts a notification. When <paramref name="dedupeKey"/> is set, the same key is suppressed
    /// for <paramref name="dedupeWindow"/> (default 10 minutes) to avoid duplicate alerts.
    /// Returns null when suppressed.
    /// </summary>
    DockNotification? Post(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        string? sourceId = null,
        string? actionUrl = null,
        string? dedupeKey = null,
        TimeSpan? dedupeWindow = null);

    void Dismiss(string id);

    void Clear();
}

public sealed class NotificationService(TimeProvider? timeProvider = null) : INotificationService
{
    public const int MaxHistory = 100;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<DockNotification> _history = [];
    private readonly Dictionary<string, DateTimeOffset> _dedupe = new(StringComparer.Ordinal);

    public event EventHandler<DockNotification>? Posted;

    public event EventHandler? HistoryChanged;

    public IReadOnlyList<DockNotification> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    public DockNotification? Post(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        string? sourceId = null,
        string? actionUrl = null,
        string? dedupeKey = null,
        TimeSpan? dedupeWindow = null)
    {
        var now = _time.GetUtcNow();
        DockNotification notification;
        lock (_gate)
        {
            if (dedupeKey is not null)
            {
                if (_dedupe.TryGetValue(dedupeKey, out var last) && now - last < (dedupeWindow ?? TimeSpan.FromMinutes(10)))
                {
                    return null;
                }

                _dedupe[dedupeKey] = now;
            }

            notification = new DockNotification(Guid.NewGuid().ToString("N"), title, message, severity, _time.GetLocalNow(), sourceId, actionUrl);
            _history.Insert(0, notification);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            }
        }

        Posted?.Invoke(this, notification);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return notification;
    }

    public void Dismiss(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _history.RemoveAll(n => n.Id == id) > 0;
        }

        if (removed)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }
}
