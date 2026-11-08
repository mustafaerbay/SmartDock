using System.Runtime.InteropServices;
using System.Text;
using SmartDock.Core.Services;
using SmartDock.Windows.Interop;

namespace SmartDock.Windows.SystemIntegration;

/// <summary>
/// Stores secrets (e.g. a GitHub token) encrypted with DPAPI for the current Windows user,
/// one file per secret under %LOCALAPPDATA%\SmartDock\secrets. Nothing is written in plaintext.
/// </summary>
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var plain = Unprotect(File.ReadAllBytes(path));
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    public void Set(string name, string? value)
    {
        var path = PathFor(name);
        if (value is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        var cipher = Protect(Encoding.UTF8.GetBytes(value)) ?? throw new InvalidOperationException("DPAPI encryption failed.");
        File.WriteAllBytes(path, cipher);
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(directory, safe + ".bin");
    }

    private static byte[]? Protect(byte[] data) => Transform(data, protect: true);

    private static byte[]? Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
            var ok = protect
                ? NativeMethods.CryptProtectData(ref input, "SmartDock", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : NativeMethods.CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
            {
                return null;
            }

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                NativeMethods.LocalFree(output.pbData);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
