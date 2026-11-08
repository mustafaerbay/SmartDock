namespace SmartDock.Core.Interfaces;

/// <summary>Raw cumulative CPU times in 100ns ticks. On Windows <see cref="Kernel"/> includes idle time.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes)
{
    public ulong UsedBytes => TotalBytes >= AvailableBytes ? TotalBytes - AvailableBytes : 0;

    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <summary>Cumulative byte counters across active, non-loopback interfaces.</summary>
public readonly record struct NetworkCounters(long BytesReceived, long BytesSent, bool IsConnected);

public readonly record struct BatteryStatus(bool HasBattery, int? Percent, bool IsCharging, bool OnAcPower, TimeSpan? Remaining);

public readonly record struct DiskStatus(string RootPath, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <summary>Read-only access to OS metrics. Implementations must be cheap and non-blocking.</summary>
public interface ISystemMetrics
{
    bool TryGetCpuTimes(out CpuTimes times);

    MemoryStatus? GetMemoryStatus();

    NetworkCounters GetNetworkCounters();

    BatteryStatus GetBatteryStatus();

    DiskStatus? GetDisk(string rootPath);

    IReadOnlyList<string> GetFixedDriveRoots();
}

public sealed record MediaSnapshot(
    string? Title,
    string? Artist,
    string? AlbumTitle,
    string? SourceAppId,
    bool IsPlaying,
    bool CanGoNext,
    bool CanGoPrevious,
    bool CanPlayPause,
    byte[]? Thumbnail);

/// <summary>Abstraction over the OS media session (GSMTC on Windows). All members are thread-safe.</summary>
public interface IMediaService
{
    /// <summary>False when the platform API is unavailable (e.g. service failed to start).</summary>
    bool IsSupported { get; }

    /// <summary>Null when no media session exists.</summary>
    MediaSnapshot? Current { get; }

    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> TogglePlayPauseAsync();

    Task<bool> PauseAsync();

    Task<bool> NextAsync();

    Task<bool> PreviousAsync();
}
