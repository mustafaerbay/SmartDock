using System.Diagnostics;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.WindowManager;

/// <summary>A top-level window that would appear on the Windows taskbar.</summary>
public sealed record AppWindowInfo(IntPtr Handle, string Title, uint ProcessId, string? ProcessPath, bool IsMinimized)
{
    /// <summary>
    /// Grouping key for the dock. Packaged (UWP) apps all run inside ApplicationFrameHost.exe, so
    /// grouping those by path would merge unrelated apps; they are grouped per window instead.
    /// </summary>
    public string GroupKey =>
        ProcessPath is null || ProcessPath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            ? $"hwnd:{Handle}"
            : ProcessPath.ToUpperInvariant();
}

public static class WindowTracker
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Enumerates taskbar-eligible windows in z-order (top first), excluding this process.</summary>
    public static IReadOnlyList<AppWindowInfo> GetAppWindows()
    {
        var ownPid = (uint)Environment.ProcessId;
        var result = new List<AppWindowInfo>();
        var pathCache = new Dictionary<uint, string?>();

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownPid)
            {
                return true;
            }

            if (!pathCache.TryGetValue(pid, out var path))
            {
                path = NativeMethods.GetProcessPath(pid);
                pathCache[pid] = path;
            }

            result.Add(new AppWindowInfo(hwnd, NativeMethods.GetWindowTitle(hwnd), pid, path, NativeMethods.IsIconic(hwnd)));
            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>Approximates the Alt-Tab/taskbar rules: visible, unowned, not a tool window, not cloaked, has a title.</summary>
    public static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var hasOwner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero;
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (hasOwner && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        if (NativeMethods.GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        if (NativeMethods.IsCloaked(hwnd))
        {
            // Windows on other virtual desktops and suspended UWP frames are cloaked.
            return false;
        }

        return !IgnoredClasses.Contains(NativeMethods.GetClassNameOf(hwnd));
    }

    /// <summary>Brings a window to the foreground, restoring it first when minimised.</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);
    }

    public static void Minimize(IntPtr hwnd) => NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);

    /// <summary>Asks the window to close (same as clicking X); the app may still prompt to save.</summary>
    public static void RequestClose(IntPtr hwnd) => NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static IntPtr Foreground => NativeMethods.GetForegroundWindow();

    /// <summary>Opens the Start menu by synthesising the Windows key, so Explorer keeps owning Start.</summary>
    public static void OpenStartMenu()
    {
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Launches an executable, shortcut, folder or URI through the shell (no command interpreter).</summary>
    public static void Launch(string path, string? arguments = null)
    {
        var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(path))
        {
            UseShellExecute = true,
        };
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            psi.Arguments = arguments;
        }

        Process.Start(psi)?.Dispose();
    }
}
