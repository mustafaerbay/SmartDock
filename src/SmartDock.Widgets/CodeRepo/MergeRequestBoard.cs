using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Events;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.CodeRepo;

/// <summary>
/// The shared "review list" behind every code widget popup (CodeHub / GitLab, GitHub): groups by project or
/// category, quick-filter chips, drafts toggle, expand/collapse memory, pipelines and the search window.
/// View-only settings (groupBy, filter, hideDrafts, groupStates) live in the widget's own settings bag and never
/// cause a network refresh.
/// </summary>
public sealed class MergeRequestBoard : ObservableObject
{
    public const string GroupByKey = "groupBy";
    public const string FilterKey = "filter";
    public const string HideDraftsKey = "hideDrafts";
    public const string GroupStatesKey = "groupStates";

    private readonly Func<WidgetSettings> _settings;
    private readonly Func<string> _instanceId;
    private readonly Func<IEventBus> _events;
    private readonly Action? _refresh;
    private IReadOnlyList<MrGroup> _groups = [];
    private IReadOnlyList<MrFilterChip> _chips = [];
    private IReadOnlyList<PipelineItem> _pipelines = [];
    private string? _filterSummary;
    private IReadOnlyList<(MergeRequestInfo Info, MrRole Role)> _mrs = [];
    private IReadOnlyList<PipelineInfo> _pipelineInfos = [];
    private IReadOnlyList<string> _projects = [];
    private RelayCommand? _toggleGrouping;
    private RelayCommand? _toggleDrafts;
    private RelayCommand? _clear;
    private RelayCommand? _search;
    private RelayCommand? _expandAll;
    private RelayCommand? _collapseAll;

    /// <param name="settings">The widget's settings (resolved lazily: widgets get them on initialisation).</param>
    /// <param name="noun">"merge requests" or "pull requests" for headings.</param>
    public MergeRequestBoard(Func<WidgetSettings> settings, Func<string> instanceId, Func<IEventBus> events, string noun = "merge requests",
        Action? refresh = null)
    {
        _refresh = refresh;
        _settings = settings;
        _instanceId = instanceId;
        _events = events;
        Noun = noun;
    }

    public string Noun { get; }

    /// <summary>"No open merge requests." etc. for the empty state.</summary>
    public string EmptyText => $"No open {Noun}.";

    private WidgetSettings Settings => _settings();

    public IReadOnlyList<MrGroup> Groups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                OnPropertyChanged(nameof(HasGroups));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>True when there's nothing at all (not just filtered away).</summary>
    public bool IsEmpty => _mrs.Count == 0 && _pipelines.Count == 0 && Groups.Count == 0;

    /// <summary>Pipelines list, shown under the groups in category mode (project groups show them in the header).</summary>
    public IReadOnlyList<PipelineItem> Pipelines
    {
        get => _pipelines;
        private set
        {
            if (SetProperty(ref _pipelines, value))
            {
                OnPropertyChanged(nameof(ShowPipelineList));
            }
        }
    }

    public bool ShowPipelineList => !GroupByProject && Pipelines.Count > 0;

    public MrGrouping Grouping => Settings.Get(GroupByKey, nameof(MrGrouping.Project)) == nameof(MrGrouping.Category) ? MrGrouping.Category : MrGrouping.Project;

    public bool GroupByProject => Grouping == MrGrouping.Project;

    /// <summary>Label of the grouping switch: what you get when you click it.</summary>
    public string GroupingButtonText => GroupByProject ? "By category" : "By project";

    public MrQuickFilter ActiveFilter => Enum.TryParse<MrQuickFilter>(Settings.Get(FilterKey, nameof(MrQuickFilter.All)), out var f) ? f : MrQuickFilter.All;

    public bool HideDrafts => Settings.Get(HideDraftsKey, false);

    public string DraftsChipText => HideDrafts ? "Drafts hidden" : "Hide drafts";

    /// <summary>All / To review / Mine / Conflicts / Open 7d+ with counts.</summary>
    public IReadOnlyList<MrFilterChip> FilterChips
    {
        get => _chips;
        private set => SetProperty(ref _chips, value);
    }

    /// <summary>"Showing 4 of 27" when a filter is active; null otherwise.</summary>
    public string? FilterSummary
    {
        get => _filterSummary;
        private set => SetProperty(ref _filterSummary, value);
    }

    public RelayCommand ToggleGroupingCommand => _toggleGrouping ??= new RelayCommand(() =>
        Settings.Set(GroupByKey, (GroupByProject ? MrGrouping.Category : MrGrouping.Project).ToString()));

    public RelayCommand ToggleDraftsCommand => _toggleDrafts ??= new RelayCommand(() => Settings.Set(HideDraftsKey, !HideDrafts));

    public RelayCommand ClearFilterCommand => _clear ??= new RelayCommand(() =>
    {
        Settings.Set(FilterKey, nameof(MrQuickFilter.All));
        Settings.Set(HideDraftsKey, false);
    });

    /// <summary>Opens the search window (typing needs a normal window – the dock never takes keyboard focus).</summary>
    public RelayCommand SearchCommand => _search ??= new RelayCommand(() =>
        _events().Publish(new OpenMergeRequestSearchRequested(MergeRequestIndex.All, _instanceId())));

    public RelayCommand ExpandAllCommand => _expandAll ??= new RelayCommand(() => SetAllExpanded(true));

    public RelayCommand CollapseAllCommand => _collapseAll ??= new RelayCommand(() => SetAllExpanded(false));

    /// <summary>New data from a refresh. <paramref name="projects"/> = watched project names in configured order.</summary>
    public void Update(IReadOnlyList<(MergeRequestInfo Info, MrRole Role)> mrs, IReadOnlyList<PipelineInfo> pipelines, IReadOnlyList<string> projects)
    {
        _mrs = mrs;
        _pipelineInfos = pipelines;
        _projects = projects;
        Pipelines = pipelines.Select(p => new PipelineItem(p)).ToList();
        Rebuild();
    }

    /// <summary>Makes these MRs searchable from the search window (all servers / widgets).</summary>
    public void Publish(string title, IReadOnlyList<MergeRequestItem> everything) =>
        MergeRequestIndex.Update(new MergeRequestSearchSource(_instanceId(), title, everything, DateTimeOffset.Now, _refresh));

    public void Unpublish() => MergeRequestIndex.Remove(_instanceId());

    /// <summary>
    /// Handles the board's own view settings. Returns true when <paramref name="key"/> was one of them, so the
    /// widget can skip its network refresh.
    /// </summary>
    public bool HandleSettingChanged(string key)
    {
        switch (key)
        {
            case GroupByKey:
                Rebuild();
                OnPropertyChanged(nameof(Grouping));
                OnPropertyChanged(nameof(GroupByProject));
                OnPropertyChanged(nameof(GroupingButtonText));
                OnPropertyChanged(nameof(ShowPipelineList));
                return true;
            case FilterKey or HideDraftsKey:
                Rebuild();
                OnPropertyChanged(nameof(ActiveFilter));
                OnPropertyChanged(nameof(HideDrafts));
                OnPropertyChanged(nameof(DraftsChipText));
                return true;
            case GroupStatesKey:
                return true;
            default:
                return false;
        }
    }

    private void Rebuild()
    {
        var filter = ActiveFilter;
        var hideDrafts = HideDrafts;
        var filtering = filter != MrQuickFilter.All || hideDrafts;
        var visible = filtering
            ? _mrs.Where(m => MergeRequestFilter.Matches(new MergeRequestItem(m.Info, m.Role), null, filter, hideDrafts)).ToList()
            : _mrs;

        var groups = MrGroup.Build(visible, _pipelineInfos, _projects, Grouping, Settings.Get<Dictionary<string, bool>?>(GroupStatesKey, null),
            RememberExpanded, Noun);
        Groups = filtering ? groups.Where(g => g.Count > 0).ToList() : groups;

        // Chip counts respect the drafts toggle, so the numbers match what a click will show.
        var pool = _mrs.Select(m => new MergeRequestItem(m.Info, m.Role)).Where(i => !(hideDrafts && i.Info.Draft)).ToList();
        FilterChips = Enum.GetValues<MrQuickFilter>()
            .Select(f => new MrFilterChip(f, pool.Count(i => MergeRequestFilter.Passes(i, f)), f == filter, SelectFilter))
            .ToList();
        FilterSummary = filtering ? $"Showing {visible.Count} of {_mrs.Count}" : null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SelectFilter(MrQuickFilter filter) =>
        Settings.Set(FilterKey, (filter == ActiveFilter ? MrQuickFilter.All : filter).ToString()); // click the active chip again to clear

    /// <summary>Remembers expanded/collapsed per group in the widget's settings (layout.json), so it survives restarts.</summary>
    private void RememberExpanded(string key, bool expanded)
    {
        var states = Settings.Get<Dictionary<string, bool>?>(GroupStatesKey, null) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        states[key] = expanded;
        if (states.Count > 200)
        {
            states.Remove(states.Keys.First(k => k != key)); // keep the bag small when projects come and go
        }

        Settings.Set(GroupStatesKey, states);
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var group in Groups)
        {
            group.IsExpanded = expanded;
        }
    }
}
