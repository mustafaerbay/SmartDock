using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>
/// Registers the dock as a Windows "application desktop toolbar" (AppBar) so the shell shrinks the
/// work area by the dock's height: maximised windows then stop at the dock instead of sliding under it.
/// This is the same documented mechanism the Windows taskbar uses. It is not persistent: the reservation
/// is removed on exit (ABM_REMOVE), and the shell drops it when the window is destroyed or Explorer restarts.
/// </summary>
public sealed class AppBarRegistration
{
    private readonly IntPtr _hwnd;
    private BandKey? _last;
    private RECT _lastBand;

    public AppBarRegistration(IntPtr hwnd)
    {
        _hwnd = hwnd;
        CallbackMessage = NativeMethods.RegisterWindowMessage("SmartDock.AppBarNotify");
    }

    /// <summary>Window message the shell sends with ABN_* notifications.</summary>
    public uint CallbackMessage { get; }

    public bool IsRegistered { get; private set; }

    /// <summary>
    /// Reserves a band of <paramref name="thickness"/> physical pixels along the top or bottom of
    /// <paramref name="monitorBounds"/> (shifted by the shell to avoid other app bars such as the taskbar)
    /// and returns the band actually granted, or null if registration failed.
    /// </summary>
    public RECT? Reserve(RECT monitorBounds, bool top, int thickness, bool force = false)
    {
        if (!IsRegistered)
        {
            var data = NewData();
            data.uCallbackMessage = CallbackMessage;
            IsRegistered = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) != UIntPtr.Zero;
            if (!IsRegistered)
            {
                return null;
            }

            _last = null;
        }

        // Re-sending an identical position makes the shell broadcast a work-area change, which would make
        // the dock reposition again; skip it unless the shell told us other bars moved.
        var key = new BandKey(monitorBounds, top, thickness);
        if (!force && _last is { } last && last.Equals(key))
        {
            return _lastBand;
        }

        var bar = NewData();
        bar.uEdge = top ? NativeMethods.ABE_TOP : NativeMethods.ABE_BOTTOM;
        bar.rc = monitorBounds;
        Clamp(ref bar.rc, top, thickness);
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref bar);
        Clamp(ref bar.rc, top, thickness);
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref bar);

        // Tell the shell our position settled so it re-applies the work area to other windows.
        var moved = NewData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_WINDOWPOSCHANGED, ref moved);

        _last = key;
        _lastBand = bar.rc;
        return bar.rc;
    }

    public void Release()
    {
        if (!IsRegistered)
        {
            return;
        }

        var data = NewData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        IsRegistered = false;
        _last = null;
    }

    private static void Clamp(ref RECT rc, bool top, int thickness)
    {
        if (top)
        {
            rc.Bottom = rc.Top + thickness;
        }
        else
        {
            rc.Top = rc.Bottom - thickness;
        }
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
    };

    private readonly record struct BandKey(RECT Bounds, bool Top, int Thickness);

    /// <summary>
    /// Re-fits windows that are maximised on <paramref name="monitor"/> after the reserved band changed.
    /// Windows moves maximised windows when the work area shrinks, but doesn't always grow them back when it
    /// gets bigger again (e.g. the dock goes from three rows back to two). A window counts as "maximised to the
    /// old work area" when its rectangle is the old work area plus the usual invisible frame (≤ 16 px); it then
    /// gets the new work area plus the same frame. Windows that already fit, belong to elevated processes
    /// (UIPI blocks the move) or live on another monitor are left alone.
    /// </summary>
    public static int RefitMaximizedWindows(IntPtr monitor, RECT oldWork, RECT newWork, IntPtr except)
    {
        if (oldWork.Equals(newWork))
        {
            return 0;
        }

        var moved = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == except || !NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.IsZoomed(hwnd)
                || NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */) != monitor
                || !NativeMethods.GetWindowRect(hwnd, out var r))
            {
                return true;
            }

            int left = oldWork.Left - r.Left, top = oldWork.Top - r.Top, right = r.Right - oldWork.Right, bottom = r.Bottom - oldWork.Bottom;
            if (left is < 0 or > 16 || top is < 0 or > 16 || right is < 0 or > 16 || bottom is < 0 or > 16)
            {
                return true; // not maximised to the old work area (or already adjusted by Windows)
            }

            var x = newWork.Left - left;
            var y = newWork.Top - top;
            var w = (newWork.Right + right) - x;
            var h = (newWork.Bottom + bottom) - y;
            if (NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS))
            {
                moved++;
            }

            return true;
        }, IntPtr.Zero);
        return moved;
    }

    /// <summary>Current work area of a monitor (after the shell applied app-bar changes).</summary>
    public static RECT? WorkAreaOf(IntPtr monitor)
    {
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcWork : null;
    }
}
