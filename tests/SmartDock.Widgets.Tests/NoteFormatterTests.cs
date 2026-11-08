using SmartDock.Widgets.Notes;

namespace SmartDock.Widgets.Tests;

public class NoteFormatterTests
{
    [Fact]
    public void Formats_whole_note_json_and_keeps_unicode_readable()
    {
        var result = NoteFormatter.Beautify("{\"name\":\"Müşteri\",\"tags\":[1,2],}");

        Assert.True(result.Changed);
        Assert.Empty(result.Issues);
        Assert.Equal("{\n  \"name\": \"Müşteri\",\n  \"tags\": [\n    1,\n    2\n  ]\n}", result.Text);
    }

    [Fact]
    public void Reports_json_error_position_and_leaves_text_unchanged()
    {
        const string broken = "{\n  \"a\": 1\n  \"b\": 2\n}";
        var result = NoteFormatter.Beautify(broken);

        Assert.False(result.Changed);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(3, issue.Line);
        Assert.Equal("JSON", issue.Context);
    }

    [Fact]
    public void Formats_xml()
    {
        var result = NoteFormatter.Beautify("<a><b x=\"1\">t</b><c/></a>");
        Assert.Equal("<a>\n  <b x=\"1\">t</b>\n  <c />\n</a>", result.Text);
    }

    [Fact]
    public void Validates_yaml_without_rewriting_it()
    {
        const string yaml = "apiVersion: v1\nkind: Pod\nmetadata:\n  name: x   \n  labels: [a, b\n";
        var result = NoteFormatter.Beautify(yaml);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("YAML", issue.Context);
        Assert.Contains("  name: x\n", result.Text); // only trailing spaces trimmed
    }

    [Fact]
    public void Tidies_markdown_text()
    {
        const string note =
            "#Meeting notes   \n" +
            "* first\n" +
            "+ second\n" +
            "\n\n\n" +
            "1. one\n" +
            "1. two\n" +
            "5. three\n" +
            "##Next\n" +
            "\tindented";
        var result = NoteFormatter.Beautify(note);

        Assert.Equal(
            "# Meeting notes\n" +
            "- first\n" +
            "- second\n" +
            "\n" +
            "1. one\n" +
            "2. two\n" +
            "3. three\n" +
            "\n" +
            "## Next\n" +
            "    indented",
            result.Text);
        Assert.Contains(result.Changes, c => c.StartsWith("Renumbered", StringComparison.Ordinal));
    }

    [Fact]
    public void Formats_json_code_blocks_and_leaves_other_code_untouched()
    {
        const string note =
            "Payload:\n" +
            "```json\n" +
            "{\"id\":7}\n" +
            "```\n" +
            "```bash\n" +
            "*   keep   this   \n" +
            "```";
        var result = NoteFormatter.Beautify(note);

        Assert.Contains("```json\n{\n  \"id\": 7\n}\n```", result.Text);
        Assert.Contains("*   keep   this   \n", result.Text); // bash block byte-for-byte
    }

    [Fact]
    public void Reports_errors_inside_code_blocks_with_note_line_numbers()
    {
        const string note = "intro\n```json\n{\n  \"a\": ,\n}\n```";
        var issue = Assert.Single(NoteFormatter.Check(note));
        Assert.Equal(("JSON block", 4), (issue.Context, issue.Line));
    }

    [Fact]
    public void Reports_unclosed_code_block() =>
        Assert.Contains(NoteFormatter.Check("text\n```python\nprint(1)"), i => i.Line == 2 && i.Message.Contains("never closed"));

    [Theory]
    [InlineData("[todo] call Ali", NoteContentKind.Text)]
    [InlineData("[1, \"x\"]", NoteContentKind.Json)]
    [InlineData("Meeting: tomorrow\nRoom: 4", NoteContentKind.Text)]
    [InlineData("#!/bin/bash", NoteContentKind.Text)]
    public void Detects_content(string text, NoteContentKind kind) => Assert.Equal(kind, NoteFormatter.Detect(text));

    [Fact]
    public void Already_tidy_text_is_unchanged() =>
        Assert.False(NoteFormatter.Beautify("# Title\n\n- a\n- b").Changed);
}
