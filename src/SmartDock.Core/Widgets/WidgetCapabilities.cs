namespace SmartDock.Core.Widgets;

/// <summary>
/// Optional widget capabilities. The dock discovers them with type checks, so widgets only implement
/// what they need and the base contract stays small.
/// </summary>
/// <remarks>
/// Popups belong to a no-activate dock window, so they support mouse interaction (buttons, sliders,
/// lists) but not keyboard text entry. Widgets that need text input use <see cref="IConfigurableWidget"/>
/// (a normal window) or <see cref="IClickableWidget"/> to ask the app to open a window.
/// </remarks>
public interface IPopupWidget
{
    /// <summary>Called on the UI thread just before the popup opens (e.g. to refresh its list).</summary>
    void OnPopupOpened();

    /// <summary>Called on the UI thread after the popup closed (e.g. to stop live updates). Optional.</summary>
    void OnPopupClosed()
    {
    }
}

/// <summary>A plain left click on the card (not on an inner button) triggers <see cref="OnClick"/>.</summary>
public interface IClickableWidget
{
    void OnClick();
}

/// <summary>Mouse-wheel over the card; <paramref name="delta"/> is in WHEEL_DELTA units (120 per notch).</summary>
public interface IWheelWidget
{
    void OnWheel(int delta);
}

public enum SettingFieldKind
{
    Text,
    MultilineText,

    /// <summary>Stored encrypted in ISecretStore under <see cref="WidgetSecrets.Name"/>, never in layout.json.</summary>
    Secret,
    Number,
    Toggle,
    Choice,
}

/// <summary>Describes one field of the generic "Configure widget" dialog.</summary>
public sealed record WidgetSettingField(
    string Key,
    string Label,
    SettingFieldKind Kind,
    string? Help = null,
    IReadOnlyList<string>? Choices = null,
    string? DefaultText = null,
    double DefaultNumber = 0,
    bool DefaultToggle = false);

/// <summary>Widgets that expose settings in the generic configuration dialog.</summary>
public interface IConfigurableWidget
{
    IReadOnlyList<WidgetSettingField> SettingFields { get; }
}

public static class WidgetSecrets
{
    /// <summary>Secret names are scoped per widget instance so two GitHub widgets can use different tokens.</summary>
    public static string Name(string instanceId, string key) => $"{instanceId}.{key}";
}

/// <summary>Widget that accepts files/folders dragged onto its card (e.g. from Explorer).</summary>
public interface IFileDropWidget
{
    bool AcceptsFiles(IReadOnlyList<string> paths);

    void OnFilesDropped(IReadOnlyList<string> paths);
}

/// <summary>
/// Optional check of free-text settings before they are saved, so lines the widget would ignore are
/// reported to the user instead of silently dropped.
/// </summary>
public interface ISettingsValidator
{
    /// <summary>Human-readable problems with <paramref name="value"/> for <paramref name="key"/>; empty when fine.</summary>
    IReadOnlyList<string> Validate(string key, string value);
}
