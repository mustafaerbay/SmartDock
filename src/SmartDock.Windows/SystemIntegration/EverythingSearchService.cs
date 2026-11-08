using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Interfaces;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>
/// Instant file search through voidtools Everything's documented IPC (the same WM_COPYDATA protocol the
/// official SDK wraps), so no SDK DLL has to be shipped or installed. Everything must be running; it
/// answers by sending WM_COPYDATA back to a message-only window owned by this service.
/// </summary>
/// <remarks>
/// Protocol (everything_ipc.h, structs packed to 1 byte):
/// query  = { DWORD reply_hwnd; DWORD reply_copydata_message; DWORD search_flags; DWORD offset;
///            DWORD max_results; WCHAR search_string[] } sent with dwData = EVERYTHING_IPC_COPYDATAQUERYW (2);
/// reply  = { DWORD totfolders, totfiles, totitems, numfolders, numfiles, numitems, offset;
///            { DWORD flags, filename_offset, path_offset } items[numitems] } with offsets from the reply start.
/// </remarks>
public sealed class EverythingSearchService : IFileSearchService, IDisposable
{
    private const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const string IpcWindowClass15 = "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)";
    private const int WM_COPYDATA = 0x004A;
    private const int CopyDataQueryW = 2;
    private const uint MatchPath = 0x00000004;
    private const uint ItemFolder = 0x00000001;
    private const int QueryHeaderSize = 20;
    private const int ListHeaderSize = 28;
    private const int ItemSize = 12;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<EverythingSearchService> _logger;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FileSearchResult>> _pending = new();
    private HwndSource? _replyWindow;
    private uint _nextReplyId = 0x534D0000; // "SM" prefix; any value is fine, it just has to be unique per query.
    private string? _lastKnownExecutable;

    public EverythingSearchService(ILogger<EverythingSearchService> logger) => _logger = logger;

    public string ProviderName => "Everything";

    public string? ExecutableOverride { get; set; }

    public FileSearchAvailability GetAvailability()
    {
        if (FindIpcWindow() != IntPtr.Zero)
        {
            return FileSearchAvailability.Ready;
        }

        return FindExecutable() is null ? FileSearchAvailability.NotInstalled : FileSearchAvailability.NotRunning;
    }

    public async Task<FileSearchResult> SearchAsync(string query, int maxResults, bool matchPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new FileSearchResult([], 0);
        }

        var everything = FindIpcWindow();
        if (everything == IntPtr.Zero)
        {
            throw new InvalidOperationException("Everything isn't running.");
        }

        var replyId = Interlocked.Increment(ref _nextReplyId);
        var completion = new TaskCompletionSource<FileSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[replyId] = completion;
        try
        {
            var sent = await _dispatcher.InvokeAsync(() => SendQuery(everything, replyId, query, Math.Clamp(maxResults, 1, 1000), matchPath));
            if (!sent)
            {
                throw new InvalidOperationException("Everything didn't accept the search request.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReplyTimeout);
            using (timeout.Token.Register(() => completion.TrySetCanceled()))
            {
                try
                {
                    return await completion.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Everything didn't answer in time (is the database still loading?).");
                }
            }
        }
        finally
        {
            _pending.TryRemove(replyId, out _);
        }
    }

    public bool OpenProviderWindow(string? query)
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Passed as a single argument (no shell, no string concatenation).
            start.ArgumentList.Add("-search");
            start.ArgumentList.Add(query);
        }

        return TryStart(start);
    }

    public bool StartProvider()
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("-startup");
        return TryStart(start);
    }

    public void Dispose()
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        if (_replyWindow is { } window)
        {
            _dispatcher.Invoke(() =>
            {
                window.RemoveHook(WndProc);
                window.Dispose();
            });
            _replyWindow = null;
        }
    }

    private static IntPtr FindIpcWindow()
    {
        var hwnd = NativeMethods.FindWindow(IpcWindowClass, null);
        return hwnd != IntPtr.Zero ? hwnd : NativeMethods.FindWindow(IpcWindowClass15, null);
    }

    private bool TryStart(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Starting Everything failed");
            return false;
        }
    }

    /// <summary>Running instance first (exact install), then the user's override, then the default install folders.</summary>
    private string? FindExecutable()
    {
        var running = FindIpcWindow();
        if (running != IntPtr.Zero)
        {
            _ = NativeMethods.GetWindowThreadProcessId(running, out var pid);
            if (NativeMethods.GetProcessPath(pid) is { } path && File.Exists(path))
            {
                return _lastKnownExecutable = path;
            }
        }

        if (!string.IsNullOrWhiteSpace(ExecutableOverride))
        {
            var custom = Environment.ExpandEnvironmentVariables(ExecutableOverride.Trim().Trim('"'));
            if (File.Exists(custom) && custom.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return custom;
            }
        }

        if (_lastKnownExecutable is { } known && File.Exists(known))
        {
            return known;
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        string[] candidates = ["Everything\\Everything.exe", "Everything 1.5a\\Everything64.exe", "Everything 1.5a\\Everything.exe"];
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path))
                {
                    return _lastKnownExecutable = path;
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- UI-thread IPC

    private bool SendQuery(IntPtr everything, uint replyId, string query, int maxResults, bool matchPath)
    {
        var reply = EnsureReplyWindow();
        var chars = query.Length + 1;
        var size = QueryHeaderSize + (chars * 2);
        var buffer = Marshal.AllocHGlobal(size);
        var cds = Marshal.AllocHGlobal(Marshal.SizeOf<COPYDATASTRUCT>());
        try
        {
            // reply_hwnd is a DWORD in the protocol: window handles fit in 32 bits even on x64.
            Marshal.WriteInt32(buffer, 0, unchecked((int)reply.ToInt64()));
            Marshal.WriteInt32(buffer, 4, unchecked((int)replyId));
            Marshal.WriteInt32(buffer, 8, unchecked((int)(matchPath ? MatchPath : 0)));
            Marshal.WriteInt32(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 16, maxResults);
            var text = (query + '\0').ToCharArray();
            Marshal.Copy(text, 0, buffer + QueryHeaderSize, text.Length);

            Marshal.StructureToPtr(new COPYDATASTRUCT { dwData = CopyDataQueryW, cbData = size, lpData = buffer }, cds, false);
            var ok = NativeMethods.SendMessageTimeout(everything, WM_COPYDATA, reply, cds, NativeMethods.SMTO_ABORTIFHUNG, 2000, out var result);
            return ok != IntPtr.Zero && result != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(cds);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr EnsureReplyWindow()
    {
        if (_replyWindow is null)
        {
            // Message-only window (parent HWND_MESSAGE) that receives Everything's WM_COPYDATA replies.
            var window = new HwndSource(new HwndSourceParameters("SmartDock Everything client") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            window.AddHook(WndProc);

            // Allow replies from an elevated Everything instance as well (UIPI blocks them by default).
            _ = ChangeWindowMessageFilterEx(window.Handle, WM_COPYDATA, 1, IntPtr.Zero);
            _replyWindow = window;
        }

        return _replyWindow.Handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_COPYDATA || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        var id = unchecked((uint)cds.dwData.ToInt64());
        if (!_pending.TryGetValue(id, out var completion))
        {
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            // The data is only valid while this message is being handled: copy it out now.
            completion.TrySetResult(ParseList(cds.lpData, cds.cbData));
        }
        catch (Exception ex) when (ex is ArgumentException or AccessViolationException or IndexOutOfRangeException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Unreadable reply from Everything");
            completion.TrySetException(new InvalidOperationException("Everything sent a reply SmartDock couldn't read."));
        }

        return new IntPtr(1);
    }

    private static FileSearchResult ParseList(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size < ListHeaderSize)
        {
            throw new InvalidDataException("Reply too short");
        }

        var totalItems = Marshal.ReadInt32(data, 8);
        var count = Marshal.ReadInt32(data, 20);
        if (count < 0 || ListHeaderSize + ((long)count * ItemSize) > size)
        {
            throw new InvalidDataException("Bad item count");
        }

        var hits = new List<FileSearchHit>(count);
        for (var i = 0; i < count; i++)
        {
            var item = ListHeaderSize + (i * ItemSize);
            var flags = (uint)Marshal.ReadInt32(data, item);
            var name = ReadString(data, size, Marshal.ReadInt32(data, item + 4));
            var path = ReadString(data, size, Marshal.ReadInt32(data, item + 8));
            hits.Add(new FileSearchHit(name, path, (flags & ItemFolder) != 0));
        }

        return new FileSearchResult(hits, Math.Max(totalItems, count));
    }

    private static string ReadString(IntPtr data, int size, int offset)
    {
        if (offset < ListHeaderSize || offset >= size)
        {
            throw new InvalidDataException("Bad string offset");
        }

        // Null-terminated UTF-16, bounded by the reply size.
        var maxChars = (size - offset) / 2;
        var chars = new char[maxChars];
        Marshal.Copy(data + offset, chars, 0, maxChars);
        var end = Array.IndexOf(chars, '\0');
        return new string(chars, 0, end < 0 ? maxChars : end);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);
}
