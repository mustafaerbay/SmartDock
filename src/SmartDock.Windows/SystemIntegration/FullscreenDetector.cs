using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>Detects a fullscreen foreground window (games, video players, presentations) on a given monitor.</summary>
public static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public static bool IsFullscreenOn(IntPtr monitorHandle, IntPtr ownWindow)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow || fg == NativeMethods.GetShellWindow())
        {
            return false;
        }

        if (ShellClasses.Contains(NativeMethods.GetClassNameOf(fg)))
        {
            return false;
        }

        if (NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONULL) != monitorHandle)
        {
            return false;
        }

        var monitor = MonitorService.FromWindow(fg);
        if (monitor is null || !NativeMethods.GetWindowRect(fg, out var rect))
        {
            return false;
        }

        // A normal maximised window with a caption is not fullscreen, even when the taskbar is auto-hidden
        // and the work area equals the monitor (replacement mode).
        var style = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_STYLE).ToInt64();
        if (NativeMethods.IsZoomed(fg) && (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION)
        {
            return false;
        }

        // Borderless fullscreen windows cover the whole monitor (maximised windows only cover the work area).
        var b = monitor.Bounds;
        return rect.Left <= b.Left && rect.Top <= b.Top && rect.Right >= b.Right && rect.Bottom >= b.Bottom;
    }
}
