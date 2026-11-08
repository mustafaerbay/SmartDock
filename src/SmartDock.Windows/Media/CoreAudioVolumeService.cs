using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Interfaces;

namespace SmartDock.Windows.Media;

/// <summary>
/// Master volume of the default render endpoint through Windows Core Audio (IMMDeviceEnumerator /
/// IAudioEndpointVolume). The endpoint is re-resolved on every call so switching the default output
/// device (headphones, HDMI) is picked up automatically.
/// </summary>
public sealed class CoreAudioVolumeService(ILogger<CoreAudioVolumeService> logger) : IVolumeService
{
    private static Guid _eventContext = Guid.NewGuid();
    private readonly Lock _gate = new();

    public VolumeState? GetState()
    {
        lock (_gate)
        {
            return WithEndpoint((volume, name) =>
            {
                Check(volume.GetMasterVolumeLevelScalar(out var level));
                Check(volume.GetMute(out var muted));
                return (VolumeState?)new VolumeState(level, muted, name);
            });
        }
    }

    public bool SetLevel(float level)
    {
        lock (_gate)
        {
            return WithEndpoint((volume, _) =>
            {
                Check(volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _eventContext));
                return (bool?)true;
            }) ?? false;
        }
    }

    public bool SetMute(bool muted)
    {
        lock (_gate)
        {
            return WithEndpoint((volume, _) =>
            {
                Check(volume.SetMute(muted, ref _eventContext));
                return (bool?)true;
            }) ?? false;
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private T? WithEndpoint<T>(Func<IAudioEndpointVolume, string, T?> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
            if (hr < 0 || device is null)
            {
                return default; // No output device (e.g. none plugged in).
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out endpointObject));
            return action((IAudioEndpointVolume)endpointObject, ReadFriendlyName(device));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Core Audio call failed");
            return default;
        }
        finally
        {
            Release(endpointObject);
            Release(device);
            Release(enumerator);
        }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) < 0 || store is null)
        {
            return "Speakers";
        }

        try
        {
            var key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return "Speakers";
            }

            try
            {
                return value.vt == VtLpwstr && value.pointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.pointerValue) ?? "Speakers"
                    : "Speakers";
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    // ---------------- COM interop ----------------
    private const int ClsCtxAll = 23;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;

    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private enum EDataFlow
    {
        Render = 0,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, int pid)
    {
        public Guid FormatId = fmtid;
        public int PropertyId = pid;
    }

    /// <summary>Only the fields needed for a VT_LPWSTR value; the union is pointer-sized at offset 8.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointerValue;
        public IntPtr padding;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetAt(int index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
