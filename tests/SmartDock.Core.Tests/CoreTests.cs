using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartDock.Core.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class TestWidget(TimeSpan interval, Func<int, Task>? onRefresh = null) : WidgetBase
{
    public int Refreshes { get; private set; }

    public override TimeSpan RefreshInterval => interval;

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        Refreshes++;
        if (onRefresh is not null)
        {
            await onRefresh(Refreshes);
        }
    }
}

internal static class Contexts
{
    public static readonly WidgetDescriptor Descriptor = new("test", "Test", "", WidgetCategory.Other, "", DockRegion.Center, _ => throw new NotSupportedException());

    public static WidgetContext Create(string id = "w1") =>
        new(id, Descriptor, new WidgetSettings([]), new EventBus(), new NotificationService(), NullLogger.Instance);
}

public class WidgetHostTests
{
    [Fact]
    public async Task Refreshes_widget_on_its_interval_from_a_single_scheduler()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = new WidgetHost(timeProvider: time);
        var widget = new TestWidget(TimeSpan.FromSeconds(5));
        await host.AddAsync(widget, Contexts.Create());

        await host.TickAsync();
        Assert.Equal(1, widget.Refreshes);
        Assert.Equal(WidgetState.Ready, widget.State);

        time.Advance(TimeSpan.FromSeconds(2));
        await host.TickAsync();
        Assert.Equal(1, widget.Refreshes);

        time.Advance(TimeSpan.FromSeconds(3));
        await host.TickAsync();
        Assert.Equal(2, widget.Refreshes);
    }

    [Fact]
    public async Task Failing_widget_is_isolated_and_backs_off()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = new WidgetHost(timeProvider: time);
        var bad = new TestWidget(TimeSpan.FromSeconds(2), _ => throw new InvalidOperationException("boom"));
        var good = new TestWidget(TimeSpan.FromSeconds(2));
        await host.AddAsync(bad, Contexts.Create("bad"));
        await host.AddAsync(good, Contexts.Create("good"));

        await host.TickAsync();
        Assert.Equal(WidgetState.Error, bad.State);
        Assert.Equal("boom", bad.StatusMessage);
        Assert.Equal(WidgetState.Ready, good.State);

        // Normal interval elapsed, but the failed widget waits for its backoff (2s * 2^1 = 4s).
        time.Advance(TimeSpan.FromSeconds(2));
        await host.TickAsync();
        Assert.Equal(1, bad.Refreshes);
        Assert.Equal(2, good.Refreshes);

        time.Advance(TimeSpan.FromSeconds(2));
        await host.TickAsync();
        Assert.Equal(2, bad.Refreshes);
    }

    [Fact]
    public async Task Widget_recovers_from_error_after_successful_refresh()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = new WidgetHost(timeProvider: time);
        var widget = new TestWidget(TimeSpan.FromSeconds(1), n => n == 1 ? throw new IOException("offline") : Task.CompletedTask);
        await host.AddAsync(widget, Contexts.Create());

        await host.TickAsync();
        Assert.Equal(WidgetState.Error, widget.State);

        time.Advance(WidgetHost.ComputeBackoff(TimeSpan.FromSeconds(1), 1));
        await host.TickAsync();
        Assert.Equal(WidgetState.Ready, widget.State);
        Assert.Null(widget.StatusMessage);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Backoff_doubles_per_failure(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WidgetHost.ComputeBackoff(TimeSpan.FromSeconds(1), failures));

    [Fact]
    public void Backoff_is_capped() =>
        Assert.Equal(WidgetHost.MaxBackoff, WidgetHost.ComputeBackoff(TimeSpan.FromMinutes(1), 10));

    [Fact]
    public async Task Removed_widget_is_disposed_and_no_longer_refreshed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = new WidgetHost(timeProvider: time);
        var widget = new TestWidget(TimeSpan.FromSeconds(1));
        await host.AddAsync(widget, Contexts.Create());
        await host.RemoveAsync("w1");

        await host.TickAsync();
        Assert.Equal(0, widget.Refreshes);
        Assert.Equal(0, host.Count);
    }
}

public class EventBusTests
{
    [Fact]
    public void Faulty_subscriber_does_not_stop_others()
    {
        var bus = new EventBus();
        var received = 0;
        using var a = bus.Subscribe<string>(_ => throw new InvalidOperationException());
        using var b = bus.Subscribe<string>(_ => received++);

        bus.Publish("x");

        Assert.Equal(1, received);
    }

    [Fact]
    public void Disposed_subscription_stops_receiving()
    {
        var bus = new EventBus();
        var received = 0;
        var sub = bus.Subscribe<int>(_ => received++);
        bus.Publish(1);
        sub.Dispose();
        bus.Publish(2);

        Assert.Equal(1, received);
    }
}

public class NotificationServiceTests
{
    [Fact]
    public void Dedupe_key_suppresses_repeats_within_window()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new NotificationService(time);

        Assert.NotNull(service.Post("Stars", "1000!", dedupeKey: "stars-1000"));
        Assert.Null(service.Post("Stars", "1000!", dedupeKey: "stars-1000"));

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.NotNull(service.Post("Stars", "1000!", dedupeKey: "stars-1000"));
        Assert.Equal(2, service.History.Count);
    }

    [Fact]
    public void History_is_capped_and_newest_first()
    {
        var service = new NotificationService();
        for (var i = 0; i < NotificationService.MaxHistory + 5; i++)
        {
            service.Post("t", i.ToString());
        }

        Assert.Equal(NotificationService.MaxHistory, service.History.Count);
        Assert.Equal((NotificationService.MaxHistory + 4).ToString(), service.History[0].Message);
    }
}

public class SettingsAndLayoutTests
{
    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var s = new DockSettings
        {
            Appearance = new AppearanceSettings { BackgroundOpacity = 7, DockHeight = 10, CornerRadius = double.NaN, Theme = "Nope", AccentColor = "red" },
        }.Normalize();

        Assert.Equal(1.0, s.Appearance.BackgroundOpacity);
        Assert.Equal(36, s.Appearance.DockHeight);
        Assert.Equal(12, s.Appearance.CornerRadius);
        Assert.Equal(ThemeNames.Midnight, s.Appearance.Theme);
        Assert.Equal("#7C5CFF", s.Appearance.AccentColor);
        Assert.True(s.Appearance.WidgetHeight <= s.Appearance.DockHeight);
    }

    [Fact]
    public void Layout_move_between_regions_preserves_item()
    {
        var layout = DockLayout.CreateDefault();
        var id = layout.CenterWidgets[0].InstanceId;

        Assert.True(layout.Move(id, DockRegion.Right, 0));

        Assert.Equal(id, layout.RightWidgets[0].InstanceId);
        Assert.DoesNotContain(layout.CenterWidgets, w => w.InstanceId == id);
    }

    [Fact]
    public void Normalize_drops_unknown_widgets_and_fixes_duplicate_ids()
    {
        var layout = new DockLayout
        {
            CenterWidgets =
            [
                new() { InstanceId = "a", TypeId = "cpu" },
                new() { InstanceId = "a", TypeId = "cpu" },
                new() { InstanceId = "x", TypeId = "removed-widget" },
            ],
        }.Normalize(t => t == "cpu");

        Assert.Equal(2, layout.CenterWidgets.Count);
        Assert.NotEqual(layout.CenterWidgets[0].InstanceId, layout.CenterWidgets[1].InstanceId);
    }

    [Fact]
    public void Corrupt_settings_file_is_quarantined_and_defaults_are_used()
    {
        var dir = Directory.CreateTempSubdirectory("smartdock-test");
        try
        {
            var paths = new AppPaths(dir.FullName);
            paths.EnsureCreated();
            File.WriteAllText(paths.SettingsFile, "{ this is not json");

            var service = new SettingsService(paths, NullLogger<SettingsService>.Instance);

            Assert.Equal(ThemeNames.Midnight, service.Current.Appearance.Theme);
            Assert.Single(Directory.GetFiles(dir.FullName, "settings.corrupt-*.json"));
            Assert.True(File.Exists(paths.SettingsFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var dir = Directory.CreateTempSubdirectory("smartdock-test");
        try
        {
            var paths = new AppPaths(dir.FullName);
            paths.EnsureCreated();
            var first = new SettingsService(paths, NullLogger<SettingsService>.Instance);
            first.Update(s =>
            {
                s.Appearance.Theme = ThemeNames.Light;
                s.Behavior.Mode = TaskbarMode.Replacement;
            });

            var second = new SettingsService(paths, NullLogger<SettingsService>.Instance);
            Assert.Equal(ThemeNames.Light, second.Current.Appearance.Theme);
            Assert.Equal(TaskbarMode.Replacement, second.Current.Behavior.Mode);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Widget_settings_fall_back_to_default_on_wrong_type()
    {
        var settings = new WidgetSettings([]);
        settings.Set("refreshSeconds", "not a number");

        Assert.Equal(2.0, settings.Get("refreshSeconds", 2.0));
    }
}
