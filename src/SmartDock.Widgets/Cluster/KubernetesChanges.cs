using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;

namespace SmartDock.Widgets.Cluster;

/// <summary>One entry of <c>kubectl rollout history</c>.</summary>
public sealed record RolloutRevision(long Revision, string Images, string? ChangeCause, DateTime? Created, bool IsCurrent, V1PodTemplateSpec Template)
{
    public string Age => Created is { } c ? ClusterAnalyzer.Age(DateTime.UtcNow - c) : string.Empty;

    public string Display => $"Revision {Revision}{(IsCurrent ? " (current)" : string.Empty)} · {Images}"
        + (Age.Length > 0 ? $" · {Age} ago" : string.Empty)
        + (ChangeCause is { Length: > 0 } cause ? $" · {cause}" : string.Empty);
}

/// <summary>
/// Pure builders for deployment changes (patch bodies, rollout history, YAML preparation and checks).
/// Public for tests; <see cref="KubernetesExplorer"/> sends them.
/// </summary>
public static partial class DeploymentChanges
{
    public const string RestartedAtAnnotation = "kubectl.kubernetes.io/restartedAt";
    public const string RevisionAnnotation = "deployment.kubernetes.io/revision";
    public const string ChangeCauseAnnotation = "kubernetes.io/change-cause";

    /// <summary>{"spec":{"replicas":n}} for the scale subresource (merge patch).</summary>
    public static string ScalePatch(int replicas) =>
        new JsonObject { ["spec"] = new JsonObject { ["replicas"] = replicas } }.ToJsonString();

    /// <summary>Strategic merge patch that changes one container's image (containers merge by name), like kubectl set image.</summary>
    public static string ImagePatch(string container, string image) =>
        new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["spec"] = new JsonObject
                    {
                        ["containers"] = new JsonArray(new JsonObject { ["name"] = container, ["image"] = image }),
                    },
                },
            },
        }.ToJsonString();

    /// <summary>What <c>kubectl rollout restart</c> does: bump an annotation on the pod template.</summary>
    public static string RestartPatch(DateTimeOffset now) =>
        new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        ["annotations"] = new JsonObject
                        {
                            [RestartedAtAnnotation] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        },
                    },
                },
            },
        }.ToJsonString();

    /// <summary>
    /// JSON patch that puts an earlier revision's pod template back, as <c>kubectl rollout undo</c> does
    /// (without the ReplicaSet's pod-template-hash label).
    /// </summary>
    public static string RollbackPatch(V1PodTemplateSpec template)
    {
        var copy = KubernetesJson.Deserialize<V1PodTemplateSpec>(KubernetesJson.Serialize(template));
        copy.Metadata?.Labels?.Remove("pod-template-hash");
        var value = JsonNode.Parse(KubernetesJson.Serialize(copy));
        return new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = "/spec/template", ["value"] = value }).ToJsonString();
    }

    /// <summary>Rollout history from the deployment's ReplicaSets (owned by <paramref name="deploymentUid"/>), newest first.</summary>
    public static IReadOnlyList<RolloutRevision> History(IEnumerable<V1ReplicaSet> replicaSets, string deploymentUid, string? currentRevision)
    {
        var list = new List<RolloutRevision>();
        foreach (var rs in replicaSets)
        {
            if (rs.Metadata?.OwnerReferences?.Any(o => o.Uid == deploymentUid) != true || rs.Spec?.Template is not { } template)
            {
                continue;
            }

            var annotations = rs.Metadata.Annotations ?? new Dictionary<string, string>();
            if (!annotations.TryGetValue(RevisionAnnotation, out var text) || !long.TryParse(text, out var revision))
            {
                continue;
            }

            var images = string.Join(", ", template.Spec?.Containers?.Select(c => DeploymentRow.ShortImage(c.Image)) ?? []);
            list.Add(new RolloutRevision(revision, images, annotations.TryGetValue(ChangeCauseAnnotation, out var cause) ? cause : null,
                rs.Metadata.CreationTimestamp?.ToUniversalTime(), text == currentRevision, template));
        }

        return list.OrderByDescending(r => r.Revision).ToList();
    }

    /// <summary>Image reference sanity check (no spaces/quotes, not empty); null when OK.</summary>
    public static string? ValidateImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return "Enter an image, e.g. registry.example.com/team/api:1.4.3.";
        }

        return ImageRegex().IsMatch(image.Trim()) ? null : "That doesn't look like an image reference (no spaces or quotes allowed).";
    }

    /// <summary>The deployment as YAML for editing, without managedFields and status (server-maintained).</summary>
    public static string ToEditableYaml(V1Deployment deployment)
    {
        var copy = KubernetesJson.Deserialize<V1Deployment>(KubernetesJson.Serialize(deployment));
        copy.Status = null;
        if (copy.Metadata is { } meta)
        {
            meta.ManagedFields = null;
        }

        copy.ApiVersion ??= "apps/v1";
        copy.Kind ??= "Deployment";
        return KubernetesYaml.Serialize(copy);
    }

    /// <summary>
    /// Parses edited YAML and checks it's still the same deployment (kind, name, namespace). Returns the object or
    /// throws <see cref="FormatException"/> with a readable message. Public for tests.
    /// </summary>
    public static V1Deployment ParseEdited(string yaml, string expectedNamespace, string expectedName)
    {
        V1Deployment edited;
        try
        {
            edited = KubernetesYaml.Deserialize<V1Deployment>(yaml);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("The YAML can't be read: " + ex.Message.Split('\n')[0], ex);
        }

        if (edited is null)
        {
            throw new FormatException("The YAML is empty.");
        }

        if (!string.Equals(edited.Kind, "Deployment", StringComparison.Ordinal))
        {
            throw new FormatException($"kind must stay Deployment (found \"{edited.Kind}\").");
        }

        if (edited.Metadata?.Name != expectedName)
        {
            throw new FormatException($"metadata.name must stay \"{expectedName}\" – renaming would need a new deployment.");
        }

        if ((edited.Metadata.NamespaceProperty ?? expectedNamespace) != expectedNamespace)
        {
            throw new FormatException($"metadata.namespace must stay \"{expectedNamespace}\".");
        }

        edited.Metadata.NamespaceProperty = expectedNamespace;
        edited.Status = null;
        return edited;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/:@]*$")]
    private static partial Regex ImageRegex();
}

/// <summary>Deployment changes. Only called when the widget's "Allow changing deployments" option is on.</summary>
public sealed partial class KubernetesExplorer
{
    public async Task ScaleAsync(string ns, string name, int replicas, CancellationToken ct) =>
        await _client.AppsV1.PatchNamespacedDeploymentScaleAsync(
            new V1Patch(DeploymentChanges.ScalePatch(replicas), V1Patch.PatchType.MergePatch), name, ns, cancellationToken: ct).ConfigureAwait(false);

    public async Task SetImageAsync(string ns, string name, string container, string image, CancellationToken ct) =>
        await _client.AppsV1.PatchNamespacedDeploymentAsync(
            new V1Patch(DeploymentChanges.ImagePatch(container, image.Trim()), V1Patch.PatchType.StrategicMergePatch), name, ns, cancellationToken: ct).ConfigureAwait(false);

    public async Task RestartAsync(string ns, string name, CancellationToken ct) =>
        await _client.AppsV1.PatchNamespacedDeploymentAsync(
            new V1Patch(DeploymentChanges.RestartPatch(DateTimeOffset.Now), V1Patch.PatchType.StrategicMergePatch), name, ns, cancellationToken: ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RolloutRevision>> RolloutHistoryAsync(string ns, string name, CancellationToken ct)
    {
        var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
        var selector = DeploymentRow.LabelSelector(deployment.Spec?.Selector);
        var sets = await _client.AppsV1.ListNamespacedReplicaSetAsync(ns, labelSelector: selector, cancellationToken: ct).ConfigureAwait(false);
        var current = deployment.Metadata?.Annotations is { } a && a.TryGetValue(DeploymentChanges.RevisionAnnotation, out var rev) ? rev : null;
        return DeploymentChanges.History(sets.Items, deployment.Metadata?.Uid ?? string.Empty, current);
    }

    public async Task RollbackAsync(string ns, string name, RolloutRevision revision, CancellationToken ct) =>
        await _client.AppsV1.PatchNamespacedDeploymentAsync(
            new V1Patch(DeploymentChanges.RollbackPatch(revision.Template), V1Patch.PatchType.JsonPatch), name, ns, cancellationToken: ct).ConfigureAwait(false);

    public async Task<string> ReadYamlAsync(string ns, string name, CancellationToken ct) =>
        DeploymentChanges.ToEditableYaml(await _client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>
    /// Replaces the deployment with the edited YAML. With <paramref name="dryRun"/> the server only validates it
    /// (nothing is changed). The resourceVersion in the YAML makes the server refuse the update (409) when someone
    /// else changed the deployment since it was loaded.
    /// </summary>
    public async Task ApplyYamlAsync(string ns, string name, string yaml, bool dryRun, CancellationToken ct)
    {
        var edited = DeploymentChanges.ParseEdited(yaml, ns, name);
        await _client.AppsV1.ReplaceNamespacedDeploymentAsync(edited, name, ns, dryRun: dryRun ? "All" : null, cancellationToken: ct).ConfigureAwait(false);
    }
}
