# Kubernetes cluster widget

The widget gives a read-only health view of **one cluster per widget**, using your existing kubeconfig.

## Setup
1. Add the widget: Widgets › **Kubernetes cluster**.
2. Right-click the widget › **Configure…**.
   - **Kubeconfig file:** leave it empty to use `KUBECONFIG` or `%USERPROFILE%\.kube\config`, or enter a path.
   - **Context:** pick one from the list, which is read from the file. To watch another cluster, add the widget again.
   - **Optional:** a display name, namespaces to limit it to, how often it checks, the restart and pending
     thresholds, and notifications.

Authentication works exactly as it does for `kubectl`: client certificates, bearer tokens and `exec` plugins
(kubelogin, OIDC, cloud CLIs) are all handled by the official Kubernetes C# client. SmartDock only *reads*
the kubeconfig and never copies credentials into its own settings.

> **Exec plugins:** the plugin must be on the `PATH` that SmartDock sees, which is your Windows user `PATH`
> at sign-in. Plugins that need interactive login, such as a device-code flow, should be run once in a terminal
> first so they have a cached token. If they aren't, the widget reports a timeout instead of hanging.

## What it shows
| Section | Rules |
|---|---|
| Nodes | Ready / NotReady (critical); Memory, Disk or PID pressure (warning); cordoned |
| Pods | CrashLoopBackOff, ImagePullBackOff, ErrImagePull, CreateContainer*Error (critical); Failed (critical); OOMKilled within the last hour, at least *N* restarts within the last hour, Pending longer than *M* minutes, stuck Terminating (warning) |
| Workloads | Deployments, StatefulSets and DaemonSets below the desired ready count: degraded (warning) or 0 ready (critical); rollouts stuck with `ProgressDeadlineExceeded` (critical) |
| Storage / Jobs | PVC Pending longer than *M* minutes (warning) or Lost (critical); Jobs that failed within the last 6 hours (warning) |
| Events | Warning events from the last 30 minutes, grouped by reason and object |

Clicking a problem copies a `kubectl … describe` command, and the 📄 button copies a `kubectl … logs`
command (`--previous` for crashed containers). Notifications are sent only for **critical** problems, and
only after a problem has been seen on **two checks in a row**.

## Load on the API server
Every check makes a handful of `list` calls (nodes, pods, deployments, statefulsets, daemonsets, PVCs,
jobs, and events of type Warning). Pods are listed with `status.phase!=Succeeded` in pages of 500. Limiting
the widget to namespaces turns the cluster-wide calls into per-namespace calls.

## Recommended read-only RBAC
For production clusters, give the widget its own read-only identity:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: smartdock-readonly
rules:
  - apiGroups: [""]
    resources: [nodes, pods, events, persistentvolumeclaims]
    verbs: [get, list, watch]
  - apiGroups: [apps]
    resources: [deployments, statefulsets, daemonsets]
    verbs: [get, list, watch]
  - apiGroups: [batch]
    resources: [jobs]
    verbs: [get, list, watch]
---
apiVersion: v1
kind: ServiceAccount
metadata:
  name: smartdock
  namespace: kube-system
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: smartdock-readonly
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: smartdock-readonly
subjects:
  - kind: ServiceAccount
    name: smartdock
    namespace: kube-system
```

If some resources are denied (403), the widget keeps working with the rest and shows a
"No permission to list: …" note. If you only have namespace-scoped access, enter those namespaces in the
widget's settings.

## Pods, deployments & live logs (v1.23.0)

Open it with **Pods & logs** in the popup, right-click › *Pods, deployments & logs…*, or the **View live logs**
button on a pod problem.

| Part | What it does | API calls (read-only) |
|---|---|---|
| Namespace picker | Configured namespaces, or every namespace the account may list | `list namespaces` (optional) |
| Deployments tab | READY, UP-TO-DATE, AVAILABLE, AGE, images; double-click → its pods | `list deployments` |
| Pods tab | READY, STATUS (kubectl rules), RESTARTS, AGE, NODE, IP | `list pods` (label selector for a deployment) |
| Logs | Follow, tail, previous container, timestamps, per container or all; all pods of a deployment (max 10) | `get pods/log` (streamed) |

- The window has its own client built from the same kubeconfig/context and disposes it when closed.
- Lists refresh every 10 s while the window is visible (can be turned off); logs stream continuously.
- Up to 20,000 log lines are kept per view; older lines are dropped.
- Nothing is ever changed in the cluster. Restart/scale/exec are deliberately not offered.
- Needed RBAC: `list` on pods/deployments (and namespaces for the picker), `get` on `pods/log`.

## Shell in a pod (v1.24.0, opt-in)

*Open shell…* (pods list button or right-click a pod) starts the user's own kubectl in a new console window:

```
kubectl --kubeconfig <file> --context <context> -n <namespace> exec -it <pod> -c <container> -- sh -c "if command -v bash …; then exec bash; else exec sh; fi"
```

- Disabled until *Allow opening a shell in pods (kubectl exec)* is turned on for that widget (spec: no shell
  execution by default). Each use is confirmed in a dialog that names pod, container and context, and logged.
- SmartDock does not implement the exec protocol or a terminal; kubectl does, with the user's kubeconfig and RBAC
  (`create` on `pods/exec`).
- Names are checked against Kubernetes naming rules and every value is a separate process argument (no cmd.exe /
  PowerShell parsing). The pod must be Running.
- When kubectl exits within 8 seconds with an error, the explorer explains likely causes (no permission,
  distroless image, restart). *Copy exec command* gives the same command to run manually.

## Changing deployments (v1.25.0, opt-in)

Enabled per widget with *Allow changing deployments*; otherwise the explorer stays read-only.

| Action | API call | Like |
|---|---|---|
| Scale | `PATCH deployments/<name>/scale` (merge) | `kubectl scale --replicas=N` |
| Set image | `PATCH deployments/<name>` (strategic merge, container by name) | `kubectl set image` |
| Restart | `PATCH` pod-template annotation `kubectl.kubernetes.io/restartedAt` | `kubectl rollout restart` |
| Roll back | read ReplicaSets owned by the deployment; JSON-patch `/spec/template` with the chosen revision's template (minus `pod-template-hash`) | `kubectl rollout undo --to-revision` |
| Edit YAML | `PUT deployments/<name>` with the edited object; *Validate* uses `dryRun=All` | `kubectl edit` |

Safeguards: confirmation naming the context for every change; extra warning when scaling to 0; image references
validated; edited YAML must keep kind/name/namespace; optimistic concurrency through resourceVersion (409 is
explained); each change logged. Required RBAC: `patch` on deployments and deployments/scale, `update` on
deployments, `list` on replicasets.

