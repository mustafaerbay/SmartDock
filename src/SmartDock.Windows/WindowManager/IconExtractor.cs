using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.WindowManager;

/// <summary>Extracts frozen, cacheable WPF images for executables and windows.</summary>
public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, ImageSource?> FileCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path);
        return FileCache.GetOrAdd(expanded, static p =>
        {
            // URIs such as ms-settings: have no file icon.
            if (p.Contains(':') && !System.IO.Path.IsPathRooted(p))
            {
                return null;
            }

            var info = new SHFILEINFO();
            var result = NativeMethods.SHGetFileInfo(p, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return FromHIcon(info.hIcon);
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        });
    }

    /// <summary>Asks the window for its icon. The returned handle belongs to the window, so it is not destroyed.</summary>
    public static ImageSource? ForWindow(IntPtr hwnd)
    {
        var icon = SendGetIcon(hwnd, NativeMethods.ICON_BIG);
        if (icon == IntPtr.Zero)
        {
            icon = SendGetIcon(hwnd, NativeMethods.ICON_SMALL2);
        }

        if (icon == IntPtr.Zero)
        {
            icon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
        }

        if (icon == IntPtr.Zero)
        {
            icon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
        }

        return icon == IntPtr.Zero ? null : FromHIcon(icon);
    }

    private static IntPtr SendGetIcon(IntPtr hwnd, int which)
    {
        // Timeout guards against hung applications blocking the dock.
        NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, new IntPtr(which), IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 100, out var result);
        return result;
    }

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }
}
