using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartDock.App.Services;
using SmartDock.Widgets.Cluster;

namespace SmartDock.App.Views;

/// <summary>
/// Read-only cluster explorer for one Kubernetes widget: deployments, pods and live logs (like
/// <c>kubectl get deploy,pods</c> and <c>kubectl logs -f</c>). It never changes anything in the cluster.
/// <list type="bullet">
/// <item>Select a pod → its logs stream on the right (pick a container, tail size, previous instance, timestamps).</item>
/// <item>Select a deployment → its pods are listed; "Logs of all pods" streams them together, each line prefixed.</item>
/// <item>Scroll up (or Pause) to stop auto-scrolling; new lines are counted and appear when you resume.</item>
/// </list>
/// </summary>
public sealed partial class KubernetesExplorerWindow : Window
{
    private const string AllNamespaces = "(all namespaces)";
    private const string AllContainers = "(all containers)";
    private const int MaxLines = 20_000;
    private const int MaxDeploymentPods = 10;

    private readonly KubernetesExplorer _explorer;
    private readonly IReadOnlyList<string> _configuredNamespaces;
    private ExecOptions _exec;

    // Toolbar
    private readonly ComboBox _namespace = new() { IsEditable = true, MinWidth = 200, Margin = new Thickness(6, 0, 12, 0) };
    private readonly TextBox _listFilter = new() { MinWidth = 180, Padding = new Thickness(6, 3, 6, 3) };
    private readonly CheckBox _autoRefresh = new() { Content = "Auto-refresh (10 s)", IsChecked = true, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _listStatus = new() { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    // Lists
    private readonly TabControl _tabs = new();
    private readonly ListView _deployments = new();
    private readonly ListView _pods = new();
    private readonly DockPanel _podScopeBanner = new() { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _podScopeText = new() { VerticalAlignment = VerticalAlignment.Center };

    // Logs
    private readonly TextBlock _logTarget = new() { FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _container = new() { MinWidth = 150, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _tail = new() { Width = 80, Margin = new Thickness(4, 0, 10, 0), ItemsSource = new[] { 100, 500, 1000, 5000 }, SelectedIndex = 1 };
    private readonly CheckBox _follow = new() { Content = "Follow", IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _previous = new() { Content = "Previous container", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Logs of the last crashed/restarted instance (kubectl logs --previous)" };
    private readonly CheckBox _timestamps = new() { Content = "Timestamps", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _wrap = new() { Content = "Wrap", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _errorsOnly = new() { Content = "Warnings & errors only", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _pause = new() { Content = "Pause", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _logFilter = new() { MinWidth = 160, Padding = new Thickness(6, 3, 6, 3), ToolTip = "Show only lines containing this text" };
    private readonly ListBox _log = new() { BorderThickness = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
    private readonly TextBlock _logStatus = new() { Opacity = 0.65, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly List<LogLine> _allLines = [];
    private readonly ObservableCollection<LogLine> _visibleLines = [];
    private readonly ConcurrentQueue<LogLine> _incoming = new();
    private readonly DispatcherTimer _drainTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private IReadOnlyList<DeploymentRow> _deploymentRows = [];
    private IReadOnlyList<PodRow> _podRows = [];
    private DeploymentRow? _podScope;                // pods tab limited to this deployment
    private (string Ns, IReadOnlyList<PodRow> Pods, string Label)? _logSource;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _logCts;
    private int _activeStreams;
    private int _pendingWhilePaused;
    private string? _pendingFocusPod;
    private bool _pendingPrevious;
    private bool _loading;

    public KubernetesExplorerWindow(OpenKubernetesExplorerRequested request)
    {
        _explorer = request.Explorer;
        _configuredNamespaces = request.Namespaces;
        _exec = request.Exec ?? new ExecOptions(false, null, ExecShell.Auto);
        _allowChanges = request.AllowChanges;
        WindowStyling.ApplyFluent(this);
        Title = $"{request.Title} — pods, deployments & logs";
        Width = 1320;
        Height = 780;
        MinWidth = 760;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = BuildLayout();

        _log.ItemsSource = _visibleLines;
        ApplyWrap();
        _drainTimer.Tick += (_, _) => DrainIncoming();
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_autoRefresh.IsChecked == true && IsVisible && WindowState != WindowState.Minimized)
            {
                await LoadListsAsync();
            }
        };

        Loaded += async (_, _) =>
        {
            await FillNamespacesAsync(request.FocusNamespace);
            ShowRequest(request);
            _drainTimer.Start();
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _drainTimer.Stop();
            _refreshTimer.Stop();
            _listCts?.Cancel();
            _logCts?.Cancel();
            _explorer.Dispose();
        };
        PreviewKeyDown += OnKey;
    }

    /// <summary>Same cluster asked again (e.g. "View logs" on another problem): jump to that pod.</summary>
    public void ShowRequest(OpenKubernetesExplorerRequested request)
    {
        if (request.Exec is { } exec)
        {
            _exec = exec; // settings may have changed since the window opened
            UpdateShellButton();
        }

        _allowChanges = request.AllowChanges;
        UpdateChangeButtons();

        if (request.Explorer != _explorer)
        {
            request.Explorer.Dispose(); // this window already has a client for the cluster
        }

        if (request.FocusNamespace is { Length: > 0 } ns && !string.Equals(_namespace.Text, ns, StringComparison.Ordinal))
        {
            _namespace.Text = ns;
        }

        if (request.FocusPod is { Length: > 0 } pod)
        {
            _pendingFocusPod = pod;
            _pendingPrevious = request.Previous;
            _podScope = null;
            _tabs.SelectedIndex = 1;
        }

        _ = LoadListsAsync();
    }

    // ---- Layout ------------------------------------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadListsAsync();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Namespace", VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(_namespace);
        left.Children.Add(new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        left.Children.Add(_listFilter);
        left.Children.Add(refresh);
        left.Children.Add(_autoRefresh);
        DockPanel.SetDock(left, Dock.Left);
        toolbar.Children.Add(left);
        toolbar.Children.Add(_listStatus);

        _namespace.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => OnNamespaceChanged());
        _namespace.LostKeyboardFocus += (_, _) => OnNamespaceChanged();
        _listFilter.TextChanged += (_, _) => ApplyListFilter();

        // Deployments tab
        _deployments.View = Columns(
            ("Name ▲", nameof(DeploymentRow.Name), 220), ("Ready", nameof(DeploymentRow.Ready), 60), ("Up-to-date", nameof(DeploymentRow.UpToDate), 72),
            ("Available", nameof(DeploymentRow.Available), 66), ("Age", nameof(DeploymentRow.Age), 50), ("Images", nameof(DeploymentRow.Images), 260),
            ("Namespace", nameof(DeploymentRow.Namespace), 120));
        _deployments.ItemContainerStyle = RowStyle(nameof(DeploymentRow.Healthy));
        _deployments.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, _deployments, ref _deploymentSort)));
        _deployments.MouseDoubleClick += (_, _) => ShowDeploymentPods(_deployments.SelectedItem as DeploymentRow);
        var deploymentButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var showPods = new Button { Content = "Show its pods", Padding = new Thickness(10, 3, 10, 3) };
        showPods.Click += (_, _) => ShowDeploymentPods(_deployments.SelectedItem as DeploymentRow);
        var allLogs = new Button { Content = "Logs of all its pods", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        allLogs.Click += async (_, _) => await StartDeploymentLogsAsync(_deployments.SelectedItem as DeploymentRow);
        deploymentButtons.Children.Add(showPods);
        deploymentButtons.Children.Add(allLogs);
        deploymentButtons.Children.Add(new TextBlock { Text = "Double-click a deployment to list its pods.", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var deploymentPanel = new DockPanel();
        var changeButtons = BuildChangeButtons();
        DockPanel.SetDock(changeButtons, Dock.Bottom);
        DockPanel.SetDock(deploymentButtons, Dock.Bottom);
        deploymentPanel.Children.Add(changeButtons);
        deploymentPanel.Children.Add(deploymentButtons);
        _deployments.ContextMenu = BuildDeploymentMenu();
        deploymentPanel.Children.Add(_deployments);

        // Pods tab
        _pods.View = Columns(
            ("Name ▲", nameof(PodRow.Name), 260), ("Ready", nameof(PodRow.Ready), 54), ("Status", nameof(PodRow.Status), 130),
            ("Restarts", nameof(PodRow.Restarts), 60), ("Age", nameof(PodRow.Age), 50), ("Node", nameof(PodRow.Node), 140),
            ("IP", nameof(PodRow.Ip), 100), ("Namespace", nameof(PodRow.Namespace), 120));
        _pods.ItemContainerStyle = RowStyle(nameof(PodRow.Healthy));
        _pods.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) => OnHeaderClick(e, _pods, ref _podSort)));
        _pods.SelectionChanged += (_, _) =>
        {
            if (_pods.SelectedItem is PodRow pod && !_loading)
            {
                StartPodLogs(pod);
            }
        };
        var clearScope = new Button { Content = "Show all pods", Padding = new Thickness(10, 2, 10, 2) };
        clearScope.Click += async (_, _) =>
        {
            _podScope = null;
            await LoadListsAsync();
        };
        DockPanel.SetDock(clearScope, Dock.Right);
        _podScopeBanner.Children.Add(clearScope);
        _podScopeBanner.Children.Add(_podScopeText);
        var podButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _shellButton.Click += (_, _) => OpenShell(_pods.SelectedItem as PodRow);
        var copyExec = new Button { Content = "Copy exec command", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        copyExec.Click += (_, _) => CopyExecCommand(_pods.SelectedItem as PodRow);
        podButtons.Children.Add(_shellButton);
        podButtons.Children.Add(copyExec);
        podButtons.Children.Add(new TextBlock { Text = "Click a pod for its logs · right-click for more", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        UpdateShellButton();
        _pods.ContextMenu = BuildPodMenu();

        var podPanel = new DockPanel();
        DockPanel.SetDock(_podScopeBanner, Dock.Top);
        DockPanel.SetDock(podButtons, Dock.Bottom);
        podPanel.Children.Add(_podScopeBanner);
        podPanel.Children.Add(podButtons);
        podPanel.Children.Add(_pods);

        _tabs.Items.Add(new TabItem { Header = "Deployments", Content = deploymentPanel });
        _tabs.Items.Add(new TabItem { Header = "Pods", Content = podPanel });
        _tabs.SelectedIndex = 1;

        // Logs
        var logHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_container, Dock.Right);
        logHeader.Children.Add(_container);
        logHeader.Children.Add(_logTarget);
        _logTarget.Text = "Select a pod to see its logs";
        _container.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                RestartLogs();
            }
        };

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        options.Children.Add(new TextBlock { Text = "Last", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(_tail);
        options.Children.Add(_follow);
        options.Children.Add(_previous);
        options.Children.Add(_timestamps);
        options.Children.Add(_wrap);
        options.Children.Add(_errorsOnly);
        _tail.SelectionChanged += (_, _) => RestartLogs();
        foreach (var box in new[] { _follow, _previous, _timestamps })
        {
            box.Click += (_, _) => RestartLogs();
        }

        _wrap.Click += (_, _) => ApplyWrap();
        _errorsOnly.Click += (_, _) => RebuildVisibleLines();

        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        clear.Click += (_, _) =>
        {
            _allLines.Clear();
            _visibleLines.Clear();
            UpdateLogStatus();
        };
        var copy = new Button { Content = "Copy shown lines", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        copy.Click += (_, _) => CopyLines();
        var reconnect = new Button { Content = "Reconnect", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        reconnect.Click += (_, _) => RestartLogs();
        _pause.Click += (_, _) => SetPaused(_pause.IsChecked == true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_pause);
        buttons.Children.Add(reconnect);
        buttons.Children.Add(clear);
        buttons.Children.Add(copy);
        DockPanel.SetDock(buttons, Dock.Left);
        actions.Children.Add(buttons);
        var filterLabel = new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        DockPanel.SetDock(filterLabel, Dock.Left);
        actions.Children.Add(filterLabel);
        actions.Children.Add(_logFilter);
        _logFilter.TextChanged += (_, _) => RebuildVisibleLines();

        _log.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _log.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _log.SetValue(ScrollViewer.CanContentScrollProperty, true);
        _log.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta > 0 && _pause.IsChecked != true && _visibleLines.Count > 0)
            {
                SetPaused(true); // scrolling up to read: stop auto-scroll
            }
        };
        _log.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyLines(_log.SelectedItems.Cast<LogLine>().ToList());
                e.Handled = true;
            }
        };
        _log.SelectionMode = SelectionMode.Extended;

        var logPanel = new DockPanel();
        foreach (var top in new UIElement[] { logHeader, options, actions })
        {
            DockPanel.SetDock(top, Dock.Top);
            logPanel.Children.Add(top);
        }

        DockPanel.SetDock(_logStatus, Dock.Bottom);
        logPanel.Children.Add(_logStatus);
        logPanel.Children.Add(new Border
        {
            Child = _log,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 300 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 320 });
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(logPanel, 2);
        body.Children.Add(_tabs);
        body.Children.Add(splitter);
        body.Children.Add(logPanel);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(body);
        return root;
    }

    // ---- Shell (kubectl exec) -----------------------------------------------------------------------------------

    private readonly Button _shellButton = new() { Content = "Open shell…", Padding = new Thickness(10, 3, 10, 3) };

    private void UpdateShellButton()
    {
        _shellButton.IsEnabled = _exec.Allowed;
        _shellButton.ToolTip = _exec.Allowed
            ? "Opens kubectl exec -it in a terminal window (after you confirm)"
            : "Turned off. Enable \"Allow opening a shell in pods\" in the widget's Configure…";
        ToolTipService.SetShowOnDisabled(_shellButton, true);
    }

    private ContextMenu BuildPodMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action<PodRow> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (_pods.SelectedItem is PodRow pod)
                {
                    action(pod);
                }
            };
            return item;
        }

        var shell = Item("Open shell (kubectl exec)…", OpenShell);
        menu.Items.Add(Item("Show logs", StartPodLogs));
        menu.Items.Add(Item("Show logs of the previous container", pod =>
        {
            _previous.IsChecked = true;
            StartPodLogs(pod);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(shell);
        menu.Items.Add(Item("Copy exec command", CopyExecCommand));
        menu.Items.Add(Item("Copy pod name", pod => CopyText(pod.Name)));
        menu.Opened += (_, _) =>
        {
            shell.IsEnabled = _exec.Allowed;
            shell.Header = _exec.Allowed ? "Open shell (kubectl exec)…" : "Open shell – turned off in Configure…";
        };
        return menu;
    }

    /// <summary>Container for exec/copy: the one picked in the logs pane for this pod, else its first container.</summary>
    private string? ContainerFor(PodRow pod) =>
        _logSource is { Pods.Count: 1 } source && source.Pods[0].Key == pod.Key && _container.SelectedItem is string c && c != AllContainers
            ? c
            : pod.Containers.FirstOrDefault();

    private void CopyExecCommand(PodRow? pod)
    {
        if (pod is null)
        {
            _listStatus.Text = "Select a pod first.";
            return;
        }

        CopyText(KubectlExec.CommandText(_explorer.KubeconfigPath, _explorer.Context, pod.Namespace, pod.Name, ContainerFor(pod), _exec.Shell, includeKubeconfig: true));
    }

    private void OpenShell(PodRow? pod)
    {
        if (pod is null)
        {
            _listStatus.Text = "Select a pod first.";
            return;
        }

        if (!_exec.Allowed)
        {
            MessageBox.Show(this, "Opening a shell in pods is turned off for this widget.\n\nTurn on \"Allow opening a shell in pods (kubectl exec)\" in the widget's Configure… if you want it.",
                "Open shell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var container = ContainerFor(pod);
        if (KubectlExec.Validate(pod.Namespace, pod.Name, container, _explorer.Context) is { } problem)
        {
            MessageBox.Show(this, problem, "Open shell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (pod.Status != "Running")
        {
            MessageBox.Show(this, $"{pod.Name} is {pod.Status}. A shell can only be opened in a running container.", "Open shell",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (KubectlExec.FindKubectl(_exec.KubectlPath) is not { } kubectl)
        {
            MessageBox.Show(this, _exec.KubectlPath is null
                    ? "kubectl.exe wasn't found on PATH.\n\nInstall kubectl (e.g. winget install Kubernetes.kubectl) or set its location in the widget's Configure…."
                    : $"kubectl wasn't found at {_exec.KubectlPath}.",
                "Open shell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Open an interactive shell in\n\n    {pod.Namespace}/{pod.Name}{(container is null ? string.Empty : $"  (container {container})")}\n    context: {_explorer.Context}\n\n" +
            "Commands you type run inside that container with your account's permissions, and can change it. " +
            "The shell opens in a terminal window; type exit to leave.",
            "Open shell in pod", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var info = KubectlExec.BuildStartInfo(kubectl, _explorer.KubeconfigPath, _explorer.Context, pod.Namespace, pod.Name, container, _exec.Shell);
            var process = System.Diagnostics.Process.Start(info);
            Serilog.Log.Information("Opened kubectl exec shell in {Namespace}/{Pod} container {Container} (context {Context})",
                pod.Namespace, pod.Name, container, _explorer.Context);
            _listStatus.Text = $"Shell opened in {pod.Name} – it runs in its own terminal window.";
            if (process is not null)
            {
                _ = WatchShellAsync(process, pod.Name);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            MessageBox.Show(this, "Couldn't start kubectl: " + ex.Message, "Open shell", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The terminal window closes when kubectl ends. If that happens within seconds with an error, tell the user why
    /// (no shell in the image, no exec permission…) instead of leaving them with a window that flashed.
    /// </summary>
    private async Task WatchShellAsync(System.Diagnostics.Process process, string pod)
    {
        using (process)
        {
            var started = DateTime.UtcNow;
            await process.WaitForExitAsync().ConfigureAwait(true);
            if (process.ExitCode != 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(8))
            {
                _listStatus.Text = $"The shell in {pod} closed right away (kubectl exit code {process.ExitCode}). Possible causes: no permission for pods/exec, " +
                    "the image has no shell (distroless), or the container restarted. Try \"Copy exec command\" and run it in a terminal to see kubectl's message.";
            }
        }
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _listStatus.Text = "Copied: " + (text.Length > 90 ? text[..90] + "…" : text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _listStatus.Text = "The clipboard is busy – try again.";
        }
    }

    // ---- Column sorting ----------------------------------------------------------------------------------------

    private (string Column, bool Ascending) _podSort = ("Name", true);
    private (string Column, bool Ascending) _deploymentSort = ("Name", true);

    /// <summary>Click a header to sort by it; click again to reverse. The arrow shows the current order.</summary>
    private void OnHeaderClick(RoutedEventArgs e, ListView list, ref (string Column, bool Ascending) sort)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column, Role: not GridViewColumnHeaderRole.Padding })
        {
            return;
        }

        var name = HeaderName(column.Header);
        // Second click reverses. Restarts starts with the most restarts first; everything else ascending (Age: newest first).
        sort = sort.Column == name ? (name, !sort.Ascending) : (name, name != "Restarts");

        if (list.View is GridView view)
        {
            foreach (var c in view.Columns)
            {
                var plain = HeaderName(c.Header);
                c.Header = plain == sort.Column ? plain + (sort.Ascending ? " ▲" : " ▼") : plain;
            }
        }

        ApplyListFilter();
    }

    private static string HeaderName(object? header) => (header as string ?? string.Empty).Replace(" ▲", string.Empty).Replace(" ▼", string.Empty);

    private static GridView Columns(params (string Header, string Path, double Width)[] columns)
    {
        var view = new GridView();
        foreach (var (header, path, width) in columns)
        {
            view.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(path), Width = width });
        }

        return view;
    }

    /// <summary>Unhealthy rows (not ready, crash looping…) in an amber/red tint.</summary>
    private static Style RowStyle(string healthyProperty)
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var trigger = new DataTrigger { Binding = new Binding(healthyProperty), Value = false };
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x4C))));
        style.Triggers.Add(trigger);
        return style;
    }

    private void ApplyWrap()
    {
        var wrap = _wrap.IsChecked == true;
        _log.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogLine.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        var template = new DataTemplate(typeof(LogLine)) { VisualTree = text };
        var error = new DataTrigger { Binding = new Binding(nameof(LogLine.Level)), Value = LogSeverity.Error };
        error.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x5D))));
        var warning = new DataTrigger { Binding = new Binding(nameof(LogLine.Level)), Value = LogSeverity.Warning };
        warning.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41))));
        template.Triggers.Add(error);
        template.Triggers.Add(warning);
        _log.ItemTemplate = template;
    }

    // ---- Namespaces & lists ------------------------------------------------------------------------------------

    private async Task FillNamespacesAsync(string? focus)
    {
        var items = new List<string>();
        if (_configuredNamespaces.Count > 0)
        {
            if (_configuredNamespaces.Count > 1)
            {
                items.Add(AllNamespaces); // = all configured namespaces
            }

            items.AddRange(_configuredNamespaces);
        }
        else
        {
            items.Add(AllNamespaces);
            try
            {
                if (await _explorer.ListNamespacesAsync(CancellationToken.None) is { } names)
                {
                    items.AddRange(names);
                }
            }
            catch (Exception ex)
            {
                _listStatus.Text = KubernetesExplorer.Describe(ex);
            }
        }

        _loading = true;
        _namespace.ItemsSource = items;
        _namespace.Text = focus is { Length: > 0 } ? focus : items[0];
        _loading = false;
    }

    private string _lastNamespace = "\0";

    private void OnNamespaceChanged()
    {
        if (_loading || _namespace.Text == _lastNamespace)
        {
            return;
        }

        _podScope = null;
        _ = LoadListsAsync();
    }

    /// <summary>Namespaces to query: one, all configured ones, or "" = the whole cluster.</summary>
    private IReadOnlyList<string> Scope()
    {
        var text = (_namespace.Text ?? string.Empty).Trim();
        if (text.Length == 0 || text == AllNamespaces)
        {
            return _configuredNamespaces.Count > 0 ? _configuredNamespaces : new[] { string.Empty };
        }

        return new[] { text };
    }

    private async Task LoadListsAsync()
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _lastNamespace = _namespace.Text;
        var scope = Scope();
        var selector = _podScope?.Selector;
        _listStatus.Text = "Loading…";
        try
        {
            var deployments = new List<DeploymentRow>();
            var pods = new List<PodRow>();
            foreach (var ns in _podScope is { } d ? new[] { d.Namespace } : scope)
            {
                var deploymentTask = _explorer.ListDeploymentsAsync(ns, cts.Token);
                var podTask = _explorer.ListPodsAsync(ns, selector, cts.Token);
                deployments.AddRange(await deploymentTask);
                pods.AddRange(await podTask);
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _deploymentRows = deployments;
            _podRows = pods;
            ApplyListFilter();
            _listStatus.Text = $"{deployments.Count} deployments · {pods.Count} pods{(selector is null ? string.Empty : " (filtered)")} · {_explorer.Server} · {DateTime.Now:T}";
            FocusPendingPod();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _listStatus.Text = KubernetesExplorer.Describe(ex);
        }
    }

    private void ApplyListFilter()
    {
        var filter = _listFilter.Text.Trim();
        bool Matches(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        _loading = true;
        try
        {
            var selectedDeployment = (_deployments.SelectedItem as DeploymentRow)?.Key;
            var selectedPod = (_pods.SelectedItem as PodRow)?.Key;
            var deployments = ExplorerSort.Deployments(_deploymentRows.Where(d => Matches(d.Name) || Matches(d.Images)), _deploymentSort.Column, _deploymentSort.Ascending);
            var pods = ExplorerSort.Pods(_podRows.Where(p => Matches(p.Name) || Matches(p.Status) || Matches(p.Node)), _podSort.Column, _podSort.Ascending);
            _deployments.ItemsSource = deployments;
            _pods.ItemsSource = pods;
            _deployments.SelectedItem = deployments.FirstOrDefault(d => d.Key == selectedDeployment);
            _pods.SelectedItem = pods.FirstOrDefault(p => p.Key == selectedPod);
            ((TabItem)_tabs.Items[0]).Header = $"Deployments ({deployments.Count})";
            ((TabItem)_tabs.Items[1]).Header = $"Pods ({pods.Count})";
            _podScopeBanner.Visibility = _podScope is null ? Visibility.Collapsed : Visibility.Visible;
            _podScopeText.Text = _podScope is { } d ? $"Pods of deployment {d.Namespace}/{d.Name}" : string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private void FocusPendingPod()
    {
        if (_pendingFocusPod is not { } name)
        {
            return;
        }

        if (_podRows.FirstOrDefault(p => p.Name == name) is { } pod)
        {
            _pendingFocusPod = null;
            _tabs.SelectedIndex = 1;
            _previous.IsChecked = _pendingPrevious;
            _pods.SelectedItem = pod;
            _pods.ScrollIntoView(pod);
            StartPodLogs(pod);
        }
    }

    private void ShowDeploymentPods(DeploymentRow? deployment)
    {
        if (deployment is null)
        {
            return;
        }

        if (deployment.Selector is null)
        {
            _listStatus.Text = $"{deployment.Name} has no label selector – can't tell which pods are its own.";
            return;
        }

        _podScope = deployment;
        _tabs.SelectedIndex = 1;
        _ = LoadListsAsync();
    }

    // ---- Logs --------------------------------------------------------------------------------------------------

    private void StartPodLogs(PodRow pod)
    {
        _loading = true;
        var containers = pod.Containers.Count > 1 ? new[] { AllContainers }.Concat(pod.Containers).ToList() : pod.Containers.ToList();
        _container.ItemsSource = containers;
        _container.SelectedIndex = containers.Count > 1 ? 1 : 0; // first real container by default
        _container.Visibility = containers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
        _logSource = (pod.Namespace, new[] { pod }, $"{pod.Namespace}/{pod.Name}");
        RestartLogs();
    }

    private async Task StartDeploymentLogsAsync(DeploymentRow? deployment)
    {
        if (deployment?.Selector is null)
        {
            _listStatus.Text = "Select a deployment first.";
            return;
        }

        try
        {
            var pods = await _explorer.ListPodsAsync(deployment.Namespace, deployment.Selector, CancellationToken.None);
            var chosen = pods.Where(p => p.Status == "Running").Concat(pods.Where(p => p.Status != "Running")).Take(MaxDeploymentPods).ToList();
            if (chosen.Count == 0)
            {
                _listStatus.Text = $"{deployment.Name} has no pods right now.";
                return;
            }

            _loading = true;
            _container.ItemsSource = new[] { AllContainers };
            _container.SelectedIndex = 0;
            _container.Visibility = Visibility.Collapsed;
            _loading = false;
            var more = pods.Count > chosen.Count ? $" (first {chosen.Count} of {pods.Count})" : string.Empty;
            _logSource = (deployment.Namespace, chosen, $"deployment {deployment.Namespace}/{deployment.Name} · {chosen.Count} pods{more}");
            RestartLogs();
        }
        catch (Exception ex)
        {
            _listStatus.Text = KubernetesExplorer.Describe(ex);
        }
    }

    /// <summary>(Re)starts the log streams for the current source and options.</summary>
    private void RestartLogs()
    {
        if (_loading || _logSource is not { } source)
        {
            return;
        }

        _logCts?.Cancel();
        var cts = _logCts = new CancellationTokenSource();
        while (_incoming.TryDequeue(out _))
        {
        }

        _allLines.Clear();
        _visibleLines.Clear();
        _pendingWhilePaused = 0;
        SetPaused(false);
        _logTarget.Text = source.Label;

        var tail = _tail.SelectedItem is int n ? n : 500;
        var follow = _follow.IsChecked == true;
        var previous = _previous.IsChecked == true;
        var timestamps = _timestamps.IsChecked == true;
        var selectedContainer = _container.SelectedItem as string;
        var multiPod = source.Pods.Count > 1;

        var streams = new List<(PodRow Pod, string? Container, string? Prefix)>();
        foreach (var pod in source.Pods)
        {
            IReadOnlyList<string> containers = selectedContainer is null or AllContainers ? pod.Containers : new[] { selectedContainer };
            IEnumerable<string?> targets = containers.Count == 0 ? new string?[] { null } : containers.Select(c => (string?)c);
            foreach (var container in targets)
            {
                var prefix = (multiPod ? ShortPodName(pod.Name) : null) is { } p
                    ? containers.Count > 1 ? $"{p}/{container}" : p
                    : containers.Count > 1 ? container : null;
                streams.Add((pod, container, prefix));
            }
        }

        // With many streams keep the backlog small so the window stays responsive.
        var perStreamTail = streams.Count > 1 ? Math.Max(20, tail / streams.Count) : tail;
        _activeStreams = streams.Count;
        foreach (var (pod, container, prefix) in streams)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _explorer.StreamLogsAsync(pod.Namespace, pod.Name, container, perStreamTail, follow, previous, timestamps,
                        line => _incoming.Enqueue(LogLine.Parse(line, prefix)), cts.Token).ConfigureAwait(false);
                    if (!cts.IsCancellationRequested && follow && !previous)
                    {
                        _incoming.Enqueue(new LogLine("— log stream ended (container restarted or pod gone) – click Reconnect —", LogSeverity.Warning, prefix));
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    if (!cts.IsCancellationRequested)
                    {
                        _incoming.Enqueue(new LogLine("⚠ " + KubernetesExplorer.Describe(ex), LogSeverity.Error, prefix));
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeStreams);
                }
            });
        }

        UpdateLogStatus();
    }

    private static string ShortPodName(string name)
    {
        // web-7d9f8c6b5-x2k4p → x2k4p (the unique part); fall back to the whole name.
        var dash = name.LastIndexOf('-');
        return dash > 0 && dash < name.Length - 1 ? name[(dash + 1)..] : name;
    }

    private void DrainIncoming()
    {
        if (_incoming.IsEmpty)
        {
            UpdateLogStatus();
            return;
        }

        var batch = new List<LogLine>();
        while (batch.Count < 5000 && _incoming.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        _allLines.AddRange(batch);
        if (_allLines.Count > MaxLines)
        {
            _allLines.RemoveRange(0, _allLines.Count - MaxLines);
        }

        if (_pause.IsChecked == true)
        {
            _pendingWhilePaused += batch.Count;
            UpdateLogStatus();
            return;
        }

        AppendVisible(batch);
    }

    private void AppendVisible(IEnumerable<LogLine> lines)
    {
        var added = false;
        foreach (var line in lines.Where(ShowLine))
        {
            _visibleLines.Add(line);
            added = true;
        }

        while (_visibleLines.Count > MaxLines)
        {
            _visibleLines.RemoveAt(0);
        }

        if (added && _pause.IsChecked != true && _visibleLines.Count > 0)
        {
            _log.ScrollIntoView(_visibleLines[^1]);
        }

        UpdateLogStatus();
    }

    private bool ShowLine(LogLine line) =>
        (_errorsOnly.IsChecked != true || line.Level != LogSeverity.Normal)
        && (_logFilter.Text.Length == 0 || line.Display.Contains(_logFilter.Text, StringComparison.OrdinalIgnoreCase));

    private void RebuildVisibleLines()
    {
        _visibleLines.Clear();
        AppendVisible(_allLines);
    }

    private void SetPaused(bool paused)
    {
        _pause.IsChecked = paused;
        _pause.Content = paused ? "Resume" : "Pause";
        if (!paused && _pendingWhilePaused > 0)
        {
            _pendingWhilePaused = 0;
            RebuildVisibleLines();
        }

        UpdateLogStatus();
    }

    private void UpdateLogStatus()
    {
        if (_logSource is null)
        {
            _logStatus.Text = "Tip: select a pod, or a deployment › \"Logs of all its pods\".";
            return;
        }

        var state = Volatile.Read(ref _activeStreams) > 0 ? (_follow.IsChecked == true && _previous.IsChecked != true ? "following" : "loading") : "stopped";
        var paused = _pause.IsChecked == true ? $" · paused{(_pendingWhilePaused > 0 ? $" ({_pendingWhilePaused} new lines)" : string.Empty)} – Resume to continue" : string.Empty;
        var shown = _visibleLines.Count == _allLines.Count ? $"{_allLines.Count} lines" : $"{_visibleLines.Count} of {_allLines.Count} lines";
        _logStatus.Text = $"{state} · {shown}{paused} · Ctrl+C copies selected lines";
    }

    private void CopyLines(IReadOnlyList<LogLine>? lines = null)
    {
        var text = new StringBuilder();
        foreach (var line in lines is { Count: > 0 } ? lines : _visibleLines)
        {
            text.AppendLine(line.Display);
        }

        try
        {
            Clipboard.SetText(text.ToString());
            _logStatus.Text = "Copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _logStatus.Text = "The clipboard is busy – try again.";
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = LoadListsAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _logFilter.Focus();
            _logFilter.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetPaused(_pause.IsChecked != true);
            e.Handled = true;
        }
    }
}
