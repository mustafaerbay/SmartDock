using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SmartDock.App.Services;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.App.Views;

/// <summary>
/// Generic per-widget settings dialog generated from <see cref="IConfigurableWidget.SettingFields"/>.
/// Plain values go to the widget's settings (layout.json); Secret fields go to the DPAPI secret store.
/// It is a normal (activating) window so keyboard input works, unlike dock popups.
/// </summary>
public sealed class WidgetConfigWindow : Window
{
    private readonly string _instanceId;
    private readonly WidgetSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly Action _onSaved;
    private readonly List<Func<bool>> _apply = [];
    private readonly ISettingsValidator? _validator;

    public WidgetConfigWindow(IDockWidget widget, IConfigurableWidget configurable, WidgetSettings settings, ISecretStore secrets, Action onSaved)
    {
        _instanceId = widget.InstanceId;
        _settings = settings;
        _secrets = secrets;
        _onSaved = onSaved;
        _validator = configurable as ISettingsValidator;

        WindowStyling.ApplyFluent(this);
        Title = $"Configure {widget.Descriptor.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        panel.Children.Add(new TextBlock { Text = widget.Descriptor.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = widget.Descriptor.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 16) });

        foreach (var field in configurable.SettingFields)
        {
            panel.Children.Add(BuildField(field));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = new Button { Content = "Save", Padding = new Thickness(18, 5, 18, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };

        // IsCancel only closes windows opened with ShowDialog(); this one is modeless. Nothing is applied
        // until Save, so closing discards the edits (including a pending "Remove saved value").
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private FrameworkElement BuildField(WidgetSettingField field)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var label = new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        switch (field.Kind)
        {
            case SettingFieldKind.Toggle:
            {
                var check = new CheckBox { Content = field.Label, IsChecked = _settings.Get(field.Key, field.DefaultToggle) };
                box.Children.Add(check);
                _apply.Add(() =>
                {
                    _settings.Set(field.Key, check.IsChecked == true);
                    return true;
                });
                break;
            }

            case SettingFieldKind.Number:
            {
                box.Children.Add(label);
                var text = new TextBox { Text = _settings.Get(field.Key, field.DefaultNumber).ToString(CultureInfo.CurrentCulture), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
                box.Children.Add(text);
                _apply.Add(() =>
                {
                    if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                        && !double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        MessageBox.Show(this, $"\"{field.Label}\" must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    _settings.Set(field.Key, value);
                    return true;
                });
                break;
            }

            case SettingFieldKind.Choice:
            {
                box.Children.Add(label);
                var combo = new ComboBox { ItemsSource = field.Choices ?? Array.Empty<string>(), Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
                combo.SelectedItem = _settings.Get(field.Key, field.DefaultText ?? field.Choices?.FirstOrDefault() ?? string.Empty);
                box.Children.Add(combo);
                _apply.Add(() =>
                {
                    if (combo.SelectedItem is string choice)
                    {
                        _settings.Set(field.Key, choice);
                    }

                    return true;
                });
                break;
            }

            case SettingFieldKind.Secret:
            {
                var name = WidgetSecrets.Name(_instanceId, field.Key);
                var hasValue = !string.IsNullOrEmpty(_secrets.Get(name));
                box.Children.Add(label);
                var password = new PasswordBox();
                box.Children.Add(password);
                var status = new TextBlock
                {
                    Text = hasValue ? "A value is saved (encrypted). Leave empty to keep it." : "Nothing saved yet.",
                    FontSize = 11.5,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                box.Children.Add(status);
                var remove = false;
                if (hasValue)
                {
                    var clear = new Button { Content = "Remove saved value", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                    clear.Click += (_, _) =>
                    {
                        remove = true;
                        password.Clear();
                        status.Text = "Will be removed when you save.";
                    };
                    box.Children.Add(clear);
                }

                _apply.Add(() =>
                {
                    if (password.Password.Length > 0)
                    {
                        _secrets.Set(name, password.Password.Trim());
                    }
                    else if (remove)
                    {
                        _secrets.Set(name, null);
                    }

                    return true;
                });
                break;
            }

            default:
            {
                box.Children.Add(label);
                var multiline = field.Kind == SettingFieldKind.MultilineText;
                var text = new TextBox
                {
                    Text = _settings.Get(field.Key, field.DefaultText ?? string.Empty),
                    AcceptsReturn = multiline,
                    TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    MinHeight = multiline ? 120 : 0,
                    VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                    FontFamily = multiline ? new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") : FontFamily,
                };
                box.Children.Add(text);
                _apply.Add(() =>
                {
                    var value = text.Text.Replace("\r\n", "\n").Trim();
                    var problems = _validator?.Validate(field.Key, value) ?? Array.Empty<string>();
                    if (problems.Count > 0)
                    {
                        var list = string.Join("\n", problems.Take(12).Select(p => "• " + p))
                            + (problems.Count > 12 ? $"\n… and {problems.Count - 12} more" : string.Empty);
                        var answer = MessageBox.Show(this,
                            $"\"{field.Label}\" has entries that won't be used:\n\n{list}\n\nSave anyway? (Choose No to fix them first.)",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes)
                        {
                            text.Focus();
                            return false;
                        }
                    }

                    _settings.Set(field.Key, value);
                    return true;
                });
                break;
            }
        }

        if (field.Help is { Length: > 0 } help)
        {
            box.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Opacity = 0.65, Margin = new Thickness(0, 4, 0, 0) });
        }

        return box;
    }

    private void Save()
    {
        try
        {
            foreach (var apply in _apply)
            {
                if (!apply())
                {
                    return;
                }
            }

            _onSaved();
            Close();
        }
        catch (Exception ex)
        {
            // Never fail silently: the user should see why Save didn't work.
            MessageBox.Show(this, "Saving failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
