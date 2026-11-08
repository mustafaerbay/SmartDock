using SmartDock.Widgets.Productivity;

namespace SmartDock.Widgets.Tests;

public class QuickFoldersTests
{
    [Fact]
    public void Parses_named_and_bare_paths_skips_urls_and_duplicates()
    {
        var items = QuickFoldersWidget.Parse(
            "Projects | D:\\work\\projects\n" +
            "\\\\fileserver\\team\\docs\n" +
            "# comment\n" +
            "Site | https://example.com\n" +
            "Downloads | %USERPROFILE%\\Downloads\n" +
            "Again | d:\\WORK\\projects\n" +
            "\"C:\\Program Files\"\n");

        Assert.Collection(
            items,
            i => Assert.Equal(("Projects", "D:\\work\\projects"), (i.Name, i.Path)),
            i => Assert.Equal("docs", i.Name),
            i => Assert.Equal(("Downloads", "%USERPROFILE%\\Downloads"), (i.Name, i.Path)),
            i => Assert.Equal("C:\\Program Files", i.Path));
    }

    [Fact]
    public void Format_round_trips()
    {
        var original = QuickFoldersWidget.Parse("A | C:\\a\nB | \\\\srv\\b");
        Assert.Equal(original, QuickFoldersWidget.Parse(QuickFoldersWidget.Format(original)));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Downloads\\", "Downloads")]
    [InlineData("C:\\", "C:")]
    [InlineData("\\\\server\\share", "share")]
    public void Default_names(string path, string expected) => Assert.Equal(expected, QuickFoldersWidget.DefaultName(path));
}
