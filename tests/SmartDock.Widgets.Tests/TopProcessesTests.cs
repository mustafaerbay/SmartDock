using SmartDock.Widgets.SystemMonitor;

namespace SmartDock.Widgets.Tests;

public class TopProcessesTests
{
    private static readonly ProcessUsage[] Sample =
    [
        new("chrome", 23, 12.5, 2_400_000_000),
        new("devenv", 1, 30.0, 900_000_000),
        new("explorer", 1, 0.4, 150_000_000),
        .. Enumerable.Range(0, 20).Select(i => new ProcessUsage($"svc{i}", 1, 0.1, 10_000_000)),
    ];

    [Fact]
    public void Cpu_order_shows_ten_busiest_with_bars_relative_to_the_top()
    {
        var rows = TopProcesses.Build(Sample, TopProcessOrder.Cpu, cpuReady: true);

        Assert.Equal(TopProcesses.Count, rows.Count);
        Assert.Equal(("devenv", $"{30.0:0.0} %", 1.0), (rows[0].Name, rows[0].Primary, rows[0].Fraction)); // culture-aware decimal separator
        Assert.Equal(("chrome", "×23"), (rows[1].Name, rows[1].Count));
        Assert.Equal(12.5 / 30.0, rows[1].Fraction, 3);
    }

    [Fact]
    public void Memory_order_puts_largest_first_and_waits_for_cpu_baseline()
    {
        var rows = TopProcesses.Build(Sample, TopProcessOrder.Memory, cpuReady: false);

        Assert.Equal("chrome", rows[0].Name);
        Assert.Equal("… CPU", rows[0].Secondary);
        Assert.Equal(1.0, rows[0].Fraction);
    }

    [Fact]
    public void Sampler_reads_this_machine_without_throwing()
    {
        var sampler = new ProcessSampler();
        var first = sampler.Sample();
        Assert.NotEmpty(first);
        Assert.False(sampler.HasBaseline);
        Assert.True(sampler.Sample().All(u => u.CpuPercent is >= 0 and <= 100));
        Assert.True(sampler.HasBaseline);
    }
}
