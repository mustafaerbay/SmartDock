using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Interfaces;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>
/// Clipboard text listener built on AddClipboardFormatListener (event driven, no polling). It runs only
/// while a Clipboard widget has history explicitly enabled, keeps nothing on disk, and skips content
/// that the source app marks as private (password managers set these formats).
/// </summary>
public sealed class ClipboardService(ILogger<ClipboardService> logger) : IClipboardService, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxTextLength = 20_000;

    // Presence alone marks content private.
    private static readonly string[] PrivacyFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    ];

    // DWORD formats: private only when the value is 0.
    private static readonly string[] PrivacyDwordFormats =
    [
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private HwndSource? _window;
    private string? _ownText;

    public event EventHandler<ClipboardEntry>? TextCopied;

    public bool IsListening => _window is not null;

    public bool Start() => _dispatcher.Invoke(() =>
    {
        if (_window is not null)
        {
            return true;
        }

        // Message-only window (parent HWND_MESSAGE) just to receive WM_CLIPBOARDUPDATE.
        var parameters = new HwndSourceParameters("SmartDock clipboard listener")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        };
        var window = new HwndSource(parameters);
        window.AddHook(WndProc);
        if (!AddClipboardFormatListener(window.Handle))
        {
            logger.LogWarning("AddClipboardFormatListener failed ({Error})", Marshal.GetLastWin32Error());
            window.Dispose();
            return false;
        }

        _window = window;
        logger.LogInformation("Clipboard history enabled");
        return true;
    });

    public void Stop() => _dispatcher.Invoke(() =>
    {
        if (_window is null)
        {
            return;
        }

        RemoveClipboardFormatListener(_window.Handle);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
        logger.LogInformation("Clipboard history disabled");
    });

    public void SetText(string text) => _dispatcher.Invoke(() =>
    {
        _ownText = text;
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "Clipboard busy");
        }
    });

    public void Dispose() => Stop();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            ReadClipboard();
        }

        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null || !data.GetDataPresent(DataFormats.UnicodeText))
                {
                    return;
                }

                if (PrivacyFormats.Any(data.GetDataPresent) || PrivacyDwordFormats.Any(f => IsZeroDword(data, f)))
                {
                    return; // Marked private by the source app.
                }

                if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
                {
                    return;
                }

                if (text == _ownText)
                {
                    _ownText = null;
                    return;
                }

                TextCopied?.Invoke(this, new ClipboardEntry(Guid.NewGuid().ToString("N"), text, DateTimeOffset.Now));
                return;
            }
            catch (COMException)
            {
                // Another app holds the clipboard open; try again shortly.
                Thread.Sleep(40);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private static bool IsZeroDword(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format))
        {
            return false;
        }

        try
        {
            return data.GetData(format) switch
            {
                MemoryStream ms when ms.Length >= 4 => BitConverter.ToInt32(ms.ToArray(), 0) == 0,
                byte[] { Length: >= 4 } bytes => BitConverter.ToInt32(bytes, 0) == 0,
                int value => value == 0,
                _ => true, // Unreadable: err on the side of privacy.
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException or ArgumentException)
        {
            return true;
        }
    }
}
