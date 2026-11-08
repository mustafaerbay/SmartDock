using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartDock.Widgets.CodeRepo;

/// <summary>How the popup groups open merge requests.</summary>
public enum MrGrouping
{
    /// <summary>One collapsible group per watched project, with its pipeline in the header (default).</summary>
    Project,

    /// <summary>Needs your review / Your MRs / Other open MRs.</summary>
    Category,
}

/// <summary>
/// A collapsible group of merge requests in the CodeHub popup. Shows the first <see cref="PreviewCount"/> rows and a
/// "Show N more" button, so a busy project doesn't turn the popup into one long list.
/// </summary>
public sealed class MrGroup : ObservableObject
{
    public const int PreviewCount = 6;

    private readonly Action<string, bool>? _onExpandedChanged;
    private bool _isExpanded;
    private bool _showAll;

    public MrGroup(string key, string title, IReadOnlyList<MergeRequestItem> items, PipelineItem? pipeline, bool isExpanded,
        Action<string, bool>? onExpandedChanged = null)
    {
        Key = key;
        Title = title;
        Items = items;
        Pipeline = pipeline;
        _isExpanded = isExpanded;
        _onExpandedChanged = onExpandedChanged;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        ShowAllCommand = new RelayCommand(() =>
        {
            _showAll = true;
            OnPropertyChanged(nameof(VisibleItems));
            OnPropertyChanged(nameof(HasMore));
        });
    }

    /// <summary>Stable key used to remember whether the group is expanded ("p:Gateway", "c:Review").</summary>
    public string Key { get; }

    public string Title { get; }

    public IReadOnlyList<MergeRequestItem> Items { get; }

    /// <summary>Latest pipeline of the project (project grouping only).</summary>
    public PipelineItem? Pipeline { get; }

    public bool HasPipeline => Pipeline is not null;

    public int Count => Items.Count;

    public int ReviewCount => Items.Count(i => i.Role == MrRole.Review);

    public int MineCount => Items.Count(i => i.Role == MrRole.Mine);

    /// <summary>"2 to review · 1 yours · 9 open" or "No open MRs".</summary>
    public string Summary
    {
        get
        {
            if (Count == 0)
            {
                return "Nothing open";
            }

            var parts = new List<string>();
            if (ReviewCount > 0)
            {
                parts.Add($"{ReviewCount} to review");
            }

            if (MineCount > 0)
            {
                parts.Add($"{MineCount} yours");
            }

            var conflicts = Items.Count(i => i.HasConflicts);
            if (conflicts > 0)
            {
                parts.Add($"{conflicts} with conflicts");
            }

            parts.Add($"{Count} open");
            return string.Join(" · ", parts);
        }
    }

    public bool NeedsAttention => ReviewCount > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                _onExpandedChanged?.Invoke(Key, value);
            }
        }
    }

    /// <summary>Segoe Fluent chevron: down when expanded, right when collapsed.</summary>
    public string Chevron => IsExpanded ? "" : "";

    public IReadOnlyList<MergeRequestItem> VisibleItems => _showAll || Items.Count <= PreviewCount + 1 ? Items : Items.Take(PreviewCount).ToList();

    public bool HasMore => VisibleItems.Count < Items.Count;

    public string MoreText => $"Show {Items.Count - PreviewCount} more";

    public RelayCommand ToggleCommand { get; }

    public RelayCommand ShowAllCommand { get; }

    /// <summary>
    /// Builds the popup groups. Pure (no I/O) so it can be unit-tested.
    /// </summary>
    /// <param name="mrs">Merge requests to show with their role (already filtered by the "Show" setting).</param>
    /// <param name="pipelines">Latest pipeline per watched project.</param>
    /// <param name="projectOrder">Watched project names in the order they were configured.</param>
    /// <param name="savedStates">Remembered expanded/collapsed state per group key.</param>
    public static IReadOnlyList<MrGroup> Build(
        IReadOnlyList<(MergeRequestInfo Info, MrRole Role)> mrs,
        IReadOnlyList<PipelineInfo> pipelines,
        IReadOnlyList<string> projectOrder,
        MrGrouping grouping,
        IReadOnlyDictionary<string, bool>? savedStates,
        Action<string, bool>? onExpandedChanged = null,
        string noun = "merge requests")
    {
        var groups = new List<MrGroup>();
        if (grouping == MrGrouping.Category)
        {
            foreach (var (role, title) in new[] { (MrRole.Review, "Needs your review"), (MrRole.Mine, "Your " + noun), (MrRole.Other, "Other open " + noun) })
            {
                var items = mrs.Where(m => m.Role == role).OrderByDescending(m => m.Info.Updated)
                    .Select(m => new MergeRequestItem(m.Info, m.Role)).ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                var key = "c:" + role;
                var defaultExpanded = role != MrRole.Other || items.Count <= PreviewCount;
                groups.Add(new MrGroup(key, title, items, null, Expanded(key, defaultExpanded), onExpandedChanged));
            }

            return groups;
        }

        // Project grouping: configured order first, then any other project names that came back.
        var names = projectOrder.Concat(mrs.Select(m => m.Info.Project)).Concat(pipelines.Select(p => p.Project))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var single = names.Count == 1;
        foreach (var name in names)
        {
            var items = mrs.Where(m => string.Equals(m.Info.Project, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Role switch { MrRole.Review => 0, MrRole.Mine => 1, _ => 2 })
                .ThenByDescending(m => m.Info.Updated)
                .Select(m => new MergeRequestItem(m.Info, m.Role, showProject: false)).ToList();
            var pipeline = pipelines.FirstOrDefault(p => string.Equals(p.Project, name, StringComparison.OrdinalIgnoreCase));
            var key = "p:" + name;

            // Open by default when there's something for me, the list is short, or it's the only project.
            var defaultExpanded = single || items.Any(i => i.Role != MrRole.Other) || (items.Count > 0 && items.Count <= 3);
            groups.Add(new MrGroup(key, name, items, pipeline is null ? null : new PipelineItem(pipeline), Expanded(key, defaultExpanded), onExpandedChanged));
        }

        return groups;

        bool Expanded(string key, bool fallback) => savedStates is not null && savedStates.TryGetValue(key, out var saved) ? saved : fallback;
    }
}
