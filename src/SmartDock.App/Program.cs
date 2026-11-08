using System.Diagnostics;
using SmartDock.Infrastructure.Configuration;
using SmartDock.Windows.Interop;
using SmartDock.Windows.Taskbar;

namespace SmartDock.App;

/// <summary>
/// Entry point. Besides the normal UI start it supports headless modes that never load WPF windows:
///   --restore-taskbar   Restore the Windows taskbar from the recovery file and exit (Start-menu "rescue" shortcut).
///   --watchdog PID      Watch the dock process PID; if it exits, or its UI stays hung for ~10 s,
///                       while it has the taskbar hidden, restore the taskbar.
///   --wait-for PID      Wait for PID to exit before starting (used by "Restart Dock").
///   --shutdown          Ask a running SmartDock to exit cleanly (taskbar restored) and wait up to 20 s
///                       for it; used by the installer before upgrading or uninstalling.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = new AppPaths();

        if (args.Contains("--restore-taskbar", StringComparer.OrdinalIgnoreCase))
        {
            TaskbarController.RestoreFromRecoveryFile(paths.TaskbarRecoveryFile, forceShow: true);
            return 0;
        }

        if (args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            return RequestShutdown(TimeSpan.FromSeconds(20));
        }

        if (TryGetPidArg(args, "--watchdog", out var watchedPid))
        {
            RunWatchdog(watchedPid, paths);
            return 0;
        }

        if (TryGetPidArg(args, "--wait-for", out var previousPid))
        {
            WaitForExit(previousPid, TimeSpan.FromSeconds(15));
        }

        var app = new App(paths);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>Signals the running instance and waits until every other SmartDock process has exited.</summary>
    private static int RequestShutdown(TimeSpan timeout)
    {
        if (EventWaitHandle.TryOpenExisting(App.ShutdownEventName, out var signal))
        {
            using (signal)
            {
                signal.Set();
            }
        }

        var self = Environment.ProcessId;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var others = Process.GetProcessesByName("SmartDock").Where(p => p.Id != self).ToList();
            var running = others.Count;
            others.ForEach(p => p.Dispose());
            if (running == 0)
            {
                return 0;
            }

            Thread.Sleep(250);
        }

        return 1; // Still running; the installer then falls back to Windows' "files in use" handling.
    }

    private static void RunWatchdog(int pid, AppPaths paths)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            RestoreIfOwnedBy(pid, paths);
            return;
        }

        using (process)
        {
            var hungPolls = 0;
            while (!process.WaitForExit(2000))
            {
                // A hung UI thread cannot run its own tray "Restore" command, so the watchdog does it.
                hungPolls = HasHungWindow(pid) ? hungPolls + 1 : 0;
                if (hungPolls == 5)
                {
                    RestoreIfOwnedBy(pid, paths);
                }
            }
        }

        RestoreIfOwnedBy(pid, paths);
    }

    private static void RestoreIfOwnedBy(int pid, AppPaths paths)
    {
        // Only act if the recovery record belongs to the process we watched (a newer instance may own it).
        var state = TaskbarController.ReadRecovery(paths.TaskbarRecoveryFile);
        if (state is not null && (state.OwnerProcessId == pid || state.OwnerProcessId == 0))
        {
            TaskbarController.RestoreFromRecoveryFile(paths.TaskbarRecoveryFile, forceShow: true);
        }
    }

    private static bool HasHungWindow(int pid)
    {
        var hung = false;
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)pid && NativeMethods.IsWindowVisible(hwnd) && NativeMethods.IsHungAppWindow(hwnd))
            {
                hung = true;
                return false;
            }

            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return hung;
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                process.WaitForExit();
            }
            else
            {
                process.WaitForExit(timeout);
            }
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryGetPidArg(string[] args, string name, out int pid)
    {
        pid = 0;
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out pid);
    }
}
