using System.Reflection;

namespace SmartDock.Core;

/// <summary>Release version of the running build, from &lt;Version&gt; in Directory.Build.props.</summary>
public static class AppInfo
{
    /// <summary>SemVer without build metadata, e.g. "1.0.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Display form, e.g. "v1.0.0".</summary>
    public static string DisplayVersion => "v" + Version;

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var v = assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
