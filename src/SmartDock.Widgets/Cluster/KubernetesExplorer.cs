using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SmartDock.Widgets.Cluster;

/// <summary>Published by a Kubernetes widget to open the pods / deployments / logs window.</summary>
/// <param name="Title">Widget title (cluster name) for the window caption.</param>
/// <param name="Explorer">A read-only explorer with its own API client; the window disposes it when closed.</param>
/// <param name="Namespaces">Namespaces configured on the widget (empty = all).</param>
/// <param name="FocusNamespace">Optional namespace to select first (e.g. from a problem row).</param>
/// <param name="FocusPod">Optional pod whose logs to show right away.</param>
/// <param name="Previous">Show the previous (crashed) container's logs for <paramref name="FocusPod"/>.</param>
public sealed record OpenKubernetesExplorerRequested(
    string Title,
    KubernetesExplorer Explorer,
    IReadOnlyList<string> Namespaces,
    string? FocusNamespace = null,
    string? FocusPod = null,
    bool Previous = false,
    ExecOptions? Exec = null,
    bool AllowChanges = false);

/// <summary>Shell-in-pod settings from the widget. <paramref name="Allowed"/> is off unless the user enabled it.</summary>
public sealed record ExecOptions(bool Allowed, string? KubectlPath, ExecShell Shell);

/// <summary>One pod row ("kubectl get pods" columns).</summary>
public sealed record PodRow(
    string Namespace,
    string Name,
    string Ready,
    string Status,
    int Restarts,
    string Age,
    string Node,
    string Ip,
    IReadOnlyList<string> Containers,
    bool Healthy,
    IReadOnlyDictionary<string, string> Labels)
{
    public string Key => Namespace + "/" + Name;

    /// <summary>Creation time (UTC), used to sort by age.</summary>
    public DateTime? Created { get; init; }

    /// <summary>Maps a pod the way <c>kubectl get pods</c> shows it; public for tests.</summary>
    public static PodRow From(V1Pod pod, DateTime nowUtc)
    {
        var spec = pod.Spec;
        var statuses = pod.Status?.ContainerStatuses ?? [];
        var containers = spec?.Containers?.Select(c => c.Name).ToList() ?? [];
        var ready = statuses.Count(s => s.Ready);
        var restarts = statuses.Sum(s => s.RestartCount);
        var status = StatusOf(pod);
        var healthy = status is "Running" or "Completed" or "Succeeded" && (status != "Running" || ready == containers.Count);
        return new PodRow(
            pod.Metadata?.NamespaceProperty ?? string.Empty,
            pod.Metadata?.Name ?? "?",
            $"{ready}/{containers.Count}",
            status,
            restarts,
            pod.Metadata?.CreationTimestamp is { } created ? ClusterAnalyzer.Age(nowUtc - created.ToUniversalTime()) : string.Empty,
            spec?.NodeName ?? string.Empty,
            pod.Status?.PodIP ?? string.Empty,
            containers,
            healthy,
            pod.Metadata?.Labels is { } labels ? new Dictionary<string, string>(labels) : new Dictionary<string, string>())
        {
            Created = pod.Metadata?.CreationTimestamp?.ToUniversalTime(),
        };
    }

    /// <summary>The STATUS column: a waiting/terminated reason beats the phase (CrashLoopBackOff, ImagePullBackOff, OOMKilled…).</summary>
    public static string StatusOf(V1Pod pod)
    {
        if (pod.Metadata?.DeletionTimestamp is not null)
        {
            return "Terminating";
        }

        foreach (var s in pod.Status?.InitContainerStatuses ?? [])
        {
            if (s.State?.Waiting?.Reason is { Length: > 0 } waiting && waiting != "PodInitializing")
            {
                return "Init:" + waiting;
            }

            if (s.State?.Terminated is { ExitCode: not 0 } t)
            {
                return "Init:" + (t.Reason ?? "Error");
            }
        }

        foreach (var s in pod.Status?.ContainerStatuses ?? [])
        {
            if (s.State?.Waiting?.Reason is { Length: > 0 } waiting)
            {
                return waiting;
            }

            if (s.State?.Terminated?.Reason is { Length: > 0 } terminated && pod.Status?.Phase != "Succeeded")
            {
                return terminated;
            }
        }

        return pod.Status?.Phase switch
        {
            "Succeeded" => "Completed",
            { Length: > 0 } phase => phase,
            _ => "Unknown",
        };
    }
}

/// <summary>A container of a pod template and its image.</summary>
public sealed record ContainerImage(string Name, string Image);

/// <summary>One deployment row ("kubectl get deployments" columns).</summary>
public sealed record DeploymentRow(
    string Namespace,
    string Name,
    string Ready,
    int UpToDate,
    int Available,
    string Age,
    string Images,
    string? Selector,
    bool Healthy)
{
    public string Key => Namespace + "/" + Name;

    /// <summary>Creation time (UTC), used to sort by age.</summary>
    public DateTime? Created { get; init; }

    /// <summary>Desired replicas (spec.replicas).</summary>
    public int Replicas { get; init; }

    /// <summary>Containers of the pod template with their full image references.</summary>
    public IReadOnlyList<ContainerImage> Containers { get; init; } = [];

    /// <summary>Current rollout revision (deployment.kubernetes.io/revision).</summary>
    public string? Revision { get; init; }

    public static DeploymentRow From(V1Deployment d, DateTime nowUtc)
    {
        var desired = d.Spec?.Replicas ?? 1;
        var ready = d.Status?.ReadyReplicas ?? 0;
        var images = d.Spec?.Template?.Spec?.Containers?.Select(c => ShortImage(c.Image)).Distinct() ?? [];
        return new DeploymentRow(
            d.Metadata?.NamespaceProperty ?? string.Empty,
            d.Metadata?.Name ?? "?",
            $"{ready}/{desired}",
            d.Status?.UpdatedReplicas ?? 0,
            d.Status?.AvailableReplicas ?? 0,
            d.Metadata?.CreationTimestamp is { } created ? ClusterAnalyzer.Age(nowUtc - created.ToUniversalTime()) : string.Empty,
            string.Join(", ", images),
            LabelSelector(d.Spec?.Selector),
            ready >= desired)
        {
            Created = d.Metadata?.CreationTimestamp?.ToUniversalTime(),
            Replicas = desired,
            Containers = d.Spec?.Template?.Spec?.Containers?.Select(c => new ContainerImage(c.Name, c.Image ?? string.Empty)).ToList() ?? [],
            Revision = d.Metadata?.Annotations is { } a && a.TryGetValue("deployment.kubernetes.io/revision", out var rev) ? rev : null,
        };
    }

    /// <summary>"app=web,tier=api" from matchLabels and matchExpressions; null when the selector is empty. Public for tests.</summary>
    public static string? LabelSelector(V1LabelSelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var (key, value) in selector.MatchLabels ?? new Dictionary<string, string>())
        {
            parts.Add($"{key}={value}");
        }

        foreach (var e in selector.MatchExpressions ?? [])
        {
            var values = string.Join(",", e.Values ?? []);
            parts.Add(e.OperatorProperty switch
            {
                "In" => $"{e.Key} in ({values})",
                "NotIn" => $"{e.Key} notin ({values})",
                "Exists" => e.Key,
                "DoesNotExist" => "!" + e.Key,
                _ => e.Key,
            });
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    /// <summary>"registry.example.com/team/api:1.4.2" → "api:1.4.2" (digests shortened).</summary>
    public static string ShortImage(string? image)
    {
        if (string.IsNullOrEmpty(image))
        {
            return "?";
        }

        var name = image[(image.LastIndexOf('/') + 1)..];
        var at = name.IndexOf("@sha256:", StringComparison.Ordinal);
        return at > 0 && name.Length > at + 15 ? name[..(at + 15)] + "…" : name;
    }
}

/// <summary>
/// Column sorting for the explorer lists, with real values behind the text: age by creation time, IP addresses
/// numerically, READY as numbers, restarts as integers. Ties fall back to namespace/name. Public for tests.
/// </summary>
public static class ExplorerSort
{
    public static IReadOnlyList<PodRow> Pods(IEnumerable<PodRow> rows, string column, bool ascending)
    {
        Func<PodRow, IComparable> key = column switch
        {
            "Ready" => p => Fraction(p.Ready),
            "Status" => p => p.Status,
            "Restarts" => p => p.Restarts,
            "Age" => p => AgeKey(p.Created),
            "Node" => p => p.Node,
            "IP" => p => IpKey(p.Ip),
            "Namespace" => p => p.Namespace,
            _ => p => p.Name,
        };
        var ordered = ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
        return ordered.ThenBy(p => p.Namespace, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<DeploymentRow> Deployments(IEnumerable<DeploymentRow> rows, string column, bool ascending)
    {
        Func<DeploymentRow, IComparable> key = column switch
        {
            "Ready" => d => Fraction(d.Ready),
            "Up-to-date" => d => d.UpToDate,
            "Available" => d => d.Available,
            "Age" => d => AgeKey(d.Created),
            "Images" => d => d.Images,
            "Namespace" => d => d.Namespace,
            _ => d => d.Name,
        };
        var ordered = ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
        return ordered.ThenBy(d => d.Namespace, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Smaller key = younger, so ascending age lists the newest first (like reading the AGE column).</summary>
    private static long AgeKey(DateTime? created) => created is { } c ? -c.Ticks : long.MinValue;

    /// <summary>"2/3" → (2, 3); anything else → (0, 0).</summary>
    private static (int, int) Fraction(string text)
    {
        var slash = text.IndexOf('/');
        return slash > 0 && int.TryParse(text.AsSpan(0, slash), out var a) && int.TryParse(text.AsSpan(slash + 1), out var b) ? (a, b) : (0, 0);
    }

    /// <summary>Sortable form of an IP: "10.0.0.9" before "10.0.0.10"; IPv6 after IPv4; empty last.</summary>
    public static string IpKey(string ip)
    {
        if (System.Net.IPAddress.TryParse(ip, out var address))
        {
            var bytes = address.MapToIPv6().GetAddressBytes();
            return Convert.ToHexString(bytes);
        }

        return "ZZ" + ip; // after every real address (hex keys only use 0-9 and A-F)
    }
}

/// <summary>Severity of a log line, guessed from common level words (used only for colouring).</summary>
public enum LogSeverity
{
    Normal,
    Warning,
    Error,
}

/// <summary>One log line in the window.</summary>
public sealed partial record LogLine(string Text, LogSeverity Level, string? Source = null)
{
    public static LogLine Parse(string text, string? source = null) => new(text, LevelOf(text), source);

    public string Display => Source is null ? Text : $"[{Source}] {Text}";

    public static LogSeverity LevelOf(string text) =>
        ErrorRegex().IsMatch(text) ? LogSeverity.Error : WarnRegex().IsMatch(text) ? LogSeverity.Warning : LogSeverity.Normal;

    [GeneratedRegex(@"\b(ERROR|ERR|FATAL|CRITICAL|PANIC|SEVERE|Traceback)\b|Exception\b|""level""\s*:\s*""(error|fatal)""|level=(error|fatal)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"\b(WARN|WARNING)\b|""level""\s*:\s*""warn(ing)?""|level=warn(ing)?", RegexOptions.IgnoreCase)]
    private static partial Regex WarnRegex();
}

/// <summary>
/// Read-only browsing of one cluster for the explorer window: namespaces, pods, deployments and log streams
/// (the equivalent of <c>kubectl get</c> and <c>kubectl logs -f</c>). Never changes anything in the cluster.
/// Owns its API client; dispose it when the window closes.
/// </summary>
public sealed partial class KubernetesExplorer : IDisposable
{
    private const int MaxPods = 3000;
    private readonly IKubernetes _client;

    public KubernetesExplorer(string kubeconfigPath, string context)
    {
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath: kubeconfigPath, currentContext: context);
        _client = new Kubernetes(config);
        Context = context;
        KubeconfigPath = kubeconfigPath;
    }

    public string Context { get; }

    /// <summary>The kubeconfig file this explorer reads (passed to kubectl for "Open shell").</summary>
    public string KubeconfigPath { get; }

    public string Server => _client.BaseUri?.Host ?? string.Empty;

    /// <summary>Namespaces visible to the account; null when listing namespaces isn't allowed.</summary>
    public async Task<IReadOnlyList<string>?> ListNamespacesAsync(CancellationToken ct)
    {
        try
        {
            var list = await _client.CoreV1.ListNamespaceAsync(cancellationToken: ct).ConfigureAwait(false);
            return list.Items.Select(n => n.Metadata.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Pods in a namespace (empty = all namespaces), optionally limited by a label selector.</summary>
    public async Task<IReadOnlyList<PodRow>> ListPodsAsync(string? ns, string? labelSelector, CancellationToken ct)
    {
        var pods = string.IsNullOrEmpty(ns)
            ? await _client.CoreV1.ListPodForAllNamespacesAsync(labelSelector: labelSelector, limit: MaxPods, cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: labelSelector, limit: MaxPods, cancellationToken: ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return pods.Items.Select(p => PodRow.From(p, now))
            .OrderBy(p => p.Namespace, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DeploymentRow>> ListDeploymentsAsync(string? ns, CancellationToken ct)
    {
        var list = string.IsNullOrEmpty(ns)
            ? await _client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return list.Items.Select(d => DeploymentRow.From(d, now))
            .OrderBy(d => d.Namespace, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Streams a container's log (like <c>kubectl logs -f --tail N</c>) and calls <paramref name="onLine"/> for each
    /// line, on a background thread, until cancelled or the stream ends (pod deleted, container restarted, or
    /// <paramref name="follow"/> is off).
    /// </summary>
    public async Task StreamLogsAsync(string ns, string pod, string? container, int tailLines, bool follow, bool previous, bool timestamps,
        Action<string> onLine, CancellationToken ct)
    {
        using var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(
            name: pod,
            namespaceParameter: ns,
            container: container,
            follow: follow && !previous,
            previous: previous,
            tailLines: tailLines,
            timestamps: timestamps,
            cancellationToken: ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }

    /// <summary>Short, human error text for the window ("No permission to read logs (403)").</summary>
    public static string Describe(Exception ex) => ex switch
    {
        HttpOperationException { Response.StatusCode: HttpStatusCode.Forbidden } => "No permission for this (403) — your account's RBAC doesn't allow it.",
        HttpOperationException { Response.StatusCode: HttpStatusCode.NotFound } => "Not found (404) — it may have been deleted.",
        HttpOperationException { Response.StatusCode: HttpStatusCode.BadRequest } h => "Rejected: " + FirstLine(h.Response.Content),
        HttpOperationException { Response.StatusCode: HttpStatusCode.Unauthorized } => "Not authorised (401) — log in again / refresh the kubeconfig token.",
        HttpRequestException h => "Cluster unreachable: " + h.Message,
        _ => ex.Message,
    };

    public void Dispose() => _client.Dispose();

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "bad request";
        }

        // API errors are a Status object: show its message when we can find it.
        var match = Regex.Match(text, "\"message\"\\s*:\\s*\"(?<m>[^\"]+)\"");
        var message = match.Success ? match.Groups["m"].Value : text;
        var newline = message.IndexOf('\n');
        return newline > 0 ? message[..newline] : message;
    }
}
