using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using k8s;
using k8s.Autorest;
using k8s.Exceptions;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Cluster;

/// <summary>A problem row in the popup, with copy-to-clipboard kubectl commands.</summary>
public sealed class ClusterIssueItem(ClusterIssue issue, string describe, string? logs, Action<string> copy, Action<ClusterIssue>? viewLogs = null)
{
    public ClusterIssue Issue { get; } = issue;

    public string Title => $"{Issue.Kind} {Issue.Location}";

    public string Reason => Issue.Reason;

    public string Detail => Issue.Detail;

    public bool IsCritical => Issue.Severity == IssueSeverity.Critical;

    public string Age => Issue.Since is { } since ? ClusterAnalyzer.Age(DateTime.UtcNow - since) : string.Empty;

    public bool HasLogs => logs is not null;

    /// <summary>Click on the row: copies a <c>kubectl describe</c> command.</summary>
    public RelayCommand CopyDescribeCommand { get; } = new(() => copy(describe));

    public RelayCommand CopyLogsCommand { get; } = new(() => copy(logs ?? describe));

    /// <summary>Opens the explorer on this pod's logs (previous container for crash loops).</summary>
    public RelayCommand ViewLogsCommand { get; } = new(() => viewLogs?.Invoke(issue));
}

/// <summary>
/// Read-only health view of one Kubernetes cluster (one kubeconfig context per widget): node readiness,
/// failing pods, degraded workloads, stuck rollouts, pending volumes, failed jobs and recent Warning events.
/// Authenticates exactly like kubectl from the user's kubeconfig (certificates, tokens, exec/OIDC plugins);
/// credentials are never copied into SmartDock's settings. Only get/list calls are made.
/// </summary>
public sealed class KubernetesWidget(IClipboardService clipboard) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "kubernetes";
    private const int AlertAfterChecks = 2;

    private readonly Dictionary<string, int> _seenCount = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly object _clientGate = new();
    private IKubernetes? _client;
    private string? _clientKey;
    private TimeSpan _interval = TimeSpan.FromSeconds(60);

    private ClusterHealth _health = ClusterHealth.Unknown;
    private string _title = "Kubernetes";
    private string _summary = "Not set up";
    private string _server = string.Empty;
    private string? _note;
    private string? _copied;
    private IReadOnlyList<NodeSummary> _nodes = [];
    private IReadOnlyList<ClusterIssueItem> _issues = [];
    private IReadOnlyList<WarningEvent> _events = [];
    private RelayCommand? _refreshCommand;
    private RelayCommand? _exploreCommand;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Kubernetes cluster",
        "Read-only health of one cluster from your kubeconfig: nodes, failing pods, degraded workloads, stuck rollouts and warning events.",
        WidgetCategory.Developer,
        "",
        DockRegion.Right,
        sp => new KubernetesWidget(sp.GetRequiredService<IClipboardService>()))
    {
        AllowMultiple = true, // One widget per cluster/context.
    };

    public ClusterHealth Health
    {
        get => _health;
        private set => SetProperty(ref _health, value);
    }

    /// <summary>Display name (custom name or the context name).</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Card text, e.g. "6/6 nodes · 2 issues".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Server
    {
        get => _server;
        private set => SetProperty(ref _server, value);
    }

    /// <summary>Partial-access or other non-fatal notes ("No permission to list: events").</summary>
    public string? Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    /// <summary>Feedback after a copy ("Copied: kubectl …").</summary>
    public string? Copied
    {
        get => _copied;
        private set => SetProperty(ref _copied, value);
    }

    public IReadOnlyList<NodeSummary> Nodes
    {
        get => _nodes;
        private set => SetProperty(ref _nodes, value);
    }

    public IReadOnlyList<ClusterIssueItem> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }

    public IReadOnlyList<WarningEvent> Events
    {
        get => _events;
        private set => SetProperty(ref _events, value);
    }

    public RelayCommand RefreshNowCommand => _refreshCommand ??= new RelayCommand(() => Context.RequestRefresh?.Invoke());

    public override TimeSpan RefreshInterval => _interval;

    /// <summary>Opens the pods / deployments / live logs window for this cluster.</summary>
    public RelayCommand ExploreCommand => _exploreCommand ??= new RelayCommand(() => OpenExplorer(null, null, false));

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Refresh now", RefreshNowCommand),
        new("Pods, deployments & logs…", ExploreCommand),
    ];

    public IReadOnlyList<WidgetSettingField> SettingFields
    {
        get
        {
            var path = ResolveKubeconfigPath(Settings.Get("kubeconfig", string.Empty));
            var contexts = ReadContexts(path);
            var contextField = contexts.Count > 0
                ? new WidgetSettingField("context", "Context (cluster)", SettingFieldKind.Choice,
                    $"From {path}. One widget per cluster: add the widget again for another context.",
                    Choices: contexts, DefaultText: contexts[0])
                : new WidgetSettingField("context", "Context (cluster)", SettingFieldKind.Text,
                    $"No contexts could be read from {path}. Set the kubeconfig file first, save, then reopen Configure… to pick from a list.");

            return
            [
                new("kubeconfig", "Kubeconfig file", SettingFieldKind.Text,
                    "Leave empty for the KUBECONFIG variable or %USERPROFILE%\\.kube\\config. The file stays where it is; SmartDock only reads it."),
                contextField,
                new("displayName", "Name on the dock (optional)", SettingFieldKind.Text, "Defaults to the context name."),
                new("namespaces", "Namespaces (optional)", SettingFieldKind.Text,
                    "Comma-separated, e.g. \"payments, gateway\". Empty = all namespaces (needs cluster-wide list permission)."),
                new("intervalSeconds", "Check every (seconds)", SettingFieldKind.Number, "20–3600.", DefaultNumber: 60),
                new("restartThreshold", "Flag pods restarting at least this many times (last hour)", SettingFieldKind.Number, DefaultNumber: 5),
                new("pendingMinutes", "Flag pods/volumes pending longer than (minutes)", SettingFieldKind.Number, DefaultNumber: 5),
                new("notify", "Notify me about new critical problems", SettingFieldKind.Toggle, DefaultToggle: true),
                new("allowExec", "Allow opening a shell in pods (kubectl exec)", SettingFieldKind.Toggle,
                    "Off by default. Adds \"Open shell\" to the pods list: it starts your own kubectl exec -it in a terminal window, after a confirmation. Needs kubectl installed and RBAC for pods/exec.",
                    DefaultToggle: false),
                new("execShell", "Shell", SettingFieldKind.Choice, "Auto = bash if the image has it, otherwise sh.",
                    Choices: ["auto", "bash", "sh"], DefaultText: "auto"),
                new("kubectlPath", "kubectl.exe (optional)", SettingFieldKind.Text, "Leave empty to use kubectl from PATH."),
                new("allowChanges", "Allow changing deployments (scale, image, restart, rollback, edit YAML)", SettingFieldKind.Toggle,
                    "Off by default: the explorer is read-only. When on, deployment actions appear; each one is confirmed and logged. Your RBAC still decides what's permitted.",
                    DefaultToggle: false),
            ];
        }
    }

    public void OnPopupOpened()
    {
        Copied = null;
    }

    protected override ValueTask OnDisposeAsync()
    {
        lock (_clientGate)
        {
            _client?.Dispose();
            _client = null;
        }

        return ValueTask.CompletedTask;
    }

    protected override void OnSettingChanged(string key)
    {
        if (key is "kubeconfig" or "context")
        {
            lock (_clientGate)
            {
                _client?.Dispose();
                _client = null; // Rebuilt with the new file/context on the next refresh.
            }

            _seenCount.Clear();
            _notified.Clear();
        }

        base.OnSettingChanged(key);
    }

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        _interval = TimeSpan.FromSeconds(Math.Clamp(Settings.Get("intervalSeconds", 60.0), 20, 3600));
        var path = ResolveKubeconfigPath(Settings.Get("kubeconfig", string.Empty));
        var context = Settings.Get("context", string.Empty).Trim();
        Title = NonEmpty(Settings.Get("displayName", string.Empty)) ?? NonEmpty(context) ?? "Kubernetes";

        if (!File.Exists(path))
        {
            Summary = "No kubeconfig";
            Health = ClusterHealth.Unknown;
            SetUnavailable($"Kubeconfig not found at {path}. Set it in Configure….");
            return;
        }

        if (context.Length == 0)
        {
            Summary = "Pick a context";
            Health = ClusterHealth.Unknown;
            SetUnavailable("Choose which cluster (context) this widget shows: right-click › Configure….");
            return;
        }

        try
        {
            var client = GetClient(path, context);
            var namespaces = Settings.Get("namespaces", string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var denied = new List<string>();

            // Nodes are cluster-scoped; with namespace-only RBAC this is simply reported as not permitted.
            var nodesTask = TryList("nodes", denied, ct => client.CoreV1.ListNodeAsync(cancellationToken: ct), l => l.Items, cancellationToken);
            var podsTask = ListPodsAsync(client, namespaces, denied, cancellationToken);
            var deploymentsTask = PerScope("deployments", namespaces, denied,
                ct => client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: ct),
                (ns, ct) => client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct), l => l.Items, cancellationToken);
            var statefulSetsTask = PerScope("statefulsets", namespaces, denied,
                ct => client.AppsV1.ListStatefulSetForAllNamespacesAsync(cancellationToken: ct),
                (ns, ct) => client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct), l => l.Items, cancellationToken);
            var daemonSetsTask = PerScope("daemonsets", namespaces, denied,
                ct => client.AppsV1.ListDaemonSetForAllNamespacesAsync(cancellationToken: ct),
                (ns, ct) => client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct), l => l.Items, cancellationToken);
            var claimsTask = PerScope("persistentvolumeclaims", namespaces, denied,
                ct => client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(cancellationToken: ct),
                (ns, ct) => client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct), l => l.Items, cancellationToken);
            var jobsTask = PerScope("jobs", namespaces, denied,
                ct => client.BatchV1.ListJobForAllNamespacesAsync(cancellationToken: ct),
                (ns, ct) => client.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: ct), l => l.Items, cancellationToken);
            var eventsTask = PerScope("events", namespaces, denied,
                ct => client.CoreV1.ListEventForAllNamespacesAsync(fieldSelector: "type=Warning", cancellationToken: ct),
                (ns, ct) => client.CoreV1.ListNamespacedEventAsync(ns, fieldSelector: "type=Warning", cancellationToken: ct), l => l.Items, cancellationToken);

            await Task.WhenAll(nodesTask, podsTask, deploymentsTask, statefulSetsTask, daemonSetsTask, claimsTask, jobsTask, eventsTask).ConfigureAwait(false);

            if (podsTask.Result is null && nodesTask.Result is null && deploymentsTask.Result is null)
            {
                throw new UnauthorizedAccessException("Your account can't list nodes, pods or deployments" +
                    (namespaces.Count > 0 ? " in those namespaces." : ". Try limiting the widget to namespaces you can access."));
            }

            var options = new AnalyzerOptions(
                (int)Math.Clamp(Settings.Get("restartThreshold", 5.0), 1, 1000),
                TimeSpan.FromMinutes(Math.Clamp(Settings.Get("pendingMinutes", 5.0), 1, 1440)));
            var snapshot = ClusterAnalyzer.Analyze(nodesTask.Result, podsTask.Result, deploymentsTask.Result, statefulSetsTask.Result,
                daemonSetsTask.Result, claimsTask.Result, jobsTask.Result, eventsTask.Result, DateTime.UtcNow, options);

            Apply(snapshot, context, nodesTask.Result is not null);
            Server = client.BaseUri?.Host ?? string.Empty;
            Note = denied.Count > 0 ? "No permission to list: " + string.Join(", ", denied.Distinct()) : null;
            SetReady();
            LastUpdated = DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Health = ClusterHealth.Unknown;
            Summary = "Unreachable";
            var message = Describe(ex);
            SetDisconnected(message);
            if (ex is KubeConfigException or HttpOperationException { Response.StatusCode: HttpStatusCode.Unauthorized })
            {
                lock (_clientGate)
                {
                    _client?.Dispose();
                    _client = null; // Re-read the kubeconfig next time (e.g. after re-login refreshed the token).
                }
            }
        }
    }

    private void Apply(ClusterSnapshot snapshot, string context, bool nodesVisible)
    {
        var contextArg = context.Contains(' ') ? $"\"{context}\"" : context;
        Nodes = snapshot.Nodes;
        Events = snapshot.Events;
        Issues = snapshot.Issues.Select(i => new ClusterIssueItem(i, DescribeCommand(contextArg, i), LogsCommand(contextArg, i), Copy, ViewLogs)).ToList();
        Health = snapshot.Health;

        var nodePart = nodesVisible ? $"{snapshot.ReadyNodes}/{snapshot.Nodes.Count} nodes" : $"{snapshot.PodCount} pods";
        Summary = snapshot.Issues.Count == 0
            ? nodePart + " · OK"
            : $"{nodePart} · {snapshot.Issues.Count} issue{(snapshot.Issues.Count == 1 ? string.Empty : "s")}";

        NotifyNewProblems(snapshot);
    }

    /// <summary>Critical problems notify once, after being seen on two consecutive checks (no alert for a blip).</summary>
    private void NotifyNewProblems(ClusterSnapshot snapshot)
    {
        var critical = snapshot.Issues.Where(i => i.Severity == IssueSeverity.Critical).ToList();
        var current = critical.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _seenCount.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _seenCount.Remove(gone);
            _notified.Remove(gone);
        }

        var fresh = new List<ClusterIssue>();
        foreach (var issue in critical)
        {
            var seen = _seenCount[issue.Key] = _seenCount.GetValueOrDefault(issue.Key) + 1;
            if (seen >= AlertAfterChecks && _notified.Add(issue.Key))
            {
                fresh.Add(issue);
            }
        }

        if (fresh.Count == 0 || !Settings.Get("notify", true))
        {
            return;
        }

        var title = fresh.Count == 1 ? $"{Title}: {fresh[0].Reason}" : $"{Title}: {fresh.Count} new problems";
        var body = string.Join("\n", fresh.Take(4).Select(i => $"{i.Kind} {i.Location} – {i.Detail}"));
        Context.Notifications.Post(title, body, NotificationSeverity.Error, InstanceId,
            dedupeKey: $"k8s:{InstanceId}:{string.Join("|", fresh.Select(i => i.Key))}", dedupeWindow: TimeSpan.FromMinutes(10));
    }

    private void Copy(string command)
    {
        try
        {
            clipboard.SetText(command);
            Copied = "Copied: " + command;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Copied = "Couldn't copy (clipboard busy) – try again.";
        }
    }

    private void ViewLogs(ClusterIssue issue) =>
        OpenExplorer(issue.Namespace, issue.Kind == "Pod" ? issue.Name : null, issue.Reason is "CrashLoopBackOff" or "Restarting" or "OOMKilled");

    /// <summary>
    /// Opens the explorer window with its own read-only client (so closing it never disturbs the widget's checks).
    /// </summary>
    private void OpenExplorer(string? focusNamespace, string? focusPod, bool previous)
    {
        var path = ResolveKubeconfigPath(Settings.Get("kubeconfig", string.Empty));
        var context = Settings.Get("context", string.Empty).Trim();
        if (!File.Exists(path) || context.Length == 0)
        {
            Context.Notifications.Post("Kubernetes not set up", "Choose a kubeconfig and context first: right-click › Configure….",
                NotificationSeverity.Warning, InstanceId);
            return;
        }

        KubernetesExplorer explorer;
        try
        {
            explorer = new KubernetesExplorer(path, context);
        }
        catch (Exception ex) when (ex is KubeConfigException or IOException or InvalidOperationException or ArgumentException)
        {
            Context.Notifications.Post("Can't open the cluster", ex.Message, NotificationSeverity.Warning, InstanceId);
            return;
        }

        var namespaces = Settings.Get("namespaces", string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exec = new ExecOptions(Settings.Get("allowExec", false), NonEmpty(Settings.Get("kubectlPath", string.Empty)),
            KubectlExec.ParseShell(Settings.Get("execShell", "auto")));
        Context.Events.Publish(new OpenKubernetesExplorerRequested(Title, explorer, namespaces, focusNamespace, focusPod, previous, exec,
            Settings.Get("allowChanges", false)));
    }

    private IKubernetes GetClient(string path, string context)
    {
        var key = path + "|" + context;
        lock (_clientGate)
        {
            if (_client is not null && _clientKey == key)
            {
                return _client;
            }

            _client?.Dispose();
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath: path, currentContext: context);
            _client = new Kubernetes(config);
            _clientKey = key;
            return _client;
        }
    }

    /// <summary>
    /// Pods that aren't Succeeded, paged so a large cluster doesn't produce one huge response. Running pods are
    /// still needed because CrashLoopBackOff pods report phase Running.
    /// </summary>
    private static async Task<IReadOnlyList<V1Pod>?> ListPodsAsync(IKubernetes client, List<string> namespaces, List<string> denied, CancellationToken ct)
    {
        const string selector = "status.phase!=Succeeded";
        const int pageSize = 500;
        const int maxPods = 20_000;
        var result = new List<V1Pod>();
        try
        {
            foreach (var ns in namespaces.Count == 0 ? [string.Empty] : namespaces)
            {
                string? token = null;
                do
                {
                    var page = ns.Length == 0
                        ? await client.CoreV1.ListPodForAllNamespacesAsync(continueParameter: token, fieldSelector: selector, limit: pageSize, cancellationToken: ct).ConfigureAwait(false)
                        : await client.CoreV1.ListNamespacedPodAsync(ns, continueParameter: token, fieldSelector: selector, limit: pageSize, cancellationToken: ct).ConfigureAwait(false);
                    result.AddRange(page.Items);
                    token = page.Metadata?.ContinueProperty;
                }
                while (!string.IsNullOrEmpty(token) && result.Count < maxPods);
            }

            return result;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            lock (denied)
            {
                denied.Add("pods");
            }

            return null;
        }
    }

    private static Task<IReadOnlyList<T>?> PerScope<TList, T>(
        string resource,
        List<string> namespaces,
        List<string> denied,
        Func<CancellationToken, Task<TList>> all,
        Func<string, CancellationToken, Task<TList>> one,
        Func<TList, IList<T>> items,
        CancellationToken cancellationToken)
    {
        if (namespaces.Count == 0)
        {
            return TryList(resource, denied, all, items, cancellationToken);
        }

        return Gather();

        async Task<IReadOnlyList<T>?> Gather()
        {
            var lists = await Task.WhenAll(namespaces.Select(ns => TryList(resource, denied, ct => one(ns, ct), items, cancellationToken))).ConfigureAwait(false);
            return lists.All(l => l is null) ? null : lists.Where(l => l is not null).SelectMany(l => l!).ToList();
        }
    }

    /// <summary>A 403/404 for one resource type isn't fatal: it's reported as "no permission" and skipped.</summary>
    private static async Task<IReadOnlyList<T>?> TryList<TList, T>(
        string resource,
        List<string> denied,
        Func<CancellationToken, Task<TList>> call,
        Func<TList, IList<T>> items,
        CancellationToken cancellationToken)
    {
        try
        {
            return items(await call(cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            lock (denied)
            {
                denied.Add(resource);
            }

            return null;
        }
    }

    private static string DescribeCommand(string context, ClusterIssue issue) => issue.Kind switch
    {
        "Node" => $"kubectl --context {context} describe node {issue.Name}",
        "PVC" => $"kubectl --context {context} -n {issue.Namespace} describe pvc {issue.Name}",
        "Deployment" when issue.Reason == "RolloutStuck" => $"kubectl --context {context} -n {issue.Namespace} rollout status deployment/{issue.Name}",
        _ => $"kubectl --context {context} -n {issue.Namespace} describe {issue.Kind.ToLowerInvariant()} {issue.Name}",
    };

    private static string? LogsCommand(string context, ClusterIssue issue) => issue.Kind == "Pod"
        ? $"kubectl --context {context} -n {issue.Namespace} logs {issue.Name} --all-containers{(issue.Reason is "CrashLoopBackOff" or "Restarting" or "OOMKilled" ? " --previous" : string.Empty)}"
        : null;

    private static string Describe(Exception ex) => ex switch
    {
        KubeConfigException => "Kubeconfig problem: " + ex.Message,
        HttpOperationException { Response.StatusCode: HttpStatusCode.Unauthorized } =>
            "The API server rejected the credentials (401). The token or certificate may have expired – log in again.",
        HttpOperationException { Response.StatusCode: HttpStatusCode.Forbidden } => "Access denied (403): " + ex.Message,
        HttpOperationException http => $"API server error {(int)http.Response.StatusCode}: {ex.Message}",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "TLS error talking to the API server (certificate not trusted or wrong CA in kubeconfig).",
        HttpRequestException => "Can't reach the API server: " + ex.Message,
        OperationCanceledException or TimeoutException => "The API server didn't answer in time.",
        UnauthorizedAccessException => ex.Message,
        System.ComponentModel.Win32Exception => "The kubeconfig's login command (exec plugin) couldn't be started: " + ex.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    internal static string ResolveKubeconfigPath(string? configured)
    {
        if (NonEmpty(configured) is { } custom)
        {
            return Environment.ExpandEnvironmentVariables(custom.Trim('"'));
        }

        // KUBECONFIG may hold several files; the first existing one is used.
        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (NonEmpty(env) is { } list)
        {
            var first = list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);
            if (first is not null)
            {
                return first;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
    }

    private static IReadOnlyList<string> ReadContexts(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var config = KubernetesClientConfiguration.LoadKubeConfig(path);
            return (config.Contexts ?? Enumerable.Empty<k8s.KubeConfigModels.Context>()).Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable or malformed file: fall back to a free-text context field.
            return [];
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
