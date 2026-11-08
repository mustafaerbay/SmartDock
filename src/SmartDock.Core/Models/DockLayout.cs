using System.Text.Json;

namespace SmartDock.Core.Models;

public enum DockRegion
{
    Center,
    Right,
}

/// <summary>Root of layout.json: pinned apps and the ordered widget instances per region.</summary>
public sealed class DockLayout
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<PinnedApp> PinnedApps { get; set; } = [];

    public List<WidgetInstanceConfig> CenterWidgets { get; set; } = [];

    public List<WidgetInstanceConfig> RightWidgets { get; set; } = [];

    public List<WidgetInstanceConfig> GetRegion(DockRegion region) =>
        region == DockRegion.Center ? CenterWidgets : RightWidgets;

    public (DockRegion Region, int Index)? Find(string instanceId)
    {
        var i = CenterWidgets.FindIndex(w => w.InstanceId == instanceId);
        if (i >= 0)
        {
            return (DockRegion.Center, i);
        }

        i = RightWidgets.FindIndex(w => w.InstanceId == instanceId);
        return i >= 0 ? (DockRegion.Right, i) : null;
    }

    /// <summary>Moves an existing instance to <paramref name="index"/> in <paramref name="region"/>; index is clamped.</summary>
    public bool Move(string instanceId, DockRegion region, int index)
    {
        var found = Find(instanceId);
        if (found is null)
        {
            return false;
        }

        var source = GetRegion(found.Value.Region);
        var item = source[found.Value.Index];
        source.RemoveAt(found.Value.Index);

        var target = GetRegion(region);
        target.Insert(Math.Clamp(index, 0, target.Count), item);
        return true;
    }

    public bool Remove(string instanceId)
    {
        var found = Find(instanceId);
        if (found is null)
        {
            return false;
        }

        GetRegion(found.Value.Region).RemoveAt(found.Value.Index);
        return true;
    }

    /// <summary>Repairs nulls, duplicate instance ids and pins without a path after deserialisation.</summary>
    public DockLayout Normalize(Func<string, bool> isKnownWidgetType)
    {
        PinnedApps = (PinnedApps ?? [])
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path))
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        CenterWidgets = Clean(CenterWidgets);
        RightWidgets = Clean(RightWidgets);
        SchemaVersion = CurrentSchemaVersion;
        return this;

        List<WidgetInstanceConfig> Clean(List<WidgetInstanceConfig>? list) =>
            (list ?? [])
                .Where(w => w is not null && !string.IsNullOrWhiteSpace(w.TypeId) && isKnownWidgetType(w.TypeId))
                .Select(w =>
                {
                    if (string.IsNullOrWhiteSpace(w.InstanceId) || !seen.Add(w.InstanceId))
                    {
                        w.InstanceId = NewInstanceId(w.TypeId);
                        seen.Add(w.InstanceId);
                    }

                    w.Settings ??= [];
                    return w;
                })
                .ToList();
    }

    /// <summary>Returns e.g. "cpu-3f9a1c2e".</summary>
    public static string NewInstanceId(string typeId) => $"{typeId}-{Guid.NewGuid().ToString("N")[..8]}";

    public static DockLayout CreateDefault() => new()
    {
        PinnedApps =
        [
            new PinnedApp
            {
                Id = "explorer",
                DisplayName = "File Explorer",
                Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            },
        ],
        CenterWidgets =
        [
            new() { InstanceId = "media-default", TypeId = "media" },
            new() { InstanceId = "cpu-default", TypeId = "cpu" },
            new() { InstanceId = "memory-default", TypeId = "memory" },
            new() { InstanceId = "disk-default", TypeId = "disk" },
        ],
        RightWidgets =
        [
            new() { InstanceId = "network-default", TypeId = "network" },
            new() { InstanceId = "volume-default", TypeId = "volume" },
            new() { InstanceId = "battery-default", TypeId = "battery" },
            new() { InstanceId = "clock-default", TypeId = "clock" },
        ],
    };
}

public sealed class PinnedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Executable, .lnk shortcut or shell URI (e.g. ms-settings:).</summary>
    public string Path { get; set; } = string.Empty;

    public string? Arguments { get; set; }
}

public sealed class WidgetInstanceConfig
{
    public string InstanceId { get; set; } = string.Empty;

    public string TypeId { get; set; } = string.Empty;

    /// <summary>Optional fixed width in DIPs; null lets the widget size to its content.</summary>
    public double? Width { get; set; }

    public Dictionary<string, JsonElement> Settings { get; set; } = [];
}
