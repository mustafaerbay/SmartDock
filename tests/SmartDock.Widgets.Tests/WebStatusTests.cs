using SmartDock.Widgets.Web;

namespace SmartDock.Widgets.Tests;

public class WebStatusParseTests
{
    [Fact]
    public void Parses_named_and_bare_urls_and_skips_invalid_lines()
    {
        var sites = WebStatusWidget.Parse(
            "Wiki | https://wiki.example.com/start\n" +
            "https://status.example.org\n" +
            "# comment\n" +
            "Files | file:///C:/secret.txt\n" +
            "not a url\n" +
            "Wiki | https://wiki.example.com/start\n");

        Assert.Collection(
            sites,
            s => Assert.Equal(("Wiki", "wiki.example.com"), (s.Name, s.Url.Host)),
            s => Assert.Equal(("status.example.org", "https"), (s.Name, s.Url.Scheme)));
    }

    [Fact]
    public void Caps_the_number_of_sites()
    {
        var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"https://site{i}.example.com"));
        Assert.Equal(WebStatusWidget.MaxSites, WebStatusWidget.Parse(text).Count);
    }

    [Fact]
    public void Empty_text_has_no_sites() => Assert.Empty(WebStatusWidget.Parse(null));
}
