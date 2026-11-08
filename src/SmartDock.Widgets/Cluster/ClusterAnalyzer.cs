using System.Globalization;
using k8s.Models;

namespace SmartDock.Widgets.Cluster;

public enum ClusterHealth
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

public enum IssueSeverity
{
    Warning,
    Critical,
}

/// <summary>Something in the cluster that needs attention.</summary>
public sealed record ClusterIssue(
    IssueSeverity Severity,
    string Kind,
    string? Namespace,
    string Name,
    string Reason,
    string Detail,
    DateTime? Since)
{
    /// <summary>Stable identity used to notify once per problem.</summary>
    public string Key => $"{Kind}/{Namespace}/{Name}/{Reason}";

    public string Location => Namespace is null ? Name : $"{Namespace}/{Name}";
}

public sealed record NodeSummary(string Name, bool Ready, string Roles, string Conditions, string Version);

public sealed record WarningEvent(string Reason, string Object, string Message, int Count, DateTime? Last);

public sealed record ClusterSnapshot(
    IReadOnlyList<NodeSummary> Nodes,
    IReadOnlyList<ClusterIssue> Issues,
    IReadOnlyList<WarningEvent> Events,
    int PodCount)
{
    public int ReadyNodes => Nodes.Count(n => n.Ready);

    public ClusterHealth Health =>
        Issues.Any(i => i.Severity == IssueSeverity.Critical) ? ClusterHealth.Critical
        : Issues.Count > 0 ? ClusterHealth.Warning
        : ClusterHealth.Healthy;
}

public sealed record AnalyzerOptions(int RestartThreshold = 5, TimeSpan? PendingAfter = null, TimeSpan? RecentWindow = null)
{
    public TimeSpan Pending => PendingAfter ?? TimeSpan.FromMinutes(5);

    public TimeSpan Recent => RecentWindow ?? TimeSpan.FromHours(1);
}

/// <summary>
/// Turns raw Kubernetes objects into the short list of things worth a human's attention. Pure (no I/O),
/// so it is unit-tested with hand-built objects.
/// </summary>
public static class ClusterAnalyzer
{
    private static readonly HashSet<string> BadWaitingReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "CreateContainerConfigError",
        "CreateContainerError", "InvalidImageName", "RunContainerError",
    };

    public static ClusterSnapshot Analyze(
        IReadOnlyList<V1Node>? nodes,
        IReadOnlyList<V1Pod>? pods,
        IReadOnlyList<V1Deployment>? deployments,
        IReadOnlyList<V1StatefulSet>? statefulSets,
        IReadOnlyList<V1DaemonSet>? daemonSets,
        IReadOnlyList<V1PersistentVolumeClaim>? claims,
        IReadOnlyList<V1Job>? jobs,
        IReadOnlyList<Corev1Event>? events,
        DateTime nowUtc,
        AnalyzerOptions? options = null)
    {
        options ??= new AnalyzerOptions();
        var issues = new List<ClusterIssue>();
        var nodeRows = new List<NodeSummary>();

        foreach (var node in nodes ?? Array.Empty<V1Node>())
        {
            nodeRows.Add(SummarizeNode(node, issues));
        }

        foreach (var pod in pods ?? Array.Empty<V1Pod>())
        {
            if (AnalyzePod(pod, nowUtc, options) is { } issue)
            {
                issues.Add(issue);
            }
        }

        foreach (var d in deployments ?? Array.Empty<V1Deployment>())
        {
            var stuck = d.Status?.Conditions?.FirstOrDefault(c => c.Type == "Progressing" && c.Reason == "ProgressDeadlineExceeded");
            if (stuck is not null)
            {
                issues.Add(new ClusterIssue(IssueSeverity.Critical, "Deployment", d.Metadata?.NamespaceProperty, d.Metadata?.Name ?? "?",
                    "RolloutStuck", stuck.Message ?? "Rollout exceeded its progress deadline", stuck.LastUpdateTime));
                continue;
            }

            AddReplicaIssue(issues, "Deployment", d.Metadata, d.Spec?.Replicas ?? 1, d.Status?.ReadyReplicas ?? 0);
        }

        foreach (var s in statefulSets ?? Array.Empty<V1StatefulSet>())
        {
            AddReplicaIssue(issues, "StatefulSet", s.Metadata, s.Spec?.Replicas ?? 1, s.Status?.ReadyReplicas ?? 0);
        }

        foreach (var ds in daemonSets ?? Array.Empty<V1DaemonSet>())
        {
            AddReplicaIssue(issues, "DaemonSet", ds.Metadata, ds.Status?.DesiredNumberScheduled ?? 0, ds.Status?.NumberReady ?? 0);
        }

        foreach (var pvc in claims ?? Array.Empty<V1PersistentVolumeClaim>())
        {
            var created = pvc.Metadata?.CreationTimestamp;
            if (pvc.Status?.Phase == "Pending" && created is { } c && nowUtc - c > options.Pending)
            {
                issues.Add(new ClusterIssue(IssueSeverity.Warning, "PVC", pvc.Metadata?.NamespaceProperty, pvc.Metadata?.Name ?? "?",
                    "Pending", $"Unbound for {Age(nowUtc - c)}", created));
            }
            else if (pvc.Status?.Phase == "Lost")
            {
                issues.Add(new ClusterIssue(IssueSeverity.Critical, "PVC", pvc.Metadata?.NamespaceProperty, pvc.Metadata?.Name ?? "?",
                    "Lost", "The bound volume no longer exists", created));
            }
        }

        foreach (var job in jobs ?? Array.Empty<V1Job>())
        {
            var failed = job.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
            if (failed is not null && failed.LastTransitionTime is { } when && nowUtc - when < TimeSpan.FromHours(6))
            {
                issues.Add(new ClusterIssue(IssueSeverity.Warning, "Job", job.Metadata?.NamespaceProperty, job.Metadata?.Name ?? "?",
                    failed.Reason ?? "Failed", failed.Message ?? "Job failed", when));
            }
        }

        var warnings = (events ?? Array.Empty<Corev1Event>())
            .Where(e => e.Type == "Warning")
            .Select(e => (Event: e, Last: e.LastTimestamp ?? e.EventTime ?? e.Metadata?.CreationTimestamp))
            .Where(x => x.Last is { } last && nowUtc - last < TimeSpan.FromMinutes(30))
            .GroupBy(x => (x.Event.Reason, x.Event.InvolvedObject?.Kind, x.Event.InvolvedObject?.NamespaceProperty, x.Event.InvolvedObject?.Name))
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Last).First();
                var obj = latest.Event.InvolvedObject;
                var location = obj?.NamespaceProperty is { } ns ? $"{obj.Kind} {ns}/{obj.Name}" : $"{obj?.Kind} {obj?.Name}";
                return new WarningEvent(latest.Event.Reason ?? "Warning", location, latest.Event.Message ?? string.Empty,
                    g.Sum(x => Math.Max(1, x.Event.Count ?? 1)), latest.Last);
            })
            .OrderByDescending(w => w.Last)
            .Take(20)
            .ToList();

        var ordered = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Kind == "Node" ? 0 : 1)
            .ThenBy(i => i.Location, StringComparer.Ordinal)
            .ToList();

        return new ClusterSnapshot(nodeRows, ordered, warnings, pods?.Count ?? 0);
    }

    internal static ClusterIssue? AnalyzePod(V1Pod pod, DateTime nowUtc, AnalyzerOptions options)
    {
        var ns = pod.Metadata?.NamespaceProperty;
        var name = pod.Metadata?.Name ?? "?";
        var status = pod.Status;
        if (status is null || status.Phase == "Succeeded")
        {
            return null;
        }

        if (pod.Metadata?.DeletionTimestamp is { } deleting && nowUtc - deleting > TimeSpan.FromMinutes(10))
        {
            return new ClusterIssue(IssueSeverity.Warning, "Pod", ns, name, "StuckTerminating", $"Terminating for {Age(nowUtc - deleting)}", deleting);
        }

        if (status.Phase == "Failed")
        {
            return new ClusterIssue(IssueSeverity.Critical, "Pod", ns, name, status.Reason ?? "Failed", status.Message ?? "Pod failed", pod.Metadata?.CreationTimestamp);
        }

        var containers = (status.InitContainerStatuses ?? Array.Empty<V1ContainerStatus>()).Concat(status.ContainerStatuses ?? Array.Empty<V1ContainerStatus>()).ToList();
        foreach (var c in containers)
        {
            var waiting = c.State?.Waiting;
            if (waiting?.Reason is { } reason && BadWaitingReasons.Contains(reason))
            {
                var detail = $"{c.Name}: {reason}" + (c.RestartCount > 0 ? $" · {c.RestartCount} restarts" : string.Empty);
                if (c.LastState?.Terminated is { } last)
                {
                    detail += $" · last exit {last.ExitCode}" + (last.Reason is { } why ? $" ({why})" : string.Empty);
                }

                return new ClusterIssue(IssueSeverity.Critical, "Pod", ns, name, reason, detail, pod.Metadata?.CreationTimestamp);
            }
        }

        foreach (var c in containers)
        {
            var last = c.LastState?.Terminated;
            var recent = last?.FinishedAt is { } finished && nowUtc - finished < options.Recent;
            if (recent && last!.Reason == "OOMKilled")
            {
                return new ClusterIssue(IssueSeverity.Warning, "Pod", ns, name, "OOMKilled", $"{c.Name} ran out of memory · {c.RestartCount} restarts", last.FinishedAt);
            }

            if (recent && c.RestartCount >= options.RestartThreshold)
            {
                return new ClusterIssue(IssueSeverity.Warning, "Pod", ns, name, "Restarting",
                    $"{c.Name}: {c.RestartCount} restarts · last exit {last!.ExitCode}{(last.Reason is { } why ? $" ({why})" : string.Empty)}", last.FinishedAt);
            }
        }

        if (status.Phase == "Pending" && pod.Metadata?.CreationTimestamp is { } created && nowUtc - created > options.Pending)
        {
            var scheduled = status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
            var detail = scheduled?.Message ?? $"Pending for {Age(nowUtc - created)}";
            return new ClusterIssue(IssueSeverity.Warning, "Pod", ns, name, scheduled?.Reason ?? "Pending", detail, created);
        }

        return null;
    }

    private static NodeSummary SummarizeNode(V1Node node, List<ClusterIssue> issues)
    {
        var name = node.Metadata?.Name ?? "?";
        var conditions = node.Status?.Conditions ?? Array.Empty<V1NodeCondition>();
        var readyCondition = conditions.FirstOrDefault(c => c.Type == "Ready");
        var ready = readyCondition?.Status == "True";
        var pressures = conditions
            .Where(c => c.Type is "MemoryPressure" or "DiskPressure" or "PIDPressure" or "NetworkUnavailable" && c.Status == "True")
            .Select(c => c.Type)
            .ToList();
        var cordoned = node.Spec?.Unschedulable == true;

        if (!ready)
        {
            issues.Add(new ClusterIssue(IssueSeverity.Critical, "Node", null, name, "NotReady",
                readyCondition?.Message ?? "Kubelet stopped posting status", readyCondition?.LastTransitionTime));
        }

        foreach (var pressure in pressures)
        {
            issues.Add(new ClusterIssue(IssueSeverity.Warning, "Node", null, name, pressure, $"Node reports {pressure}", null));
        }

        var roles = string.Join(",", (node.Metadata?.Labels ?? new Dictionary<string, string>())
            .Keys.Where(k => k.StartsWith("node-role.kubernetes.io/", StringComparison.Ordinal))
            .Select(k => k["node-role.kubernetes.io/".Length..])
            .Where(r => r.Length > 0)
            .Order(StringComparer.Ordinal));
        var flags = new List<string>(pressures);
        if (cordoned)
        {
            flags.Add("Cordoned");
        }

        return new NodeSummary(name, ready, roles.Length > 0 ? roles : "worker", string.Join(", ", flags), node.Status?.NodeInfo?.KubeletVersion ?? string.Empty);
    }

    private static void AddReplicaIssue(List<ClusterIssue> issues, string kind, V1ObjectMeta? meta, int desired, int ready)
    {
        if (desired <= 0 || ready >= desired)
        {
            return;
        }

        issues.Add(new ClusterIssue(ready == 0 ? IssueSeverity.Critical : IssueSeverity.Warning, kind, meta?.NamespaceProperty,
            meta?.Name ?? "?", ready == 0 ? "Unavailable" : "Degraded", $"{ready}/{desired} ready", null));
    }

    public static string Age(TimeSpan span) => span.TotalDays >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d")
        : span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)span.TotalMinutes)}m");
}
