using System.Windows;
using System.Windows.Media.Animation;

namespace SmartDock.App.Services;

/// <summary>Honours the Windows "Animation effects" setting (Settings › Accessibility › Visual effects).</summary>
public static class AnimationSettings
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static IEasingFunction Ease { get; } = CreateEase();

    public static Duration Duration(int milliseconds) =>
        new(TimeSpan.FromMilliseconds(Enabled ? milliseconds : 0));

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}

/// <summary>App-wide UI commands reachable from controls and the tray without passing the App around.</summary>
public static class AppCommands
{
    public static Action ShowWidgetManager { get; set; } = static () => { };

    public static Action ShowSettings { get; set; } = static () => { };

    /// <summary>Opens the generic configuration dialog for a widget instance.</summary>
    public static Action<string> ConfigureWidget { get; set; } = static _ => { };
}
