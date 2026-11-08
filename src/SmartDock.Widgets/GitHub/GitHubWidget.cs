using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.CodeRepo;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.GitHub;

public sealed class GitHubItem(string kind, string title, string repository, string url)
{
    public string Kind { get; } = kind;

    public string Title { get; } = title;

    public string Repository { get; } = repository;

    public RelayCommand OpenCommand { get; } = new(() => SafeLauncher.OpenWebUrl(url));
}

/// <summary>
/// Pull requests (review requests, your own, assigned, and optionally all open PRs of watched repositories),
/// assigned issues, unread notifications and one repository's stars and latest Actions run, through the documented
/// GitHub REST API with a personal access token. Pull requests use the same board as the CodeHub widget: grouping,
/// filters, hover quick view, open time and the shared search window. The token is stored with DPAPI.
/// </summary>
public sealed class GitHubWidget(ISecretStore secrets, HttpClient http) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "github";
    public const string TokenKey = "token";
    private const string Api = "https://api.github.com";
    private const string ShowMine = "Only pull requests that involve me";
    private const string ShowAll = "Also all open pull requests in the watched repositories";

    private int _issues;
    private int _reviews;
    private int? _notifications;
    private string _repoText = string.Empty;
    private IReadOnlyList<GitHubItem> _items = [];
    private long? _lastRunId;
    private string? _lastRunStatus;
    private string _title = "GitHub";
    private string? _note;
    private RelayCommand? _refresh;
    private MergeRequestBoard? _board;
    private HashSet<string>? _knownReviewKeys;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "GitHub",
        "Assigned issues, pull requests awaiting your review, notifications and repository build status.",
        WidgetCategory.Developer,
        "",
        DockRegion.Center,
        sp => new GitHubWidget(sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<HttpClient>()))
    {
        AllowMultiple = true,
    };

    public int Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }

    public int Reviews
    {
        get => _reviews;
        private set => SetProperty(ref _reviews, value);
    }

    /// <summary>Null when the token cannot read notifications (fine-grained tokens cannot).</summary>
    public int? Notifications
    {
        get => _notifications;
        private set
        {
            if (SetProperty(ref _notifications, value))
            {
                OnPropertyChanged(nameof(HasNotificationCount));
            }
        }
    }

    public bool HasNotificationCount => _notifications is not null;

    /// <summary>e.g. "owner/repo · ★ 1,204 · build passing".</summary>
    public string RepoText
    {
        get => _repoText;
        private set => SetProperty(ref _repoText, value);
    }

    public IReadOnlyList<GitHubItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public string Repository => Settings.Get("repository", string.Empty).Trim();

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Non-fatal problems (e.g. a watched repository that doesn't exist).</summary>
    public string? Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    /// <summary>Pull requests: grouping, filters, search, quick view (shared with the CodeHub widget).</summary>
    public MergeRequestBoard Board => _board ??= new MergeRequestBoard(() => Settings, () => InstanceId, () => Context.Events, "pull requests",
        () => Context.RequestRefresh?.Invoke());

    public RelayCommand RefreshNowCommand => _refresh ??= new RelayCommand(() => Context.RequestRefresh?.Invoke());

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Refresh now", RefreshNowCommand),
        new("Search pull requests…", Board.SearchCommand),
    ];

    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Settings.Get("refreshMinutes", 5.0), 1, 120));

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new(TokenKey, "Personal access token", SettingFieldKind.Secret,
            "Classic token with 'repo' and 'notifications' scopes, or a fine-grained token (notifications are then unavailable). Stored encrypted."),
        new("repository", "Repository to watch (optional)", SettingFieldKind.Text, "owner/name — shows stars and the latest Actions run."),
        new("repositories", "Repositories for pull requests (optional, one per line)", SettingFieldKind.MultilineText,
            "owner/name per line. Their PRs are grouped in this order; with \"Also all open pull requests\" every open PR there is listed.",
            DefaultText: string.Empty),
        new("show", "Show", SettingFieldKind.Choice, Choices: [ShowMine, ShowAll], DefaultText: ShowMine),
        new("groupBy", "Group pull requests by", SettingFieldKind.Choice,
            "Project: one collapsible group per repository. Category: needs review / yours / others. Also switchable in the popup.",
            Choices: [nameof(MrGrouping.Project), nameof(MrGrouping.Category)], DefaultText: nameof(MrGrouping.Project)),
        new("displayName", "Name on the dock (optional)", SettingFieldKind.Text, "Defaults to \"GitHub\"."),
        new("notify", "Notify me about new review requests", SettingFieldKind.Toggle, DefaultToggle: true),
        new("refreshMinutes", "Refresh every (minutes)", SettingFieldKind.Number, DefaultNumber: 5),
    ];

    public void OnPopupOpened()
    {
    }

    protected override void OnSettingChanged(string key)
    {
        if (Board.HandleSettingChanged(key))
        {
            return; // grouping / filters only: no API calls
        }

        if (key == TokenKey)
        {
            _knownReviewKeys = null;
        }

        base.OnSettingChanged(key);
    }

    protected override ValueTask OnDisposeAsync()
    {
        Board.Unpublish();
        return base.OnDisposeAsync();
    }

    /// <summary>Watched repositories (owner/name), accepting pasted github.com URLs; public for tests.</summary>
    public static IReadOnlyList<string> ParseRepositories(string? text)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split('\n', ',', ';'))
        {
            var repo = raw.Trim();
            if (repo.Length == 0 || repo.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(repo, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal))
            {
                repo = url.AbsolutePath.Trim('/');
            }

            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var name = $"{parts[0]}/{parts[1]}";
                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(name);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// One pull request from a search/issues result. The search API doesn't return branches, reviewers or merge
    /// status, so those are marked as not available (PartialDetails) instead of being guessed. Public for tests.
    /// </summary>
    public static MergeRequestInfo ParsePullRequest(JsonElement item)
    {
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        static DateTimeOffset? Date(JsonElement e, string name) =>
            Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        static IReadOnlyList<string> Names(JsonElement e, string array, string field) =>
            e.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => Str(x, field)).OfType<string>().ToList()
                : [];

        var repoUrl = Str(item, "repository_url") ?? string.Empty;
        var repo = repoUrl.Replace(Api + "/repos/", string.Empty, StringComparison.OrdinalIgnoreCase);
        var author = item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Str(user, "login") ?? "?" : "?";
        var milestone = item.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "title") : null;
        int? up = null, down = null;
        if (item.TryGetProperty("reactions", out var reactions) && reactions.ValueKind == JsonValueKind.Object)
        {
            up = Int(reactions, "+1");
            down = Int(reactions, "-1");
        }

        var title = Str(item, "title") ?? "(untitled)";
        var draft = item.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        return new MergeRequestInfo(
            repo,
            Int(item, "number") ?? 0,
            title,
            Str(item, "html_url") ?? string.Empty,
            author,
            Names(item, "assignees", "login"),
            [],
            draft,
            false,
            string.Empty,
            string.Empty,
            Date(item, "updated_at"),
            Date(item, "created_at"),
            new MergeRequestExtras(Str(item, "body"), null, Names(item, "labels", "name"), milestone, null, up, down, Int(item, "comments"),
                NumberPrefix: "#", PartialDetails: true));
    }

    /// <summary>GitHub Actions status/conclusion in the vocabulary <see cref="PipelineInfo"/> uses (failed, canceled, running…).</summary>
    public static string PipelineStatus(string? status, string? conclusion) => status == "completed"
        ? conclusion switch
        {
            "success" => "success",
            "failure" or "startup_failure" => "failed",
            "cancelled" => "canceled",
            "timed_out" => "timedout",
            null => "unknown",
            _ => conclusion,
        }
        : status is "queued" or "waiting" or "pending" or "requested" ? "pending" : "running";

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var token = secrets.Get(WidgetSecrets.Name(InstanceId, TokenKey));
        if (string.IsNullOrWhiteSpace(token))
        {
            SetUnavailable("Right-click › Configure… to add a GitHub token");
            return;
        }

        try
        {
            Title = Settings.Get("displayName", string.Empty) is { Length: > 0 } display ? display.Trim() : "GitHub";
            var repositories = ParseRepositories(Settings.Get("repositories", string.Empty));
            var showAll = Settings.Get("show", ShowMine) == ShowAll;

            var issuesTask = SearchAsync(token, "is:open is:issue assignee:@me archived:false", cancellationToken);
            var reviewsTask = SearchPullRequestsAsync(token, "is:open is:pr review-requested:@me archived:false", cancellationToken);
            var authoredTask = SearchPullRequestsAsync(token, "is:open is:pr author:@me archived:false", cancellationToken);
            var assignedTask = SearchPullRequestsAsync(token, "is:open is:pr assignee:@me archived:false", cancellationToken);
            var watchedTask = showAll && repositories.Count > 0
                ? SearchPullRequestsAsync(token, "is:open is:pr archived:false " + string.Join(' ', repositories.Select(r => "repo:" + r)), cancellationToken, tolerateMissing: true)
                : Task.FromResult<(int Total, List<MergeRequestInfo> Items)?>((0, new List<MergeRequestInfo>()));
            var notificationsTask = CountNotificationsAsync(token, cancellationToken);
            await Task.WhenAll(issuesTask, reviewsTask, authoredTask, assignedTask, watchedTask, notificationsTask).ConfigureAwait(false);

            var (issueCount, issueItems) = issuesTask.Result;
            var (reviewCount, reviewPrs) = reviewsTask.Result!.Value;
            Issues = issueCount;
            Reviews = reviewCount;
            Notifications = notificationsTask.Result;
            Items = issueItems.Select(i => new GitHubItem("Issue", i.Title, i.Repo, i.Url)).ToList();

            // One entry per PR; the most relevant role wins (review > mine > assigned-as-review > other).
            var prs = new Dictionary<string, (MergeRequestInfo Info, MrRole Role)>(StringComparer.OrdinalIgnoreCase);
            void Add(IEnumerable<MergeRequestInfo> list, MrRole role)
            {
                foreach (var pr in list)
                {
                    prs.TryAdd(pr.Key, (pr, role));
                }
            }

            Add(reviewPrs.Where(p => !p.Draft), MrRole.Review);
            Add(authoredTask.Result!.Value.Items, MrRole.Mine);
            Add(assignedTask.Result!.Value.Items, MrRole.Review);
            Add(reviewPrs, MrRole.Other); // draft review requests: listed, but not counted as waiting for you
            if (watchedTask.Result is { } watched)
            {
                Add(watched.Items, MrRole.Other);
                Note = null;
            }
            else
            {
                Note = "Some watched repositories couldn't be searched (check the names and the token's access).";
            }

            var all = prs.Values.OrderByDescending(p => p.Info.Updated).ToList();

            PipelineInfo? pipeline = null;
            if (Repository.Length > 0)
            {
                (RepoText, pipeline) = await GetRepositoryAsync(token, Repository, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RepoText = string.Empty;
            }

            var order = repositories.Concat(Repository.Length > 0 ? new[] { Repository } : Array.Empty<string>()).ToList();
            Board.Update(all, pipeline is null ? Array.Empty<PipelineInfo>() : new[] { pipeline }, order);
            Board.Publish(Title, all.Select(p => new MergeRequestItem(p.Info, p.Role)).ToList());
            NotifyNewReviews(all.Where(p => p.Role == MrRole.Review).Select(p => p.Info).ToList());
            SetReady();
        }
        catch (GitHubException ex)
        {
            SetDisconnected(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            SetDisconnected("GitHub unreachable: " + ex.Message);
        }
    }

    private async Task<(int Total, List<(string Title, string Repo, string Url)> Items)> SearchAsync(string token, string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(token, $"{Api}/search/issues?per_page=10&q={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        var items = new List<(string, string, string)>();
        foreach (var item in doc!.RootElement.GetProperty("items").EnumerateArray())
        {
            var repoUrl = item.TryGetProperty("repository_url", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var repo = repoUrl.Replace(Api + "/repos/", string.Empty, StringComparison.OrdinalIgnoreCase);
            items.Add((
                $"#{item.GetProperty("number").GetInt32()} {item.GetProperty("title").GetString()}",
                repo,
                item.GetProperty("html_url").GetString() ?? string.Empty));
        }

        return (doc.RootElement.GetProperty("total_count").GetInt32(), items);
    }

    private async Task<int?> CountNotificationsAsync(string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(token, $"{Api}/notifications?per_page=50", ct, allowForbidden: true).ConfigureAwait(false);
        return doc?.RootElement.GetArrayLength();
    }

    private async Task<(string Text, PipelineInfo? Pipeline)> GetRepositoryAsync(string token, string repository, CancellationToken ct)
    {
        PipelineInfo? pipeline = null;
        using var repo = await GetJsonAsync(token, $"{Api}/repos/{repository}", ct).ConfigureAwait(false);
        var stars = repo!.RootElement.GetProperty("stargazers_count").GetInt32();
        Context.Events.Publish(new MetricObserved($"github.stars:{repository}", $"{repository} stars", stars, InstanceId));

        var text = $"{repository} · ★ {stars:N0}";
        using var runs = await GetJsonAsync(token, $"{Api}/repos/{repository}/actions/runs?per_page=1", ct, allowForbidden: true).ConfigureAwait(false);
        if (runs is not null && runs.RootElement.GetProperty("workflow_runs") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
        {
            var run = list[0];
            var id = run.GetProperty("id").GetInt64();
            var status = run.GetProperty("status").GetString();
            var conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var name = run.TryGetProperty("name", out var n) ? n.GetString() : "build";
            var branch = run.TryGetProperty("head_branch", out var hb) && hb.ValueKind == JsonValueKind.String ? hb.GetString() : null;
            var updated = run.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ua.GetString(), out var when) ? when : (DateTimeOffset?)null;
            pipeline = new PipelineInfo(repository, id, PipelineStatus(status, conclusion), (name ?? "build") + (branch is null ? string.Empty : " · " + branch),
                run.TryGetProperty("html_url", out var hu) ? hu.GetString() : null, updated);
            text += status == "completed" ? $" · {name} {conclusion}" : $" · {name} running";

            // Notify once when a run we saw in progress completes.
            if (_lastRunId == id && _lastRunStatus != "completed" && status == "completed")
            {
                Context.Notifications.Post(
                    conclusion == "success" ? "Build succeeded" : "Build finished: " + conclusion,
                    $"{repository} · {name}",
                    conclusion == "success" ? NotificationSeverity.Success : NotificationSeverity.Warning,
                    InstanceId,
                    run.GetProperty("html_url").GetString(),
                    dedupeKey: $"gh-run:{id}");
            }

            _lastRunId = id;
            _lastRunStatus = status;
        }

        return (text, pipeline);
    }

    private async Task<(int Total, List<MergeRequestInfo> Items)?> SearchPullRequestsAsync(string token, string query, CancellationToken ct, bool tolerateMissing = false)
    {
        // 422 = a repo: qualifier names a repository that doesn't exist or isn't visible to this token.
        using var doc = await GetJsonAsync(token, $"{Api}/search/issues?per_page=50&sort=updated&q={Uri.EscapeDataString(query)}", ct,
            allowForbidden: tolerateMissing, allowUnprocessable: tolerateMissing).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(ParsePullRequest).ToList();
        return (doc.RootElement.GetProperty("total_count").GetInt32(), items);
    }

    /// <summary>Notifies once per new review request; the first refresh only records what's already open.</summary>
    private void NotifyNewReviews(IReadOnlyList<MergeRequestInfo> reviews)
    {
        var keys = reviews.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = _knownReviewKeys;
        _knownReviewKeys = keys;
        if (known is null || !Settings.Get("notify", true))
        {
            return;
        }

        foreach (var pr in reviews.Where(r => !known.Contains(r.Key)).Take(5))
        {
            Context.Notifications.Post($"Review requested: {pr.Project} #{pr.Iid}", $"{pr.Title}\nby {pr.Author}", NotificationSeverity.Info, InstanceId, pr.Url,
                dedupeKey: $"gh-pr:{InstanceId}:{pr.Key}", dedupeWindow: TimeSpan.FromHours(12));
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string token, string url, CancellationToken ct, bool allowForbidden = false, bool allowUnprocessable = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubException("GitHub rejected the token (401). Update it via Configure…");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound && allowForbidden)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity && allowUnprocessable)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
        {
            var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var values) && long.TryParse(values.FirstOrDefault(), out var epoch)
                ? $" until {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm}"
                : string.Empty;
            throw new GitHubException($"GitHub rate limit or permission error{reset}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubException($"Not found: {new Uri(url).AbsolutePath}");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class GitHubException(string message) : Exception(message);
}
