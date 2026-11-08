using System.Globalization;

namespace SmartDock.Widgets.Common;

public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>1536 → "1.5 KB" (binary multiples, compact for a 40px widget).</summary>
    public static string Bytes(double bytes, int decimals = 1)
    {
        if (double.IsNaN(bytes) || bytes < 0)
        {
            bytes = 0;
        }

        var unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        var digits = unit == 0 ? 0 : (bytes >= 100 ? 0 : decimals);
        return bytes.ToString("F" + digits, CultureInfo.CurrentCulture) + " " + ByteUnits[unit];
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Percent(double value) =>
        Math.Round(Math.Clamp(value, 0, 100)).ToString("F0", CultureInfo.CurrentCulture) + "%";
}

/// <summary>Fixed-size history of samples exposed as immutable snapshots (safe to bind from any thread).</summary>
public sealed class SampleHistory(int capacity)
{
    private readonly Queue<double> _samples = new(capacity);

    public IReadOnlyList<double> Add(double value)
    {
        lock (_samples)
        {
            if (_samples.Count == capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(value);
            return [.. _samples];
        }
    }
}
