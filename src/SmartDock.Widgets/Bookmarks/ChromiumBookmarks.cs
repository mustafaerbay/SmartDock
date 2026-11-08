using System.Text.Json;

namespace SmartDock.Widgets.Bookmarks;

public sealed class BookmarkNode
{
    public required string Name { get; init; }

    /// <summary>Null for folders.</summary>
    public string? Url { get; init; }

    public List<BookmarkNode> Children { get; } = [];

    public bool IsFolder => Url is null;

    /// <summary>All links below this node, with their folder path ("Bookmarks bar / Work").</summary>
    public IEnumerable<(BookmarkNode Link, string Folder)> Flatten(string folder = "")
    {
        foreach (var child in Children)
        {
            if (child.IsFolder)
            {
                foreach (var nested in child.Flatten(folder.Length == 0 ? child.Name : $"{folder} / {child.Name}"))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return (child, folder);
            }
        }
    }
}

/// <summary>A browser profile that has a bookmarks file (Chrome, Edge, Brave… all use the Chromium format).</summary>
public sealed record BrowserProfile(string Browser, string ProfileName, string BookmarksPath, string? BrowserExe)
{
    /// <summary>Text used in settings, e.g. "Chrome – Work (Profile 1)".</summary>
    public string Label => $"{Browser} – {ProfileName}";
}

/// <summary>
/// Reads the bookmarks file that Chromium browsers keep in the user's profile folder (plain JSON, read-only access;
/// nothing is written and nothing leaves the PC). Discovery covers Chrome, Edge and Brave for the current user.
/// </summary>
public static class ChromiumBookmarks
{
    private static readonly (string Browser, string UserData, string[] Exe)[] Browsers =
    [
        ("Chrome", @"Google\Chrome\User Data", [@"Google\Chrome\Application\chrome.exe"]),
        ("Edge", @"Microsoft\Edge\User Data", [@"Microsoft\Edge\Application\msedge.exe"]),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data", [@"BraveSoftware\Brave-Browser\Application\brave.exe"]),
    ];

    /// <summary>Finds every browser profile with a Bookmarks file under %LOCALAPPDATA%.</summary>
    public static IReadOnlyList<BrowserProfile> DiscoverProfiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result = new List<BrowserProfile>();
        foreach (var (browser, userData, exes) in Browsers)
        {
            var root = Path.Combine(local, userData);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var names = ReadProfileNames(Path.Combine(root, "Local State"));
            var exe = FindExe(exes);
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, "Bookmarks");
                if (!File.Exists(file))
                {
                    continue;
                }

                var folder = Path.GetFileName(dir);
                var display = names.TryGetValue(folder, out var name) && name.Length > 0 && name != folder ? $"{name} ({folder})" : folder;
                result.Add(new BrowserProfile(browser, display, file, exe));
            }
        }

        return result;
    }

    /// <summary>Parses a Chromium "Bookmarks" JSON document into Bookmarks bar / Other / Mobile roots.</summary>
    public static BookmarkNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        var root = new BookmarkNode { Name = "Bookmarks" };
        if (!doc.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        foreach (var (key, label) in new[] { ("bookmark_bar", "Bookmarks bar"), ("other", "Other bookmarks"), ("synced", "Mobile bookmarks") })
        {
            if (roots.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Object)
            {
                var folder = Convert(node, depth: 0);
                if (folder is not null)
                {
                    root.Children.Add(new BookmarkNode { Name = label }.With(folder.Children));
                }
            }
        }

        return root;
    }

    private static BookmarkNode With(this BookmarkNode node, IEnumerable<BookmarkNode> children)
    {
        node.Children.AddRange(children);
        return node;
    }

    private static BookmarkNode? Convert(JsonElement e, int depth)
    {
        if (depth > 64)
        {
            return null;
        }

        var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
        if (type == "url")
        {
            var url = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            return url is null ? null : new BookmarkNode { Name = name.Length > 0 ? name : url, Url = url };
        }

        var folder = new BookmarkNode { Name = name };
        if (e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (Convert(child, depth + 1) is { } converted)
                {
                    folder.Children.Add(converted);
                }
            }
        }

        return folder;
    }

    private static Dictionary<string, string> ReadProfileNames(string localStatePath)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(localStatePath))
            {
                return names;
            }

            using var stream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("profile", out var profile) && profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in cache.EnumerateObject())
                {
                    if (entry.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        names[entry.Name] = name.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Names are cosmetic; folder names are used instead.
        }

        return names;
    }

    private static string? FindExe(string[] relative)
    {
        foreach (var baseDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        })
        {
            foreach (var rel in relative)
            {
                if (baseDir.Length > 0 && File.Exists(Path.Combine(baseDir, rel)))
                {
                    return Path.Combine(baseDir, rel);
                }
            }
        }

        return null;
    }

    /// <summary>Reads the file while the browser may have it open (shared read).</summary>
    public static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
