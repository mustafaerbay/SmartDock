using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SmartDock.Widgets.CodeRepo;

public sealed record RepoUser(long? Id, string Username);

public sealed record MergeRequestInfo(
    string Project,
    long Iid,
    string Title,
    string Url,
    string Author,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> Reviewers,
    bool Draft,
    bool HasConflicts,
    string SourceBranch,
    string TargetBranch,
    DateTimeOffset? Updated,
    DateTimeOffset? Created = null,
    MergeRequestExtras? Extras = null)
{
    public string Key => $"{Project}!{Iid}";
}

/// <summary>
/// Extra MR fields for the hover quick view. All come from the same merge_requests list response
/// (no extra requests); anything the server doesn't send stays null/empty.
/// </summary>
public sealed record MergeRequestExtras(
    string? Description,
    string? AuthorName,
    IReadOnlyList<string> Labels,
    string? Milestone,
    string? MergeStatus,
    int? Upvotes,
    int? Downvotes,
    int? Comments,
    string NumberPrefix = "!",
    bool PartialDetails = false);

public sealed record PipelineInfo(string Project, long Id, string Status, string Ref, string? Url, DateTimeOffset? Updated)
{
    public bool Failed => Status is "failed" or "timedout" or "canceled";

    public bool Running => Status is "running" or "pending" or "created" or "preparing" or "waiting_for_resource";
}

public sealed class RepoApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Minimal client for the GitLab v4 REST API, which Huawei's internal CodeHub also exposes
/// (<c>/api/v4/projects/{id}/merge_requests</c>). JSON is parsed defensively: fields that a given
/// server version doesn't return (e.g. <c>reviewers</c>) are simply treated as empty.
/// </summary>
public sealed class GitLabApi(HttpClient http, Uri baseUrl, string token, string authHeader)
{
    public const string PrivateTokenHeader = "PRIVATE-TOKEN";
    public const string BearerHeader = "Authorization: Bearer";

    /// <summary>Accepts "https://host", "https://host/" or "https://host/api/v4" and returns the API root.</summary>
    public static Uri ApiRoot(string server)
    {
        var text = server.Trim().TrimEnd('/');
        if (!text.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
        {
            text += "/api/v4";
        }

        if (!Uri.TryCreate(text + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new RepoApiException("The server address must start with https:// (or http://).");
        }

        return uri;
    }

    /// <summary>Numeric ids stay as they are; "group/sub/project" paths are URL-encoded as the API requires.</summary>
    public static string EncodeProject(string project) =>
        long.TryParse(project, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? project : Uri.EscapeDataString(project.Trim('/'));

    public async Task<RepoUser?> GetCurrentUserAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync("user", ct).ConfigureAwait(false);
            var root = doc.RootElement;
            return new RepoUser(Long(root, "id"), Str(root, "username") ?? string.Empty);
        }
        catch (RepoApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null; // Some servers don't expose /user; the username setting is used instead.
        }
    }

    /// <summary>Display name and web URL of a project ("Group / Project").</summary>
    public async Task<(string Name, string? Url)> GetProjectAsync(string project, CancellationToken ct)
    {
        using var doc = await GetAsync($"projects/{EncodeProject(project)}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return (Str(root, "name_with_namespace") ?? Str(root, "path_with_namespace") ?? Str(root, "name") ?? project, Str(root, "web_url"));
    }

    public async Task<IReadOnlyList<MergeRequestInfo>> GetOpenMergeRequestsAsync(string project, string projectName, CancellationToken ct)
    {
        using var doc = await GetAsync($"projects/{EncodeProject(project)}/merge_requests?state=opened&per_page=50&order_by=updated_at", ct).ConfigureAwait(false);
        var list = new List<MergeRequestInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var mr in doc.RootElement.EnumerateArray())
        {
            var title = Str(mr, "title") ?? "(untitled)";
            var draft = Bool(mr, "draft") || Bool(mr, "work_in_progress")
                || title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) || title.StartsWith("WIP:", StringComparison.OrdinalIgnoreCase);
            var assignees = Users(mr, "assignees");
            if (assignees.Count == 0 && mr.TryGetProperty("assignee", out var single) && single.ValueKind == JsonValueKind.Object && Str(single, "username") is { } one)
            {
                assignees = [one];
            }

            list.Add(new MergeRequestInfo(
                projectName,
                Long(mr, "iid") ?? Long(mr, "id") ?? 0,
                title,
                Str(mr, "web_url") ?? string.Empty,
                mr.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Str(author, "username") ?? Str(author, "name") ?? "?" : "?",
                assignees,
                Users(mr, "reviewers"),
                draft,
                Bool(mr, "has_conflicts") || Str(mr, "merge_status") == "cannot_be_merged",
                Str(mr, "source_branch") ?? string.Empty,
                Str(mr, "target_branch") ?? string.Empty,
                Date(mr, "updated_at"),
                Date(mr, "created_at"),
                ParseExtras(mr)));
        }

        return list;
    }

    /// <summary>Latest pipeline (optionally for one branch), or null if the project has none / the server has no pipelines API.</summary>
    public async Task<PipelineInfo?> GetLatestPipelineAsync(string project, string projectName, string? branch, CancellationToken ct)
    {
        var query = "per_page=1&order_by=id&sort=desc" + (string.IsNullOrWhiteSpace(branch) ? string.Empty : "&ref=" + Uri.EscapeDataString(branch));
        try
        {
            using var doc = await GetAsync($"projects/{EncodeProject(project)}/pipelines?{query}", ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var p in doc.RootElement.EnumerateArray())
            {
                return new PipelineInfo(projectName, Long(p, "id") ?? 0, Str(p, "status") ?? "unknown", Str(p, "ref") ?? branch ?? string.Empty,
                    Str(p, "web_url"), Date(p, "updated_at") ?? Date(p, "created_at"));
            }

            return null;
        }
        catch (RepoApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private async Task<JsonDocument> GetAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authHeader == BearerHeader)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            request.Headers.TryAddWithoutValidation(PrivateTokenHeader, token);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The server rejected the token (401). Check that it's valid and has API read access.",
                HttpStatusCode.Forbidden => "Access denied (403).",
                HttpStatusCode.NotFound => $"Not found (404): {relative.Split('?')[0]}",
                _ => $"Server error {(int)response.StatusCode}.",
            };
            throw new RepoApiException(message, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Typical for SSO/login pages returned instead of JSON.
            throw new RepoApiException("The server didn't return JSON – is the address right, and does it need VPN or sign-in?");
        }
    }

    /// <summary>Quick-view fields from one merge request object; public for tests.</summary>
    public static MergeRequestExtras ParseExtras(JsonElement mr)
    {
        var labels = new List<string>();
        if (mr.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in arr.EnumerateArray())
            {
                // Plain names, or objects when the server was asked for label details.
                var name = label.ValueKind == JsonValueKind.String ? label.GetString()
                    : label.ValueKind == JsonValueKind.Object ? Str(label, "name") : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    labels.Add(name);
                }
            }
        }

        var milestone = mr.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "title") : null;
        var authorName = mr.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "name") : null;
        return new MergeRequestExtras(
            Str(mr, "description"),
            authorName,
            labels,
            milestone,
            Str(mr, "detailed_merge_status") ?? Str(mr, "merge_status"),
            (int?)Long(mr, "upvotes"),
            (int?)Long(mr, "downvotes"),
            (int?)Long(mr, "user_notes_count"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static IReadOnlyList<string> Users(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Where(u => u.ValueKind == JsonValueKind.Object)
            .Select(u => Str(u, "username"))
            .OfType<string>()
            .ToList();
    }
}
