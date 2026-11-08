using System.Runtime.InteropServices;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.WindowManager;

/// <summary>
/// A live DWM thumbnail of another window, drawn by the compositor into a rectangle of one of our
/// windows (the same mechanism the Windows taskbar uses for its previews). Nothing is captured or
/// copied: DWM renders the source window directly and it stays live while registered.
/// The destination must be a top-level, non-layered window (WPF: AllowsTransparency = false).
/// </summary>
public sealed class DwmThumbnail : IDisposable
{
    private const int DWM_TNP_RECTDESTINATION = 0x01;
    private const int DWM_TNP_OPACITY = 0x04;
    private const int DWM_TNP_VISIBLE = 0x08;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    private IntPtr _handle;

    private DwmThumbnail(IntPtr handle, IntPtr source)
    {
        _handle = handle;
        Source = source;
    }

    public IntPtr Source { get; }

    /// <summary>Registers a thumbnail of <paramref name="source"/> on <paramref name="destination"/>; null if DWM refuses.</summary>
    public static DwmThumbnail? Create(IntPtr destination, IntPtr source)
    {
        if (destination == IntPtr.Zero || source == IntPtr.Zero || !NativeMethods.IsWindow(source))
        {
            return null;
        }

        return DwmRegisterThumbnail(destination, source, out var handle) == 0 && handle != IntPtr.Zero
            ? new DwmThumbnail(handle, source)
            : null;
    }

    /// <summary>Size of the source window in physical pixels (0×0 if unknown).</summary>
    public (int Width, int Height) SourceSize =>
        _handle != IntPtr.Zero && DwmQueryThumbnailSourceSize(_handle, out var size) == 0 ? (size.cx, size.cy) : (0, 0);

    /// <summary>Places the thumbnail at a rectangle of the destination's client area, in physical pixels.</summary>
    public bool Update(int left, int top, int right, int bottom, bool visible = true)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            opacity = 255,
            fVisible = visible,
            fSourceClientAreaOnly = false,
        };
        return DwmUpdateThumbnailProperties(_handle, ref props) == 0;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _ = DwmUnregisterThumbnail(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PSIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out PSIZE pSize);
}
