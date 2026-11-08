using k8s.Models;
using SmartDock.Widgets.Cluster;

namespace SmartDock.Widgets.Tests;

public class ClusterAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static V1Node Node(string name, string ready, params (string Type, string Status)[] extra)
    {
        var conditions = new List<V1NodeCondition> { new() { Type = "Ready", Status = ready } };
        conditions.AddRange(extra.Select(e => new V1NodeCondition { Type = e.Type, Status = e.Status }));
        return new V1Node
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string> { ["node-role.kubernetes.io/control-plane"] = "" } },
            Status = new V1NodeStatus { Conditions = conditions },
        };
    }

    private static V1Pod Pod(string name, string phase, V1ContainerStatus? container = null, DateTime? created = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "apps", CreationTimestamp = created ?? Now.AddHours(-2) },
        Status = new V1PodStatus { Phase = phase, ContainerStatuses = container is null ? null : [container] },
    };

    [Fact]
    public void Healthy_cluster_has_no_issues()
    {
        var snapshot = ClusterAnalyzer.Analyze(
            [Node("cp-1", "True")],
            [Pod("web-1", "Running", new V1ContainerStatus { Name = "web", Ready = true, RestartCount = 0, State = new V1ContainerState { Running = new V1ContainerStateRunning() } })],
            [new V1Deployment { Metadata = new V1ObjectMeta { Name = "web", NamespaceProperty = "apps" }, Spec = new V1DeploymentSpec { Replicas = 2 }, Status = new V1DeploymentStatus { ReadyReplicas = 2 } }],
            null, null, null, null, null, Now);

        Assert.Equal(ClusterHealth.Healthy, snapshot.Health);
        Assert.Equal(1, snapshot.ReadyNodes);
        Assert.Equal("control-plane", snapshot.Nodes[0].Roles);
    }

    [Fact]
    public void Flags_not_ready_node_and_crashlooping_pod_as_critical()
    {
        var crashing = new V1ContainerStatus
        {
            Name = "api",
            RestartCount = 7,
            State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" } },
            LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1, Reason = "Error", FinishedAt = Now.AddMinutes(-1) } },
        };

        var snapshot = ClusterAnalyzer.Analyze([Node("w-1", "False"), Node("w-2", "True", ("DiskPressure", "True"))],
            [Pod("api-1", "Running", crashing)], null, null, null, null, null, null, Now);

        Assert.Equal(ClusterHealth.Critical, snapshot.Health);
        Assert.Contains(snapshot.Issues, i => i is { Kind: "Node", Name: "w-1", Reason: "NotReady", Severity: IssueSeverity.Critical });
        Assert.Contains(snapshot.Issues, i => i is { Kind: "Node", Name: "w-2", Reason: "DiskPressure", Severity: IssueSeverity.Warning });
        var pod = Assert.Single(snapshot.Issues, i => i.Kind == "Pod");
        Assert.Equal("CrashLoopBackOff", pod.Reason);
        Assert.Contains("7 restarts", pod.Detail);
        Assert.Contains("last exit 1", pod.Detail);
    }

    [Fact]
    public void Pending_pod_only_flagged_after_threshold()
    {
        var fresh = Pod("new", "Pending", created: Now.AddMinutes(-2));
        var stuck = Pod("stuck", "Pending", created: Now.AddMinutes(-20));
        stuck.Status.Conditions = [new V1PodCondition { Type = "PodScheduled", Status = "False", Reason = "Unschedulable", Message = "0/3 nodes are available" }];

        var snapshot = ClusterAnalyzer.Analyze(null, [fresh, stuck], null, null, null, null, null, null, Now);

        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(("stuck", "Unschedulable"), (issue.Name, issue.Reason));
        Assert.Equal(ClusterHealth.Warning, snapshot.Health);
    }

    [Fact]
    public void Degraded_and_unavailable_workloads()
    {
        V1Deployment Deploy(string name, int desired, int ready) => new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "apps" },
            Spec = new V1DeploymentSpec { Replicas = desired },
            Status = new V1DeploymentStatus { ReadyReplicas = ready },
        };

        var snapshot = ClusterAnalyzer.Analyze(null, null, [Deploy("half", 4, 2), Deploy("none", 2, 0), Deploy("scaled-to-zero", 0, 0)],
            null, null, null, null, null, Now);

        Assert.Equal(2, snapshot.Issues.Count);
        Assert.Contains(snapshot.Issues, i => i is { Name: "half", Reason: "Degraded", Detail: "2/4 ready", Severity: IssueSeverity.Warning });
        Assert.Contains(snapshot.Issues, i => i is { Name: "none", Reason: "Unavailable", Severity: IssueSeverity.Critical });
    }

    [Fact]
    public void Oom_killed_recently_is_a_warning_but_old_one_is_ignored()
    {
        V1ContainerStatus Oom(DateTime finished) => new()
        {
            Name = "worker",
            RestartCount = 1,
            State = new V1ContainerState { Running = new V1ContainerStateRunning() },
            LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { Reason = "OOMKilled", ExitCode = 137, FinishedAt = finished } },
        };

        var snapshot = ClusterAnalyzer.Analyze(null, [Pod("recent", "Running", Oom(Now.AddMinutes(-10))), Pod("old", "Running", Oom(Now.AddDays(-2)))],
            null, null, null, null, null, null, Now);

        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(("recent", "OOMKilled"), (issue.Name, issue.Reason));
    }

    [Fact]
    public void Warning_events_are_grouped_and_recent_only()
    {
        Corev1Event Ev(string reason, DateTime last, int count) => new()
        {
            Type = "Warning",
            Reason = reason,
            Message = "back-off",
            Count = count,
            LastTimestamp = last,
            InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "api-1", NamespaceProperty = "apps" },
            Metadata = new V1ObjectMeta { Name = Guid.NewGuid().ToString() },
        };

        var snapshot = ClusterAnalyzer.Analyze(null, null, null, null, null, null, null,
            [Ev("BackOff", Now.AddMinutes(-1), 3), Ev("BackOff", Now.AddMinutes(-5), 2), Ev("FailedMount", Now.AddHours(-3), 1)], Now);

        var warning = Assert.Single(snapshot.Events);
        Assert.Equal(("BackOff", 5, "Pod apps/api-1"), (warning.Reason, warning.Count, warning.Object));
    }
}
