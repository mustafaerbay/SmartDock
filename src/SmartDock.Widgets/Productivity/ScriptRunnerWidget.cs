using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Productivity;

public enum ScriptState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Stopped,
}

/// <summary>A script line from the settings: <c>Name | Path | Arguments | Every N minutes</c>.</summary>
public sealed record ScriptDefinition(string Name, string Path, string Arguments, int EveryMinutes)
{
    public static readonly string[] AllowedExtensions = [".ps1", ".bat", ".cmd", ".py", ".exe"];
}

/// <summary>One script's live state in the popup.</summary>
public sealed class ScriptItem : ObservableObject
{
    private const int MaxOutputLines = 400;
    private readonly LinkedList<string> _output = new();
    private readonly object _gate = new();
    private ScriptState _state = ScriptState.Idle;
    private string _detail = "Not run yet";
    private string _tail = string.Empty;

    public ScriptItem(ScriptDefinition definition, ScriptRunnerWidget owner)
    {
        Definition = definition;
        RunCommand = new RelayCommand(() => owner.Start(this), () => State != ScriptState.Running);
        StopCommand = new RelayCommand(() => owner.Stop(this), () => State == ScriptState.Running);
        CopyOutputCommand = new RelayCommand(() => owner.CopyOutput(this));
    }

    public ScriptDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Schedule => Definition.EveryMinutes > 0 ? $"every {Definition.EveryMinutes} min" : "manual";

    public ScriptState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                RunCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunning => _state == ScriptState.Running;

    /// <summary>"Exit 0 · 4.2 s · 14:05", "Running for 12 s", ...</summary>
    public string Detail
    {
        get => _detail;
        internal set => SetProperty(ref _detail, value);
    }

    /// <summary>Last few output lines, for the popup.</summary>
    public string Tail
    {
        get => _tail;
        private set => SetProperty(ref _tail, value);
    }

    public RelayCommand RunCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand CopyOutputCommand { get; }

    internal Process? Process { get; set; }

    internal DateTimeOffset? StartedAt { get; set; }

    internal DateTimeOffset? LastRunAt { get; set; }

    internal void ClearOutput()
    {
        lock (_gate)
        {
            _output.Clear();
        }

        Tail = string.Empty;
    }

    internal void AppendOutput(string line)
    {
        string tail;
        lock (_gate)
        {
            _output.AddLast(line);
            while (_output.Count > MaxOutputLines)
            {
                _output.RemoveFirst();
            }

            tail = string.Join("\n", _output.Skip(Math.Max(0, _output.Count - 5)));
        }

        Tail = tail;
    }

    internal string FullOutput()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _output);
        }
    }
}

/// <summary>
/// Runs the user's own local scripts in the background (on demand or every N minutes) and shows their status,
/// duration and output. Off until the user enables it in Configure…; only local script files with an allowed
/// extension run, as the user's own account (never elevated), without a console window, with a timeout.
/// See ADR 0009.
/// </summary>
public sealed class ScriptRunnerWidget(IClipboardService clipboard) : WidgetBase, IPopupWidget, IConfigurableWidget, ISettingsValidator
{
    public const string TypeId = "script-runner";
    public const int MaxScripts = 20;

    private const string NotifyFailures = "Failures only";
    private const string NotifyAll = "Every run";
    private const string NotifyNever = "Never";

    private IReadOnlyList<ScriptItem> _scripts = [];
    private string _summary = "Off";
    private ScriptState _overall = ScriptState.Idle;
    private string? _feedback;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Script runner",
        "Runs your own scripts (.ps1, .bat, .cmd, .py, .exe) in the background, on demand or on a schedule, and shows their status and output.",
        WidgetCategory.Developer,
        "",
        DockRegion.Right,
        sp => new ScriptRunnerWidget(sp.GetRequiredService<IClipboardService>()))
    {
        AllowMultiple = true,
    };

    public IReadOnlyList<ScriptItem> Scripts
    {
        get => _scripts;
        private set => SetProperty(ref _scripts, value);
    }

    /// <summary>Card text: "1 running", "1 failed", "3 OK", "Off".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Card colour: Running (accent), Failed/TimedOut (red), Succeeded (green), Idle (neutral).</summary>
    public ScriptState Overall
    {
        get => _overall;
        private set => SetProperty(ref _overall, value);
    }

    public string? Feedback
    {
        get => _feedback;
        private set => SetProperty(ref _feedback, value);
    }

    /// <summary>Scheduler tick: checks which scheduled scripts are due and refreshes the "running for" text.</summary>
    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(15);

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("enabled", "Allow this widget to run my scripts", SettingFieldKind.Toggle,
            "Scripts run with your Windows account and can do anything you can. Only add scripts you wrote or trust."),
        new("scripts", "Scripts (one per line: Name | Path | Arguments | Every N minutes)", SettingFieldKind.MultilineText,
            "Local .ps1, .bat, .cmd, .py or .exe files. Arguments and the schedule are optional; leave the schedule empty (or 0) to run only on demand.\n" +
            "e.g.  Backup notes | D:\\scripts\\backup.ps1 | -Target E:\\ | 60",
            DefaultText: string.Empty),
        new("timeoutMinutes", "Stop a script after (minutes)", SettingFieldKind.Number, "1–720.", DefaultNumber: 10),
        new("notify", "Notify me", SettingFieldKind.Choice, Choices: [NotifyFailures, NotifyAll, NotifyNever], DefaultText: NotifyFailures),
    ];

    public void OnPopupOpened() => Feedback = null;

    public IReadOnlyList<string> Validate(string key, string value) => key == "scripts" ? Explain(value) : [];

    /// <summary>Explains every line <see cref="Parse"/> would skip (or that will fail when run); public for tests.</summary>
    public static IReadOnlyList<string> Explain(string? text)
    {
        var problems = new List<string>();
        var lineNumber = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var path = (parts.Length >= 2 ? parts[1] : parts[0]).Trim('"');
            var where = $"Line {lineNumber}";
            if (path.Length == 0)
            {
                problems.Add($"{where}: no script path (format: Name | Path | Arguments | Every N minutes).");
            }
            else if (path.Contains("://", StringComparison.Ordinal))
            {
                problems.Add($"{where}: \"{path}\" is a URL – only local script files can run.");
            }
            else if (!System.IO.Path.IsPathFullyQualified(path))
            {
                problems.Add($"{where}: \"{path}\" isn't a full file path. Commands can't be typed here directly – " +
                    "put them in a .ps1 or .bat file and enter its full path, e.g. D:\\scripts\\check.ps1.");
            }
            else if (!ScriptDefinition.AllowedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{where}: \"{System.IO.Path.GetExtension(path)}\" files aren't supported (use .ps1, .bat, .cmd, .py or .exe).");
            }
            else if (!File.Exists(path))
            {
                problems.Add($"{where}: file not found: {path} (it will be kept, but fails until the file exists).");
            }

            if (parts.Length >= 4 && parts[3].Length > 0
                && !int.TryParse(parts[3].Replace("min", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                problems.Add($"{where}: schedule \"{parts[3]}\" isn't a number of minutes – the script will run only on demand.");
            }
        }

        return problems;
    }

    /// <summary>Parses the script list; public for tests. Invalid lines are skipped.</summary>
    public static IReadOnlyList<ScriptDefinition> Parse(string? text)
    {
        var list = new List<ScriptDefinition>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var (name, path) = parts.Length >= 2 ? (parts[0], parts[1].Trim('"')) : (string.Empty, parts[0].Trim('"'));
            var arguments = parts.Length >= 3 ? parts[2] : string.Empty;
            var every = parts.Length >= 4 && int.TryParse(parts[3].Replace("min", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? Math.Clamp(minutes, 0, 7 * 24 * 60) : 0;

            if (!IsAllowedPath(path))
            {
                continue;
            }

            list.Add(new ScriptDefinition(name.Length > 0 ? name : System.IO.Path.GetFileNameWithoutExtension(path), path, arguments, every));
            if (list.Count == MaxScripts)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Absolute local path with an allowed extension (no URLs, no relative paths).</summary>
    public static bool IsAllowedPath(string path)
    {
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var extension = System.IO.Path.GetExtension(path);
        return ScriptDefinition.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Splits an argument string the way a command line does (double quotes group words).</summary>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0 || any)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0 || any)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>How each script type is started: interpreter + arguments (no shell parsing of the user's text).</summary>
    public static ProcessStartInfo BuildStartInfo(ScriptDefinition script)
    {
        var extension = System.IO.Path.GetExtension(script.Path).ToLowerInvariant();
        var start = extension switch
        {
            // Process-scope bypass only (nothing in Windows' execution policy is changed).
            ".ps1" => new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script.Path } },
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "call", script.Path } },
            ".py" => new ProcessStartInfo("python.exe") { ArgumentList = { "-u", script.Path } },
            ".exe" => new ProcessStartInfo(script.Path),
            _ => throw new InvalidOperationException($"{extension} files aren't allowed."),
        };

        foreach (var argument in SplitArguments(script.Arguments))
        {
            start.ArgumentList.Add(argument);
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true; // Closed immediately, so a script waiting for input fails instead of hanging.
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.WorkingDirectory = System.IO.Path.GetDirectoryName(script.Path) ?? Environment.CurrentDirectory;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        return start;
    }

    internal void Start(ScriptItem item)
    {
        if (!Settings.Get("enabled", false))
        {
            Feedback = "Script running is off. Turn it on in Configure….";
            return;
        }

        if (item.State == ScriptState.Running)
        {
            return;
        }

        if (!File.Exists(item.Definition.Path))
        {
            Finish(item, ScriptState.Failed, "File not found: " + item.Definition.Path);
            return;
        }

        Process process;
        try
        {
            process = new Process { StartInfo = BuildStartInfo(item.Definition), EnableRaisingEvents = true };
            item.ClearOutput();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    item.AppendOutput(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    item.AppendOutput("! " + e.Data);
                }
            };

            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var hint = item.Definition.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? " (is Python installed and on PATH?)" : string.Empty;
            Finish(item, ScriptState.Failed, $"Couldn't start: {ex.Message}{hint}");
            return;
        }

        item.Process = process;
        item.StartedAt = DateTimeOffset.Now;
        item.LastRunAt = item.StartedAt;
        item.State = ScriptState.Running;
        item.Detail = "Started " + item.StartedAt.Value.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        UpdateSummary();
        _ = WatchAsync(item, process);
    }

    internal void Stop(ScriptItem item)
    {
        if (item.Process is { } process)
        {
            item.State = ScriptState.Stopped; // Set first so WatchAsync reports "Stopped", not "Failed".
            Kill(process);
        }
    }

    internal void CopyOutput(ScriptItem item)
    {
        var text = item.FullOutput();
        if (text.Length == 0)
        {
            Feedback = "No output yet.";
            return;
        }

        try
        {
            clipboard.SetText(text);
            Feedback = $"Copied {item.Name} output.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Feedback = "Clipboard is busy – try again.";
        }
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var definitions = Parse(Settings.Get("scripts", string.Empty));
        var previous = Scripts.GroupBy(s => s.Definition).ToDictionary(g => g.Key, g => g.First());
        var items = definitions.Select(d => previous.GetValueOrDefault(d) ?? new ScriptItem(d, this)).ToList();

        // Scripts removed from the list are stopped.
        // (Command state changes must happen on the UI thread, hence PostToUi.)
        foreach (var removed in Scripts.Where(s => !items.Contains(s) && s.State == ScriptState.Running).ToList())
        {
            PostToUi(() => Stop(removed));
        }

        if (!items.SequenceEqual(Scripts))
        {
            Scripts = items;
        }

        if (!Settings.Get("enabled", false))
        {
            foreach (var running in items.Where(s => s.State == ScriptState.Running).ToList())
            {
                PostToUi(() => Stop(running)); // Turning the widget off stops what it started.
            }

            Summary = "Off";
            Overall = ScriptState.Idle;
            SetUnavailable("Script running is off. Right-click › Configure… to add scripts and turn it on.");
            return Task.CompletedTask;
        }

        if (items.Count == 0)
        {
            Summary = "No scripts";
            Overall = ScriptState.Idle;
            SetUnavailable("Add scripts in Configure….");
            return Task.CompletedTask;
        }

        SetReady();
        var timeout = TimeSpan.FromMinutes(Math.Clamp(Settings.Get("timeoutMinutes", 10.0), 1, 720));
        var now = DateTimeOffset.Now;
        foreach (var item in items)
        {
            if (item.State == ScriptState.Running && item.StartedAt is { } started)
            {
                if (now - started > timeout && item.Process is { } process)
                {
                    PostToUi(() =>
                    {
                        item.State = ScriptState.TimedOut;
                        Kill(process);
                    });
                }
                else
                {
                    item.Detail = $"Running for {Duration(now - started)}";
                }
            }
            else if (item.Definition.EveryMinutes > 0
                && (item.LastRunAt is null || now - item.LastRunAt.Value >= TimeSpan.FromMinutes(item.Definition.EveryMinutes)))
            {
                PostToUi(() => Start(item));
            }
        }

        PostToUi(UpdateSummary);
        return Task.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync()
    {
        // Don't leave background scripts running after the widget is removed or SmartDock exits.
        foreach (var item in Scripts)
        {
            if (item.Process is { } process)
            {
                Kill(process);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task WatchAsync(ScriptItem item, Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit(); // Flushes the asynchronous output readers.
        }
        catch (InvalidOperationException)
        {
            // Process object was disposed while waiting.
        }

        var elapsed = item.StartedAt is { } started ? DateTimeOffset.Now - started : TimeSpan.Zero;
        var exitCode = SafeExitCode(process);
        process.Dispose();
        item.Process = null;

        var state = item.State switch
        {
            ScriptState.Stopped => ScriptState.Stopped,
            ScriptState.TimedOut => ScriptState.TimedOut,
            _ => exitCode == 0 ? ScriptState.Succeeded : ScriptState.Failed,
        };
        var detail = state switch
        {
            ScriptState.Stopped => $"Stopped after {Duration(elapsed)}",
            ScriptState.TimedOut => $"Timed out after {Duration(elapsed)}",
            _ => $"Exit {exitCode} · {Duration(elapsed)} · {DateTimeOffset.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}",
        };

        PostToUi(() => Finish(item, state, detail));
    }

    private void Finish(ScriptItem item, ScriptState state, string detail)
    {
        item.State = state;
        item.Detail = detail;
        UpdateSummary();

        var mode = Settings.Get("notify", NotifyFailures);
        var failed = state is ScriptState.Failed or ScriptState.TimedOut;
        if (mode == NotifyNever || (mode == NotifyFailures && !failed) || state == ScriptState.Stopped)
        {
            return;
        }

        var last = item.Tail.Split('\n').LastOrDefault(l => l.Length > 0) ?? string.Empty;
        Context.Notifications.Post(
            failed ? $"{item.Name} failed" : $"{item.Name} finished",
            last.Length > 0 ? $"{detail}\n{last}" : detail,
            failed ? NotificationSeverity.Error : NotificationSeverity.Success,
            InstanceId);
    }

    private void UpdateSummary()
    {
        var items = Scripts;
        var running = items.Count(s => s.State == ScriptState.Running);
        var failed = items.Count(s => s.State is ScriptState.Failed or ScriptState.TimedOut);
        var ok = items.Count(s => s.State == ScriptState.Succeeded);
        Overall = running > 0 ? ScriptState.Running : failed > 0 ? ScriptState.Failed : ok > 0 ? ScriptState.Succeeded : ScriptState.Idle;
        Summary = running > 0 ? $"{running} running"
            : failed > 0 ? $"{failed} failed"
            : ok > 0 ? $"{ok} OK"
            : $"{items.Count} ready";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.TotalSeconds:0.0} s";
}
