using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.Taskbar;

/// <summary>
/// Hides and restores the Windows taskbar for Replacement mode without touching system files.
///
/// Technique (see docs/adr/0002-taskbar-replacement.md):
///  1. Record the taskbar's current AppBar state (auto-hide on/off) in a recovery file.
///  2. Switch the taskbar to auto-hide via ABM_SETSTATE so Windows releases the reserved work area.
///  3. Hide every taskbar window (Shell_TrayWnd + Shell_SecondaryTrayWnd for other monitors).
///  4. Re-apply when Explorer restarts (TaskbarCreated) or when Explorer re-shows the taskbar.
/// Restore reverses 3 then 2 from the recovery file and deletes it. The file is also read by
/// "SmartDock.exe --restore-taskbar" and by the watchdog process after a crash.
///
/// All members are safe to call from any thread: they only use Win32 calls and file IO.
/// </summary>
public sealed class TaskbarController
{
    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    private readonly string _recoveryFile;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private volatile bool _isHidden;

    public TaskbarController(string recoveryFile, ILogger<TaskbarController>? logger = null)
    {
        _recoveryFile = recoveryFile;
        _logger = logger ?? NullLogger<TaskbarController>.Instance;
    }

    public bool IsHidden => _isHidden;

    public static bool TaskbarExists => NativeMethods.FindWindow(PrimaryTrayClass, null) != IntPtr.Zero;

    /// <summary>Hides the taskbar. Returns false (and leaves it visible) when the result cannot be verified.</summary>
    public bool Hide()
    {
        lock (_gate)
        {
            var primary = NativeMethods.FindWindow(PrimaryTrayClass, null);
            if (primary == IntPtr.Zero)
            {
                _logger.LogWarning("Taskbar window not found (Explorer not running?); staying in companion mode");
                return false;
            }

            if (!_isHidden)
            {
                // Only capture the original state the first time, so re-applying never records "auto-hide" as original.
                var existing = ReadRecovery(_recoveryFile);
                var originalState = existing?.OriginalAppBarState ?? GetAppBarState(primary);
                if (!WriteRecovery(new TaskbarRecoveryState(Environment.ProcessId, originalState, DateTimeOffset.Now)))
                {
                    // Without a recovery record a crash could leave the desktop without a taskbar: refuse.
                    return false;
                }
            }

            SetAppBarState(primary, NativeMethods.ABS_AUTOHIDE | NativeMethods.ABS_ALWAYSONTOP);
            foreach (var hwnd in EnumerateTaskbars())
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }

            _isHidden = true;

            if (NativeMethods.IsWindowVisible(primary))
            {
                _logger.LogWarning("Taskbar is still visible after hiding; restoring and falling back to companion mode");
                RestoreCore();
                return false;
            }

            _logger.LogInformation("Windows taskbar hidden (replacement mode)");
            return true;
        }
    }

    /// <summary>Restores the taskbar if this process (or a crashed previous run) hid it.</summary>
    public void Restore()
    {
        lock (_gate)
        {
            RestoreCore();
        }
    }

    /// <summary>
    /// Called on TaskbarCreated (Explorer restarted) and from the periodic enforcement check:
    /// re-hides the taskbar if Explorer showed it again while we are in replacement mode.
    /// </summary>
    /// <returns>False when the taskbar could not be hidden again (it is then restored).</returns>
    public bool Reapply()
    {
        lock (_gate)
        {
            if (!_isHidden)
            {
                // Nothing hidden by us (e.g. restored from the tray); the caller decides on the mode.
                return true;
            }

            if (!EnumerateTaskbars().Any(NativeMethods.IsWindowVisible))
            {
                return true;
            }

            _logger.LogDebug("Taskbar became visible again; re-hiding");
            return Hide();
        }
    }

    /// <summary>
    /// Restores the taskbar using only the recovery file. Safe to call from a different process
    /// (watchdog, --restore-taskbar) and when no recovery file exists (then it just shows the taskbar).
    /// </summary>
    public static void RestoreFromRecoveryFile(string recoveryFile, bool forceShow = true)
    {
        var state = ReadRecovery(recoveryFile);
        var primary = NativeMethods.FindWindow(PrimaryTrayClass, null);
        if (primary != IntPtr.Zero)
        {
            if (state is not null)
            {
                SetAppBarState(primary, state.OriginalAppBarState);
            }

            if (state is not null || forceShow)
            {
                foreach (var hwnd in EnumerateTaskbars())
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNA);
                }
            }
        }

        if (state is not null && primary != IntPtr.Zero)
        {
            TryDelete(recoveryFile);
        }
    }

    public static TaskbarRecoveryState? ReadRecovery(string recoveryFile)
    {
        try
        {
            if (!File.Exists(recoveryFile))
            {
                return null;
            }

            return JsonSerializer.Deserialize<TaskbarRecoveryState>(File.ReadAllText(recoveryFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged recovery file still means "we hid it": restore with the Windows default (always visible).
            return new TaskbarRecoveryState(0, NativeMethods.ABS_ALWAYSONTOP, DateTimeOffset.MinValue);
        }
    }

    private static IEnumerable<IntPtr> EnumerateTaskbars()
    {
        var primary = NativeMethods.FindWindow(PrimaryTrayClass, null);
        if (primary != IntPtr.Zero)
        {
            yield return primary;
        }

        var secondary = IntPtr.Zero;
        for (var guard = 0; guard < 16; guard++)
        {
            secondary = NativeMethods.FindWindowEx(IntPtr.Zero, secondary, SecondaryTrayClass, null);
            if (secondary == IntPtr.Zero)
            {
                yield break;
            }

            yield return secondary;
        }
    }

    private static int GetAppBarState(IntPtr taskbar)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
        return (int)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data).ToUInt32();
    }

    private static void SetAppBarState(IntPtr taskbar, int state)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar, lParam = new IntPtr(state) };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref data);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RestoreCore()
    {
        RestoreFromRecoveryFile(_recoveryFile, forceShow: _isHidden);
        if (_isHidden)
        {
            _logger.LogInformation("Windows taskbar restored");
        }

        _isHidden = false;
    }

    private bool WriteRecovery(TaskbarRecoveryState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recoveryFile)!);
            File.WriteAllText(_recoveryFile, JsonSerializer.Serialize(state));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write taskbar recovery file");
            return false;
        }
    }
}

public sealed record TaskbarRecoveryState(int OwnerProcessId, int OriginalAppBarState, DateTimeOffset HiddenAt);
