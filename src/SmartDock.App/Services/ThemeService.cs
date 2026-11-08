using System.Windows;
using System.Windows.Media;
using SmartDock.Core.Models;

namespace SmartDock.App.Services;

/// <summary>
/// Builds the active theme as a ResourceDictionary and swaps it into Application resources, so every
/// DynamicResource updates instantly (no restart). Glass themes use translucent gradients rather than
/// a per-frame blur, which keeps idle GPU usage near zero (see docs/adr/0003-glass-effects.md).
/// </summary>
public sealed class ThemeService
{
    public event EventHandler? ThemeApplied;

    public bool IsLightTheme { get; private set; }

    public void Apply(AppearanceSettings appearance)
    {
        var accent = ParseColor(appearance.AccentColor, Color.FromRgb(0x7C, 0x5C, 0xFF));
        var palette = CreatePalette(appearance.Theme, accent, appearance.BackgroundOpacity);
        IsLightTheme = palette.IsLight;

        var d = new ResourceDictionary
        {
            ["AccentColor"] = accent,
            ["DockBackgroundBrush"] = palette.DockBackground,
            ["DockBorderBrush"] = Solid(palette.DockBorder),
            ["WidgetBackgroundBrush"] = palette.WidgetBackground,
            ["WidgetHoverBrush"] = Solid(palette.WidgetHover),
            ["WidgetBorderBrush"] = Solid(palette.WidgetBorder),
            ["TextPrimaryBrush"] = Solid(palette.TextPrimary),
            ["TextSecondaryBrush"] = Solid(palette.TextSecondary),
            ["AccentBrush"] = Solid(accent),
            ["AccentSoftBrush"] = Solid(Color.FromArgb(0x38, accent.R, accent.G, accent.B)),
            ["SuccessBrush"] = Solid(Color.FromRgb(0x2F, 0xC4, 0x96)),
            ["WarningBrush"] = Solid(Color.FromRgb(0xF5, 0xB3, 0x2E)),
            ["DangerBrush"] = Solid(Color.FromRgb(0xF0, 0x4D, 0x6C)),
            ["PopupBackgroundBrush"] = Solid(palette.Popup),
            ["WindowBackgroundBrush"] = Solid(palette.Window),
            ["ControlBackgroundBrush"] = Solid(palette.Control),
            ["DividerBrush"] = Solid(palette.DockBorder),
            ["DockCornerRadius"] = new CornerRadius(appearance.CornerRadius),
            ["WidgetCornerRadius"] = new CornerRadius(Math.Max(0, appearance.CornerRadius - 3)),
            ["DockPadding"] = new Thickness(appearance.HorizontalPadding, appearance.VerticalPadding, appearance.HorizontalPadding, appearance.VerticalPadding),
            ["WidgetMargin"] = new Thickness(appearance.WidgetSpacing / 2, 0, appearance.WidgetSpacing / 2, 0),
            ["WidgetHeight"] = appearance.WidgetHeight,
            ["DockHeight"] = appearance.DockHeight,
            ["AppIconSize"] = Math.Round(appearance.WidgetHeight * 0.65),
            ["UiFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            ["IconFont"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = d;
        }
        else
        {
            merged.Insert(0, d);
        }

        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    public static Color ParseColor(string? hex, Color fallback) =>
        ColorParsing.TryParseHexRgb(hex, out var r, out var g, out var b) ? Color.FromRgb(r, g, b) : fallback;

    private static Palette CreatePalette(string theme, Color accent, double opacity)
    {
        byte A(double alpha) => (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);

        switch (theme)
        {
            case ThemeNames.Dark:
                return new Palette(
                    Solid(Color.FromArgb(A(opacity), 0x1C, 0x1C, 0x1F)),
                    Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
                    Solid(Color.FromRgb(0x2A, 0x2A, 0x2E)),
                    Color.FromRgb(0x35, 0x35, 0x3A),
                    Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xF2, 0xF2, 0xF5),
                    Color.FromRgb(0x9A, 0x9A, 0xA3),
                    Color.FromArgb(0xF7, 0x23, 0x23, 0x27),
                    Color.FromRgb(0x1E, 0x1E, 0x21),
                    Color.FromRgb(0x2C, 0x2C, 0x31),
                    false);

            case ThemeNames.Light:
                return new Palette(
                    Solid(Color.FromArgb(A(opacity), 0xF3, 0xF4, 0xF8)),
                    Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
                    Solid(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    Color.FromRgb(0xEA, 0xEC, 0xF3),
                    Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                    Color.FromRgb(0x16, 0x18, 0x1F),
                    Color.FromRgb(0x5E, 0x63, 0x74),
                    Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xF6, 0xF7, 0xFA),
                    Color.FromRgb(0xFF, 0xFF, 0xFF),
                    true);

            case ThemeNames.FrostedGlass:
            {
                var bg = new LinearGradientBrush(
                    Color.FromArgb(A(opacity * 0.55), 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(A(opacity * 0.35), 0xC8, 0xD2, 0xE6),
                    90);
                bg.Freeze();
                return new Palette(
                    bg,
                    Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
                    Solid(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xD0, 0xF1, 0xF4, 0xFA),
                    Color.FromArgb(0xF2, 0x2A, 0x30, 0x40),
                    Color.FromRgb(0x1D, 0x22, 0x2E),
                    Color.FromRgb(0x2A, 0x30, 0x40),
                    false);
            }

            case ThemeNames.ColorfulGlass:
            {
                var complement = Color.FromRgb((byte)(255 - accent.R / 2), (byte)(accent.G / 2 + 40), (byte)Math.Min(255, accent.B + 60));
                var bg = new LinearGradientBrush(
                    Color.FromArgb(A(opacity * 0.75), accent.R, accent.G, accent.B),
                    Color.FromArgb(A(opacity * 0.65), complement.R, complement.G, complement.B),
                    0);
                bg.Freeze();
                return new Palette(
                    bg,
                    Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF),
                    Solid(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                    Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xF2, 0x22, 0x1C, 0x3A),
                    Color.FromRgb(0x1A, 0x17, 0x2B),
                    Color.FromRgb(0x29, 0x24, 0x40),
                    false);
            }

            default: // Midnight
                return new Palette(
                    Solid(Color.FromArgb(A(opacity), 0x0B, 0x0D, 0x14)),
                    Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
                    Solid(Color.FromRgb(0x16, 0x1A, 0x26)),
                    Color.FromRgb(0x21, 0x27, 0x3A),
                    Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xED, 0xEF, 0xF7),
                    Color.FromRgb(0x8A, 0x90, 0xA6),
                    Color.FromArgb(0xF7, 0x13, 0x16, 0x22),
                    Color.FromRgb(0x10, 0x13, 0x1C),
                    Color.FromRgb(0x1A, 0x1F, 0x2E),
                    false);
        }
    }

    private static SolidColorBrush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private sealed record Palette(
        Brush DockBackground,
        Color DockBorder,
        Brush WidgetBackground,
        Color WidgetHover,
        Color WidgetBorder,
        Color TextPrimary,
        Color TextSecondary,
        Color Popup,
        Color Window,
        Color Control,
        bool IsLight);
}
