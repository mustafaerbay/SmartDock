# ADR 0008 — Read-only Kubernetes widget via the official C# client

**Status:** accepted (v1.7.0)

## Decision
- Use the official `KubernetesClient` NuGet package (19.0.x), configured with
  `BuildConfigFromConfigFile(kubeconfigPath, currentContext)`. It handles certificates, tokens and
  exec/OIDC credential plugins the same way kubectl does. The alternative of shelling out to `kubectl`
  was rejected: it starts a process for every query, its output has to be parsed, and it goes against the
  "no shell execution" rule.
- Each widget instance watches exactly one context. Credentials stay in the user's kubeconfig.
- Only `list` verbs are used. Nothing can change the cluster from the dock. The popup copies kubectl
  commands to the clipboard instead of running them.
- The analysis (`ClusterAnalyzer`) is a pure function over Kubernetes model objects, so it is unit-tested.
- A 403 on one resource type is reported and skipped rather than failing the whole widget.
- Critical problems notify once each, after being seen on two consecutive checks.

## Consequences
- The Widgets project now depends on KubernetesClient and its dependencies (YamlDotNet, Fractions).
- Exec plugins run with SmartDock's environment, so they have to be on the user `PATH`.
- Metrics (CPU and memory per node) are not shown yet; they would need metrics-server. This is a possible follow-up.
