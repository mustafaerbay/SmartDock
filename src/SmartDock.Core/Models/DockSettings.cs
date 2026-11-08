namespace SmartDock.Core.Models;

public enum DockEdge
{
    Bottom,
    Top,
}

public enum TaskbarMode
{
    /// <summary>Windows taskbar stays visible; the dock floats independently. Default.</summary>
    Companion,

    /// <summary>Windows taskbar is temporarily hidden while SmartDock runs.</summary>
    Replacement,
}

public enum FullscreenBehavior
{
    /// <summary>Hide the dock while a fullscreen application is in the foreground on the dock's monitor.</summary>
    Hide,

    /// <summary>Keep the dock visible on top of fullscreen applications.</summary>
    StayVisible,
}

/// <summary>Root of settings.json.</summary>
public sealed class DockSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppearanceSettings Appearance { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    /// <summary>Clamps every value into a usable range so a hand-edited or partially corrupt file cannot break layout.</summary>
    public DockSettings Normalize()
    {
        Appearance ??= new AppearanceSettings();
        Behavior ??= new BehaviorSettings();
        Updates ??= new UpdateSettings();
        Appearance.Normalize();
        Behavior.Normalize();
        Updates.Normalize();
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    public DockSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Appearance = Appearance.Clone(),
        Behavior = Behavior.Clone(),
        Updates = Updates.Clone(),
    };
}

/// <summary>Where SmartDock looks for new versions (nothing is checked until a feed is set).</summary>
public sealed class UpdateSettings
{
    /// <summary>
    /// Release folder containing latest.json and the MSI: https://host/smartdock/, ssh://user@host/srv/smartdock,
    /// \\server\share\smartdock or a local folder. Empty = updates off.
    /// </summary>
    public string FeedLocation { get; set; } = string.Empty;

    /// <summary>Check at start-up, every <see cref="CheckIntervalMinutes"/>, and after resume/unlock.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>Minutes between automatic checks (15–1440). Replaces the old 6-hour CheckIntervalHours.</summary>
    public double CheckIntervalMinutes { get; set; } = 60;

    /// <summary>Plain http:// feeds are refused unless this is set (trusted LAN only).</summary>
    public bool AllowUnencryptedHttp { get; set; }

    /// <summary>Optional private key for ssh:// feeds; empty = the user's default OpenSSH keys/agent.</summary>
    public string SshKeyPath { get; set; } = string.Empty;

    public void Normalize()
    {
        FeedLocation = (FeedLocation ?? string.Empty).Trim();
        SshKeyPath = (SshKeyPath ?? string.Empty).Trim();
        if (double.IsNaN(CheckIntervalMinutes) || CheckIntervalMinutes < 15 || CheckIntervalMinutes > 1440)
        {
            CheckIntervalMinutes = 60;
        }
    }

    public UpdateSettings Clone() => (UpdateSettings)MemberwiseClone();
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = ThemeNames.Midnight;

    public double BackgroundOpacity { get; set; } = 0.92;

    public double CornerRadius { get; set; } = 12;

    public double WidgetSpacing { get; set; } = 5;

    public double DockHeight { get; set; } = 52;

    public double WidgetHeight { get; set; } = 40;

    public double HorizontalPadding { get; set; } = 8;

    public double VerticalPadding { get; set; } = 6;

    /// <summary>Accent colour as #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#7C5CFF";

    /// <summary>0 disables the shadow entirely (cheapest rendering), 1 is the strongest.</summary>
    public double ShadowIntensity { get; set; } = 0.5;

    /// <summary>Overall dock size: scales everything on the dock (icons, text, widgets, height). 1.0 = 100%.</summary>
    public double DockScale { get; set; } = 1.0;

    public void Normalize()
    {
        if (!ThemeNames.All.Contains(Theme, StringComparer.OrdinalIgnoreCase))
        {
            Theme = ThemeNames.Midnight;
        }

        BackgroundOpacity = Clamp(BackgroundOpacity, 0.3, 1.0, 0.92);
        CornerRadius = Clamp(CornerRadius, 0, 26, 12);
        WidgetSpacing = Clamp(WidgetSpacing, 0, 24, 5);
        DockHeight = Clamp(DockHeight, 36, 96, 52);
        VerticalPadding = Clamp(VerticalPadding, 0, 20, 6);
        HorizontalPadding = Clamp(HorizontalPadding, 0, 32, 8);

        // The widget must fit inside the dock after vertical padding.
        WidgetHeight = Clamp(WidgetHeight, 24, DockHeight - (2 * VerticalPadding), 40);
        ShadowIntensity = Clamp(ShadowIntensity, 0, 1, 0.5);
        DockScale = Clamp(DockScale, 0.6, 1.5, 1.0);

        if (!ColorParsing.TryParseHexRgb(AccentColor, out _, out _, out _))
        {
            AccentColor = "#7C5CFF";
        }
    }

    public AppearanceSettings Clone() => (AppearanceSettings)MemberwiseClone();

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(value, min, max);
    }
}

public sealed class BehaviorSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    public bool AutoHide { get; set; }

    /// <summary>
    /// Stick to the screen edge: reserve the dock's height in the work area (Windows AppBar) so maximised
    /// windows stop at the dock instead of going under it. Ignored while <see cref="AutoHide"/> is on.
    /// </summary>
    public bool ReserveSpace { get; set; } = true;

    /// <summary>Blink an app's dock icon when it requests attention (e.g. a new chat message), like the taskbar does.</summary>
    public bool ShowAttention { get; set; } = true;

    /// <summary>Show live thumbnails of an app's windows when hovering its dock icon.</summary>
    public bool ShowWindowPreviews { get; set; } = true;

    /// <summary>Span the whole screen width like the Windows taskbar instead of a centred, content-sized bar.</summary>
    public bool FullWidth { get; set; }

    /// <summary>When apps/widgets don't fit on one line, continue on a second (third…) row instead of clipping.</summary>
    public bool WrapWhenFull { get; set; } = true;

    public TaskbarMode Mode { get; set; } = TaskbarMode.Companion;

    public bool StartWithWindows { get; set; }

    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.Hide;

    /// <summary>GDI device name of the target monitor (e.g. \\.\DISPLAY2); null means the primary monitor.</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>Gap in DIPs between the dock and the screen edge (or the Windows taskbar in companion mode).</summary>
    public double EdgeMargin { get; set; } = 8;

    /// <summary>Gap in DIPs between the dock and maximised windows; used when <see cref="ReserveSpace"/> is on.</summary>
    public double WindowGap { get; set; } = 8;

    public void Normalize()
    {
        if (!Enum.IsDefined(Edge))
        {
            Edge = DockEdge.Bottom;
        }

        if (!Enum.IsDefined(Mode))
        {
            Mode = TaskbarMode.Companion;
        }

        if (!Enum.IsDefined(Fullscreen))
        {
            Fullscreen = FullscreenBehavior.Hide;
        }

        if (double.IsNaN(EdgeMargin) || EdgeMargin < 0 || EdgeMargin > 64)
        {
            EdgeMargin = 8;
        }

        if (double.IsNaN(WindowGap) || WindowGap < 0 || WindowGap > 64)
        {
            WindowGap = 8;
        }

        if (string.IsNullOrWhiteSpace(MonitorDeviceName))
        {
            MonitorDeviceName = null;
        }
    }

    public BehaviorSettings Clone() => (BehaviorSettings)MemberwiseClone();
}

public static class ThemeNames
{
    public const string Midnight = "Midnight";
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string FrostedGlass = "Frosted Glass";
    public const string ColorfulGlass = "Colorful Glass";

    public static IReadOnlyList<string> All { get; } = [Midnight, Dark, Light, FrostedGlass, ColorfulGlass];
}

public static class ColorParsing
{
    public static bool TryParseHexRgb(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim().TrimStart('#');
        if (s.Length != 6)
        {
            return false;
        }

        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        r = (byte)((rgb >> 16) & 0xFF);
        g = (byte)((rgb >> 8) & 0xFF);
        b = (byte)(rgb & 0xFF);
        return true;
    }
}
