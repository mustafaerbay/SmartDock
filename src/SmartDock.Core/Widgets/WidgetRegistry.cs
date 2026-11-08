namespace SmartDock.Core.Widgets;

public interface IWidgetRegistry
{
    IReadOnlyList<WidgetDescriptor> All { get; }

    bool TryGet(string typeId, out WidgetDescriptor descriptor);
}

/// <summary>
/// Holds every widget type available to the dock. Built-in widgets are registered through DI as
/// <see cref="WidgetDescriptor"/> singletons. External widgets are intentionally not loaded from disk:
/// see docs/adr/0004-widget-extensibility.md for the planned signed-package model.
/// </summary>
public sealed class WidgetRegistry : IWidgetRegistry
{
    private readonly Dictionary<string, WidgetDescriptor> _byId;

    public WidgetRegistry(IEnumerable<WidgetDescriptor> descriptors)
    {
        _byId = new Dictionary<string, WidgetDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors)
        {
            if (!_byId.TryAdd(d.TypeId, d))
            {
                throw new InvalidOperationException($"Duplicate widget type id '{d.TypeId}'.");
            }
        }

        All = [.. _byId.Values.OrderBy(d => d.Category).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public IReadOnlyList<WidgetDescriptor> All { get; }

    public bool TryGet(string typeId, out WidgetDescriptor descriptor) =>
        _byId.TryGetValue(typeId, out descriptor!);
}
