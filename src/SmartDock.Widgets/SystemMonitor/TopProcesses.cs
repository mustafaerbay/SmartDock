using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.SystemMonitor;

public enum TopProcessOrder
{
    Cpu,
    Memory,
}

/// <summary>Row in the CPU/Memory popup.</summary>
public sealed record TopProcessRow(string Name, string Count, string Primary, string Secondary, double Fraction);

/// <summary>
/// Live "top 10 processes" list for the CPU and Memory popups. Sampling runs only while the popup is open
/// (every 2 s on a background thread), so it costs nothing the rest of the time.
/// </summary>
public sealed class TopProcesses(TopProcessOrder order) : ObservableObject
{
    public const int Count = 10;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly ProcessSampler _sampler = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<TopProcessRow> _rows = [];
    private string _status = string.Empty;

    public IReadOnlyList<TopProcessRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>"Measuring…", "312 processes · updated every 2 s".</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public RelayCommand OpenTaskManagerCommand { get; } = new(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Task Manager disabled by policy: nothing to do.
        }
    });

    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Status = "Measuring…";
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Turns grouped samples into the top rows; public for tests.</summary>
    public static IReadOnlyList<TopProcessRow> Build(IReadOnlyList<ProcessUsage> usage, TopProcessOrder order, bool cpuReady)
    {
        var sorted = order == TopProcessOrder.Cpu
            ? usage.OrderByDescending(u => u.CpuPercent).ThenByDescending(u => u.MemoryBytes)
            : usage.OrderByDescending(u => u.MemoryBytes).ThenByDescending(u => u.CpuPercent);
        var top = sorted.Take(Count).ToList();
        var max = order == TopProcessOrder.Cpu
            ? Math.Max(1, top.Select(u => u.CpuPercent).DefaultIfEmpty(0).Max())
            : Math.Max(1, top.Select(u => (double)u.MemoryBytes).DefaultIfEmpty(0).Max());

        return top.Select(u =>
        {
            var cpu = cpuReady ? $"{u.CpuPercent:0.0} % CPU" : "… CPU";
            var memory = Format.Bytes(u.MemoryBytes);
            return order == TopProcessOrder.Cpu
                ? new TopProcessRow(u.Name, u.Count > 1 ? $"×{u.Count}" : string.Empty, cpuReady ? $"{u.CpuPercent:0.0} %" : "…", memory, u.CpuPercent / max)
                : new TopProcessRow(u.Name, u.Count > 1 ? $"×{u.Count}" : string.Empty, memory, cpu, u.MemoryBytes / max);
        }).ToList();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            // First sample is the CPU baseline; memory can be shown at once.
            var first = _sampler.Sample();
            if (order == TopProcessOrder.Memory)
            {
                Rows = Build(first, order, cpuReady: false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                var usage = _sampler.Sample();
                Rows = Build(usage, order, cpuReady: true);
                Status = $"{usage.Sum(u => u.Count):N0} processes · grouped by name · updates every 2 s";
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Popup closed.
        }
    }
}
