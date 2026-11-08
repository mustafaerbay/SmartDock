using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Network;

public static class NetworkRateCalculator
{
    /// <summary>Bytes/second between two counter samples; null when the counters reset (adapter change).</summary>
    public static (double Down, double Up)? Compute(NetworkCounters previous, DateTimeOffset previousAt, NetworkCounters current, DateTimeOffset currentAt)
    {
        var seconds = (currentAt - previousAt).TotalSeconds;
        var down = current.BytesReceived - previous.BytesReceived;
        var up = current.BytesSent - previous.BytesSent;
        if (seconds <= 0 || down < 0 || up < 0)
        {
            return null;
        }

        return (down / seconds, up / seconds);
    }
}

public sealed class NetworkWidget(ISystemMetrics metrics, TimeProvider? time = null) : WidgetBase
{
    public const string TypeId = "network";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SampleHistory _downHistory = new(30);
    private NetworkCounters? _previous;
    private DateTimeOffset _previousAt;
    private string _downText = "–";
    private string _upText = "–";
    private bool _isConnected = true;
    private IReadOnlyList<double> _history = [];

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Network",
        "Live download/upload speed and connectivity.",
        WidgetCategory.Network,
        "",
        DockRegion.Right,
        sp => new NetworkWidget(sp.GetRequiredService<ISystemMetrics>()));

    public string DownText
    {
        get => _downText;
        private set => SetProperty(ref _downText, value);
    }

    public string UpText
    {
        get => _upText;
        private set => SetProperty(ref _upText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>Download history normalised to 0..100 of the window's peak, for the sparkline.</summary>
    public IReadOnlyList<double> History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var counters = metrics.GetNetworkCounters();
        IsConnected = counters.IsConnected;

        if (_previous is { } prev && NetworkRateCalculator.Compute(prev, _previousAt, counters, now) is { } rate)
        {
            DownText = Format.Rate(rate.Down);
            UpText = Format.Rate(rate.Up);
            var raw = _downHistory.Add(rate.Down);
            var peak = Math.Max(raw.Max(), 1);
            History = raw.Select(v => 100 * v / peak).ToArray();
        }

        _previous = counters;
        _previousAt = now;

        if (!counters.IsConnected)
        {
            SetDisconnected("No network connection");
            DownText = "Offline";
            UpText = string.Empty;
        }
        else
        {
            SetReady();
        }

        return Task.CompletedTask;
    }
}
