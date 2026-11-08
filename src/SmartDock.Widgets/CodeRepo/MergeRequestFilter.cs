using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartDock.Widgets.CodeRepo;

/// <summary>One-click filters shown as chips in the CodeHub popup and the search window.</summary>
public enum MrQuickFilter
{
    All,
    ToReview,
    Mine,
    Conflicts,
    Stale,
}

/// <summary>
/// Search and filtering for merge requests. Pure (no I/O), shared by the popup and the search window.
/// <para>
/// Query syntax: every word must match (title, project, !number, author, branches, labels, reviewers, assignees).
/// Qualifiers narrow a word to one field: <c>author:ali</c>, <c>label:bug</c>, <c>branch:feature</c> (source or
/// target), <c>target:master</c>, <c>project:web</c>, <c>reviewer:veli</c> (reviewers or assignees),
/// <c>is:draft|conflict|stale|review|mine</c>, <c>!123</c>. Put <c>-</c> in front to exclude
/// (<c>-is:draft</c>, <c>-label:wip</c>) and quotes around phrases (<c>"null pointer"</c>).
/// </para>
/// </summary>
public static partial class MergeRequestFilter
{
    public static string Label(MrQuickFilter filter) => filter switch
    {
        MrQuickFilter.ToReview => "To review",
        MrQuickFilter.Mine => "Mine",
        MrQuickFilter.Conflicts => "Conflicts",
        MrQuickFilter.Stale => $"Open {MergeRequestItem.StaleAfterDays}d+",
        _ => "All",
    };

    public static bool Passes(MergeRequestItem item, MrQuickFilter filter) => filter switch
    {
        MrQuickFilter.ToReview => item.Role == MrRole.Review,
        MrQuickFilter.Mine => item.Role == MrRole.Mine,
        MrQuickFilter.Conflicts => item.HasConflicts,
        MrQuickFilter.Stale => item.IsStale,
        _ => true,
    };

    /// <summary>True when the MR passes the quick filter, the drafts toggle and every term of the query.</summary>
    public static bool Matches(MergeRequestItem item, string? query, MrQuickFilter filter = MrQuickFilter.All, bool hideDrafts = false)
    {
        if (!Passes(item, filter) || (hideDrafts && item.Info.Draft))
        {
            return false;
        }

        foreach (var (field, value, negate) in Parse(query))
        {
            if (MatchesTerm(item, field, value) == negate)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits a query into (field, value, negate) terms; field is empty for plain words. Public for tests.</summary>
    public static IReadOnlyList<(string Field, string Value, bool Negate)> Parse(string? query)
    {
        var terms = new List<(string, string, bool)>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return terms;
        }

        foreach (Match m in TermRegex().Matches(query))
        {
            var negate = m.Groups["neg"].Success;
            var field = m.Groups["field"].Success ? m.Groups["field"].Value.ToLowerInvariant() : string.Empty;
            var value = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["word"].Value;
            if (value.Length == 0)
            {
                continue;
            }

            if (field.Length == 0 && (value.StartsWith('!') || value.StartsWith('#')) && long.TryParse(value.AsSpan(1), out _))
            {
                field = "iid";
                value = value[1..];
            }

            terms.Add((field, value, negate));
        }

        return terms;
    }

    private static bool MatchesTerm(MergeRequestItem item, string field, string value)
    {
        var info = item.Info;
        bool Has(string? text) => text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);
        bool Any(IEnumerable<string> list) => list.Any(Has);

        return field switch
        {
            "author" => Has(info.Author) || Has(info.Extras?.AuthorName),
            "label" or "labels" => info.Extras is { } e && Any(e.Labels),
            "branch" or "source" => Has(info.SourceBranch) || (field == "branch" && Has(info.TargetBranch)),
            "target" => Has(info.TargetBranch),
            "project" or "repo" => Has(info.Project),
            "reviewer" or "assignee" => Any(info.Reviewers) || Any(info.Assignees),
            "title" => Has(info.Title),
            "iid" or "id" => info.Iid.ToString(System.Globalization.CultureInfo.InvariantCulture) == value.TrimStart('!', '#'),
            "is" => value.ToLowerInvariant() switch
            {
                "draft" or "wip" => info.Draft,
                "conflict" or "conflicts" => item.HasConflicts,
                "stale" or "old" => item.IsStale,
                "review" => item.Role == MrRole.Review,
                "mine" => item.Role == MrRole.Mine,
                _ => false,
            },
            _ => Has(info.Title) || Has(info.Project) || Has(info.Author) || Has(info.Extras?.AuthorName)
                 || Has(info.SourceBranch) || Has(info.TargetBranch) || Any(info.Reviewers) || Any(info.Assignees)
                 || (info.Extras is { } x && Any(x.Labels)) || Has(item.Number),
        };
    }

    [GeneratedRegex("""(?<neg>-)?(?:(?<field>[A-Za-z]+):)?(?:"(?<quoted>[^"]*)"|(?<word>[^\s"]+))""")]
    private static partial Regex TermRegex();
}

/// <summary>A filter chip in the popup ("To review 3").</summary>
public sealed class MrFilterChip(MrQuickFilter filter, int count, bool isActive, Action<MrQuickFilter> select) : ObservableObject
{
    public MrQuickFilter Filter { get; } = filter;

    public string Text => Filter == MrQuickFilter.All ? $"All {count}" : $"{MergeRequestFilter.Label(Filter)} {count}";

    public bool IsActive { get; } = isActive;

    /// <summary>Chips with nothing to show are dimmed (but stay clickable when active, to switch back).</summary>
    public bool IsEmpty => count == 0 && Filter != MrQuickFilter.All;

    public RelayCommand SelectCommand { get; } = new(() => select(filter));
}

/// <summary>Everything the search window needs from one CodeHub widget.</summary>
/// <param name="Refresh">Asks the widget to reload from the server now (the index is updated when it's done).</param>
public sealed record MergeRequestSearchSource(string InstanceId, string Title, IReadOnlyList<MergeRequestItem> Items, DateTimeOffset AsOf, Action? Refresh = null);

/// <summary>Published by a CodeHub widget to open the (keyboard-capable) search window.</summary>
public sealed record OpenMergeRequestSearchRequested(IReadOnlyList<MergeRequestSearchSource> Sources, string? PreferredInstanceId);

/// <summary>Latest open MRs of every CodeHub widget, so one search window covers all servers.</summary>
public static class MergeRequestIndex
{
    private static readonly ConcurrentDictionary<string, MergeRequestSearchSource> Sources = new(StringComparer.Ordinal);

    /// <summary>Raised (on the widget's thread) whenever a widget publishes new data or goes away.</summary>
    public static event Action? Changed;

    public static void Update(MergeRequestSearchSource source)
    {
        Sources[source.InstanceId] = source;
        Changed?.Invoke();
    }

    public static void Remove(string instanceId)
    {
        if (Sources.TryRemove(instanceId, out _))
        {
            Changed?.Invoke();
        }
    }

    public static IReadOnlyList<MergeRequestSearchSource> All => Sources.Values.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
}
