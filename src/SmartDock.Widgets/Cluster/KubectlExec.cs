using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace SmartDock.Widgets.Cluster;

/// <summary>Which shell to start inside the container.</summary>
public enum ExecShell
{
    /// <summary>bash when the image has it, otherwise sh.</summary>
    Auto,
    Sh,
    Bash,
}

/// <summary>
/// Opens an interactive shell in a container by starting the user's own <c>kubectl exec -it</c> in a new console
/// window (Windows Terminal when it's the default terminal). SmartDock doesn't implement a terminal or talk to the
/// exec API itself: kubectl does, with the same kubeconfig, context and RBAC the user already has.
/// Off by default (widget setting), and every start is confirmed by the user in the explorer window.
/// All arguments are passed as an argument list (no shell parsing) after validation.
/// </summary>
public static partial class KubectlExec
{
    public const string AutoShellScript = "if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi";

    /// <summary>kubectl.exe from the setting, or the first one on PATH; null when there's none.</summary>
    public static string? FindKubectl(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            return File.Exists(path) ? path : null;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "kubectl.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>Why these values can't be used, or null when they're fine. Public for tests.</summary>
    public static string? Validate(string ns, string pod, string? container, string context)
    {
        if (!NameRegex().IsMatch(ns) || ns.Length > 63)
        {
            return $"Unexpected namespace name \"{ns}\".";
        }

        if (!NameRegex().IsMatch(pod) || pod.Length > 253)
        {
            return $"Unexpected pod name \"{pod}\".";
        }

        if (container is not null && (!NameRegex().IsMatch(container) || container.Length > 63))
        {
            return $"Unexpected container name \"{container}\".";
        }

        if (context.Length == 0 || context.Any(char.IsControl))
        {
            return "The kubeconfig context name is empty or contains control characters.";
        }

        return null;
    }

    /// <summary>
    /// The kubectl command line as an argument list, e.g.
    /// <c>kubectl --kubeconfig C:\u\.kube\config --context prod -n shop exec -it web-1 -c app -- sh -c "…"</c>. Public for tests.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string kubectl, string kubeconfigPath, string context, string ns, string pod, string? container, ExecShell shell)
    {
        var info = new ProcessStartInfo(kubectl)
        {
            UseShellExecute = false,
            CreateNoWindow = false, // a GUI app starting a console app gets a new console window
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var arg in Arguments(kubeconfigPath, context, ns, pod, container, shell))
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    public static IReadOnlyList<string> Arguments(string kubeconfigPath, string context, string ns, string pod, string? container, ExecShell shell)
    {
        var args = new List<string> { "--kubeconfig", kubeconfigPath, "--context", context, "-n", ns, "exec", "-it", pod };
        if (container is not null)
        {
            args.Add("-c");
            args.Add(container);
        }

        args.Add("--");
        switch (shell)
        {
            case ExecShell.Bash:
                args.Add("bash");
                break;
            case ExecShell.Sh:
                args.Add("sh");
                break;
            default:
                args.Add("sh");
                args.Add("-c");
                args.Add(AutoShellScript);
                break;
        }

        return args;
    }

    /// <summary>The same command as text for copying into your own terminal (PowerShell/cmd quoting).</summary>
    public static string CommandText(string kubeconfigPath, string context, string ns, string pod, string? container, ExecShell shell, bool includeKubeconfig)
    {
        var args = Arguments(kubeconfigPath, context, ns, pod, container, shell).ToList();
        if (!includeKubeconfig)
        {
            args.RemoveRange(0, 2);
        }

        return "kubectl " + string.Join(' ', args.Select(Quote));

        static string Quote(string a) => a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_./:=@\\".Contains(c)) ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    public static ExecShell ParseShell(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "sh" => ExecShell.Sh,
        "bash" => ExecShell.Bash,
        _ => ExecShell.Auto,
    };

    // RFC 1123 subdomain (pods) / label (namespaces, containers) characters.
    [GeneratedRegex("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")]
    private static partial Regex NameRegex();
}
