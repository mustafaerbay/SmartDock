using System.Windows;
using System.Windows.Interop;
using SmartDock.Windows.Interop;

namespace SmartDock.App.Services;

public static class WindowStyling
{
    /// <summary>
    /// Uses WPF's built-in Windows 11 (Fluent) control theme for regular windows. The dock itself
    /// keeps its own templates. Follows the system light/dark setting.
    /// </summary>
    public static void ApplyFluent(Window window)
    {
#pragma warning disable WPF0001 // ThemeMode was introduced as an evaluation API in .NET 9.
        window.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    }

    /// <summary>
    /// Shows a window and brings it in front of the active app. Needed because the dock never takes
    /// focus (WS_EX_NOACTIVATE): Windows' foreground lock can otherwise leave a newly opened window
    /// behind the maximised app, or only flash it in the (possibly hidden) taskbar.
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var thisThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
            window.Activate();
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        // Fallback if activation was still refused: raise it above normal windows without keeping it topmost.
        window.Topmost = true;
        window.Topmost = false;
    }
}
