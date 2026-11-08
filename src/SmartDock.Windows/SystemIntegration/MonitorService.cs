using System.Runtime.InteropServices;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>A physical display. All rectangles are in physical (device) pixels.</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary, uint Dpi)
{
    public double Scale => Dpi / 96.0;

    public string DisplayLabel => $"{(IsPrimary ? "Primary – " : string.Empty)}{DeviceName.Replace(@"\\.\", string.Empty)} ({Bounds.Width}×{Bounds.Height}, {Math.Round(Scale * 100)}%)";
}

public static class MonitorService
{
    private const int MDT_EFFECTIVE_DPI = 0;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = Query(hMonitor);
            if (info is not null)
            {
                result.Add(info);
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>Resolves the configured monitor, falling back to the primary one when it is disconnected.</summary>
    public static MonitorInfo? Resolve(string? deviceName)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorInfo? FromWindow(IntPtr hwnd)
    {
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return handle == IntPtr.Zero ? null : Query(handle);
    }

    private static MonitorInfo? Query(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
        {
            return null;
        }

        uint dpi = 96;
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                dpi = dpiX;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return new MonitorInfo(hMonitor, mi.szDevice ?? string.Empty, mi.rcMonitor, mi.rcWork, (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, dpi);
    }
}
