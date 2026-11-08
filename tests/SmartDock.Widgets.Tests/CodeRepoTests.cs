using SmartDock.Widgets.CodeRepo;

namespace SmartDock.Widgets.Tests;

public class CodeRepoTests
{
    [Fact]
    public void Parses_projects_by_id_path_and_url()
    {
        var projects = CodeRepoWidget.ParseProjects(
            "Gateway | 12345 | master\n" +
            "team/sub/api\n" +
            "Web | https://codehub.example.com/team/web/-/merge_requests\n" +
            "# comment\n" +
            "Gateway again | 12345 | master\n");

        Assert.Collection(
            projects,
            p => Assert.Equal(("Gateway", "12345", "master"), (p.Name, p.Project, p.Branch)),
            p => Assert.Equal(("", "team/sub/api", (string?)null), (p.Name, p.Project, p.Branch)),
            p => Assert.Equal(("Web", "team/web"), (p.Name, p.Project)));
    }

    [Theory]
    [InlineData("https://codehub.example.com", "https://codehub.example.com/api/v4/")]
    [InlineData("https://codehub.example.com/", "https://codehub.example.com/api/v4/")]
    [InlineData("https://gitlab.example.com/api/v4", "https://gitlab.example.com/api/v4/")]
    public void Builds_api_root(string server, string expected) => Assert.Equal(expected, GitLabApi.ApiRoot(server).AbsoluteUri);

    [Fact]
    public void Rejects_non_http_server() => Assert.Throws<RepoApiException>(() => GitLabApi.ApiRoot("ftp://x"));

    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("team/sub/api", "team%2Fsub%2Fapi")]
    public void Encodes_project_ids(string project, string expected) => Assert.Equal(expected, GitLabApi.EncodeProject(project));

    [Fact]
    public void Classifies_merge_requests_for_me()
    {
        MergeRequestInfo Mr(string author, string[] assignees, string[] reviewers) =>
            new("p", 1, "t", "https://x", author, assignees, reviewers, false, false, "a", "b", null);

        Assert.Equal(MrRole.Mine, CodeRepoWidget.Classify(Mr("Mustafa", [], []), "mustafa"));
        Assert.Equal(MrRole.Review, CodeRepoWidget.Classify(Mr("ali", [], ["mustafa"]), "mustafa"));
        Assert.Equal(MrRole.Review, CodeRepoWidget.Classify(Mr("ali", ["mustafa"], []), "mustafa"));
        Assert.Null(CodeRepoWidget.Classify(Mr("ali", ["veli"], []), "mustafa"));
        Assert.Null(CodeRepoWidget.Classify(Mr("ali", [], []), null));
    }

    private static MergeRequestInfo Mr(string project, long iid, int minutesAgo = 0) =>
        new(project, iid, "t" + iid, "https://x/" + iid, "ali", [], [], false, false, "a", "b", DateTimeOffset.Now.AddMinutes(-minutesAgo));

    [Fact]
    public void Groups_by_project_in_configured_order_with_review_first()
    {
        var mrs = new List<(MergeRequestInfo, MrRole)>
        {
            (Mr("Web", 1, 1), MrRole.Other),
            (Mr("Gateway", 2, 5), MrRole.Other),
            (Mr("Gateway", 3, 50), MrRole.Review),
            (Mr("Gateway", 4, 2), MrRole.Mine),
        };
        var pipelines = new List<PipelineInfo> { new("Gateway", 9, "failed", "master", null, null) };

        var groups = MrGroup.Build(mrs, pipelines, ["Gateway", "Web", "Empty"], MrGrouping.Project, null);

        Assert.Equal(new[] { "Gateway", "Web", "Empty" }, groups.Select(g => g.Title));
        Assert.Equal(new[] { 3L, 4L, 2L }, groups[0].Items.Select(i => i.Info.Iid)); // review, mine, then newest first
        Assert.True(groups[0].Pipeline!.Failed);
        Assert.Equal("1 to review · 1 yours · 3 open", groups[0].Summary);
        Assert.True(groups[0].IsExpanded);     // something for me
        Assert.True(groups[1].IsExpanded);     // short list
        Assert.Equal("No open MRs", groups[2].Summary);
        Assert.DoesNotContain("Gateway", groups[0].Items[0].Detail); // project is in the header already
        Assert.Equal("Review", groups[0].Items[0].RoleLabel);
    }

    [Fact]
    public void Long_groups_collapse_by_default_and_show_a_preview()
    {
        var mrs = Enumerable.Range(1, 15).Select(i => (Mr("Big", i, i), MrRole.Other)).ToList();
        var group = MrGroup.Build(mrs, [], ["Big", "Other"], MrGrouping.Project, null)[0];

        Assert.False(group.IsExpanded);
        Assert.Equal(MrGroup.PreviewCount, group.VisibleItems.Count);
        Assert.True(group.HasMore);
        Assert.Equal("Show 9 more", group.MoreText);
        group.ShowAllCommand.Execute(null);
        Assert.Equal(15, group.VisibleItems.Count);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void Remembers_expanded_state()
    {
        var mrs = Enumerable.Range(1, 15).Select(i => (Mr("Big", i), MrRole.Other)).ToList();
        string? changedKey = null;
        var group = MrGroup.Build(mrs, [], ["Big", "x"], MrGrouping.Project, new Dictionary<string, bool> { ["p:Big"] = true },
            (key, _) => changedKey = key)[0];

        Assert.True(group.IsExpanded);
        group.ToggleCommand.Execute(null);
        Assert.False(group.IsExpanded);
        Assert.Equal("p:Big", changedKey);
    }

    [Fact]
    public void Groups_by_category_and_skips_empty_ones()
    {
        var mrs = new List<(MergeRequestInfo, MrRole)> { (Mr("Web", 1), MrRole.Review), (Mr("Gateway", 2), MrRole.Other) };

        var groups = MrGroup.Build(mrs, [], ["Gateway", "Web"], MrGrouping.Category, null);

        Assert.Equal(new[] { "Needs your review", "Other open merge requests" }, groups.Select(g => g.Title));
        Assert.Contains("Web", groups[0].Items[0].Detail);
        Assert.Null(groups[0].Items[0].RoleLabel);
    }

    [Theory]
    [InlineData(0.2, false, "1 min")]
    [InlineData(25.0, false, "25 min")]
    [InlineData(300.0, false, "5 h")]
    [InlineData(4320.0, false, "3 d")]
    [InlineData(1440.0, true, "1 day")]
    [InlineData(28800.0, false, "2 w")]
    [InlineData(576000.0, true, "1 year")]
    public void Formats_merge_request_age(double minutes, bool verbose, string expected) =>
        Assert.Equal(expected, MergeRequestItem.FormatAge(TimeSpan.FromMinutes(minutes), verbose));

    [Fact]
    public void Marks_old_merge_requests_as_stale()
    {
        MergeRequestItem Item(int days) => new(new MergeRequestInfo("p", 1, "t", "u", "a", [], [], false, false, "s", "t",
            DateTimeOffset.Now, DateTimeOffset.Now.AddDays(-days)), MrRole.Other);

        Assert.False(Item(2).IsStale);
        Assert.True(Item(MergeRequestItem.StaleAfterDays).IsStale);
        Assert.Equal("2 d", Item(2).Age);
        Assert.Equal(string.Empty, new MergeRequestItem(Mr("p", 1), MrRole.Other).Age); // server sent no created_at
    }

    [Fact]
    public void Parses_quick_view_fields_from_the_list_response()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"description":"Fixes the gateway","author":{"username":"ali","name":"Ali Veli"},
             "labels":["bug",{"name":"backend"}],"milestone":{"title":"R5"},
             "detailed_merge_status":"not_approved","upvotes":2,"downvotes":0,"user_notes_count":5}
            """);
        var extras = GitLabApi.ParseExtras(doc.RootElement);

        Assert.Equal("Fixes the gateway", extras.Description);
        Assert.Equal("Ali Veli", extras.AuthorName);
        Assert.Equal(new[] { "bug", "backend" }, extras.Labels);
        Assert.Equal("R5", extras.Milestone);
        Assert.Equal("not_approved", extras.MergeStatus);
        Assert.Equal(2, extras.Upvotes);
        Assert.Equal(0, extras.Downvotes);
        Assert.Equal(5, extras.Comments);

        var item = new MergeRequestItem(Mr("p", 1) with { Extras = extras }, MrRole.Review);
        Assert.Equal("Ali Veli (ali)", item.AuthorText);
        Assert.Equal("Waiting for approval", item.MergeStatusText);
        Assert.Equal("2 up · 5 comments", item.ActivityText);
        Assert.Equal("bug · backend", item.LabelsText);
    }

    [Fact]
    public void Quick_view_tolerates_servers_that_send_little()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"merge_status":"can_be_merged"}""");
        var item = new MergeRequestItem(Mr("p", 1) with { Extras = GitLabApi.ParseExtras(doc.RootElement) }, MrRole.Other);

        Assert.Null(item.DescriptionPreview);
        Assert.Null(item.LabelsText);
        Assert.Null(item.ActivityText);
        Assert.Equal("Ready to merge", item.MergeStatusText);
        Assert.Equal("—", item.ReviewersText);
    }

    [Fact]
    public void Description_preview_drops_template_noise_and_truncates()
    {
        var text = "<!-- template -->\r\n**Why**\r\n\r\n\r\nBecause.\n```\ncode\n```\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => "line " + i));
        var preview = MergeRequestItem.PreviewDescription(text)!;

        Assert.StartsWith("Why\n\nBecause.\ncode", preview);
        Assert.EndsWith("…", preview);
        Assert.True(preview.Split('\n').Length <= 13);
        Assert.Null(MergeRequestItem.PreviewDescription("  \n "));
    }

    [Theory]
    [InlineData("mergeable", false, "Ready to merge")]
    [InlineData("can_be_merged", true, "Has conflicts")]
    [InlineData("ci_still_running", false, "Waiting for the pipeline")]
    [InlineData("some_new_status", false, "some new status")]
    [InlineData(null, false, "Unknown")]
    public void Describes_merge_status(string? status, bool conflicts, string expected) =>
        Assert.Equal(expected, MergeRequestItem.FriendlyMergeStatus(status, conflicts));

    private static MergeRequestItem Searchable(long iid, string title, string author, MrRole role = MrRole.Other, bool draft = false,
        bool conflicts = false, int ageDays = 1, string[]? labels = null, string target = "master") =>
        new(new MergeRequestInfo("Gateway", iid, title, "https://x/" + iid, author, [], ["veli"], draft, conflicts, "feature/login", target,
            DateTimeOffset.Now, DateTimeOffset.Now.AddDays(-ageDays),
            new MergeRequestExtras(null, author == "ali" ? "Ali Yilmaz" : null, labels ?? [], null, null, null, null, null)), role);

    [Theory]
    [InlineData("login", true)]                 // branch
    [InlineData("LOGIN timeout", true)]         // all words, case-insensitive
    [InlineData("login nothing", false)]
    [InlineData("author:ali", true)]
    [InlineData("author:yilmaz", true)]         // display name
    [InlineData("author:veli", false)]
    [InlineData("reviewer:veli", true)]
    [InlineData("label:bug", true)]
    [InlineData("-label:bug", false)]
    [InlineData("target:release", false)]
    [InlineData("!42", true)]
    [InlineData("!4", false)]                   // number must match exactly
    [InlineData("\"fix login\"", true)]
    [InlineData("\"login fix\"", false)]
    [InlineData("is:stale", true)]
    [InlineData("-is:draft", true)]
    [InlineData("project:gate", true)]
    public void Searches_merge_requests(string query, bool expected)
    {
        var mr = Searchable(42, "Fix login timeout", "ali", ageDays: 10, labels: ["bug"]);
        Assert.Equal(expected, MergeRequestFilter.Matches(mr, query));
    }

    [Fact]
    public void Quick_filters_and_draft_toggle()
    {
        var review = Searchable(1, "a", "x", MrRole.Review);
        var mine = Searchable(2, "b", "me", MrRole.Mine, draft: true);
        var conflict = Searchable(3, "c", "y", conflicts: true, ageDays: 30);

        Assert.True(MergeRequestFilter.Matches(review, null, MrQuickFilter.ToReview));
        Assert.False(MergeRequestFilter.Matches(mine, null, MrQuickFilter.ToReview));
        Assert.True(MergeRequestFilter.Matches(mine, "", MrQuickFilter.Mine));
        Assert.False(MergeRequestFilter.Matches(mine, "", MrQuickFilter.Mine, hideDrafts: true));
        Assert.True(MergeRequestFilter.Matches(conflict, null, MrQuickFilter.Conflicts));
        Assert.True(MergeRequestFilter.Matches(conflict, null, MrQuickFilter.Stale));
        Assert.False(MergeRequestFilter.Matches(review, null, MrQuickFilter.Stale));
    }

    [Fact]
    public void Parses_query_terms()
    {
        var terms = MergeRequestFilter.Parse("-is:draft author:ali \"two words\" !12 plain");
        Assert.Equal(new[] { ("is", "draft", true), ("author", "ali", false), ("", "two words", false), ("iid", "12", false), ("", "plain", false) },
            terms.Select(t => (t.Field, t.Value, t.Negate)));
    }

    [Fact]
    public void Parses_github_pull_requests_from_search_results()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"number":77,"title":"Add retries","html_url":"https://github.com/acme/api/pull/77",
             "repository_url":"https://api.github.com/repos/acme/api","user":{"login":"ali"},
             "assignees":[{"login":"veli"}],"labels":[{"name":"enhancement"}],"draft":true,
             "created_at":"2026-11-01T10:00:00Z","updated_at":"2026-11-04T10:00:00Z","comments":3,
             "reactions":{"+1":2,"-1":0},"body":"Retries with backoff","pull_request":{}}
            """);
        var pr = SmartDock.Widgets.GitHub.GitHubWidget.ParsePullRequest(doc.RootElement);
        var item = new MergeRequestItem(pr, MrRole.Review);

        Assert.Equal(("acme/api", 77L, "ali", true), (pr.Project, pr.Iid, pr.Author, pr.Draft));
        Assert.Equal("#77", item.Number);
        Assert.Equal("acme/api #77 · Draft", item.Reference);
        Assert.Null(item.BranchesText);      // not in search results: hidden, not guessed
        Assert.Null(item.ReviewersText);
        Assert.Null(item.MergeStatusText);
        Assert.Equal("enhancement", item.LabelsText);
        Assert.Equal("2 up · 3 comments", item.ActivityText);
        Assert.True(MergeRequestFilter.Matches(item, "#77 label:enh assignee:veli"));
        Assert.DoesNotContain("→", item.Detail);
    }

    [Theory]
    [InlineData("completed", "success", "success")]
    [InlineData("completed", "failure", "failed")]
    [InlineData("completed", "cancelled", "canceled")]
    [InlineData("in_progress", null, "running")]
    [InlineData("queued", null, "pending")]
    public void Maps_github_actions_status(string status, string? conclusion, string expected) =>
        Assert.Equal(expected, SmartDock.Widgets.GitHub.GitHubWidget.PipelineStatus(status, conclusion));

    [Fact]
    public void Parses_watched_github_repositories()
    {
        var repos = SmartDock.Widgets.GitHub.GitHubWidget.ParseRepositories("acme/api\nhttps://github.com/acme/web/pulls\n# skip\nACME/api\nbad");
        Assert.Equal(new[] { "acme/api", "acme/web" }, repos);
    }
}
