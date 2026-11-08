using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Models;
using SmartDock.App.Services;
using SmartDock.Core.Services;
using SmartDock.Windows.SystemIntegration;

namespace SmartDock.App.ViewModels;

public sealed record Option<T>(T Value, string Label);

/// <summary>
/// Two-way view over ISettingsService. Every setter writes through immediately, so the dock updates
/// live (theme, size, position, mode) without an Apply button or restart.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private DockSettings _s;

    public SettingsViewModel(ISettingsService settings, UpdateCoordinator updates)
    {
        _settings = settings;
        Updates = updates;
        _s = settings.Current;
        ResetAppearanceCommand = new RelayCommand(() => Update(s => s.Appearance = new AppearanceSettings()));
        RefreshMonitorsCommand = new RelayCommand(LoadMonitors);
        LoadMonitors();
    }

    public IReadOnlyList<string> Themes => ThemeNames.All;

    public IReadOnlyList<string> AccentPresets { get; } = ["#7C5CFF", "#4F8CFF", "#2FC496", "#F5B32E", "#F0566C", "#E25CD6", "#16B8D9", "#9AA3B5"];

    public IReadOnlyList<Option<DockEdge>> Edges { get; } = [new(DockEdge.Bottom, "Bottom"), new(DockEdge.Top, "Top")];

    public IReadOnlyList<Option<TaskbarMode>> Modes { get; } =
    [
        new(TaskbarMode.Companion, "Companion – keep the Windows taskbar"),
        new(TaskbarMode.Replacement, "Replacement – hide the Windows taskbar while SmartDock runs"),
    ];

    public IReadOnlyList<Option<FullscreenBehavior>> FullscreenOptions { get; } =
    [
        new(FullscreenBehavior.Hide, "Hide the dock over fullscreen apps"),
        new(FullscreenBehavior.StayVisible, "Keep the dock visible"),
    ];

    public IReadOnlyList<Option<string?>> Monitors { get; private set; } = [];

    /// <summary>Update status and Check/Install commands (Settings › Updates).</summary>
    public UpdateCoordinator Updates { get; }

    public string UpdateFeedLocation
    {
        get => _s.Updates.FeedLocation;
        set => Update(s => s.Updates.FeedLocation = value ?? string.Empty);
    }

    public bool UpdateCheckAutomatically
    {
        get => _s.Updates.CheckAutomatically;
        set => Update(s => s.Updates.CheckAutomatically = value);
    }

    /// <summary>Minutes between automatic update checks.</summary>
    public double UpdateCheckIntervalMinutes
    {
        get => _s.Updates.CheckIntervalMinutes;
        set => Update(s => s.Updates.CheckIntervalMinutes = Math.Round(value / 15) * 15);
    }

    public bool UpdateAllowHttp
    {
        get => _s.Updates.AllowUnencryptedHttp;
        set => Update(s => s.Updates.AllowUnencryptedHttp = value);
    }

    public string UpdateSshKeyPath
    {
        get => _s.Updates.SshKeyPath;
        set => Update(s => s.Updates.SshKeyPath = value ?? string.Empty);
    }

    public RelayCommand ResetAppearanceCommand { get; }

    public RelayCommand RefreshMonitorsCommand { get; }

    public string Theme
    {
        get => _s.Appearance.Theme;
        set => Update(s => s.Appearance.Theme = value);
    }

    public double BackgroundOpacity
    {
        get => _s.Appearance.BackgroundOpacity;
        set => Update(s => s.Appearance.BackgroundOpacity = value);
    }

    public double CornerRadius
    {
        get => _s.Appearance.CornerRadius;
        set => Update(s => s.Appearance.CornerRadius = value);
    }

    public double WidgetSpacing
    {
        get => _s.Appearance.WidgetSpacing;
        set => Update(s => s.Appearance.WidgetSpacing = value);
    }

    public double DockHeight
    {
        get => _s.Appearance.DockHeight;
        set => Update(s =>
        {
            // Keep the widget/dock proportion of the defaults (40/52).
            s.Appearance.DockHeight = value;
            s.Appearance.WidgetHeight = Math.Round(value - (2 * s.Appearance.VerticalPadding));
        });
    }

    public string AccentColor
    {
        get => _s.Appearance.AccentColor;
        set
        {
            if (ColorParsing.TryParseHexRgb(value, out _, out _, out _))
            {
                Update(s => s.Appearance.AccentColor = "#" + value.Trim().TrimStart('#').ToUpperInvariant());
            }
        }
    }

    public double ShadowIntensity
    {
        get => _s.Appearance.ShadowIntensity;
        set => Update(s => s.Appearance.ShadowIntensity = value);
    }

    public DockEdge Edge
    {
        get => _s.Behavior.Edge;
        set => Update(s => s.Behavior.Edge = value);
    }

    public bool AutoHide
    {
        get => _s.Behavior.AutoHide;
        set => Update(s => s.Behavior.AutoHide = value);
    }

    public double EdgeMargin
    {
        get => _s.Behavior.EdgeMargin;
        set => Update(s => s.Behavior.EdgeMargin = Math.Round(value));
    }

    public double WindowGap
    {
        get => _s.Behavior.WindowGap;
        set => Update(s => s.Behavior.WindowGap = Math.Round(value));
    }

    public double DockScale
    {
        get => _s.Appearance.DockScale;
        set => Update(s => s.Appearance.DockScale = Math.Round(value, 2));
    }

    public bool WrapWhenFull
    {
        get => _s.Behavior.WrapWhenFull;
        set => Update(s => s.Behavior.WrapWhenFull = value);
    }

    public bool FullWidth
    {
        get => _s.Behavior.FullWidth;
        set => Update(s => s.Behavior.FullWidth = value);
    }

    public bool ShowWindowPreviews
    {
        get => _s.Behavior.ShowWindowPreviews;
        set => Update(s => s.Behavior.ShowWindowPreviews = value);
    }

    public bool ShowAttention
    {
        get => _s.Behavior.ShowAttention;
        set => Update(s => s.Behavior.ShowAttention = value);
    }

    public bool ReserveSpace
    {
        get => _s.Behavior.ReserveSpace;
        set => Update(s => s.Behavior.ReserveSpace = value);
    }

    public TaskbarMode Mode
    {
        get => _s.Behavior.Mode;
        set => Update(s => s.Behavior.Mode = value);
    }

    public bool StartWithWindows
    {
        get => _s.Behavior.StartWithWindows;
        set => Update(s => s.Behavior.StartWithWindows = value);
    }

    public FullscreenBehavior Fullscreen
    {
        get => _s.Behavior.Fullscreen;
        set => Update(s => s.Behavior.Fullscreen = value);
    }

    public string? MonitorDeviceName
    {
        get => _s.Behavior.MonitorDeviceName;
        set => Update(s => s.Behavior.MonitorDeviceName = value);
    }

    /// <summary>Re-reads settings changed elsewhere (e.g. the tray switched mode).</summary>
    public void Reload()
    {
        _s = _settings.Current;
        OnPropertyChanged(string.Empty);
    }

    private void Update(Action<DockSettings> mutate)
    {
        _settings.Update(mutate);
        _s = _settings.Current;

        // Normalisation may have clamped the value; refresh all bindings.
        OnPropertyChanged(string.Empty);
    }

    private void LoadMonitors()
    {
        var list = new List<Option<string?>> { new(null, "Primary monitor") };
        list.AddRange(MonitorService.GetMonitors().Select(m => new Option<string?>(m.DeviceName, m.DisplayLabel)));
        Monitors = list;
        OnPropertyChanged(nameof(Monitors));
    }
}
