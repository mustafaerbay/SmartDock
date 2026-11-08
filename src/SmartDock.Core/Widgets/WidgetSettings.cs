using System.Text.Json;

namespace SmartDock.Core.Widgets;

/// <summary>
/// Typed view over a widget instance's JSON settings bag. Writes go straight into the layout
/// model and raise <see cref="Changed"/> so the layout service can persist them.
/// Secrets must never be stored here (use ISecretStore).
/// </summary>
public sealed class WidgetSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonElement> _values;

    public WidgetSettings(Dictionary<string, JsonElement> backingStore)
    {
        _values = backingStore ?? throw new ArgumentNullException(nameof(backingStore));
    }

    public event EventHandler<string>? Changed;

    public T Get<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var element))
            {
                return defaultValue;
            }

            try
            {
                var value = element.Deserialize<T>(Options);
                return value is null ? defaultValue : value;
            }
            catch (JsonException)
            {
                // A wrong-typed value in a hand-edited file falls back to the default instead of failing the widget.
                return defaultValue;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value, Options);
        }

        Changed?.Invoke(this, key);
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _values.Remove(key);
        }

        if (removed)
        {
            Changed?.Invoke(this, key);
        }

        return removed;
    }
}
