using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Productivity;

// ---------------------------------------------------------------- Notes

/// <summary>Shows how many notes exist and the latest one; click opens the notes window.</summary>
public sealed class NotesWidget(INotesStore store) : WidgetBase, IClickableWidget
{
    public const string TypeId = "notes";

    private string _latest = string.Empty;
    private int _count;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Notes",
        "Quick local notes: create, edit and delete. Click to open; stored on this PC only.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        sp => new NotesWidget(sp.GetRequiredService<INotesStore>()));

    public string Latest
    {
        get => _latest;
        private set => SetProperty(ref _latest, value);
    }

    public int Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    public override TimeSpan RefreshInterval => Timeout.InfiniteTimeSpan;

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("New note", new RelayCommand(() => Context.Events.Publish(new OpenNotesRequested("new")))),
        new("Open notes", new RelayCommand(OnClick)),
    ];

    public void OnClick() => Context.Events.Publish(new OpenNotesRequested(null));

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        store.Changed += OnStoreChanged;
        return Task.CompletedTask;
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        Render();
        return Task.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync()
    {
        store.Changed -= OnStoreChanged;
        return ValueTask.CompletedTask;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var notes = store.GetAll();
        Count = notes.Count;
        Latest = notes.Count == 0 ? "No notes yet" : notes[0].Title;
    }
}

// ---------------------------------------------------------------- Quick actions

public enum QuickActionKind
{
    App,
    Url,
    Folder,
    Builtin,
}

public sealed record QuickAction(string Label, QuickActionKind Kind, string Target, string Glyph)
{
    public RelayCommand? RunCommand { get; init; }
}

/// <summary>
/// Configurable launch buttons: apps, URLs, folders and a fixed list of approved built-in actions.
/// There is deliberately no command-line/shell execution: no arguments, and script files are refused.
/// </summary>
public sealed class QuickActionsWidget : WidgetBase, IConfigurableWidget
{
    public const string TypeId = "quick-actions";

    public const string DefaultActions = "Explorer | %WINDIR%\\explorer.exe\nSettings | builtin:settings\nSnip | builtin:snip\nLock | builtin:lock";

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msc", ".scr", ".reg",
    };

    private static readonly Dictionary<string, (string Label, string Glyph)> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lock"] = ("Lock PC", ""),
        ["desktop"] = ("Show desktop", ""),
        ["snip"] = ("Screenshot", ""),
        ["settings"] = ("Settings", ""),
        ["taskmanager"] = ("Task Manager", ""),
        ["downloads"] = ("Downloads", ""),
    };

    private IReadOnlyList<QuickAction> _actions = [];

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Quick actions",
        "One-click buttons for apps, websites, folders and built-in actions (lock, screenshot, show desktop…).",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        _ => new QuickActionsWidget())
    {
        AllowMultiple = true,
    };

    public IReadOnlyList<QuickAction> Actions
    {
        get => _actions;
        private set => SetProperty(ref _actions, value);
    }

    public override TimeSpan RefreshInterval => Timeout.InfiniteTimeSpan;

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("actions", "Actions (one per line: Label | target)", SettingFieldKind.MultilineText,
            "Target can be an app path, https URL, folder, or builtin:lock / desktop / snip / settings / taskmanager / downloads.",
            DefaultText: DefaultActions),
    ];

    /// <summary>Parses the configuration text; public for tests.</summary>
    public static IReadOnlyList<QuickAction> Parse(string text)
    {
        var list = new List<QuickAction>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            var target = parts.Length == 2 ? parts[1] : parts[0];
            var label = parts.Length == 2 ? parts[0] : string.Empty;

            if (target.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                var name = target[8..];
                if (Builtins.TryGetValue(name, out var b))
                {
                    list.Add(new QuickAction(label.Length > 0 ? label : b.Label, QuickActionKind.Builtin, name.ToLowerInvariant(), b.Glyph));
                }

                continue;
            }

            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                list.Add(new QuickAction(label.Length > 0 ? label : uri.Host, QuickActionKind.Url, uri.AbsoluteUri, ""));
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(target.Trim('"'));
            if (BlockedExtensions.Contains(Path.GetExtension(expanded)))
            {
                continue; // Scripts are not allowed as quick actions.
            }

            var kind = Directory.Exists(expanded) ? QuickActionKind.Folder : QuickActionKind.App;
            var fallback = kind == QuickActionKind.Folder ? new DirectoryInfo(expanded).Name : Path.GetFileNameWithoutExtension(expanded);
            list.Add(new QuickAction(label.Length > 0 ? label : fallback, kind, expanded, kind == QuickActionKind.Folder ? "" : ""));
        }

        return list;
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var parsed = Parse(Settings.Get("actions", DefaultActions));
        Actions = parsed.Select(a => a with { RunCommand = new RelayCommand(() => Run(a)) }).ToList();
        if (Actions.Count == 0)
        {
            SetUnavailable("No valid actions – right-click › Configure…");
        }
        else
        {
            SetReady();
        }

        return Task.CompletedTask;
    }

    private void Run(QuickAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case QuickActionKind.Url:
                    SafeLauncher.OpenWebUrl(action.Target);
                    break;
                case QuickActionKind.Folder:
                case QuickActionKind.App:
                    Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true })?.Dispose();
                    break;
                case QuickActionKind.Builtin:
                    RunBuiltin(action.Target);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Context.Notifications.Post("Quick action failed", $"{action.Label}: {ex.Message}", Core.Services.NotificationSeverity.Error, InstanceId);
        }
    }

    private static void RunBuiltin(string name)
    {
        switch (name)
        {
            case "lock":
                LockWorkStation();
                break;
            case "desktop":
                keybd_event(0x5B, 0, 0, UIntPtr.Zero);       // Win down
                keybd_event(0x44, 0, 0, UIntPtr.Zero);       // D down
                keybd_event(0x44, 0, 2, UIntPtr.Zero);       // D up
                keybd_event(0x5B, 0, 2, UIntPtr.Zero);       // Win up
                break;
            case "snip":
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true })?.Dispose();
                break;
            case "settings":
                Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true })?.Dispose();
                break;
            case "taskmanager":
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
                break;
            case "downloads":
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Process.Start(new ProcessStartInfo(downloads) { UseShellExecute = true })?.Dispose();
                break;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}

// ---------------------------------------------------------------- Volume

/// <summary>Master volume: wheel to adjust, popup slider and mute, current output device.</summary>
public sealed class VolumeWidget(IVolumeService volume) : WidgetBase, IPopupWidget, IWheelWidget
{
    public const string TypeId = "volume";

    private double _level;
    private bool _isMuted;
    private string _deviceName = string.Empty;
    private RelayCommand? _toggleMute;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Volume",
        "Master volume and mute for the current output device. Scroll over it to adjust.",
        WidgetCategory.System,
        "",
        DockRegion.Right,
        sp => new VolumeWidget(sp.GetRequiredService<IVolumeService>()));

    /// <summary>0..100; setting it (popup slider) changes the system volume.</summary>
    public double Level
    {
        get => _level;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _level, clamped))
            {
                volume.SetLevel((float)(clamped / 100));
                OnPropertyChanged(nameof(LevelText));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public string LevelText => IsMuted ? "Muted" : $"{Math.Round(_level):0}%";

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(LevelText));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string Glyph => IsMuted || _level < 0.5 ? "" : _level < 34 ? "" : _level < 67 ? "" : "";

    public RelayCommand ToggleMuteCommand => _toggleMute ??= new RelayCommand(() =>
    {
        if (volume.SetMute(!IsMuted))
        {
            IsMuted = !IsMuted;
        }
    });

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);

    public void OnPopupOpened() => Context.RequestRefresh?.Invoke();

    public void OnWheel(int delta)
    {
        Level = Math.Round(_level + (delta / 120.0 * 2));
        if (IsMuted && delta > 0 && volume.SetMute(false))
        {
            IsMuted = false;
        }
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        if (volume.GetState() is not { } state)
        {
            SetUnavailable("No audio output device");
            return Task.CompletedTask;
        }

        // Update the backing field directly so a refresh never writes the volume back.
        if (SetProperty(ref _level, Math.Round(state.Level * 100), nameof(Level)))
        {
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(Glyph));
        }

        IsMuted = state.IsMuted;
        DeviceName = state.DeviceName;
        SetReady();
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------- Clipboard

/// <summary>
/// Recent clipboard text, only when the user turns history on. Entries live in memory only, are never
/// written to disk, and content marked private by the source app (e.g. password managers) is skipped.
/// </summary>
public sealed class ClipboardWidget(IClipboardService clipboard) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "clipboard";

    private readonly Lock _gate = new();
    private readonly List<ClipboardEntry> _entries = [];
    private IReadOnlyList<ClipboardEntryItem> _items = [];
    private RelayCommand? _clear;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Clipboard",
        "Recent copied text (opt-in, memory only, skips content apps mark as private). Click an entry to copy it again.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        sp => new ClipboardWidget(sp.GetRequiredService<IClipboardService>()));

    public bool HistoryEnabled => Settings.Get("historyEnabled", false);

    public int MaxEntries => (int)Math.Clamp(Settings.Get("maxEntries", 20.0), 5, 100);

    public IReadOnlyList<ClipboardEntryItem> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    public string CountText => HistoryEnabled ? $"{_items.Count} item{(_items.Count == 1 ? string.Empty : "s")}" : "History off";

    public RelayCommand ClearCommand => _clear ??= new RelayCommand(Clear);

    public override TimeSpan RefreshInterval => Timeout.InfiniteTimeSpan;

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Clipboard history enabled", new RelayCommand(() => Settings.Set("historyEnabled", !HistoryEnabled)), HistoryEnabled, true),
        new("Clear history", ClearCommand),
    ];

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("historyEnabled", "Record clipboard history", SettingFieldKind.Toggle,
            "Off by default. When on, copied text is kept in memory (never saved to disk) until you clear it or exit."),
        new("maxEntries", "Entries to keep", SettingFieldKind.Number, DefaultNumber: 20),
    ];

    public void OnPopupOpened()
    {
    }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        clipboard.TextCopied += OnTextCopied;
        return Task.CompletedTask;
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        if (HistoryEnabled && !clipboard.IsListening)
        {
            if (!clipboard.Start())
            {
                SetUnavailable("Clipboard listener could not be started");
                return Task.CompletedTask;
            }
        }
        else if (!HistoryEnabled && clipboard.IsListening)
        {
            clipboard.Stop();
            Clear();
        }

        OnPropertyChanged(nameof(CountText));
        SetReady();
        return Task.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync()
    {
        clipboard.TextCopied -= OnTextCopied;
        if (clipboard.IsListening)
        {
            clipboard.Stop();
        }

        Clear();
        return ValueTask.CompletedTask;
    }

    private void OnTextCopied(object? sender, ClipboardEntry entry)
    {
        if (!HistoryEnabled)
        {
            return;
        }

        lock (_gate)
        {
            _entries.RemoveAll(e => e.Text == entry.Text);
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }

        Publish();
    }

    private void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Publish();
    }

    private void Publish()
    {
        List<ClipboardEntry> snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        Items = snapshot.Select(e => new ClipboardEntryItem(e, new RelayCommand(() => clipboard.SetText(e.Text)))).ToList();
    }
}

public sealed record ClipboardEntryItem(ClipboardEntry Entry, RelayCommand CopyCommand)
{
    public string Preview => Entry.Preview;

    public string TimeText => Entry.CopiedAt.ToString("HH:mm");
}
