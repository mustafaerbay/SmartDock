using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.CodeRepo;

public sealed record WatchedProject(string Name, string Project, string? Branch);

public enum MrRole
{
    Review,
    Mine,
    Other,
}

/// <summary>Merge-request row in the popup.</summary>
public sealed class MergeRequestItem(MergeRequestInfo info, MrRole role, bool showProject = true)
{
    public MergeRequestInfo Info { get; } = info;

    public MrRole Role { get; } = role;

    public string Title => (Info.Draft ? "[Draft] " : string.Empty) + Info.Title;

    /// <summary>"!12" for GitLab/CodeHub, "#12" for GitHub.</summary>
    public string Number => (Info.Extras?.NumberPrefix ?? "!") + Info.Iid;

    public string Detail =>
        (showProject ? $"{Info.Project} " : string.Empty) + $"{Number} · {Info.Author}"
        + (BranchesText is { } branches ? " · " + branches : string.Empty)
        + (Info.HasConflicts ? " · conflicts" : string.Empty)
        + (Info.Updated is { } u ? " · " + RelativeTime.Ago(u, DateTimeOffset.Now) : string.Empty);

    public bool HasConflicts => Info.HasConflicts;

    // ---- Hover quick view -------------------------------------------------------------------------------------

    public string FullTitle => Info.Title;

    public string Reference => $"{Info.Project} {Number}" + (Info.Draft ? " · Draft" : string.Empty);

    public string AuthorText => Info.Extras?.AuthorName is { Length: > 0 } name && !string.Equals(name, Info.Author, StringComparison.OrdinalIgnoreCase)
        ? $"{name} ({Info.Author})"
        : Info.Author;

    /// <summary>"feature → master"; null when the server's list response doesn't include branches (GitHub search).</summary>
    public string? BranchesText => Info.SourceBranch.Length == 0 && Info.TargetBranch.Length == 0 ? null : $"{Info.SourceBranch} → {Info.TargetBranch}";

    /// <summary>Null when the source doesn't report reviewers (GitHub search results), rather than a misleading "—".</summary>
    public string? ReviewersText => Info.Reviewers.Count > 0 ? string.Join(", ", Info.Reviewers) : Info.Extras?.PartialDetails == true ? null : "—";

    public string AssigneesText => Info.Assignees.Count > 0 ? string.Join(", ", Info.Assignees) : "—";

    public string DatesText =>
        (Info.Created is { } c ? $"Opened {c.ToLocalTime():g} ({FormatAge(DateTimeOffset.Now - c, true)} ago)" : string.Empty)
        + (Info.Created is not null && Info.Updated is not null ? "\n" : string.Empty)
        + (Info.Updated is { } u ? $"Updated {u.ToLocalTime():g}" : string.Empty);

    /// <summary>Null when the source doesn't report merge status (GitHub search results).</summary>
    public string? MergeStatusText => Info.Extras?.PartialDetails == true && Info.Extras.MergeStatus is null && !Info.HasConflicts
        ? null
        : FriendlyMergeStatus(Info.Extras?.MergeStatus, Info.HasConflicts);

    public string? LabelsText => Info.Extras is { Labels.Count: > 0 } e ? string.Join(" · ", e.Labels) : null;

    public string? Milestone => Info.Extras?.Milestone;

    /// <summary>"👍 2 · 👎 0 · 💬 5"-style counts in plain text; null when the server sent none.</summary>
    public string? ActivityText
    {
        get
        {
            if (Info.Extras is not { } e)
            {
                return null;
            }

            var parts = new List<string>();
            if (e.Upvotes is { } up)
            {
                parts.Add($"{up} up");
            }

            if (e.Downvotes is { } down and > 0)
            {
                parts.Add($"{down} down");
            }

            if (e.Comments is { } notes)
            {
                parts.Add(notes == 1 ? "1 comment" : $"{notes} comments");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    /// <summary>First part of the description, lightly cleaned of Markdown noise; null when there's none.</summary>
    public string? DescriptionPreview => PreviewDescription(Info.Extras?.Description);

    /// <summary>Description excerpt for the quick view (max ~12 lines / 600 chars); public for tests.</summary>
    public static string? PreviewDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var lines = new List<string>();
        var length = 0;
        var truncated = false;
        foreach (var raw in description.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) || trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue; // template comments and code fences add nothing in a tooltip
            }

            line = line.Replace("**", string.Empty).Replace("__", string.Empty);
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue; // collapse blank runs
            }

            if (lines.Count >= 12 || length + line.Length > 600)
            {
                truncated = true;
                break;
            }

            lines.Add(line);
            length += line.Length;
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        return string.Join("\n", lines) + (truncated ? "\n…" : string.Empty);
    }

    /// <summary>GitLab merge_status / detailed_merge_status in words; public for tests.</summary>
    public static string FriendlyMergeStatus(string? status, bool hasConflicts) => status switch
    {
        _ when hasConflicts => "Has conflicts",
        "mergeable" or "can_be_merged" => "Ready to merge",
        "cannot_be_merged" or "conflict" => "Has conflicts",
        "not_approved" or "approvals_syncing" => "Waiting for approval",
        "ci_must_pass" or "ci_still_running" => "Waiting for the pipeline",
        "discussions_not_resolved" => "Unresolved discussions",
        "draft_status" => "Draft",
        "need_rebase" => "Needs a rebase",
        "blocked_status" => "Blocked by another MR",
        "checking" or "unchecked" or "cannot_be_merged_recheck" or "preparing" => "Checking mergeability…",
        null or "" => "Unknown",
        _ => status!.Replace('_', ' '),
    };

    /// <summary>Open for this many days or more counts as stale (age shown in the warning colour).</summary>
    public const int StaleAfterDays = 7;

    /// <summary>How long the MR has been open: "25 min", "5 h", "3 d", "6 w". Empty when the server didn't say.</summary>
    public string Age => Info.Created is { } created ? FormatAge(DateTimeOffset.Now - created) : string.Empty;

    public bool HasAge => Info.Created is not null;

    public bool IsStale => Info.Created is { } created && DateTimeOffset.Now - created >= TimeSpan.FromDays(StaleAfterDays);

    /// <summary>Tooltip for the age: "Opened 02.11.2026 14:05 (3 days ago)".</summary>
    public string OpenedText => Info.Created is { } created
        ? $"Opened {created.ToLocalTime():g} ({FormatAge(DateTimeOffset.Now - created, true)} ago)"
        : string.Empty;

    /// <summary>Compact or long age text; public for tests.</summary>
    public static string FormatAge(TimeSpan span, bool verbose = false)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var (value, shortUnit, longUnit) = span switch
        {
            _ when span < TimeSpan.FromHours(1) => (Math.Max(1, (int)span.TotalMinutes), "min", "minute"),
            _ when span < TimeSpan.FromDays(1) => ((int)span.TotalHours, "h", "hour"),
            _ when span < TimeSpan.FromDays(14) => ((int)span.TotalDays, "d", "day"),
            _ when span < TimeSpan.FromDays(365) => ((int)(span.TotalDays / 7), "w", "week"),
            _ => ((int)(span.TotalDays / 365), "y", "year"),
        };
        return verbose ? $"{value} {longUnit}{(value == 1 ? string.Empty : "s")}" : $"{value} {shortUnit}";
    }

    /// <summary>Small tag shown when MRs of different roles share a group: "Review" or "Yours".</summary>
    public string? RoleLabel => !showProject ? Role switch { MrRole.Review => "Review", MrRole.Mine => "Yours", _ => null } : null;

    public bool IsReview => Role == MrRole.Review;

    public RelayCommand OpenCommand { get; } = new(() => SafeLauncher.OpenWebUrl(info.Url));
}

/// <summary>Pipeline row in the popup.</summary>
public sealed class PipelineItem(PipelineInfo info)
{
    public PipelineInfo Info { get; } = info;

    public string Title => $"{Info.Project} · {Info.Ref}";

    public string Detail => Info.Status + (Info.Updated is { } u ? " · " + RelativeTime.Ago(u, DateTimeOffset.Now) : string.Empty);

    public bool Failed => Info.Failed;

    public bool Running => Info.Running;

    public bool Succeeded => Info.Status == "success";

    public RelayCommand OpenCommand { get; } = new(() =>
    {
        if (info.Url is { } url)
        {
            SafeLauncher.OpenWebUrl(url);
        }
    });
}

/// <summary>
/// Merge requests and pipelines from a GitLab-compatible server: Huawei's internal CodeHub
/// (<c>/api/v4/projects/{id}/merge_requests</c>), self-hosted GitLab or gitlab.com. Read-only; the access
/// token is stored encrypted (DPAPI). One widget per server/account.
/// </summary>
public sealed class CodeRepoWidget(ISecretStore secrets, HttpClient http) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "code-repo";
    public const string TokenKey = "token";

    private const string ShowMine = "Only MRs that involve me";
    private const string ShowAll = "All open MRs in these projects";

    private readonly Dictionary<string, (string Name, string? Url)> _projectNames = new(StringComparer.Ordinal);
    private HashSet<string>? _knownReviewKeys;
    private Dictionary<string, string>? _pipelineStates;

    private string _title = "CodeHub";
    private string _summary = "Not set up";
    private int _reviewCount;
    private bool _anyFailing;
    private IReadOnlyList<MergeRequestItem> _reviews = [];
    private IReadOnlyList<MergeRequestItem> _mine = [];
    private IReadOnlyList<MergeRequestItem> _others = [];
    private IReadOnlyList<PipelineItem> _pipelines = [];
    private string? _note;
    private RelayCommand? _refresh;
    private MergeRequestBoard? _board;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "CodeHub / GitLab",
        "Merge requests waiting for your review, your own MRs and pipeline status from CodeHub or any GitLab-compatible server.",
        WidgetCategory.Developer,
        "",
        DockRegion.Center,
        sp => new CodeRepoWidget(sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<HttpClient>()))
    {
        AllowMultiple = true,
    };

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Card text, e.g. "2 to review · 1 failing".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public int ReviewCount
    {
        get => _reviewCount;
        private set => SetProperty(ref _reviewCount, value);
    }

    public bool AnyFailing
    {
        get => _anyFailing;
        private set => SetProperty(ref _anyFailing, value);
    }

    public IReadOnlyList<MergeRequestItem> Reviews
    {
        get => _reviews;
        private set => SetProperty(ref _reviews, value);
    }

    public IReadOnlyList<MergeRequestItem> Mine
    {
        get => _mine;
        private set => SetProperty(ref _mine, value);
    }

    public IReadOnlyList<MergeRequestItem> Others
    {
        get => _others;
        private set => SetProperty(ref _others, value);
    }

    public IReadOnlyList<PipelineItem> Pipelines
    {
        get => _pipelines;
        private set => SetProperty(ref _pipelines, value);
    }

    /// <summary>Grouping, filters, search and pipelines shown in the popup (shared with the GitHub widget).</summary>
    public MergeRequestBoard Board => _board ??= new MergeRequestBoard(() => Settings, () => InstanceId, () => Context.Events,
        refresh: () => Context.RequestRefresh?.Invoke());

    /// <summary>Per-project problems that don't stop the widget ("group/x: Not found (404)").</summary>
    public string? Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    public RelayCommand RefreshNowCommand => _refresh ??= new RelayCommand(() => Context.RequestRefresh?.Invoke());

    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Settings.Get("refreshMinutes", 3.0), 1, 120));

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Refresh now", RefreshNowCommand),
        new("Search merge requests…", Board.SearchCommand),
    ];

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("server", "Server address", SettingFieldKind.Text,
            "e.g. https://codehub.example.com or https://gitlab.com – the /api/v4 part is added automatically."),
        new(TokenKey, "Access token", SettingFieldKind.Secret,
            "A personal access token with read access to the API (GitLab: read_api). Stored encrypted with Windows DPAPI."),
        new("authHeader", "Send the token as", SettingFieldKind.Choice,
            "CodeHub and GitLab normally use PRIVATE-TOKEN.", Choices: [GitLabApi.PrivateTokenHeader, GitLabApi.BearerHeader], DefaultText: GitLabApi.PrivateTokenHeader),
        new("projects", "Projects (one per line: Name | Project ID or path | Branch for pipelines)", SettingFieldKind.MultilineText,
            "Project = numeric ID or group/subgroup/project. Name and branch are optional; without a branch the latest pipeline of any branch is shown.\n" +
            "e.g.  Gateway | 12345 | master",
            DefaultText: string.Empty),
        new("username", "My username (optional)", SettingFieldKind.Text,
            "Only needed if the server doesn't support /api/v4/user; used to find MRs that are yours or assigned to you."),
        new("show", "Show", SettingFieldKind.Choice, Choices: [ShowMine, ShowAll], DefaultText: ShowMine),
        new("groupBy", "Group merge requests by", SettingFieldKind.Choice,
            "Project: one collapsible group per project with its pipeline. Category: needs review / yours / others. Also switchable in the popup.",
            Choices: [nameof(MrGrouping.Project), nameof(MrGrouping.Category)], DefaultText: nameof(MrGrouping.Project)),
        new("displayName", "Name on the dock (optional)", SettingFieldKind.Text, "Defaults to the server's host name."),
        new("refreshMinutes", "Refresh every (minutes)", SettingFieldKind.Number, DefaultNumber: 3),
        new("notify", "Notify me about review requests and failed pipelines", SettingFieldKind.Toggle, DefaultToggle: true),
    ];

    public void OnPopupOpened()
    {
    }

    protected override void OnSettingChanged(string key)
    {
        // View-only settings (grouping, filters): regroup what we already have, no network round-trip.
        if (Board.HandleSettingChanged(key))
        {
            return;
        }

        if (key is "server" or "projects" or "username" or TokenKey)
        {
            lock (_projectNames)
            {
                _projectNames.Clear();
            }

            _knownReviewKeys = null; // New baseline: don't notify about everything that's already open.
            _pipelineStates = null;
        }

        base.OnSettingChanged(key);
    }

    /// <summary>Parses the project list; public for tests.</summary>
    public static IReadOnlyList<WatchedProject> ParseProjects(string? text)
    {
        var list = new List<WatchedProject>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var (name, project, branch) = parts.Length switch
            {
                1 => (string.Empty, parts[0], (string?)null),
                2 => (parts[0], parts[1], null),
                _ => (parts[0], parts[1], parts[2].Length > 0 ? parts[2] : null),
            };

            // Accept a pasted project URL: https://host/group/project(/-/...) -> group/project
            if (Uri.TryCreate(project, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal))
            {
                var path = url.AbsolutePath.Trim('/');
                var dash = path.IndexOf("/-/", StringComparison.Ordinal);
                project = Uri.UnescapeDataString(dash >= 0 ? path[..dash] : path);
            }

            project = project.Trim('/');
            if (project.Length == 0 || list.Any(p => p.Project == project && p.Branch == branch))
            {
                continue;
            }

            list.Add(new WatchedProject(name, project, branch));
        }

        return list;
    }

    /// <summary>Which bucket an MR belongs to for <paramref name="me"/>; null when it doesn't involve me.</summary>
    public static MrRole? Classify(MergeRequestInfo mr, string? me)
    {
        if (string.IsNullOrEmpty(me))
        {
            return null;
        }

        if (string.Equals(mr.Author, me, StringComparison.OrdinalIgnoreCase))
        {
            return MrRole.Mine;
        }

        return mr.Reviewers.Concat(mr.Assignees).Any(u => string.Equals(u, me, StringComparison.OrdinalIgnoreCase)) ? MrRole.Review : null;
    }

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var server = Settings.Get("server", string.Empty).Trim();
        var token = secrets.Get(WidgetSecrets.Name(InstanceId, TokenKey));
        var projects = ParseProjects(Settings.Get("projects", string.Empty));
        Uri apiRoot;
        try
        {
            apiRoot = server.Length > 0 ? GitLabApi.ApiRoot(server) : throw new RepoApiException("Add the server address in Configure….");
        }
        catch (RepoApiException ex)
        {
            Summary = "Not set up";
            SetUnavailable(ex.Message);
            return;
        }

        Title = NonEmpty(Settings.Get("displayName", string.Empty)) ?? apiRoot.Host;
        if (string.IsNullOrWhiteSpace(token))
        {
            Summary = "No token";
            SetUnavailable("Add an access token: right-click › Configure….");
            return;
        }

        if (projects.Count == 0)
        {
            Summary = "No projects";
            SetUnavailable("Add the projects to watch in Configure….");
            return;
        }

        var api = new GitLabApi(http, apiRoot, token, Settings.Get("authHeader", GitLabApi.PrivateTokenHeader));
        try
        {
            var user = await api.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            var me = NonEmpty(Settings.Get("username", string.Empty)) ?? user?.Username;

            var problems = new List<string>();
            var results = await Task.WhenAll(projects.Select(p => LoadProjectAsync(api, p, problems, cancellationToken))).ConfigureAwait(false);
            if (results.All(r => r is null))
            {
                // Every project failed: report the first reason as the widget's state.
                throw new RepoApiException(problems.FirstOrDefault() ?? "No project could be loaded.");
            }

            var mrs = results.Where(r => r is not null).SelectMany(r => r!.Value.Mrs).DistinctBy(m => m.Key).ToList();
            var pipelines = results.Where(r => r?.Pipeline is not null).Select(r => r!.Value.Pipeline!).ToList();

            var showAll = Settings.Get("show", ShowMine) == ShowAll;
            var reviews = new List<MergeRequestItem>();
            var mine = new List<MergeRequestItem>();
            var others = new List<MergeRequestItem>();
            foreach (var mr in mrs.OrderByDescending(m => m.Updated))
            {
                switch (Classify(mr, me))
                {
                    case MrRole.Review when !mr.Draft:
                        reviews.Add(new MergeRequestItem(mr, MrRole.Review));
                        break;
                    case MrRole.Mine:
                        mine.Add(new MergeRequestItem(mr, MrRole.Mine));
                        break;
                    default:
                        if (showAll || me is null)
                        {
                            others.Add(new MergeRequestItem(mr, MrRole.Other));
                        }

                        break;
                }
            }

            Reviews = reviews;
            Mine = mine;
            Others = others;
            Board.Update(
                reviews.Concat(mine).Concat(others).Select(i => (i.Info, i.Role)).ToList(),
                pipelines,
                results.Where(r => r is not null).Select(r => r!.Value.Name).ToList());

            // Search covers every open MR in the watched projects, whatever the popup's "Show" setting is.
            var everything = mrs.OrderByDescending(m => m.Updated)
                .Select(m => new MergeRequestItem(m, Classify(m, me) is { } role && !(role == MrRole.Review && m.Draft) ? role : MrRole.Other))
                .ToList();
            Board.Publish(Title, everything);
            Pipelines = pipelines.Select(p => new PipelineItem(p)).ToList();
            ReviewCount = reviews.Count;
            var failing = pipelines.Count(p => p.Failed);
            AnyFailing = failing > 0;
            Summary = string.Join(" · ", new[]
            {
                me is null ? $"{others.Count} open MRs" : $"{reviews.Count} to review",
                mine.Count > 0 ? $"{mine.Count} mine" : null,
                failing > 0 ? $"{failing} failing" : null,
            }.Where(s => s is not null));

            Note = (me is null ? "Couldn't tell who you are (no /user API): set 'My username' in Configure… to see review requests. " : string.Empty)
                + (problems.Count > 0 ? string.Join(" · ", problems) : string.Empty);
            if (Note.Length == 0)
            {
                Note = null;
            }

            NotifyChanges(reviews, pipelines);
            SetReady();
            LastUpdated = DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is RepoApiException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Summary = "Unreachable";
            SetDisconnected(ex switch
            {
                RepoApiException => ex.Message,
                TaskCanceledException => "The server didn't answer in time.",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                    "TLS/certificate error – the server's certificate isn't trusted by Windows.",
                _ => "Can't reach the server: " + ex.Message,
            });
        }
    }

    private async Task<(string Name, IReadOnlyList<MergeRequestInfo> Mrs, PipelineInfo? Pipeline)?> LoadProjectAsync(
        GitLabApi api, WatchedProject project, List<string> problems, CancellationToken ct)
    {
        try
        {
            (string Name, string? Url) meta;
            bool cached;
            lock (_projectNames)
            {
                cached = _projectNames.TryGetValue(project.Project, out meta);
            }

            if (!cached)
            {
                meta = await api.GetProjectAsync(project.Project, ct).ConfigureAwait(false);
                lock (_projectNames)
                {
                    _projectNames[project.Project] = meta;
                }
            }

            var name = project.Name.Length > 0 ? project.Name : ShortName(meta.Name);
            var mrsTask = api.GetOpenMergeRequestsAsync(project.Project, name, ct);
            var pipelineTask = api.GetLatestPipelineAsync(project.Project, name, project.Branch, ct);
            await Task.WhenAll(mrsTask, pipelineTask).ConfigureAwait(false);
            return (name, mrsTask.Result, pipelineTask.Result);
        }
        catch (RepoApiException ex) when (ex.Status is not System.Net.HttpStatusCode.Unauthorized)
        {
            lock (problems)
            {
                problems.Add($"{(project.Name.Length > 0 ? project.Name : project.Project)}: {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// Notifies about MRs newly needing my review and pipelines that just failed or recovered.
    /// The first successful refresh only records the baseline, so starting SmartDock doesn't flood alerts.
    /// </summary>
    private void NotifyChanges(IReadOnlyList<MergeRequestItem> reviews, IReadOnlyList<PipelineInfo> pipelines)
    {
        var reviewKeys = reviews.Select(r => r.Info.Key).ToHashSet(StringComparer.Ordinal);
        var pipelineStates = pipelines.GroupBy(p => $"{p.Project}@{p.Ref}").ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
        var firstRun = _knownReviewKeys is null;
        var newReviews = firstRun
            ? new List<MergeRequestItem>()
            : reviews.Where(r => !_knownReviewKeys!.Contains(r.Info.Key)).ToList();

        // Notify when a pipeline fails, or passes again after having failed (not on every green run).
        var changed = firstRun
            ? new List<PipelineInfo>()
            : pipelines.Where(p => !p.Running
                && _pipelineStates!.TryGetValue($"{p.Project}@{p.Ref}", out var before) && before != p.Status
                && (p.Failed || (p.Status == "success" && before is "failed" or "timedout" or "canceled"))).ToList();
        _knownReviewKeys = reviewKeys;
        _pipelineStates = pipelineStates;

        if (!Settings.Get("notify", true))
        {
            return;
        }

        foreach (var review in newReviews.Take(5))
        {
            Context.Notifications.Post($"Review requested: {review.Info.Project} !{review.Info.Iid}", $"{review.Info.Title}\nby {review.Info.Author}",
                NotificationSeverity.Info, InstanceId, review.Info.Url, dedupeKey: $"mr:{InstanceId}:{review.Info.Key}", dedupeWindow: TimeSpan.FromHours(12));
        }

        foreach (var p in changed.Where(p => p.Failed || p.Status == "success"))
        {
            if (p.Failed)
            {
                Context.Notifications.Post($"Pipeline {p.Status}: {p.Project}", $"{p.Ref} · #{p.Id}", NotificationSeverity.Error, InstanceId, p.Url,
                    dedupeKey: $"pl:{InstanceId}:{p.Project}:{p.Id}:{p.Status}", dedupeWindow: TimeSpan.FromHours(1));
            }
            else
            {
                Context.Notifications.Post($"Pipeline passed: {p.Project}", $"{p.Ref} · #{p.Id}", NotificationSeverity.Success, InstanceId, p.Url,
                    dedupeKey: $"pl:{InstanceId}:{p.Project}:{p.Id}:success", dedupeWindow: TimeSpan.FromHours(1));
            }
        }
    }

    protected override ValueTask OnDisposeAsync()
    {
        Board.Unpublish();
        return base.OnDisposeAsync();
    }

    private static string ShortName(string nameWithNamespace)
    {
        var slash = nameWithNamespace.LastIndexOf('/');
        return slash >= 0 ? nameWithNamespace[(slash + 1)..].Trim() : nameWithNamespace;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
