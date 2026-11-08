using k8s.Models;
using SmartDock.Widgets.Cluster;

namespace SmartDock.Widgets.Tests;

public class KubernetesExplorerTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static V1Pod Pod(string phase, params V1ContainerStatus[] statuses) => new()
    {
        Metadata = new V1ObjectMeta { Name = "web-7d9f8c6b5-x2k4p", NamespaceProperty = "shop", CreationTimestamp = Now.AddHours(-3) },
        Spec = new V1PodSpec { NodeName = "node-1", Containers = statuses.Select(s => new V1Container { Name = s.Name }).ToList() },
        Status = new V1PodStatus { Phase = phase, PodIP = "10.0.0.7", ContainerStatuses = statuses.ToList() },
    };

    private static V1ContainerStatus Running(string name, bool ready = true, int restarts = 0) => new()
    {
        Name = name, Ready = ready, RestartCount = restarts, Image = "x", ImageID = "x",
        State = new V1ContainerState { Running = new V1ContainerStateRunning() },
    };

    private static V1ContainerStatus Waiting(string name, string reason, int restarts = 0) => new()
    {
        Name = name, Ready = false, RestartCount = restarts, Image = "x", ImageID = "x",
        State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = reason } },
    };

    [Fact]
    public void Maps_a_healthy_pod_like_kubectl()
    {
        var row = PodRow.From(Pod("Running", Running("app"), Running("sidecar", restarts: 2)), Now);

        Assert.Equal(("shop", "web-7d9f8c6b5-x2k4p", "2/2", "Running", 2), (row.Namespace, row.Name, row.Ready, row.Status, row.Restarts));
        Assert.Equal("3h", row.Age);
        Assert.Equal(new[] { "app", "sidecar" }, row.Containers);
        Assert.True(row.Healthy);
    }

    [Fact]
    public void Waiting_reason_beats_phase_and_marks_unhealthy()
    {
        var row = PodRow.From(Pod("Running", Waiting("app", "CrashLoopBackOff", restarts: 12)), Now);

        Assert.Equal("CrashLoopBackOff", row.Status);
        Assert.Equal("0/1", row.Ready);
        Assert.False(row.Healthy);
    }

    [Fact]
    public void Running_but_not_ready_is_unhealthy_and_completed_is_fine()
    {
        Assert.False(PodRow.From(Pod("Running", Running("app", ready: false)), Now).Healthy);

        var done = Pod("Succeeded", new V1ContainerStatus
        {
            Name = "job", Ready = false, Image = "x", ImageID = "x",
            State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0, Reason = "Completed" } },
        });
        var row = PodRow.From(done, Now);
        Assert.Equal("Completed", row.Status);
        Assert.True(row.Healthy);
    }

    [Fact]
    public void Builds_label_selectors()
    {
        var selector = new V1LabelSelector
        {
            MatchLabels = new Dictionary<string, string> { ["app"] = "web" },
            MatchExpressions = [new V1LabelSelectorRequirement { Key = "tier", OperatorProperty = "In", Values = ["api", "edge"] }],
        };

        Assert.Equal("app=web,tier in (api,edge)", DeploymentRow.LabelSelector(selector));
        Assert.Null(DeploymentRow.LabelSelector(new V1LabelSelector()));
        Assert.Null(DeploymentRow.LabelSelector(null));
    }

    [Fact]
    public void Maps_deployments()
    {
        var d = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "web", NamespaceProperty = "shop", CreationTimestamp = Now.AddDays(-2) },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "web" } },
                Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Containers = [new V1Container { Name = "app", Image = "registry.local/team/web:1.4.2" }] } },
            },
            Status = new V1DeploymentStatus { ReadyReplicas = 2, UpdatedReplicas = 3, AvailableReplicas = 2 },
        };

        var row = DeploymentRow.From(d, Now);
        Assert.Equal(("2/3", 3, 2, "2d", "web:1.4.2", "app=web"), (row.Ready, row.UpToDate, row.Available, row.Age, row.Images, row.Selector));
        Assert.False(row.Healthy);
    }

    [Theory]
    [InlineData("2026-11-05 ERROR failed to connect", LogSeverity.Error)]
    [InlineData("{\"level\":\"error\",\"msg\":\"boom\"}", LogSeverity.Error)]
    [InlineData("java.lang.IllegalStateException: x", LogSeverity.Error)]
    [InlineData("Exception in thread main", LogSeverity.Error)]
    [InlineData("time=1 level=warn msg=slow", LogSeverity.Warning)]
    [InlineData("WARNING: disk 85%", LogSeverity.Warning)]
    [InlineData("GET /health 200", LogSeverity.Normal)]
    [InlineData("errors=0 warnings=0", LogSeverity.Normal)]
    public void Guesses_log_line_severity(string line, LogSeverity expected) =>
        Assert.Equal(expected, LogLine.LevelOf(line));

    [Fact]
    public void Prefixes_lines_from_several_pods()
    {
        Assert.Equal("[x2k4p] hello", LogLine.Parse("hello", "x2k4p").Display);
        Assert.Equal("hello", LogLine.Parse("hello").Display);
    }

    [Fact]
    public void Builds_kubectl_exec_as_an_argument_list()
    {
        var args = KubectlExec.Arguments(@"C:\Users\me\.kube\config", "prod cluster", "shop", "web-1", "app", ExecShell.Auto);

        Assert.Equal(new[] { "--kubeconfig", @"C:\Users\me\.kube\config", "--context", "prod cluster", "-n", "shop", "exec", "-it", "web-1", "-c", "app",
            "--", "sh", "-c", KubectlExec.AutoShellScript }, args);
        Assert.Equal(new[] { "--", "bash" }, KubectlExec.Arguments("k", "c", "ns", "p", null, ExecShell.Bash).TakeLast(2));
        Assert.DoesNotContain("-c", KubectlExec.Arguments("k", "c", "ns", "p", null, ExecShell.Sh));

        var info = KubectlExec.BuildStartInfo("kubectl.exe", "k", "c", "ns", "p", null, ExecShell.Sh);
        Assert.False(info.UseShellExecute);
        Assert.Equal(KubectlExec.Arguments("k", "c", "ns", "p", null, ExecShell.Sh), info.ArgumentList);
    }

    [Fact]
    public void Copyable_exec_command_quotes_spaces()
    {
        var text = KubectlExec.CommandText("k", "prod cluster", "shop", "web-1", "app", ExecShell.Bash, includeKubeconfig: false);
        Assert.Equal("kubectl --context \"prod cluster\" -n shop exec -it web-1 -c app -- bash", text);
    }

    [Theory]
    [InlineData("shop", "web-1", "app", "prod", true)]
    [InlineData("shop", "web-1.x", null, "arn:aws:eks:eu:1:cluster/p", true)]
    [InlineData("Shop", "web-1", null, "prod", false)]          // namespaces are lower-case
    [InlineData("shop", "web;rm -rf", null, "prod", false)]
    [InlineData("shop", "web-1", "app & calc", "prod", false)]
    [InlineData("shop", "web-1", null, "", false)]
    public void Validates_exec_targets(string ns, string pod, string? container, string context, bool ok) =>
        Assert.Equal(ok, KubectlExec.Validate(ns, pod, container, context) is null);

    [Theory]
    [InlineData("bash", ExecShell.Bash)]
    [InlineData(" SH ", ExecShell.Sh)]
    [InlineData("auto", ExecShell.Auto)]
    [InlineData(null, ExecShell.Auto)]
    public void Parses_shell_setting(string? text, ExecShell expected) => Assert.Equal(expected, KubectlExec.ParseShell(text));

    private static PodRow Row(string name, string ip = "", int restarts = 0, int ageMinutes = 0, string ready = "1/1") =>
        new("ns", name, ready, "Running", restarts, "", "", ip, [], true, new Dictionary<string, string>()) { Created = Now.AddMinutes(-ageMinutes) };

    [Fact]
    public void Sorts_ip_addresses_numerically()
    {
        var rows = new[] { Row("a", "10.0.0.10"), Row("b", "10.0.0.9"), Row("c", ""), Row("d", "9.1.1.1") };
        Assert.Equal(new[] { "d", "b", "a", "c" }, ExplorerSort.Pods(rows, "IP", true).Select(r => r.Name));
    }

    [Fact]
    public void Sorts_age_by_creation_time_and_restarts_as_numbers()
    {
        var rows = new[] { Row("old", ageMinutes: 3000, restarts: 10), Row("new", ageMinutes: 5, restarts: 9), Row("mid", ageMinutes: 90, restarts: 100) };

        Assert.Equal(new[] { "new", "mid", "old" }, ExplorerSort.Pods(rows, "Age", true).Select(r => r.Name));
        Assert.Equal(new[] { "old", "mid", "new" }, ExplorerSort.Pods(rows, "Age", false).Select(r => r.Name));
        Assert.Equal(new[] { "mid", "old", "new" }, ExplorerSort.Pods(rows, "Restarts", false).Select(r => r.Name));
    }

    [Fact]
    public void Sorts_ready_as_numbers_and_breaks_ties_by_name()
    {
        var rows = new[] { Row("b", ready: "10/10"), Row("a", ready: "2/10"), Row("c", ready: "2/10") };
        Assert.Equal(new[] { "a", "c", "b" }, ExplorerSort.Pods(rows, "Ready", true).Select(r => r.Name));
    }
}
