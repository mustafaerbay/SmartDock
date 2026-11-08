using SmartDock.Core.Models;

namespace SmartDock.Core.Services;

public interface ISettingsService
{
    /// <summary>A snapshot; mutate through <see cref="Update"/>.</summary>
    DockSettings Current { get; }

    /// <summary>Raised (on the caller's thread) after a successful update with old and new snapshots.</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    void Update(Action<DockSettings> mutate);
}

public sealed class SettingsChangedEventArgs(DockSettings previous, DockSettings current) : EventArgs
{
    public DockSettings Previous { get; } = previous;

    public DockSettings Current { get; } = current;
}

public interface ILayoutService
{
    /// <summary>The live layout model. Mutate only through <see cref="Mutate"/> so changes are persisted.</summary>
    DockLayout Current { get; }

    void Mutate(Action<DockLayout> mutate);

    /// <summary>Schedules a debounced save (used for widget settings changes).</summary>
    void ScheduleSave();

    /// <summary>Writes pending changes immediately (call on exit).</summary>
    void Flush();

    /// <summary>Replaces the layout with the default one and saves.</summary>
    void Reset();
}

/// <summary>Stores secrets (API tokens) encrypted for the current Windows user. Never in JSON.</summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string? value);
}
