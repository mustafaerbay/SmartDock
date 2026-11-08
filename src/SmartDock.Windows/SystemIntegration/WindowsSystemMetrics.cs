using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SmartDock.Core.Interfaces;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>ISystemMetrics backed by cheap Win32 calls (no WMI, no PerformanceCounter warm-up).</summary>
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    public bool TryGetCpuTimes(out CpuTimes times)
    {
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            times = new CpuTimes(idle.Value, kernel.Value, user.Value);
            return true;
        }

        times = default;
        return false;
    }

    public MemoryStatus? GetMemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? new MemoryStatus(status.ullTotalPhys, status.ullAvailPhys)
            : null;
    }

    public NetworkCounters GetNetworkCounters()
    {
        long received = 0;
        long sent = 0;
        var connected = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                connected = true;
            }
            catch (NetworkInformationException)
            {
                // Interface disappeared between enumeration and query.
            }
        }

        return new NetworkCounters(received, sent, connected && NetworkInterface.GetIsNetworkAvailable());
    }

    public BatteryStatus GetBatteryStatus()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s))
        {
            return new BatteryStatus(false, null, false, true, null);
        }

        const byte NoSystemBattery = 128;
        const byte UnknownStatus = 255;
        const byte Charging = 8;

        var hasBattery = s.BatteryFlag != UnknownStatus && (s.BatteryFlag & NoSystemBattery) == 0;
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? remaining = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new BatteryStatus(hasBattery, percent, hasBattery && (s.BatteryFlag & Charging) != 0, s.ACLineStatus == 1, remaining);
    }

    public DiskStatus? GetDisk(string rootPath)
    {
        try
        {
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady)
            {
                return null;
            }

            return new DiskStatus(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetFixedDriveRoots() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
}
