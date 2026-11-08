namespace SmartDock.Infrastructure.Configuration;

/// <summary>Locations of SmartDock's per-user files (default: %LOCALAPPDATA%\SmartDock).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartDock");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LayoutFile => Path.Combine(Root, "layout.json");

    public string SecretsDirectory => Path.Combine(Root, "secrets");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Written while the Windows taskbar is hidden; its presence at startup means a previous run did not restore it.</summary>
    public string TaskbarRecoveryFile => Path.Combine(Root, "taskbar-recovery.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
