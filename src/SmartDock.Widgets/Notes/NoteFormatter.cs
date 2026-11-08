using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SmartDock.Widgets.Notes;

public enum NoteContentKind
{
    /// <summary>Plain text / Markdown, possibly with ``` code blocks.</summary>
    Text,
    Json,
    Xml,
    Yaml,
}

/// <summary>A problem found by the checker; <see cref="Line"/>/<see cref="Column"/> are 1-based within the whole note.</summary>
public sealed record NoteIssue(int Line, int Column, string Message, string Context)
{
    public override string ToString() => $"Line {Line}, col {Column}: {Context} – {Message}";
}

public sealed record FormatResult(string Text, IReadOnlyList<NoteIssue> Issues, IReadOnlyList<string> Changes)
{
    public bool Changed { get; init; }
}

/// <summary>
/// Beautifier and checker for notes. Whole-note JSON/XML is pretty-printed; YAML is validated (re-printing YAML
/// would drop comments, so it isn't rewritten). Text/Markdown gets light, safe clean-up, and fenced code blocks
/// marked ```json / ```xml are formatted, ```yaml blocks validated, other code blocks left exactly as they are.
/// Formatting never "repairs" invalid JSON/XML: it reports where the problem is and leaves that part unchanged.
/// </summary>
public static partial class NoteFormatter
{
    private static readonly JsonDocumentOptions JsonRead = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static readonly JsonWriterOptions JsonWrite = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "ü", "→" etc. readable instead of ü
    };

    /// <summary>Guesses what the whole note is.</summary>
    public static NoteContentKind Detect(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return NoteContentKind.Text;
        }

        // "[todo] call Ali" is text, not JSON: arrays only count when they contain JSON-looking content.
        if ((t[0] == '{' && t[^1] == '}') || (t[0] == '[' && t[^1] == ']' && (t.Contains('"') || t.Contains('{'))))
        {
            return NoteContentKind.Json;
        }

        if (t[0] == '<' && t[^1] == '>')
        {
            return NoteContentKind.Xml;
        }

        return LooksLikeYaml(t) ? NoteContentKind.Yaml : NoteContentKind.Text;
    }

    /// <summary>Validation only (no changes): what Beautify would complain about.</summary>
    public static IReadOnlyList<NoteIssue> Check(string text) => Beautify(text).Issues;

    public static FormatResult Beautify(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var issues = new List<NoteIssue>();
        var changes = new List<string>();
        string result;

        switch (Detect(normalized))
        {
            case NoteContentKind.Json:
                result = FormatJson(normalized, 1, "JSON", issues) is { } json ? json : normalized;
                if (result != normalized)
                {
                    changes.Add("Formatted JSON");
                }

                break;
            case NoteContentKind.Xml:
                result = FormatXml(normalized, 1, "XML", issues) is { } xml ? xml : normalized;
                if (result != normalized)
                {
                    changes.Add("Formatted XML");
                }

                break;
            case NoteContentKind.Yaml:
                ValidateYaml(normalized, 1, "YAML", issues);
                result = TrimTrailingWhitespace(normalized, out var trimmed);
                if (trimmed > 0)
                {
                    changes.Add($"Removed trailing spaces on {trimmed} line{S(trimmed)}");
                }

                break;
            default:
                result = FormatText(normalized, issues, changes);
                break;
        }

        result = result.Trim('\n');
        return new FormatResult(result, issues, changes) { Changed = result != normalized.Trim('\n') };
    }

    // ------------------------------------------------------------------ text / markdown

    private static string FormatText(string text, List<NoteIssue> issues, List<string> changes)
    {
        var lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        int trimmed = 0, bullets = 0, headings = 0, tabs = 0, blanks = 0, numbered = 0;
        var formattedBlocks = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var fence = FenceStart().Match(lines[i]);
            if (fence.Success)
            {
                // Code block: find its end and handle the content by language.
                var marker = fence.Groups["marker"].Value;
                var language = fence.Groups["lang"].Value.Trim().ToLowerInvariant();
                var end = Array.FindIndex(lines, i + 1, l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal) && l.Trim().Trim(marker[0]).Length == 0);
                if (end < 0)
                {
                    issues.Add(new NoteIssue(i + 1, 1, "this code block is never closed (add a line with " + marker + ")", "Code block"));
                    output.AddRange(lines[i..]);
                    break;
                }

                var body = string.Join("\n", lines[(i + 1)..end]);
                var firstBodyLine = i + 2;
                string? formatted = null;
                switch (language)
                {
                    case "json" or "jsonc" or "json5":
                        formatted = FormatJson(body, firstBodyLine, "JSON block", issues);
                        break;
                    case "xml" or "xaml" or "csproj" or "svg":
                        formatted = FormatXml(body, firstBodyLine, "XML block", issues);
                        break;
                    case "yaml" or "yml":
                        ValidateYaml(body, firstBodyLine, "YAML block", issues); // validated, never rewritten (keeps comments)
                        break;
                }

                output.Add(lines[i].TrimEnd());
                if (formatted is not null && formatted != body)
                {
                    output.AddRange(formatted.Split('\n'));
                    formattedBlocks.Add($"{language.ToUpperInvariant()} block at line {i + 1}");
                }
                else
                {
                    output.AddRange(lines[(i + 1)..end]); // untouched, byte for byte
                }

                output.Add(lines[end].TrimEnd());
                i = end;
                continue;
            }

            var line = lines[i];
            var clean = line.TrimEnd();
            if (clean.Length != line.Length)
            {
                trimmed++;
            }

            // Leading tabs → 4 spaces (outside code blocks only).
            var leadingTabs = clean.Length - clean.TrimStart('\t').Length;
            if (leadingTabs > 0)
            {
                clean = new string(' ', leadingTabs * 4) + clean[leadingTabs..];
                tabs++;
            }

            // "#Title" → "# Title" (not "#hashtag" inside text: only at the very start of a line).
            var heading = HeadingWithoutSpace().Match(clean);
            if (heading.Success)
            {
                clean = heading.Groups[1].Value + " " + heading.Groups[2].Value;
                headings++;
            }

            // "* item", "+ item", "• item" → "- item".
            var bullet = OtherBullet().Match(clean);
            if (bullet.Success)
            {
                clean = bullet.Groups[1].Value + "- " + bullet.Groups[2].Value;
                bullets++;
            }

            // Blank line before a heading (Markdown renders it better and it's easier to scan).
            if (clean.StartsWith('#') && HeadingWithSpace().IsMatch(clean) && output.Count > 0 && output[^1].Length > 0)
            {
                output.Add(string.Empty);
            }

            // At most one empty line in a row.
            if (clean.Length == 0 && output.Count > 0 && output[^1].Length == 0)
            {
                blanks++;
                continue;
            }

            output.Add(clean);
        }

        numbered = RenumberOrderedLists(output);

        if (formattedBlocks.Count > 0)
        {
            changes.Add("Formatted " + string.Join(", ", formattedBlocks));
        }

        AddChange(changes, trimmed, "Removed trailing spaces on {0} line{1}");
        AddChange(changes, tabs, "Replaced leading tabs on {0} line{1}");
        AddChange(changes, headings, "Added a space after # in {0} heading{1}");
        AddChange(changes, bullets, "Unified {0} bullet{1} to \"- \"");
        AddChange(changes, numbered, "Renumbered {0} list item{1}");
        AddChange(changes, blanks, "Removed {0} extra empty line{1}");
        return string.Join("\n", output);
    }

    /// <summary>Renumbers consecutive "1." / "2)" items at the same indentation (code blocks excluded).</summary>
    private static int RenumberOrderedLists(List<string> lines)
    {
        var changed = 0;
        var inFence = false;
        string? fenceMarker = null;
        var counters = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var fence = FenceStart().Match(lines[i]);
            if (fence.Success && (!inFence || lines[i].TrimStart().StartsWith(fenceMarker!, StringComparison.Ordinal)))
            {
                inFence = !inFence;
                fenceMarker = fence.Groups["marker"].Value;
                counters.Clear();
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var item = OrderedItem().Match(lines[i]);
            if (!item.Success)
            {
                if (lines[i].Trim().Length == 0 || !lines[i].StartsWith(' '))
                {
                    // A blank line or unindented text ends the lists (keep counters for nested indented text).
                    if (lines[i].Trim().Length > 0)
                    {
                        counters.Clear();
                    }
                }

                continue;
            }

            var indent = item.Groups[1].Value.Length;
            foreach (var deeper in counters.Keys.Where(k => k > indent).ToList())
            {
                counters.Remove(deeper);
            }

            var expected = counters.TryGetValue(indent, out var last) ? last + 1 : int.Parse(item.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            counters[indent] = expected;
            var renumbered = $"{item.Groups[1].Value}{expected}{item.Groups[3].Value} {item.Groups[4].Value}";
            if (renumbered != lines[i])
            {
                lines[i] = renumbered;
                changed++;
            }
        }

        return changed;
    }

    // ------------------------------------------------------------------ JSON / XML / YAML

    private static string? FormatJson(string json, int firstLine, string context, List<NoteIssue> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonRead);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, JsonWrite))
            {
                doc.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n");
        }
        catch (JsonException ex)
        {
            var line = (int)(ex.LineNumber ?? 0) + firstLine;
            var column = (int)(ex.BytePositionInLine ?? 0) + 1;
            issues.Add(new NoteIssue(line, column, CleanJsonMessage(ex.Message), context));
            return null;
        }
    }

    private static string? FormatXml(string xml, int firstLine, string context, List<NoteIssue> issues)
    {
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
            var body = doc.ToString(SaveOptions.None);
            return (doc.Declaration is null ? body : doc.Declaration + "\n" + body).Replace("\r\n", "\n");
        }
        catch (XmlException ex)
        {
            issues.Add(new NoteIssue(ex.LineNumber - 1 + firstLine, Math.Max(1, ex.LinePosition), ex.Message.Split(" Line ")[0].Trim(), context));
            return null;
        }
    }

    /// <summary>True when the YAML parses; otherwise records the problem.</summary>
    private static bool ValidateYaml(string yaml, int firstLine, string context, List<NoteIssue> issues)
    {
        try
        {
            new YamlStream().Load(new StringReader(yaml));
            return true;
        }
        catch (YamlException ex)
        {
            var message = ex.Message;
            var cut = message.LastIndexOf("): ", StringComparison.Ordinal);
            issues.Add(new NoteIssue((int)ex.Start.Line - 1 + firstLine, (int)Math.Max(1, ex.Start.Column), cut >= 0 ? message[(cut + 3)..] : message, context));
            return false;
        }
    }

    private static bool LooksLikeYaml(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        if (lines.Count < 3 || lines.Any(l => l.StartsWith("```", StringComparison.Ordinal)))
        {
            return false;
        }

        // Kubernetes/compose-style documents, or every line is "key: value" / "- item" with some nesting.
        if (lines[0] == "---" || lines.Any(l => l.StartsWith("apiVersion:", StringComparison.Ordinal)))
        {
            return true;
        }

        return lines.All(l => YamlLine().IsMatch(l)) && lines.Any(l => l.StartsWith("  ", StringComparison.Ordinal));
    }

    private static string TrimTrailingWhitespace(string text, out int count)
    {
        var lines = text.Split('\n');
        count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd();
            if (t.Length != lines[i].Length)
            {
                lines[i] = t;
                count++;
            }
        }

        return string.Join("\n", lines);
    }

    private static string CleanJsonMessage(string message)
    {
        // "'x' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 3 | BytePositionInLine: 4."
        var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).Trim();
    }

    private static void AddChange(List<string> changes, int count, string format)
    {
        if (count > 0)
        {
            changes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, count, S(count)));
        }
    }

    private static string S(int count) => count == 1 ? string.Empty : "s";

    [GeneratedRegex(@"^\s*(?<marker>```+|~~~+)(?<lang>[^`]*)$")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^(#{1,6})([^#\s!].*)$")]
    private static partial Regex HeadingWithoutSpace();

    [GeneratedRegex(@"^#{1,6} \S")]
    private static partial Regex HeadingWithSpace();

    [GeneratedRegex(@"^(\s*)(?:[*+]|•)\s+(.*)$")]
    private static partial Regex OtherBullet();

    [GeneratedRegex(@"^(\s*)(\d{1,4})([.)])\s+(.*)$")]
    private static partial Regex OrderedItem();

    [GeneratedRegex(@"^\s*(- |-$|[A-Za-z0-9_.""'/-]+:( |$))")]
    private static partial Regex YamlLine();
}
