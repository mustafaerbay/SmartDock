using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.SystemMonitor;

public static class CpuUsageCalculator
{
    /// <summary>Busy percentage between two GetSystemTimes samples (kernel time includes idle time on Windows).</summary>
    public static double? Compute(CpuTimes previous, CpuTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (current.Idle < previous.Idle || total == 0 || idle > total)
        {
            return null;
        }

        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}

/// <summary>Shared refresh-interval setting and menu for the metric widgets.</summary>
public abstract class MetricWidgetBase : WidgetBase
{
    private readonly double _defaultSeconds;
    private double _percent;
    private string _valueText = "–";
    private IReadOnlyList<double> _history = [];

    protected MetricWidgetBase(double defaultSeconds)
    {
        _defaultSeconds = defaultSeconds;
    }

    public double Percent
    {
        get => _percent;
        protected set => SetProperty(ref _percent, value);
    }

    public string ValueText
    {
        get => _valueText;
        protected set => SetProperty(ref _valueText, value);
    }

    public IReadOnlyList<double> History
    {
        get => _history;
        protected set => SetProperty(ref _history, value);
    }

    public bool ShowGraph => Settings.Get("showGraph", true);

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(Settings.Get("refreshSeconds", _defaultSeconds), 1, 300));

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Show graph", new RelayCommand(() => Settings.Set("showGraph", !ShowGraph)), ShowGraph, true),
        new("Refresh every 1 s", new RelayCommand(() => Settings.Set("refreshSeconds", 1.0)), RefreshInterval == TimeSpan.FromSeconds(1), true),
        new("Refresh every 2 s", new RelayCommand(() => Settings.Set("refreshSeconds", 2.0)), RefreshInterval == TimeSpan.FromSeconds(2), true),
        new("Refresh every 5 s", new RelayCommand(() => Settings.Set("refreshSeconds", 5.0)), RefreshInterval == TimeSpan.FromSeconds(5), true),
    ];

    protected override void OnSettingChanged(string key)
    {
        OnPropertyChanged(nameof(ShowGraph));
        base.OnSettingChanged(key);
    }
}

public sealed class CpuWidget(ISystemMetrics metrics) : MetricWidgetBase(2), IPopupWidget
{
    /// <summary>Top 10 processes by CPU, live while the popup is open.</summary>
    public TopProcesses Top { get; } = new(TopProcessOrder.Cpu);

    public void OnPopupOpened() => Top.Start();

    public void OnPopupClosed() => Top.Stop();

    protected override ValueTask OnDisposeAsync()
    {
        Top.Stop();
        return base.OnDisposeAsync();
    }

    public const string TypeId = "cpu";

    private readonly SampleHistory _samples = new(30);
    private CpuTimes? _previous;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "CPU",
        "Processor utilisation with a compact history graph.",
        WidgetCategory.System,
        "\uE9D9",
        DockRegion.Center,
        sp => new CpuWidget(sp.GetRequiredService<ISystemMetrics>()));

    public string ProcessorInfo { get; } = $"{Environment.ProcessorCount} logical processors";

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        if (!metrics.TryGetCpuTimes(out var now))
        {
            throw new InvalidOperationException("CPU counters unavailable.");
        }

        if (_previous is { } prev && CpuUsageCalculator.Compute(prev, now) is { } usage)
        {
            Percent = usage;
            ValueText = Format.Percent(usage);
            History = _samples.Add(usage);
        }

        _previous = now;
        return Task.CompletedTask;
    }
}

public sealed class MemoryWidget(ISystemMetrics metrics) : MetricWidgetBase(3), IPopupWidget
{
    /// <summary>Top 10 processes by memory (working set), live while the popup is open.</summary>
    public TopProcesses Top { get; } = new(TopProcessOrder.Memory);

    public void OnPopupOpened() => Top.Start();

    public void OnPopupClosed() => Top.Stop();

    protected override ValueTask OnDisposeAsync()
    {
        Top.Stop();
        return base.OnDisposeAsync();
    }

    public const string TypeId = "memory";

    private readonly SampleHistory _samples = new(30);
    private string _detailText = string.Empty;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Memory",
        "RAM in use, used/available amounts and a usage meter.",
        WidgetCategory.System,
        "\uE964",
        DockRegion.Center,
        sp => new MemoryWidget(sp.GetRequiredService<ISystemMetrics>()));

    /// <summary>e.g. "9.8 GB of 16 GB · 6.2 GB free".</summary>
    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var status = metrics.GetMemoryStatus() ?? throw new InvalidOperationException("Memory status unavailable.");
        Percent = status.UsedPercent;
        ValueText = Format.Percent(status.UsedPercent);
        DetailText = $"{Format.Bytes(status.UsedBytes)} of {Format.Bytes(status.TotalBytes)} · {Format.Bytes(status.AvailableBytes)} free";
        History = _samples.Add(status.UsedPercent);
        return Task.CompletedTask;
    }
}

public sealed class DiskWidget(ISystemMetrics metrics) : MetricWidgetBase(60)
{
    public const string TypeId = "disk";

    private string _driveLabel = string.Empty;
    private string _freeText = string.Empty;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Disk",
        "Used and free space for a selected drive.",
        WidgetCategory.System,
        "\uEDA2",
        DockRegion.Center,
        sp => new DiskWidget(sp.GetRequiredService<ISystemMetrics>()))
    {
        AllowMultiple = true,
    };

    public string Drive => Settings.Get("drive", Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : "C:\\");

    public string DriveLabel
    {
        get => _driveLabel;
        private set => SetProperty(ref _driveLabel, value);
    }

    public string FreeText
    {
        get => _freeText;
        private set => SetProperty(ref _freeText, value);
    }

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
        metrics.GetFixedDriveRoots()
            .Select(root => new WidgetMenuItem($"Drive {root}", new RelayCommand(() => Settings.Set("drive", root)), string.Equals(root, Drive, StringComparison.OrdinalIgnoreCase), true))
            .ToList();

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var disk = metrics.GetDisk(Drive);
        DriveLabel = Drive.TrimEnd('\\');
        if (disk is not { } d)
        {
            SetUnavailable($"Drive {Drive} is not available");
            ValueText = "–";
            FreeText = string.Empty;
            return Task.CompletedTask;
        }

        SetReady();
        Percent = d.UsedPercent;
        ValueText = Format.Percent(d.UsedPercent);
        FreeText = $"{Format.Bytes(d.FreeBytes, 0)} free";
        StatusMessage = $"{d.Label} {Format.Bytes(d.UsedBytes)} used of {Format.Bytes(d.TotalBytes)}".Trim();
        return Task.CompletedTask;
    }
}

public sealed class BatteryWidget(ISystemMetrics metrics) : WidgetBase
{
    public const string TypeId = "battery";

    private int? _percent;
    private bool _isCharging;
    private bool _onAc;
    private bool _hasBattery = true;
    private string _detail = string.Empty;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Battery",
        "Battery level, charging state and remaining time. Shows AC power on desktops.",
        WidgetCategory.System,
        "\uE83F",
        DockRegion.Right,
        sp => new BatteryWidget(sp.GetRequiredService<ISystemMetrics>()));

    public int? Percent
    {
        get => _percent;
        private set
        {
            if (SetProperty(ref _percent, value))
            {
                OnPropertyChanged(nameof(LevelGlyph));
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(IsLow));
            }
        }
    }

    public string PercentText => HasBattery ? (Percent is { } p ? $"{p}%" : "–") : "AC";

    public bool IsCharging
    {
        get => _isCharging;
        private set => SetProperty(ref _isCharging, value);
    }

    public bool OnAcPower
    {
        get => _onAc;
        private set => SetProperty(ref _onAc, value);
    }

    public bool HasBattery
    {
        get => _hasBattery;
        private set
        {
            if (SetProperty(ref _hasBattery, value))
            {
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(LevelGlyph));
            }
        }
    }

    public bool IsLow => HasBattery && !IsCharging && Percent is <= 15;

    /// <summary>Segoe Fluent battery glyphs E850 (empty) … E85A (full); plug glyph without a battery.</summary>
    public string LevelGlyph => !HasBattery
        ? "\uE83E"
        : ((char)(0xE850 + Math.Clamp((Percent ?? 0) / 10, 0, 10))).ToString();

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(20);

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var s = metrics.GetBatteryStatus();
        HasBattery = s.HasBattery;
        Percent = s.Percent;
        IsCharging = s.IsCharging;
        OnAcPower = s.OnAcPower;
        OnPropertyChanged(nameof(IsLow));

        Detail = !s.HasBattery
            ? "No battery – running on AC power"
            : s.IsCharging
                ? "Charging"
                : s.Remaining is { } r
                    ? $"{(int)r.TotalHours} h {r.Minutes:D2} min remaining"
                    : s.OnAcPower ? "Plugged in" : "On battery";
        return Task.CompletedTask;
    }
}
