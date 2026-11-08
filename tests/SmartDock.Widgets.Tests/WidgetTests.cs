using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Clock;
using SmartDock.Widgets.Common;
using SmartDock.Widgets.Network;
using SmartDock.Widgets.Productivity;
using SmartDock.Widgets.SystemMonitor;

namespace SmartDock.Widgets.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeMetrics : ISystemMetrics
{
    public CpuTimes Cpu { get; set; }

    public MemoryStatus? Memory { get; set; } = new MemoryStatus(16UL << 30, 4UL << 30);

    public BatteryStatus Battery { get; set; } = new(false, null, false, true, null);

    public NetworkCounters Network { get; set; } = new(0, 0, true);

    public bool TryGetCpuTimes(out CpuTimes times)
    {
        times = Cpu;
        return true;
    }

    public MemoryStatus? GetMemoryStatus() => Memory;

    public NetworkCounters GetNetworkCounters() => Network;

    public BatteryStatus GetBatteryStatus() => Battery;

    public DiskStatus? GetDisk(string rootPath) => new DiskStatus(rootPath, "System", 1000, 250);

    public IReadOnlyList<string> GetFixedDriveRoots() => ["C:\\"];
}

internal static class Ctx
{
    public static (WidgetContext Context, NotificationService Notifications) For(WidgetDescriptor d, string id = "w")
    {
        var notifications = new NotificationService();
        return (new WidgetContext(id, d, new WidgetSettings([]), new EventBus(), notifications, NullLogger.Instance), notifications);
    }
}

public class CpuTests
{
    [Fact]
    public void Computes_busy_percentage_from_deltas()
    {
        var prev = new CpuTimes(Idle: 1000, Kernel: 1500, User: 500);

        // 400 ticks elapsed (kernel+user), 100 of them idle => 75% busy.
        var now = new CpuTimes(Idle: 1100, Kernel: 1800, User: 600);

        Assert.Equal(75, CpuUsageCalculator.Compute(prev, now)!.Value, precision: 3);
    }

    [Fact]
    public void Returns_null_for_counter_reset() =>
        Assert.Null(CpuUsageCalculator.Compute(new CpuTimes(10, 10, 10), new CpuTimes(5, 5, 5)));

    [Fact]
    public async Task Widget_shows_value_after_second_sample()
    {
        var metrics = new FakeMetrics { Cpu = new CpuTimes(0, 0, 0) };
        var widget = new CpuWidget(metrics);
        await widget.InitializeAsync(Ctx.For(CpuWidget.Definition).Context, default);

        await widget.RefreshAsync(default);
        Assert.Equal("–", widget.ValueText);

        metrics.Cpu = new CpuTimes(50, 150, 50); // 200 elapsed, 50 idle => 75%
        await widget.RefreshAsync(default);
        Assert.Equal(75, widget.Percent, precision: 3);
        Assert.Single(widget.History);
    }
}

public class MemoryAndBatteryTests
{
    [Fact]
    public async Task Memory_reports_used_percentage()
    {
        var widget = new MemoryWidget(new FakeMetrics());
        await widget.InitializeAsync(Ctx.For(MemoryWidget.Definition).Context, default);
        await widget.RefreshAsync(default);

        Assert.Equal(75, widget.Percent, precision: 3);
        Assert.Equal(WidgetState.Ready, widget.State);
    }

    [Fact]
    public async Task Desktop_without_battery_shows_ac_instead_of_fake_level()
    {
        var widget = new BatteryWidget(new FakeMetrics());
        await widget.InitializeAsync(Ctx.For(BatteryWidget.Definition).Context, default);
        await widget.RefreshAsync(default);

        Assert.False(widget.HasBattery);
        Assert.Equal("AC", widget.PercentText);
        Assert.False(widget.IsLow);
    }

    [Fact]
    public async Task Low_battery_is_flagged_when_discharging()
    {
        var widget = new BatteryWidget(new FakeMetrics { Battery = new BatteryStatus(true, 12, false, false, TimeSpan.FromMinutes(20)) });
        await widget.InitializeAsync(Ctx.For(BatteryWidget.Definition).Context, default);
        await widget.RefreshAsync(default);

        Assert.True(widget.IsLow);
        Assert.Equal("12%", widget.PercentText);
    }
}

public class NetworkTests
{
    [Fact]
    public void Rate_is_bytes_per_second()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var rate = NetworkRateCalculator.Compute(new NetworkCounters(1000, 500, true), t0, new NetworkCounters(5000, 1500, true), t0.AddSeconds(2));

        Assert.Equal(2000, rate!.Value.Down);
        Assert.Equal(500, rate.Value.Up);
    }

    [Fact]
    public void Counter_reset_yields_no_rate()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Null(NetworkRateCalculator.Compute(new NetworkCounters(5000, 500, true), t0, new NetworkCounters(10, 10, true), t0.AddSeconds(2)));
    }

    [Fact]
    public async Task Offline_shows_disconnected_state()
    {
        var widget = new NetworkWidget(new FakeMetrics { Network = new NetworkCounters(0, 0, false) });
        await widget.InitializeAsync(Ctx.For(NetworkWidget.Definition).Context, default);
        await widget.RefreshAsync(default);

        Assert.Equal(WidgetState.Disconnected, widget.State);
        Assert.False(widget.IsConnected);
    }
}

public class ClockTests
{
    [Fact]
    public async Task Formats_24h_with_optional_seconds_in_utc()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 15, 4, 5, TimeSpan.Zero));
        var widget = new ClockWidget(time);
        var (context, _) = Ctx.For(ClockWidget.Definition);
        await widget.InitializeAsync(context, default);
        context.Settings.Set("use24Hour", true);
        context.Settings.Set("timeZoneId", "UTC");

        Assert.Equal("15:04", widget.FormatAt(time.Now).Time);

        context.Settings.Set("showSeconds", true);
        Assert.Equal("15:04:05", widget.FormatAt(time.Now).Time);
    }

    [Fact]
    public async Task Formats_12h_with_designator()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 15, 4, 0, TimeSpan.Zero));
            var widget = new ClockWidget(time);
            var (context, _) = Ctx.For(ClockWidget.Definition);
            await widget.InitializeAsync(context, default);
            context.Settings.Set("use24Hour", false);
            context.Settings.Set("timeZoneId", "UTC");

            Assert.Equal("3:04 PM", widget.FormatAt(time.Now).Time);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class PomodoroTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Counts_down_and_pauses()
    {
        var timer = new PomodoroTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        timer.Start(T0);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Remaining(T0.AddMinutes(10)));

        timer.Pause(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Remaining(T0.AddMinutes(20)));
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Focus_completion_switches_to_paused_break()
    {
        var timer = new PomodoroTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        timer.Start(T0);

        Assert.Null(timer.Tick(T0.AddMinutes(24)));
        Assert.Equal(PomodoroPhase.Focus, timer.Tick(T0.AddMinutes(25)));
        Assert.Equal(PomodoroPhase.Break, timer.Phase);
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), timer.Remaining(T0.AddMinutes(30)));
    }

    [Fact]
    public async Task Widget_posts_notification_when_phase_ends()
    {
        var time = new ManualTimeProvider(T0);
        var widget = new PomodoroWidget(time);
        var (context, notifications) = Ctx.For(PomodoroWidget.Definition);
        await widget.InitializeAsync(context, default);

        widget.StartPauseCommand.Execute(null);
        time.Advance(TimeSpan.FromMinutes(25));
        await widget.RefreshAsync(default);

        Assert.Single(notifications.History);
        Assert.Equal("Break", widget.PhaseText);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(157286400, "150 MB")]
    public void Bytes_are_compact(double bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Format.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class RegistryTests
{
    [Fact]
    public void Built_in_widget_ids_are_unique_and_registered()
    {
        var registry = new WidgetRegistry(BuiltInWidgets.All);

        Assert.Equal(BuiltInWidgets.All.Count, registry.All.Count);
        Assert.True(registry.TryGet("CPU", out _)); // case-insensitive
    }

    [Fact]
    public void Default_layout_only_references_registered_widgets()
    {
        var registry = new WidgetRegistry(BuiltInWidgets.All);
        var layout = Core.Models.DockLayout.CreateDefault();

        Assert.All(layout.CenterWidgets.Concat(layout.RightWidgets), w => Assert.True(registry.TryGet(w.TypeId, out _), w.TypeId));
    }
}
