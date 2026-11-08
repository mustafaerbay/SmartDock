using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartDock.Core.Widgets;

/// <summary>
/// Owns widget lifecycles and refreshes them from ONE shared timer instead of a timer per widget.
/// Each refresh runs off the UI thread with a timeout; failures are isolated per widget and retried
/// with exponential backoff so one broken widget can neither crash the dock nor spin in a loop.
/// </summary>
public sealed class WidgetHost : IAsyncDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public WidgetHost(ILogger<WidgetHost>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<WidgetHost>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    /// <summary>Initialises the widget (isolated) and schedules an immediate first refresh.</summary>
    public async Task<bool> AddAsync(IDockWidget widget, WidgetContext context, CancellationToken cancellationToken = default)
    {
        context.RequestRefresh = () => RequestRefresh(context.InstanceId);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            cts.CancelAfter(RefreshTimeout);
            await widget.InitializeAsync(context, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Widget {InstanceId} failed to initialise", context.InstanceId);
            widget.ReportFailure("Failed to start: " + ex.Message);

            // Keep the slot so the user sees the error and the widget can be removed; retry later.
            lock (_gate)
            {
                _slots[context.InstanceId] = new Slot(widget) { NextDue = _time.GetUtcNow() + MaxBackoff, Failures = 1 };
            }

            return false;
        }

        lock (_gate)
        {
            _slots[context.InstanceId] = new Slot(widget) { NextDue = _time.GetUtcNow() };
        }

        return true;
    }

    public async Task RemoveAsync(string instanceId)
    {
        Slot? slot;
        lock (_gate)
        {
            if (!_slots.Remove(instanceId, out slot))
            {
                return;
            }
        }

        slot.Cancel();
        try
        {
            await slot.Widget.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Widget {InstanceId} threw during dispose", instanceId);
        }
    }

    public void RequestRefresh(string instanceId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(instanceId, out var slot))
            {
                slot.NextDue = _time.GetUtcNow();
                slot.RefreshRequested = true;
            }
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>Starts every due refresh and returns a task that completes when those refreshes finish (used by tests).</summary>
    public Task TickAsync()
    {
        var now = _time.GetUtcNow();
        List<Task>? started = null;
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.IsRunning || slot.NextDue > now)
                {
                    continue;
                }

                var interval = slot.Widget.RefreshInterval;
                if (slot.Failures == 0 && interval == Timeout.InfiniteTimeSpan && slot.HasRefreshedOnce && !slot.RefreshRequested)
                {
                    continue;
                }

                slot.IsRunning = true;
                slot.RefreshRequested = false;
                (started ??= []).Add(RunRefreshAsync(slot));
            }
        }

        return started is null ? Task.CompletedTask : Task.WhenAll(started);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        string[] ids;
        lock (_gate)
        {
            ids = [.. _slots.Keys];
        }

        foreach (var id in ids)
        {
            await RemoveAsync(id).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    internal static TimeSpan ComputeBackoff(TimeSpan interval, int failures)
    {
        var baseInterval = interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(5) : interval;
        var exponent = Math.Min(failures, 10);
        var ticks = baseInterval.Ticks * (1L << exponent);
        return ticks <= 0 || ticks > MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks(ticks);
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                // Deliberately not awaited: a slow widget must not delay the others' ticks.
                _ = TickAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunRefreshAsync(Slot slot)
    {
        var widget = slot.Widget;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cts.CancelAfter(RefreshTimeout);
        slot.SetCurrent(cts);
        try
        {
            // Hop to the thread pool so widget code never runs on the caller's (possibly UI) thread.
            await Task.Run(() => widget.RefreshAsync(cts.Token), cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                slot.Failures = 0;
                slot.HasRefreshedOnce = true;
                var interval = widget.RefreshInterval;
                slot.NextDue = interval == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _time.GetUtcNow() + (interval < TickInterval ? TickInterval : interval);
                if (slot.RefreshRequested)
                {
                    slot.NextDue = _time.GetUtcNow(); // A request arrived mid-refresh: honour it on the next tick.
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || slot.IsCancelled)
        {
            // Shutting down or the widget was removed.
        }
        catch (Exception ex)
        {
            TimeSpan backoff;
            int failures;
            lock (_gate)
            {
                failures = ++slot.Failures;
                backoff = ComputeBackoff(widget.RefreshInterval, failures);
                slot.NextDue = slot.RefreshRequested ? _time.GetUtcNow() : _time.GetUtcNow() + backoff;
            }

            var message = ex is OperationCanceledException ? "Timed out" : ex.Message;
            _logger.LogWarning(ex, "Widget {InstanceId} refresh failed ({Failures}x); retrying in {Backoff}", widget.InstanceId, failures, backoff);
            try
            {
                widget.ReportFailure(message);
            }
            catch (Exception reportEx)
            {
                _logger.LogError(reportEx, "Widget {InstanceId} failed to report its failure", widget.InstanceId);
            }
        }
        finally
        {
            slot.SetCurrent(null);
            cts.Dispose();
            lock (_gate)
            {
                slot.IsRunning = false;
            }
        }
    }

    private sealed class Slot(IDockWidget widget)
    {
        private CancellationTokenSource? _current;
        private volatile bool _cancelled;

        public IDockWidget Widget { get; } = widget;

        public DateTimeOffset NextDue { get; set; }

        public int Failures { get; set; }

        public bool IsRunning { get; set; }

        public bool HasRefreshedOnce { get; set; }

        /// <summary>Set by <see cref="RequestRefresh"/> so event-driven widgets still refresh on demand.</summary>
        public bool RefreshRequested { get; set; }

        public bool IsCancelled => _cancelled;

        public void SetCurrent(CancellationTokenSource? cts) => Volatile.Write(ref _current, cts);

        public void Cancel()
        {
            _cancelled = true;
            try
            {
                Volatile.Read(ref _current)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
