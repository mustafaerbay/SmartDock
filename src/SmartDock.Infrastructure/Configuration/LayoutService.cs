using Microsoft.Extensions.Logging;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.Infrastructure.Configuration;

public sealed class LayoutService : ILayoutService, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly JsonFileStore<DockLayout> _store;
    private readonly IWidgetRegistry _registry;
    private readonly ILogger<LayoutService> _logger;
    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;

    public LayoutService(AppPaths paths, IWidgetRegistry registry, ILogger<LayoutService> logger)
    {
        _registry = registry;
        _logger = logger;
        _store = new JsonFileStore<DockLayout>(paths.LayoutFile, DockLayout.CreateDefault, logger);
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        var result = _store.Load();
        Current = result.Value.Normalize(IsKnown);
        if (result.Outcome != JsonFileStore<DockLayout>.LoadOutcome.Loaded)
        {
            _dirty = true;
            Flush();
        }
    }

    public DockLayout Current { get; private set; }

    public void Mutate(Action<DockLayout> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            _dirty = true;
        }

        ScheduleSave();
    }

    public void ScheduleSave()
    {
        lock (_gate)
        {
            _dirty = true;
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                _store.Save(Current);
                _dirty = false;
            }
            catch (InvalidOperationException ex)
            {
                // A widget settings bag changed during serialisation; try again shortly.
                _logger.LogDebug(ex, "Layout changed while saving; retrying");
                _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to save layout");
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Current = DockLayout.CreateDefault().Normalize(IsKnown);
            _dirty = true;
        }

        Flush();
    }

    public void Dispose()
    {
        Flush();
        _saveTimer.Dispose();
    }

    private bool IsKnown(string typeId) => _registry.TryGet(typeId, out _);
}
