using System.Text.Json;
using k8s.Models;
using SmartDock.Widgets.Cluster;

namespace SmartDock.Widgets.Tests;

public class DeploymentChangesTests
{
    [Fact]
    public void Builds_scale_image_and_restart_patches()
    {
        Assert.Equal("""{"spec":{"replicas":3}}""", DeploymentChanges.ScalePatch(3));
        Assert.Equal("""{"spec":{"template":{"spec":{"containers":[{"name":"app","image":"reg/x/api:1.5"}]}}}}""",
            DeploymentChanges.ImagePatch("app", "reg/x/api:1.5"));

        using var restart = JsonDocument.Parse(DeploymentChanges.RestartPatch(new DateTimeOffset(2026, 11, 5, 18, 30, 0, TimeSpan.FromHours(3))));
        var stamp = restart.RootElement.GetProperty("spec").GetProperty("template").GetProperty("metadata").GetProperty("annotations")
            .GetProperty(DeploymentChanges.RestartedAtAnnotation).GetString();
        Assert.Equal("2026-11-05T15:30:00Z", stamp);
    }

    [Fact]
    public void Rollback_patch_replaces_the_template_without_the_hash_label()
    {
        var template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = "web", ["pod-template-hash"] = "abc123" } },
            Spec = new V1PodSpec { Containers = [new V1Container { Name = "app", Image = "web:1.4" }] },
        };

        using var patch = JsonDocument.Parse(DeploymentChanges.RollbackPatch(template));
        var op = patch.RootElement[0];
        Assert.Equal("replace", op.GetProperty("op").GetString());
        Assert.Equal("/spec/template", op.GetProperty("path").GetString());
        var labels = op.GetProperty("value").GetProperty("metadata").GetProperty("labels");
        Assert.True(labels.TryGetProperty("app", out _));
        Assert.False(labels.TryGetProperty("pod-template-hash", out _));
        Assert.True(template.Metadata.Labels.ContainsKey("pod-template-hash")); // original untouched
    }

    private static V1ReplicaSet Rs(string owner, string revision, string image, string? cause = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "web-" + revision,
            OwnerReferences = [new V1OwnerReference { Uid = owner, Name = "web", Kind = "Deployment", ApiVersion = "apps/v1" }],
            Annotations = cause is null
                ? new Dictionary<string, string> { [DeploymentChanges.RevisionAnnotation] = revision }
                : new Dictionary<string, string> { [DeploymentChanges.RevisionAnnotation] = revision, [DeploymentChanges.ChangeCauseAnnotation] = cause },
        },
        Spec = new V1ReplicaSetSpec
        {
            Selector = new V1LabelSelector(),
            Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Containers = [new V1Container { Name = "app", Image = image }] } },
        },
    };

    [Fact]
    public void Builds_rollout_history_newest_first_for_this_deployment_only()
    {
        var history = DeploymentChanges.History(
            [Rs("u1", "2", "r/web:1.4"), Rs("u1", "10", "r/web:1.6", "hotfix"), Rs("other", "3", "r/x:1"), Rs("u1", "7", "r/web:1.5")],
            "u1", "10");

        Assert.Equal(new long[] { 10, 7, 2 }, history.Select(h => h.Revision));
        Assert.True(history[0].IsCurrent);
        Assert.Equal("web:1.6", history[0].Images);
        Assert.Contains("hotfix", history[0].Display);
    }

    [Theory]
    [InlineData("registry.local/team/api:1.4.3", true)]
    [InlineData("api@sha256:abcdef", true)]
    [InlineData("", false)]
    [InlineData("api 1.4", false)]
    [InlineData("api\";rm", false)]
    public void Validates_images(string image, bool ok) => Assert.Equal(ok, DeploymentChanges.ValidateImage(image) is null);

    [Fact]
    public void Edited_yaml_must_stay_the_same_deployment()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = "web",
                NamespaceProperty = "shop",
                ResourceVersion = "42",
                ManagedFields = [new V1ManagedFieldsEntry { Manager = "kubectl" }],
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 2,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "web" } },
                Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Containers = [new V1Container { Name = "app", Image = "web:1" }] } },
            },
            Status = new V1DeploymentStatus { ReadyReplicas = 2 },
        };

        var yaml = DeploymentChanges.ToEditableYaml(deployment);
        Assert.DoesNotContain("managedFields", yaml);
        Assert.DoesNotContain("readyReplicas", yaml);
        Assert.Contains("resourceVersion", yaml);

        var parsed = DeploymentChanges.ParseEdited(yaml.Replace("replicas: 2", "replicas: 5"), "shop", "web");
        Assert.Equal(5, parsed.Spec.Replicas);
        Assert.Equal("42", parsed.Metadata.ResourceVersion);

        Assert.Throws<FormatException>(() => DeploymentChanges.ParseEdited(yaml.Replace("name: web", "name: web2"), "shop", "web"));
        Assert.Throws<FormatException>(() => DeploymentChanges.ParseEdited(yaml.Replace("namespace: shop", "namespace: prod"), "shop", "web"));
        Assert.Throws<FormatException>(() => DeploymentChanges.ParseEdited("spec: [unclosed", "shop", "web"));
    }
}
