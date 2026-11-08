using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Windows.Interop;
using SmartDock.Windows.WindowManager;

namespace SmartDock.App.ViewModels;

/// <summary>One icon in the applications region: a pin, a running app, or both.</summary>
public sealed class DockAppItem : ObservableObject
{
    private IReadOnlyList<AppWindowInfo> _windows = [];
    private bool _isActive;
    private bool _needsAttention;
    private PinnedApp? _pin;
    private string _displayName;

    public DockAppItem(string key, string displayName, ImageSource? icon)
    {
        Key = key;
        _displayName = displayName;
        Icon = icon;
    }

    public string Key { get; }

    public ImageSource? Icon { get; }

    /// <summary>First letter shown when no icon could be extracted.</summary>
    public string Monogram => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                OnPropertyChanged(nameof(Monogram));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public PinnedApp? Pin
    {
        get => _pin;
        set
        {
            if (SetProperty(ref _pin, value))
            {
                OnPropertyChanged(nameof(IsPinned));
            }
        }
    }

    public bool IsPinned => _pin is not null;

    public IReadOnlyList<AppWindowInfo> Windows
    {
        get => _windows;
        set
        {
            _windows = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(WindowCount));
            OnPropertyChanged(nameof(HasMultipleWindows));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public bool IsRunning => _windows.Count > 0;

    public int WindowCount => _windows.Count;

    public bool HasMultipleWindows => _windows.Count > 1;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>One of the app's windows flashed (FlashWindowEx, e.g. a new chat message) and hasn't been looked at yet.</summary>
    public bool NeedsAttention
    {
        get => _needsAttention;
        set
        {
            if (SetProperty(ref _needsAttention, value))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public string? ExecutablePath => _pin?.Path ?? _windows.FirstOrDefault()?.ProcessPath;

    public string ToolTip => (_needsAttention ? "● " : string.Empty) + _windows.Count switch
    {
        0 => DisplayName,
        1 => _windows[0].Title,
        _ => $"{DisplayName} – {_windows.Count} windows",
    };
}

/// <summary>Pinned + running applications, refreshed from shell hook messages (event driven).</summary>
public sealed class AppsViewModel(ILayoutService layout, INotificationService notifications, ILogger<AppsViewModel> logger)
{
    private IntPtr _lastForeground;

    // Windows that flashed, with their process id (some apps flash a hidden/owner window, so the
    // process is used as a fallback to find the dock icon).
    private readonly Dictionary<IntPtr, uint> _attention = [];

    public ObservableCollection<DockAppItem> Items { get; } = [];

    /// <summary>Rebuilds the list from the current windows. Must run on the UI thread.</summary>
    public void Refresh()
    {
        IReadOnlyList<AppWindowInfo> windows;
        try
        {
            windows = WindowTracker.GetAppWindows();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Window enumeration failed");
            return;
        }

        var existing = Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var groups = windows.GroupBy(w => w.GroupKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<AppWindowInfo>)g.ToList(), StringComparer.Ordinal);
        var desired = new List<DockAppItem>();

        foreach (var pin in layout.Current.PinnedApps)
        {
            var key = "pin:" + pin.Id;
            var item = existing.GetValueOrDefault(key) ?? new DockAppItem(key, pin.DisplayName, IconExtractor.ForFile(pin.Path));
            item.Pin = pin;
            item.DisplayName = pin.DisplayName;
            var groupKey = NormalizePath(pin.Path);
            if (groupKey is not null && groups.Remove(groupKey, out var pinWindows))
            {
                item.Windows = pinWindows;
            }
            else if (item.Windows.Count > 0)
            {
                item.Windows = [];
            }

            desired.Add(item);
        }

        // Unpinned running apps keep their previous relative order; new ones go to the end.
        var running = groups
            .Select(g =>
            {
                var item = existing.GetValueOrDefault(g.Key) ?? CreateRunningItem(g.Key, g.Value);
                item.Windows = g.Value;
                return item;
            })
            .OrderBy(i => existing.ContainsKey(i.Key) ? Items.IndexOf(existing[i.Key]) : int.MaxValue)
            .ToList();
        desired.AddRange(running);

        Sync(desired);
        UpdateActive(WindowTracker.Foreground);
    }

    /// <summary>Records the foreground window (ignoring the dock itself) and updates the active indicator.</summary>
    public void UpdateActive(IntPtr foreground, IntPtr? ownWindow = null)
    {
        if (foreground != IntPtr.Zero && foreground != ownWindow)
        {
            _lastForeground = foreground;
        }

        foreach (var item in Items)
        {
            item.IsActive = item.Windows.Any(w => w.Handle == _lastForeground);
            if (item.IsActive && _attention.Count > 0)
            {
                // The user is looking at the app now: stop blinking, like the Windows taskbar.
                foreach (var key in _attention.Where(a => item.Windows.Any(w => w.Handle == a.Key || w.ProcessId == a.Value)).Select(a => a.Key).ToList())
                {
                    _attention.Remove(key);
                }
            }
        }

        ApplyAttention();
    }

    /// <summary>
    /// A window asked for attention (HSHELL_FLASH). Returns true when this is new for its dock icon,
    /// so the dock can reveal itself if it is auto-hidden.
    /// </summary>
    public bool MarkAttention(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == WindowTracker.Foreground)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var isNew = !_attention.ContainsKey(hwnd);
        _attention[hwnd] = pid;
        var before = Items.Count(i => i.NeedsAttention);
        ApplyAttention();
        return isNew && Items.Count(i => i.NeedsAttention) > before;
    }

    private void ApplyAttention()
    {
        foreach (var gone in _attention.Keys.Where(h => !NativeMethods.IsWindow(h)).ToList())
        {
            _attention.Remove(gone);
        }

        foreach (var item in Items)
        {
            item.NeedsAttention = _attention.Count > 0
                && item.Windows.Any(w => _attention.ContainsKey(w.Handle) || _attention.ContainsValue(w.ProcessId));
        }
    }

    /// <summary>Left click: launch when not running; otherwise activate, minimise, or cycle windows.</summary>
    public void Activate(DockAppItem item)
    {
        try
        {
            if (!item.IsRunning)
            {
                Launch(item);
                return;
            }

            var windows = item.Windows;
            var current = windows.ToList().FindIndex(w => w.Handle == _lastForeground);
            if (windows.Count == 1 && current == 0 && !windows[0].IsMinimized)
            {
                WindowTracker.Minimize(windows[0].Handle);
                return;
            }

            var next = windows[(current + 1) % windows.Count];
            WindowTracker.Activate(next.Handle);
            _lastForeground = next.Handle;
            UpdateActive(next.Handle);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Activating {App} failed", item.DisplayName);
        }
    }

    public void Launch(DockAppItem item)
    {
        var path = item.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            WindowTracker.Launch(path, item.Pin?.Arguments);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            logger.LogWarning(ex, "Launching {Path} failed", path);
            notifications.Post("Could not start app", $"{item.DisplayName}: {ex.Message}", NotificationSeverity.Error, dedupeKey: "launch:" + path, dedupeWindow: TimeSpan.FromSeconds(10));
        }
    }

    public void Pin(DockAppItem item)
    {
        var path = item.ExecutablePath;
        if (item.IsPinned || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        layout.Mutate(l => l.PinnedApps.Add(new PinnedApp { DisplayName = item.DisplayName, Path = path }));
        Refresh();
    }

    public void Unpin(DockAppItem item)
    {
        if (item.Pin is not { } pin)
        {
            return;
        }

        layout.Mutate(l => l.PinnedApps.RemoveAll(p => p.Id == pin.Id));
        Refresh();
    }

    /// <summary>Moves a pin to <paramref name="pinIndex"/> among the pins (drag-and-drop reordering).</summary>
    public void MovePin(string pinId, int pinIndex)
    {
        layout.Mutate(l =>
        {
            var from = l.PinnedApps.FindIndex(p => p.Id == pinId);
            if (from < 0)
            {
                return;
            }

            var pin = l.PinnedApps[from];
            l.PinnedApps.RemoveAt(from);
            var to = pinIndex > from ? pinIndex - 1 : pinIndex;
            l.PinnedApps.Insert(Math.Clamp(to, 0, l.PinnedApps.Count), pin);
        });
        Refresh();
    }

    public int PinCount => layout.Current.PinnedApps.Count;

    public void CloseAll(DockAppItem item)
    {
        foreach (var w in item.Windows)
        {
            WindowTracker.RequestClose(w.Handle);
        }
    }

    internal static string? NormalizePath(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded).ToUpperInvariant() : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static DockAppItem CreateRunningItem(string key, IReadOnlyList<AppWindowInfo> windows)
    {
        var first = windows[0];
        var name = first.Title;
        if (first.ProcessPath is { } path && !key.StartsWith("hwnd:", StringComparison.Ordinal))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                name = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description;
            }
            catch (FileNotFoundException)
            {
                name = Path.GetFileNameWithoutExtension(path);
            }
        }

        var icon = key.StartsWith("hwnd:", StringComparison.Ordinal)
            ? IconExtractor.ForWindow(first.Handle)
            : IconExtractor.ForFile(first.ProcessPath) ?? IconExtractor.ForWindow(first.Handle);
        return new DockAppItem(key, name, icon);
    }

    private void Sync(List<DockAppItem> desired)
    {
        var keep = new HashSet<DockAppItem>(desired);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = Items.IndexOf(desired[i]);
            if (current < 0)
            {
                Items.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                Items.Move(current, i);
            }
        }
    }
}
