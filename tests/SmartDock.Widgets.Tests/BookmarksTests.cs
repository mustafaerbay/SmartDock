using SmartDock.Widgets.Bookmarks;

namespace SmartDock.Widgets.Tests;

public class BookmarksTests
{
    private const string ChromeJson = """
    {
      "checksum": "x",
      "roots": {
        "bookmark_bar": { "type": "folder", "name": "Bookmarks bar", "children": [
          { "type": "url", "name": "GitHub", "url": "https://github.com/" },
          { "type": "folder", "name": "Work", "children": [
            { "type": "url", "name": "Jenkins", "url": "http://jenkins.corp:8080/" },
            { "type": "url", "name": "Bookmarklet", "url": "javascript:alert(1)" }
          ] }
        ] },
        "other": { "type": "folder", "name": "Other bookmarks", "children": [
          { "type": "url", "name": "", "url": "https://example.org/x" }
        ] },
        "synced": { "type": "folder", "name": "Mobile bookmarks", "children": [] }
      },
      "version": 1
    }
    """;

    [Fact]
    public void Parses_chromium_roots_and_nested_folders()
    {
        var root = ChromiumBookmarks.Parse(ChromeJson);

        Assert.Equal(new[] { "Bookmarks bar", "Other bookmarks", "Mobile bookmarks" }, root.Children.Select(c => c.Name));
        var flat = root.Flatten().ToList();
        Assert.Equal(4, flat.Count);
        Assert.Contains(flat, x => x.Link.Name == "Jenkins" && x.Folder == "Bookmarks bar / Work");
        Assert.Contains(flat, x => x.Link.Name == "https://example.org/x"); // empty name falls back to the URL
    }

    [Fact]
    public void Only_web_urls_are_openable()
    {
        Assert.True(BookmarksWidget.IsWebUrl("https://github.com/", out _));
        Assert.True(BookmarksWidget.IsWebUrl("http://jenkins.corp:8080/", out _));
        Assert.False(BookmarksWidget.IsWebUrl("javascript:alert(1)", out _));
        Assert.False(BookmarksWidget.IsWebUrl("file:///C:/x", out _));
        Assert.False(BookmarksWidget.IsWebUrl("chrome://settings", out _));
    }

    [Fact]
    public void Parses_my_links()
    {
        var links = BookmarksWidget.ParseLinks("Wiki | https://wiki.example.com\nhttps://status.example.org\n# c\nBad | ftp://x");
        Assert.Collection(
            links,
            l => Assert.Equal(("Wiki", "https://wiki.example.com/"), (l.Name, l.Url)),
            l => Assert.Equal("status.example.org", l.Name));
    }

    [Fact]
    public void Open_all_ignores_non_web_links() =>
        Assert.Equal(0, BookmarksWidget.LaunchMany(["javascript:alert(1)", "chrome://settings", "file:///C:/x"], browserExe: null));
}
