using System.ComponentModel;
using System.Diagnostics;

namespace SmartDock.Widgets.SystemMonitor;

/// <summary>One program in the "top processes" list (all processes with the same name are summed).</summary>
public sealed record ProcessUsage(string Name, int Count, double CpuPercent, long MemoryBytes);

/// <summary>
/// Measures per-process CPU and memory like Task Manager: CPU% is the processor time used between two samples
/// divided by elapsed time × logical processors; memory is the working set. Processes are grouped by name
/// ("chrome ×23"). Protected system processes whose CPU time can't be read count as 0 % but still show memory.
/// Read-only: nothing is changed or ended.
/// </summary>
public sealed class ProcessSampler
{
    private readonly Dictionary<(int Pid, string Name), TimeSpan> _previous = new();
    private DateTime _previousAt;

    /// <summary>True once two samples exist, i.e. CPU percentages are meaningful.</summary>
    public bool HasBaseline { get; private set; }

    public IReadOnlyList<ProcessUsage> Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = HasBaseline || _previousAt != default ? (now - _previousAt).TotalMilliseconds : 0;
        var cores = Math.Max(1, Environment.ProcessorCount);
        var current = new Dictionary<(int, string), TimeSpan>();
        var groups = new Dictionary<string, (int Count, double Cpu, long Memory)>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        foreach (var process in processes)
        {
            using (process)
            {
                string name;
                long memory;
                try
                {
                    if (process.Id == 0)
                    {
                        continue; // "Idle" is not a program.
                    }

                    name = process.ProcessName;
                    memory = process.WorkingSet64;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    continue; // exited meanwhile
                }

                var cpu = 0.0;
                try
                {
                    var total = process.TotalProcessorTime;
                    var key = (process.Id, name);
                    current[key] = total;
                    if (elapsed > 0 && _previous.TryGetValue(key, out var before) && total >= before)
                    {
                        cpu = (total - before).TotalMilliseconds / elapsed / cores * 100;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
                {
                    // Access denied for protected/elevated processes: memory only.
                }

                var group = groups.GetValueOrDefault(name);
                groups[name] = (group.Count + 1, group.Cpu + cpu, group.Memory + memory);
            }
        }

        HasBaseline = _previousAt != default;
        _previous.Clear();
        foreach (var (key, value) in current)
        {
            _previous[key] = value;
        }

        _previousAt = now;
        return groups.Select(g => new ProcessUsage(g.Key, g.Value.Count, Math.Clamp(g.Value.Cpu, 0, 100), g.Value.Memory)).ToList();
    }
}
