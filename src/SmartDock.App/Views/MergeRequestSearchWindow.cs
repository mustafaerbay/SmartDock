using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SmartDock.App.Services;
using SmartDock.Widgets.CodeRepo;
using SmartDock.Widgets.Common;

namespace SmartDock.App.Views;

/// <summary>
/// Search and filter window for merge / pull requests of every code widget (CodeHub / GitLab, GitHub) (the dock can't take keyboard input).
/// Covers every CodeHub widget; uses the MRs they already loaded, so typing never calls the server.
/// Enter opens the selected MR, Ctrl+Shift+C copies its link, Esc closes.
/// </summary>
public sealed class MergeRequestSearchWindow : Window
{
    private const string AllSources = "All code widgets";
    private const int MaxShown = 300;

    private readonly TextBox _query = new() { FontSize = 15, Padding = new Thickness(10, 6, 10, 6) };
    private readonly ComboBox _source = new() { MinWidth = 170, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideDrafts = new() { Content = "Hide drafts", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _filters = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Opacity = 0.65, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _detail = new() { Margin = new Thickness(14, 10, 0, 0) };
    private IReadOnlyList<MergeRequestSearchSource> _sources = [];
    private MrQuickFilter _filter = MrQuickFilter.All;
    private bool _syncing;
    private readonly Button _refresh = new() { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Reload from the server (F5)" };
    private readonly Dictionary<string, DateTimeOffset> _waitingFor = new(StringComparer.Ordinal);
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimeout = new() { Interval = TimeSpan.FromSeconds(45) };
    private string? _refreshNote;

    public MergeRequestSearchWindow()
    {
        WindowStyling.ApplyFluent(this);
        Width = 980;
        Height = 600;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var hint = new TextBlock
        {
            Text = "Words match title, project, !number / #number, author, branches, labels, reviewers.  Try:  author:ali  label:bug  target:master  is:stale  -is:draft  \"exact phrase\"",
            Opacity = 0.55,
            FontSize = 11,
            Margin = new Thickness(2, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var top = new DockPanel();
        DockPanel.SetDock(_refresh, Dock.Right);
        DockPanel.SetDock(_source, Dock.Right);
        top.Children.Add(_refresh);
        top.Children.Add(_source);
        _refresh.Click += (_, _) => RefreshFromServers();
        top.Children.Add(_query);

        foreach (var filter in Enum.GetValues<MrQuickFilter>())
        {
            var chip = new ToggleButton { Tag = filter, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(10, 3, 10, 3), Focusable = false };
            chip.Click += (_, _) =>
            {
                _filter = (MrQuickFilter)chip.Tag;
                Filter();
            };
            _filters.Children.Add(chip);
        }

        _filters.Children.Add(_hideDrafts);

        var header = new StackPanel();
        header.Children.Add(top);
        header.Children.Add(hint);
        header.Children.Add(_filters);

        _results.ItemTemplate = BuildTemplate();
        _results.Margin = new Thickness(0, 8, 0, 0);
        _results.SelectionChanged += (_, _) => ShowDetail(_results.SelectedItem as Row);
        _results.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_results, (DependencyObject)e.OriginalSource) is ListBoxItem)
            {
                OpenSelected();
            }
        };

        var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
        Grid.SetColumn(detailScroll, 1);
        body.Children.Add(_results);
        body.Children.Add(detailScroll);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _query.TextChanged += (_, _) => Filter();
        _hideDrafts.Click += (_, _) => Filter();
        _source.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
            {
                Filter();
            }
        };
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => _query.Focus();

        // Live: whenever a widget publishes new data (its own schedule or our Refresh), the list follows.
        Action onIndexChanged = () => Dispatcher.BeginInvoke(OnIndexChanged);
        MergeRequestIndex.Changed += onIndexChanged;
        Closed += (_, _) =>
        {
            MergeRequestIndex.Changed -= onIndexChanged;
            _refreshTimeout.Stop();
        };
        _refreshTimeout.Tick += (_, _) =>
        {
            _refreshTimeout.Stop();
            if (_waitingFor.Count > 0)
            {
                var names = _sources.Where(s => _waitingFor.ContainsKey(s.InstanceId)).Select(s => s.Title);
                _refreshNote = "No new data from " + string.Join(", ", names) + " – check the widget (server reachable, token valid).";
                _waitingFor.Clear();
                SetRefreshing(false);
                Filter();
            }
        };

        // Coming back after a while (e.g. you merged or closed an MR in the browser): refresh automatically.
        Activated += (_, _) =>
        {
            if (!IsLoaded)
            {
                return;
            }

            Reload(MergeRequestIndex.All, null);
            if (_sources.Count > 0 && DateTimeOffset.Now - _sources.Min(s => s.AsOf) > TimeSpan.FromSeconds(60))
            {
                RefreshFromServers();
            }
        };
    }

    /// <summary>Asks the widgets behind the selected source(s) to reload now; the list updates as each one finishes.</summary>
    private void RefreshFromServers()
    {
        if (_waitingFor.Count > 0)
        {
            return; // already refreshing
        }

        var sourceName = _source.SelectedItem as string;
        var targets = _sources.Where(s => s.Refresh is not null && (sourceName is null or AllSources || s.Title == sourceName)).ToList();
        if (targets.Count == 0)
        {
            _refreshNote = "Nothing to refresh yet.";
            Filter();
            return;
        }

        _refreshNote = null;
        foreach (var source in targets)
        {
            _waitingFor[source.InstanceId] = source.AsOf;
            source.Refresh!();
        }

        SetRefreshing(true);
        _refreshTimeout.Stop();
        _refreshTimeout.Start();
    }

    private void OnIndexChanged()
    {
        Reload(MergeRequestIndex.All, null);
        foreach (var source in _sources)
        {
            if (_waitingFor.TryGetValue(source.InstanceId, out var before) && source.AsOf > before)
            {
                _waitingFor.Remove(source.InstanceId);
            }
        }

        // Widgets that disappeared meanwhile aren't waited for.
        foreach (var gone in _waitingFor.Keys.Where(id => _sources.All(s => s.InstanceId != id)).ToList())
        {
            _waitingFor.Remove(gone);
        }

        if (_waitingFor.Count == 0 && !_refresh.IsEnabled)
        {
            _refreshTimeout.Stop();
            SetRefreshing(false);
            Filter();
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        _refresh.IsEnabled = !refreshing;
        _refresh.Content = refreshing ? "Refreshing…" : "Refresh";
    }

    public void Show(OpenMergeRequestSearchRequested request)
    {
        Reload(request.Sources, request.PreferredInstanceId);
        _query.SelectAll();
        _query.Focus();
    }

    private void Reload(IReadOnlyList<MergeRequestSearchSource> sources, string? preferredInstanceId)
    {
        var previousSource = _source.SelectedItem as string;
        _sources = sources;
        _syncing = true;
        try
        {
            var names = new List<string>();
            if (sources.Count > 1)
            {
                names.Add(AllSources);
            }

            names.AddRange(sources.Select(s => s.Title));
            _source.ItemsSource = names;
            _source.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            var preferred = preferredInstanceId is null ? null : sources.FirstOrDefault(s => s.InstanceId == preferredInstanceId)?.Title;
            _source.SelectedItem = preferredInstanceId is not null && sources.Count > 1
                ? AllSources // opened from a widget: start broad, the server column tells them apart
                : previousSource is not null && names.Contains(previousSource) ? previousSource : preferred ?? names.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }

        Filter();
    }

    private void Filter()
    {
        var selected = (_results.SelectedItem as Row)?.Item.Info.Key;
        var sourceName = _source.SelectedItem as string;
        var pool = _sources
            .Where(s => sourceName is null or AllSources || s.Title == sourceName)
            .SelectMany(s => s.Items.Select(i => new Row(i, s.Title, _sources.Count > 1)))
            .ToList();
        var hideDrafts = _hideDrafts.IsChecked == true;
        var matches = pool
            .Where(r => MergeRequestFilter.Matches(r.Item, _query.Text, _filter, hideDrafts))
            .OrderBy(r => r.Item.Role switch { MrRole.Review => 0, MrRole.Mine => 1, _ => 2 })
            .ThenByDescending(r => r.Item.Info.Updated)
            .ToList();

        foreach (var chip in _filters.Children.OfType<ToggleButton>().Where(c => c.Tag is MrQuickFilter))
        {
            var f = (MrQuickFilter)chip.Tag;
            var count = pool.Count(r => MergeRequestFilter.Matches(r.Item, _query.Text, f, hideDrafts));
            chip.Content = $"{MergeRequestFilter.Label(f)}  {count}";
            chip.IsChecked = f == _filter;
        }

        var shown = matches.Take(MaxShown).ToList();
        _results.ItemsSource = shown;
        _results.SelectedItem = shown.FirstOrDefault(r => r.Item.Info.Key == selected) ?? shown.FirstOrDefault();
        if (_results.SelectedItem is null)
        {
            ShowDetail(null);
        }

        var asOf = _sources.Count > 0 ? _sources.Min(s => s.AsOf) : (DateTimeOffset?)null;
        _status.Text = (matches.Count > MaxShown ? $"Showing {MaxShown} of {matches.Count}" : $"{matches.Count} of {pool.Count} open merge / pull requests")
            + (_waitingFor.Count > 0 ? " · refreshing from the server…"
                : asOf is { } t ? $" · updated {RelativeTime.Ago(t, DateTimeOffset.Now)}" : " · no data yet – wait for a code widget to refresh")
            + (_refreshNote is { } note ? " · " + note : string.Empty)
            + " · F5: refresh · Enter: open · Ctrl+Shift+C: copy link · Esc: close";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F5:
                RefreshFromServers();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                CopySelectedLink();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _query.IsKeyboardFocusWithin && _results.Items.Count > 0:
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OpenSelected()
    {
        if (_results.SelectedItem is Row row)
        {
            row.Item.OpenCommand.Execute(null);
        }
    }

    private void CopySelectedLink()
    {
        if (_results.SelectedItem is Row { Item.Info.Url.Length: > 0 } row)
        {
            try
            {
                Clipboard.SetText(row.Item.Info.Url);
                _status.Text = "Link copied: " + row.Item.Info.Url;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                _status.Text = "The clipboard is busy – try again.";
            }
        }
    }

    private void ShowDetail(Row? row)
    {
        _detail.Children.Clear();
        if (row is null)
        {
            _detail.Children.Add(new TextBlock { Text = "No merge request selected.", Opacity = 0.6 });
            return;
        }

        var item = row.Item;
        _detail.Children.Add(new TextBlock { Text = item.FullTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _detail.Children.Add(new TextBlock { Text = item.Reference + (row.ShowSource ? " · " + row.Source : string.Empty), Opacity = 0.65, Margin = new Thickness(0, 2, 0, 10) });

        void Field(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = Brushes.Gray });
            line.Inlines.Add(new System.Windows.Documents.Run(value));
            _detail.Children.Add(line);
        }

        Field("Author", item.AuthorText);
        Field("Branches", item.BranchesText);
        Field("Reviewers", item.ReviewersText);
        Field("Assignees", item.AssigneesText);
        Field("Status", item.MergeStatusText);
        Field("Labels", item.LabelsText);
        Field("Milestone", item.Milestone);
        Field("Activity", item.ActivityText);
        if (item.DatesText.Length > 0)
        {
            _detail.Children.Add(new TextBlock { Text = item.DatesText, Opacity = 0.65, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var open = new Button { Content = "Open in browser", Padding = new Thickness(12, 4, 12, 4), Focusable = false };
        open.Click += (_, _) => item.OpenCommand.Execute(null);
        var copy = new Button { Content = "Copy link", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), Focusable = false };
        copy.Click += (_, _) => CopySelectedLink();
        buttons.Children.Add(open);
        buttons.Children.Add(copy);
        _detail.Children.Add(buttons);

        if (item.DescriptionPreview is { } description)
        {
            _detail.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
            _detail.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        }
    }

    private static DataTemplate BuildTemplate()
    {
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding("Item.Title"));
        title.SetValue(TextBlock.FontSizeProperty, 13.5);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Line)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.5);
        detail.SetValue(TextBlock.OpacityProperty, 0.65);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.AppendChild(title);
        stack.AppendChild(detail);

        var badge = new FrameworkElementFactory(typeof(TextBlock));
        badge.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Badge)));
        badge.SetValue(TextBlock.FontSizeProperty, 11.0);
        badge.SetValue(TextBlock.OpacityProperty, 0.75);
        badge.SetValue(TextBlock.MarginProperty, new Thickness(10, 1, 0, 0));
        badge.SetValue(DockPanel.DockProperty, Dock.Right);
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        dock.AppendChild(badge);
        dock.AppendChild(stack);
        return new DataTemplate(typeof(Row)) { VisualTree = dock };
    }

    /// <summary>One result row: the MR plus which CodeHub widget it came from.</summary>
    private sealed class Row(MergeRequestItem item, string source, bool showSource)
    {
        public MergeRequestItem Item { get; } = item;

        public string Source { get; } = source;

        public bool ShowSource { get; } = showSource;

        public string Line => (ShowSource ? Source + " · " : string.Empty) + Item.Detail;

        /// <summary>"Review · 3 d", "Yours · 9 d (stale)".</summary>
        public string Badge => string.Join(" · ", new[]
        {
            Item.Role switch { MrRole.Review => "Review", MrRole.Mine => "Yours", _ => null },
            Item.HasAge ? Item.Age + (Item.IsStale ? " (open long)" : string.Empty) : null,
        }.Where(s => s is not null));
    }
}
