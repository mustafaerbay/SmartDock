using Microsoft.Extensions.Logging;
using SmartDock.Core.Models;
using SmartDock.Core.Services;

namespace SmartDock.Infrastructure.Configuration;

public sealed class SettingsService : ISettingsService
{
    private readonly JsonFileStore<DockSettings> _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    private DockSettings _current;

    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        _logger = logger;
        _store = new JsonFileStore<DockSettings>(paths.SettingsFile, () => new DockSettings(), logger);
        var result = _store.Load();
        _current = result.Value.Normalize();
        if (result.Outcome != JsonFileStore<DockSettings>.LoadOutcome.Loaded)
        {
            TrySave(_current);
        }
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public DockSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current.Clone();
            }
        }
    }

    public void Update(Action<DockSettings> mutate)
    {
        DockSettings previous;
        DockSettings next;
        lock (_gate)
        {
            previous = _current.Clone();
            next = _current.Clone();
            mutate(next);
            next.Normalize();
            _current = next;
        }

        TrySave(next);
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next.Clone()));
    }

    private void TrySave(DockSettings settings)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }
}
