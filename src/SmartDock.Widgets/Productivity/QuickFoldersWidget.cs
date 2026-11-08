using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Productivity;

public sealed record QuickPath(string Name, string Path);

/// <summary>One saved location in the popup.</summary>
public sealed class QuickPathItem : ObservableObject
{
    private bool? _exists;

    public QuickPathItem(QuickPath entry, QuickFoldersWidget owner)
    {
        Entry = entry;
        OpenCommand = new RelayCommand(() => owner.Open(this));
        TerminalCommand = new RelayCommand(() => owner.OpenTerminal(this));
        CopyCommand = new RelayCommand(() => owner.CopyPath(this));
        RemoveCommand = new RelayCommand(() => owner.Remove(this));
    }

    public QuickPath Entry { get; }

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    /// <summary>Expanded path (environment variables resolved).</summary>
    public string FullPath => Environment.ExpandEnvironmentVariables(Entry.Path);

    /// <summary>null = not checked yet / unknown (e.g. a slow network share).</summary>
    public bool? Exists
    {
        get => _exists;
        set
        {
            if (SetProperty(ref _exists, value))
            {
                OnPropertyChanged(nameof(IsMissing));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public bool IsMissing => _exists == false;

    public bool IsFile { get; internal set; }

    public bool IsNetwork => FullPath.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Segoe Fluent glyph: folder, document, network folder, or warning when missing.</summary>
    public string Glyph => IsMissing ? "\uE7BA" : IsFile ? "\uE8A5" : IsNetwork ? "\uE8CE" : "\uE8B7";

    public RelayCommand OpenCommand { get; }

    public RelayCommand TerminalCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// Saved folders (and files) one click away: click a row to open it in File Explorer, open a terminal there,
/// or copy the path. Add entries in Configure… or by dragging folders/files from Explorer onto the widget.
/// Only the paths the user saved are opened, and only when clicked.
/// </summary>
public sealed class QuickFoldersWidget(IClipboardService clipboard) : WidgetBase, IPopupWidget, IConfigurableWidget, IFileDropWidget
{
    public const string TypeId = "quick-folders";
    public const int MaxEntries = 40;
    private const string ItemsKey = "items";

    private IReadOnlyList<QuickPathItem> _items = [];
    private string? _feedback;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Quick folders",
        "Your saved folders and files, one click away in File Explorer. Drag folders from Explorer onto the widget to add them.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        sp => new QuickFoldersWidget(sp.GetRequiredService<IClipboardService>()))
    {
        AllowMultiple = true, // e.g. separate work and personal lists.
    };

    public IReadOnlyList<QuickPathItem> Items
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

    public string CountText => _items.Count switch
    {
        0 => "Drop folders here",
        1 => "1 place",
        _ => $"{_items.Count} places",
    };

    /// <summary>Short feedback in the popup ("Path copied", "Couldn't open …").</summary>
    public string? Feedback
    {
        get => _feedback;
        private set => SetProperty(ref _feedback, value);
    }

    public override TimeSpan RefreshInterval => Timeout.InfiniteTimeSpan;

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new(ItemsKey, "Places (one per line: Name | Path)", SettingFieldKind.MultilineText,
            "Folders or files, local or network (\\\\server\\share). Environment variables work, e.g. \"Downloads | %USERPROFILE%\\Downloads\". " +
            "You can also drag folders from File Explorer onto the widget.",
            DefaultText: string.Empty),
    ];

    public void OnPopupOpened()
    {
        Feedback = null;
        _ = CheckExistenceAsync(Items);
    }

    /// <summary>Parses the saved list; public for tests.</summary>
    public static IReadOnlyList<QuickPath> Parse(string? text)
    {
        var list = new List<QuickPath>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var bar = line.IndexOf('|');
            var name = bar >= 0 ? line[..bar].Trim() : string.Empty;
            var path = (bar >= 0 ? line[(bar + 1)..] : line).Trim().Trim('"');
            if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) || path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            {
                continue; // Only file-system paths; URLs belong in Quick actions.
            }

            if (list.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new QuickPath(name.Length > 0 ? name : DefaultName(path), path));
            if (list.Count == MaxEntries)
            {
                break;
            }
        }

        return list;
    }

    public static string Format(IEnumerable<QuickPath> paths) => string.Join("\n", paths.Select(p => $"{p.Name} | {p.Path}"));

    public static string DefaultName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name; // "C:\" stays "C:"
    }

    public bool AcceptsFiles(IReadOnlyList<string> paths) => paths.Count > 0;

    public void OnFilesDropped(IReadOnlyList<string> paths)
    {
        var current = Parse(Settings.Get(ItemsKey, string.Empty)).ToList();
        var added = 0;
        foreach (var path in paths)
        {
            if (current.Count >= MaxEntries)
            {
                break;
            }

            if (!current.Any(p => string.Equals(Environment.ExpandEnvironmentVariables(p.Path), path, StringComparison.OrdinalIgnoreCase)))
            {
                current.Add(new QuickPath(DefaultName(path), path));
                added++;
            }
        }

        if (added > 0)
        {
            Settings.Set(ItemsKey, Format(current)); // Triggers OnSettingChanged → reload.
            Context.Notifications.Post("Quick folders", added == 1 ? $"Added {DefaultName(paths[0])}" : $"Added {added} places",
                SmartDock.Core.Services.NotificationSeverity.Success, InstanceId);
        }
    }

    internal void Open(QuickPathItem item)
    {
        var path = item.FullPath;
        try
        {
            if (File.Exists(path))
            {
                // A saved file: open it with its default app (the user chose this exact file).
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            else
            {
                // Folders (including network shares and shell paths like "shell:Downloads") open in Explorer.
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(path);
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Feedback = $"Couldn't open {item.Name}: {ex.Message}";
        }
    }

    internal void OpenTerminal(QuickPathItem item)
    {
        var folder = item.IsFile ? System.IO.Path.GetDirectoryName(item.FullPath) : item.FullPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Feedback = $"{item.Name} isn't a reachable folder.";
            return;
        }

        // Windows Terminal if installed, otherwise PowerShell; the folder is passed as an argument, not a command.
        try
        {
            var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(folder);
            Process.Start(terminal)?.Dispose();
        }
        catch (Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
            }
            catch (Win32Exception ex)
            {
                Feedback = "Couldn't start a terminal: " + ex.Message;
            }
        }
    }

    internal void CopyPath(QuickPathItem item)
    {
        try
        {
            clipboard.SetText(item.FullPath);
            Feedback = "Copied: " + item.FullPath;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Feedback = "Clipboard is busy – try again.";
        }
    }

    internal void Remove(QuickPathItem item)
    {
        var remaining = Parse(Settings.Get(ItemsKey, string.Empty)).Where(p => p != item.Entry).ToList();
        Settings.Set(ItemsKey, Format(remaining));
        Feedback = $"Removed {item.Name}";
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var entries = Parse(Settings.Get(ItemsKey, string.Empty));
        var previous = Items.GroupBy(i => i.Entry).ToDictionary(g => g.Key, g => g.First());
        Items = entries.Select(e => previous.GetValueOrDefault(e) ?? new QuickPathItem(e, this)).ToList();
        _ = CheckExistenceAsync(Items);
        SetReady();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks missing entries. Network paths can take seconds to time out, so each check runs in the
    /// background with a short limit and simply stays "unknown" if it doesn't finish.
    /// </summary>
    private static async Task CheckExistenceAsync(IReadOnlyList<QuickPathItem> items)
    {
        await Task.WhenAll(items.Select(async item =>
        {
            var check = Task.Run(() =>
            {
                var path = item.FullPath;
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    return (Exists: true, IsFile: false);
                }

                var isFile = File.Exists(path);
                return (Exists: isFile || Directory.Exists(path), IsFile: isFile);
            });

            if (await Task.WhenAny(check, Task.Delay(1500)).ConfigureAwait(false) == check)
            {
                var (exists, isFile) = await check.ConfigureAwait(false);
                item.IsFile = isFile;
                item.Exists = exists;
            }
        })).ConfigureAwait(false);
    }
}
