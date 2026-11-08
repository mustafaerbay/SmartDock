using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Bookmarks;

/// <summary>A row in the popup: a link to open or a folder to enter.</summary>
public sealed class BookmarkEntry(string name, string? url, bool isFolder, string detail, Action activate, Action? openAll = null)
{
    public string Name { get; } = name;

    public string? Url { get; } = url;

    public bool IsFolder { get; } = isFolder;

    /// <summary>Host for links ("github.com"), item count for folders.</summary>
    public string Detail { get; } = detail;

    public string Glyph => IsFolder ? "" : "";

    public RelayCommand ActivateCommand { get; } = new(activate);

    /// <summary>Folders only: open every link in the folder (and its sub-folders) as tabs.</summary>
    public RelayCommand? OpenAllCommand { get; } = openAll is null ? null : new RelayCommand(openAll);

    public bool CanOpenAll => OpenAllCommand is not null;
}

/// <summary>
/// Bookmarks one click away: your own links plus (optionally) the bookmarks of a Chrome/Edge/Brave profile,
/// read from the browser's local Bookmarks file (read-only, never uploaded). Browse folders in the popup or
/// open the search window; links open in the default browser or in the browser they came from.
/// Only http(s) links are opened.
/// </summary>
public sealed class BookmarksWidget : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "bookmarks";
    private const string NoBrowser = "None (only my links below)";
    private const string OpenDefault = "My default browser";
    private const string OpenSource = "The browser the bookmark comes from";

    private readonly Stack<BookmarkNode> _path = new();
    private BookmarkNode _root = new() { Name = "Bookmarks" };
    private string? _loadedKey;
    private BrowserProfile? _profile;
    private IReadOnlyList<BookmarkEntry> _entries = [];
    private string _location = "Bookmarks";
    private string _summary = "Bookmarks";
    private int _skipped;
    private RelayCommand? _up;
    private RelayCommand? _search;
    private RelayCommand? _openAllHere;
    private string? _feedback;
    private (BookmarkNode Folder, DateTime At)? _pendingOpenAll;

    /// <summary>Asking before opening more tabs than this at once (Chrome asks from 15 too).</summary>
    public const int ConfirmAbove = 15;

    /// <summary>Hard cap per "Open all" so a huge folder can't flood the browser.</summary>
    public const int MaxTabs = 100;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Bookmarks",
        "Your links and your Chrome/Edge/Brave bookmarks, one click away. Browse folders or search.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        _ => new BookmarksWidget())
    {
        AllowMultiple = true,
    };

    public IReadOnlyList<BookmarkEntry> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    /// <summary>Breadcrumb, e.g. "Chrome – Work › Bookmarks bar › Dev".</summary>
    public string Location
    {
        get => _location;
        private set => SetProperty(ref _location, value);
    }

    /// <summary>Card text, e.g. "248 links".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool CanGoUp => _path.Count > 1;

    public RelayCommand UpCommand => _up ??= new RelayCommand(GoUp);

    public RelayCommand SearchCommand => _search ??= new RelayCommand(OpenSearch);

    /// <summary>"Open all" for the folder currently shown (hidden at the top level).</summary>
    public RelayCommand OpenAllHereCommand => _openAllHere ??= new RelayCommand(() => OpenAll(_path.Peek()));

    public bool CanOpenAllHere => _path.Count > 1;

    /// <summary>Short message in the popup ("Opened 12 tabs", "Click again to open 37 tabs").</summary>
    public string? Feedback
    {
        get => _feedback;
        private set => SetProperty(ref _feedback, value);
    }

    /// <summary>The bookmarks file is re-read only when it changed.</summary>
    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Search bookmarks…", SearchCommand),
    ];

    public IReadOnlyList<WidgetSettingField> SettingFields
    {
        get
        {
            var profiles = ChromiumBookmarks.DiscoverProfiles();
            var choices = new List<string> { NoBrowser };
            choices.AddRange(profiles.Select(p => p.Label));
            return
            [
                new("browserProfile", "Browser bookmarks", SettingFieldKind.Choice,
                    profiles.Count == 0
                        ? "No Chrome, Edge or Brave profile with bookmarks was found for this Windows user."
                        : "Read from the browser's local Bookmarks file (read-only). Changes in the browser show up within 30 seconds.",
                    Choices: choices, DefaultText: profiles.FirstOrDefault(p => p.Browser == "Chrome")?.Label ?? NoBrowser),
                new("links", "My links (one per line: Name | URL)", SettingFieldKind.MultilineText,
                    "Shown at the top. http:// or https:// only, e.g. \"Wiki | https://wiki.example.com\".", DefaultText: string.Empty),
                new("openWith", "Open links in", SettingFieldKind.Choice, Choices: [OpenDefault, OpenSource], DefaultText: OpenDefault),
                new("displayName", "Name on the dock (optional)", SettingFieldKind.Text),
            ];
        }
    }

    public void OnPopupOpened()
    {
        Feedback = null;
        _pendingOpenAll = null;
        // Always start at the top level when the popup opens (called on the UI thread).
        _path.Clear();
        _path.Push(_root);
        ShowCurrent();
    }

    /// <summary>"Name | URL" lines; public for tests.</summary>
    public static IReadOnlyList<BookmarkNode> ParseLinks(string? text)
    {
        var list = new List<BookmarkNode>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var bar = line.LastIndexOf('|');
            var name = bar >= 0 ? line[..bar].Trim() : string.Empty;
            var url = (bar >= 0 ? line[(bar + 1)..] : line).Trim();
            if (IsWebUrl(url, out var uri))
            {
                list.Add(new BookmarkNode { Name = name.Length > 0 ? name : uri.Host, Url = uri.AbsoluteUri });
            }
        }

        return list;
    }

    public static bool IsWebUrl(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && (uri = parsed) is not null;
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var choice = Settings.Get("browserProfile", string.Empty);
        if (choice.Length == 0)
        {
            choice = ChromiumBookmarks.DiscoverProfiles().FirstOrDefault(p => p.Browser == "Chrome")?.Label ?? NoBrowser;
        }

        _profile = choice == NoBrowser ? null : ChromiumBookmarks.DiscoverProfiles().FirstOrDefault(p => p.Label == choice);
        var links = ParseLinks(Settings.Get("links", string.Empty));

        BookmarkNode? browserRoot = null;
        string? problem = null;

        // Skip the work when neither the settings nor the bookmarks file changed since the last refresh.
        var stamp = _profile is not null && File.Exists(_profile.BookmarksPath) ? File.GetLastWriteTimeUtc(_profile.BookmarksPath).Ticks : 0;
        var key = string.Join("\u001f", choice, Settings.Get("links", string.Empty), Settings.Get("displayName", string.Empty), stamp);
        if (key == _loadedKey)
        {
            return Task.CompletedTask;
        }

        if (_profile is not null)
        {
            try
            {
                browserRoot = ChromiumBookmarks.Parse(ChromiumBookmarks.ReadFile(_profile.BookmarksPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                problem = $"Couldn't read {_profile.Label} bookmarks: {ex.Message}";
            }
        }
        else if (choice != NoBrowser)
        {
            problem = $"\"{choice}\" wasn't found any more. Pick a profile again in Configure….";
        }

        // Top level: my links, then the bookmarks bar's contents (as in the browser), then the other root folders.
        var root = new BookmarkNode { Name = Title() };
        root.Children.AddRange(links);
        if (browserRoot is not null)
        {
            var bar = browserRoot.Children.FirstOrDefault(c => c.Name == "Bookmarks bar");
            if (bar is not null)
            {
                root.Children.AddRange(bar.Children);
            }

            root.Children.AddRange(browserRoot.Children.Where(c => c != bar && c.Children.Count > 0));
        }

        var all = root.Flatten().Select(x => x.Link).ToList();
        _skipped = all.Count(l => !IsWebUrl(l.Url, out _));
        _loadedKey = problem is null ? key : null; // Retry next time if reading failed.
        PostToUi(() =>
        {
            // Navigation state is only touched on the UI thread.
            _root = root;
            _path.Clear();
            _path.Push(_root);
            ShowCurrent();
        });

        var usable = all.Count - _skipped;
        Summary = usable == 1 ? "1 link" : $"{usable:N0} links";
        if (usable == 0 && problem is null)
        {
            SetUnavailable(_profile is null ? "Add links or pick a browser profile: right-click › Configure…." : "That profile has no bookmarks yet.");
        }
        else if (problem is not null && usable == 0)
        {
            SetUnavailable(problem);
        }
        else
        {
            SetReady();
        }

        return Task.CompletedTask;
    }

    private string Title() => NonEmpty(Settings.Get("displayName", string.Empty)) ?? _profile?.Label ?? "Bookmarks";

    private void ShowCurrent()
    {
        var folder = _path.Peek();
        Entries = folder.Children
            .Where(c => c.IsFolder ? c.Children.Count > 0 : IsWebUrl(c.Url, out _))
            .Select(c => c.IsFolder
                ? new BookmarkEntry(c.Name, null, true, $"{c.Flatten().Count()} links", () => Enter(c), () => OpenAll(c))
                : new BookmarkEntry(c.Name, c.Url, false, IsWebUrl(c.Url, out var u) ? u.Host : string.Empty, () => Open(c.Url!)))
            .ToList();
        Location = string.Join(" › ", _path.Reverse().Select(n => n.Name));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanOpenAllHere));
    }

    /// <summary>
    /// Opens every http(s) link in <paramref name="folder"/> and its sub-folders. More than <see cref="ConfirmAbove"/>
    /// tabs need a second click within 5 seconds; at most <see cref="MaxTabs"/> are opened.
    /// </summary>
    private void OpenAll(BookmarkNode folder)
    {
        var urls = folder.Flatten().Select(x => x.Link.Url).Where(u => IsWebUrl(u, out _)).Select(u => u!).ToList();
        if (urls.Count == 0)
        {
            Feedback = $"\"{folder.Name}\" has no web links to open.";
            return;
        }

        var confirmed = _pendingOpenAll is { } pending && ReferenceEquals(pending.Folder, folder) && DateTime.UtcNow - pending.At < TimeSpan.FromSeconds(5);
        if (urls.Count > ConfirmAbove && !confirmed)
        {
            _pendingOpenAll = (folder, DateTime.UtcNow);
            Feedback = $"\"{folder.Name}\" has {urls.Count} links – click Open all again to open {Math.Min(urls.Count, MaxTabs)} tabs.";
            return;
        }

        _pendingOpenAll = null;
        var opened = LaunchMany(urls.Take(MaxTabs).ToList(), Settings.Get("openWith", OpenDefault) == OpenSource ? _profile?.BrowserExe : null);
        Feedback = urls.Count > MaxTabs
            ? $"Opened the first {MaxTabs} of {urls.Count} links from \"{folder.Name}\"."
            : $"Opened {opened} tab{(opened == 1 ? string.Empty : "s")} from \"{folder.Name}\".";
    }

    /// <summary>
    /// Opens several links: with a known browser exe in one process (URLs as separate arguments → tabs in one
    /// window, in order); otherwise one by one through the default browser with a short pause to keep the order.
    /// </summary>
    public static int LaunchMany(IReadOnlyList<string> urls, string? browserExe)
    {
        var valid = urls.Where(u => IsWebUrl(u, out _)).Select(u => new Uri(u).AbsoluteUri).ToList();
        if (valid.Count == 0)
        {
            return 0;
        }

        if (browserExe is not null && File.Exists(browserExe))
        {
            try
            {
                var start = new ProcessStartInfo(browserExe) { UseShellExecute = false };
                foreach (var url in valid)
                {
                    start.ArgumentList.Add(url);
                }

                Process.Start(start)?.Dispose();
                return valid.Count;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Fall back to the default browser below.
            }
        }

        _ = Task.Run(async () =>
        {
            foreach (var url in valid)
            {
                SafeLauncher.OpenWebUrl(url);
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        });
        return valid.Count;
    }

    private void Enter(BookmarkNode folder)
    {
        _path.Push(folder);
        ShowCurrent();
    }

    private void GoUp()
    {
        if (_path.Count > 1)
        {
            _path.Pop();
            ShowCurrent();
        }
    }

    private void Open(string url) => Launch(url, Settings.Get("openWith", OpenDefault) == OpenSource ? _profile?.BrowserExe : null);

    /// <summary>Opens an http(s) URL in a specific browser exe (URL passed as one argument) or the default browser.</summary>
    public static bool Launch(string url, string? browserExe)
    {
        if (!IsWebUrl(url, out var uri))
        {
            return false;
        }

        if (browserExe is null || !File.Exists(browserExe))
        {
            return SafeLauncher.OpenWebUrl(uri.AbsoluteUri);
        }

        try
        {
            var start = new ProcessStartInfo(browserExe) { UseShellExecute = false };
            start.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return SafeLauncher.OpenWebUrl(uri.AbsoluteUri);
        }
    }

    private void OpenSearch()
    {
        var links = _root.Flatten()
            .Where(x => IsWebUrl(x.Link.Url, out _))
            .Select(x => new BookmarkLink(x.Link.Name, x.Link.Url!, x.Folder, _profile?.Browser ?? "My links"))
            .ToList();
        var browser = Settings.Get("openWith", OpenDefault) == OpenSource ? _profile?.BrowserExe : null;
        Context.Events.Publish(new OpenBookmarkSearchRequested(Title(), links, browser));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
