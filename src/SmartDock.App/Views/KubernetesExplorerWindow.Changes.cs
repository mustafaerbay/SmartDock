using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartDock.App.Services;
using SmartDock.Widgets.Cluster;

namespace SmartDock.App.Views;

/// <summary>
/// Deployment changes (scale, set image, restart, rollback, edit YAML). Only available when the widget's
/// "Allow changing deployments" option is on; every change is confirmed (naming the context) and logged.
/// </summary>
public sealed partial class KubernetesExplorerWindow
{
    private readonly List<Button> _changeButtonList = [];
    private bool _allowChanges;
    private TextBlock? _changesHint;

    private UIElement BuildChangeButtons()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Add(string text, Func<DeploymentRow, Task> action, string tip)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += async (_, _) => await RunOnSelectedAsync(action);
            _changeButtonList.Add(button);
            panel.Children.Add(button);
            return button;
        }

        Add("Scale…", ScaleAsync, "Set the number of replicas (kubectl scale)");
        Add("Set image…", SetImageAsync, "Change a container's image or tag (kubectl set image)");
        Add("Restart", RestartAsync, "Rolling restart of all pods (kubectl rollout restart)");
        Add("Roll back…", RollbackAsync, "Go back to an earlier revision (kubectl rollout undo)");
        Add("Edit YAML…", EditYamlAsync, "Edit and apply the deployment's YAML (kubectl edit)");
        _changesHint = new TextBlock { Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4) };
        panel.Children.Add(_changesHint);
        UpdateChangeButtons();
        return panel;
    }

    private ContextMenu BuildDeploymentMenu()
    {
        var menu = new ContextMenu();
        var changeItems = new List<MenuItem>();
        MenuItem Item(string header, Func<DeploymentRow, Task> action, bool isChange)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await RunOnSelectedAsync(action);
            if (isChange)
            {
                changeItems.Add(item);
            }

            return item;
        }

        menu.Items.Add(Item("Show its pods", d =>
        {
            ShowDeploymentPods(d);
            return Task.CompletedTask;
        }, false));
        menu.Items.Add(Item("Logs of all its pods", StartDeploymentLogsAsync, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Scale…", ScaleAsync, true));
        menu.Items.Add(Item("Set image…", SetImageAsync, true));
        menu.Items.Add(Item("Restart (rollout restart)", RestartAsync, true));
        menu.Items.Add(Item("Roll back…", RollbackAsync, true));
        menu.Items.Add(Item("Edit YAML…", EditYamlAsync, true));
        menu.Opened += (_, _) =>
        {
            foreach (var item in changeItems)
            {
                item.IsEnabled = _allowChanges;
            }
        };
        return menu;
    }

    private void UpdateChangeButtons()
    {
        foreach (var button in _changeButtonList)
        {
            button.IsEnabled = _allowChanges;
        }

        if (_changesHint is not null)
        {
            _changesHint.Text = _allowChanges
                ? $"Changes apply to context {_explorer.Context}."
                : "Read-only. Turn on \"Allow changing deployments\" in the widget's Configure… to change deployments.";
        }
    }

    private async Task RunOnSelectedAsync(Func<DeploymentRow, Task> action)
    {
        if (_deployments.SelectedItem is not DeploymentRow deployment)
        {
            _listStatus.Text = "Select a deployment first.";
            return;
        }

        await action(deployment);
    }

    // ---- Actions -----------------------------------------------------------------------------------------------

    private async Task ScaleAsync(DeploymentRow d)
    {
        if (!CheckAllowed())
        {
            return;
        }

        var dialog = new SmallDialog(this, $"Scale {d.Name}", $"Replicas for {d.Namespace}/{d.Name} (now {d.Replicas}):");
        var box = dialog.AddTextBox(d.Replicas.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!int.TryParse(box.Text.Trim(), out var replicas) || replicas is < 0 or > 1000)
        {
            Warn("Enter a whole number between 0 and 1000.");
            return;
        }

        if (replicas == d.Replicas)
        {
            return;
        }

        var extra = replicas == 0 ? "\n\nScaling to 0 stops every pod of this deployment." : string.Empty;
        if (Confirm($"Scale {d.Namespace}/{d.Name} from {d.Replicas} to {replicas} replicas?{extra}"))
        {
            await ApplyAsync($"scale {d.Name} to {replicas}", ct => _explorer.ScaleAsync(d.Namespace, d.Name, replicas, ct));
        }
    }

    private async Task SetImageAsync(DeploymentRow d)
    {
        if (!CheckAllowed())
        {
            return;
        }

        if (d.Containers.Count == 0)
        {
            Warn("This deployment has no containers in its template.");
            return;
        }

        var dialog = new SmallDialog(this, $"Set image – {d.Name}", "Container:");
        var containers = dialog.AddComboBox(d.Containers.Select(c => c.Name).ToList());
        dialog.AddLabel("Image:");
        var image = dialog.AddTextBox(d.Containers[0].Image, wide: true);
        containers.SelectionChanged += (_, _) =>
        {
            if (containers.SelectedIndex >= 0)
            {
                image.Text = d.Containers[containers.SelectedIndex].Image;
            }
        };
        if (dialog.ShowDialog() != true || containers.SelectedIndex < 0)
        {
            return;
        }

        var container = d.Containers[containers.SelectedIndex];
        var newImage = image.Text.Trim();
        if (DeploymentChanges.ValidateImage(newImage) is { } problem)
        {
            Warn(problem);
            return;
        }

        if (newImage == container.Image)
        {
            return;
        }

        if (Confirm($"Change the image of container {container.Name} in {d.Namespace}/{d.Name}\n\nfrom  {container.Image}\nto      {newImage}\n\nThis starts a rolling update."))
        {
            await ApplyAsync($"set image {d.Name}/{container.Name}={newImage}", ct => _explorer.SetImageAsync(d.Namespace, d.Name, container.Name, newImage, ct));
        }
    }

    private async Task RestartAsync(DeploymentRow d)
    {
        if (CheckAllowed() && Confirm($"Restart all pods of {d.Namespace}/{d.Name}?\n\nPods are replaced one by one (rolling restart), like kubectl rollout restart."))
        {
            await ApplyAsync($"rollout restart {d.Name}", ct => _explorer.RestartAsync(d.Namespace, d.Name, ct));
        }
    }

    private async Task RollbackAsync(DeploymentRow d)
    {
        if (!CheckAllowed())
        {
            return;
        }

        IReadOnlyList<RolloutRevision> history;
        try
        {
            history = await _explorer.RolloutHistoryAsync(d.Namespace, d.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Warn("Couldn't read the rollout history: " + KubernetesExplorer.Describe(ex));
            return;
        }

        var earlier = history.Where(r => !r.IsCurrent).ToList();
        if (earlier.Count == 0)
        {
            Warn("There's no earlier revision to roll back to (old ReplicaSets may have been cleaned up – see revisionHistoryLimit).");
            return;
        }

        var dialog = new SmallDialog(this, $"Roll back {d.Name}", "Roll back to:");
        var list = dialog.AddList(history.Select(r => r.Display).ToList(), history.ToList().FindIndex(r => !r.IsCurrent));
        if (dialog.ShowDialog() != true || list.SelectedIndex < 0)
        {
            return;
        }

        var target = history[list.SelectedIndex];
        if (target.IsCurrent)
        {
            return;
        }

        if (Confirm($"Roll {d.Namespace}/{d.Name} back to revision {target.Revision}?\n\nImages: {target.Images}\n\nThis starts a rolling update to that revision's pod template."))
        {
            await ApplyAsync($"rollback {d.Name} to revision {target.Revision}", ct => _explorer.RollbackAsync(d.Namespace, d.Name, target, ct));
        }
    }

    private async Task EditYamlAsync(DeploymentRow d)
    {
        if (!CheckAllowed())
        {
            return;
        }

        string yaml;
        try
        {
            yaml = await _explorer.ReadYamlAsync(d.Namespace, d.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Warn("Couldn't load the deployment: " + KubernetesExplorer.Describe(ex));
            return;
        }

        var editor = new YamlEditorWindow(this, _explorer, d.Namespace, d.Name, yaml);
        if (editor.ShowDialog() == true)
        {
            Serilog.Log.Information("Kubernetes change: edited YAML of deployment {Namespace}/{Name} (context {Context})", d.Namespace, d.Name, _explorer.Context);
            _listStatus.Text = $"Applied the edited YAML of {d.Name}.";
            await LoadListsAsync();
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private bool CheckAllowed()
    {
        if (_allowChanges)
        {
            return true;
        }

        MessageBox.Show(this, "Changing deployments is turned off for this widget.\n\nTurn on \"Allow changing deployments\" in the widget's Configure… if you want it.",
            "Read-only", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message + $"\n\nContext: {_explorer.Context}", "Confirm change", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
        == MessageBoxResult.OK;

    private void Warn(string message) => MessageBox.Show(this, message, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task ApplyAsync(string what, Func<CancellationToken, Task> change)
    {
        _listStatus.Text = $"Applying: {what}…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await change(cts.Token);
            Serilog.Log.Information("Kubernetes change: {Change} (context {Context})", what, _explorer.Context);
            _listStatus.Text = $"Done: {what}. The rollout continues in the cluster.";
            await LoadListsAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Kubernetes change failed: {Change} (context {Context})", what, _explorer.Context);
            _listStatus.Text = "Failed: " + KubernetesExplorer.Describe(ex);
            Warn($"Couldn't {what}:\n\n{KubernetesExplorer.Describe(ex)}");
        }
    }
}

/// <summary>Minimal modal dialog built in code: a message, a few inputs, OK / Cancel.</summary>
internal sealed class SmallDialog : Window
{
    private readonly StackPanel _body = new() { Margin = new Thickness(16, 14, 16, 8) };

    public SmallDialog(Window owner, string title, string message)
    {
        WindowStyling.ApplyFluent(this);
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 380;

        AddLabel(message);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_body);
        Content = root;
        Loaded += (_, _) => _body.Children.OfType<Control>().FirstOrDefault()?.Focus();
    }

    public void AddLabel(string text) =>
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 0, 0, 6) });

    public TextBox AddTextBox(string text, bool wide = false)
    {
        var box = new TextBox { Text = text, Width = wide ? 520 : 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6, 3, 6, 3) };
        box.Loaded += (_, _) => box.SelectAll();
        _body.Children.Add(box);
        return box;
    }

    public ComboBox AddComboBox(IReadOnlyList<string> items)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = 0, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        _body.Children.Add(combo);
        return combo;
    }

    public ListBox AddList(IReadOnlyList<string> items, int selected)
    {
        var list = new ListBox { ItemsSource = items, SelectedIndex = selected, Width = 560, MaxHeight = 280, Margin = new Thickness(0, 0, 0, 10) };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                DialogResult = true;
            }
        };
        _body.Children.Add(list);
        return list;
    }
}

/// <summary>
/// YAML editor for one deployment: Validate runs a server-side dry run (nothing changes); Apply replaces the
/// deployment after a confirmation. A conflict (someone else changed it meanwhile) is reported, with Reload.
/// </summary>
internal sealed class YamlEditorWindow : Window
{
    private readonly KubernetesExplorer _explorer;
    private readonly string _ns;
    private readonly string _name;
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12.5,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxHeight = 90 };
    private string _original;

    public YamlEditorWindow(Window owner, KubernetesExplorer explorer, string ns, string name, string yaml)
    {
        WindowStyling.ApplyFluent(this);
        Owner = owner;
        _explorer = explorer;
        _ns = ns;
        _name = name;
        _original = yaml;
        Title = $"Edit deployment {ns}/{name} — context {explorer.Context}";
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _editor.Text = yaml;
        _editor.TextChanged += (_, _) => _status.Text = _editor.Text == _original ? string.Empty : "Edited – Validate to check it, Apply to save.";

        var validate = new Button { Content = "Validate (dry run)", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        validate.Click += async (_, _) => await RunAsync(dryRun: true);
        var apply = new Button { Content = "Apply…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += async (_, _) => await RunAsync(dryRun: false);
        var reload = new Button { Content = "Reload", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        reload.Click += async (_, _) => await ReloadAsync();
        var cancel = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(validate);
        buttons.Children.Add(apply);
        buttons.Children.Add(reload);
        buttons.Children.Add(cancel);

        var hint = new TextBlock
        {
            Text = "status and managedFields are left out (the server maintains them). Keep metadata.resourceVersion: it lets the server refuse the change if someone else edited the deployment meanwhile.",
            Opacity = 0.6,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(_editor);
        Content = root;
        Loaded += (_, _) => _editor.Focus();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await RunAsync(dryRun: false);
            }
        };
        Closing += (_, e) =>
        {
            if (DialogResult != true && _editor.Text != _original
                && MessageBox.Show(this, "Discard your edits?", "Edit YAML", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        };
    }

    private async Task RunAsync(bool dryRun)
    {
        if (_editor.Text == _original && !dryRun)
        {
            _status.Text = "Nothing changed.";
            return;
        }

        try
        {
            DeploymentChanges.ParseEdited(_editor.Text, _ns, _name); // local checks first, before asking
        }
        catch (FormatException ex)
        {
            ShowError(ex.Message);
            return;
        }

        if (!dryRun && MessageBox.Show(this, $"Apply the edited YAML to {_ns}/{_name}?\n\nContext: {_explorer.Context}\n\nChanges to the pod template start a rolling update.",
                "Confirm change", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        _status.Foreground = SystemColors.ControlTextBrush;
        _status.Text = dryRun ? "Validating on the server…" : "Applying…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _explorer.ApplyYamlAsync(_ns, _name, _editor.Text, dryRun, cts.Token);
            if (dryRun)
            {
                _status.Foreground = Brushes.SeaGreen;
                _status.Text = "Valid: the server accepted it in a dry run. Nothing was changed.";
            }
            else
            {
                _original = _editor.Text;
                DialogResult = true; // closes
            }
        }
        catch (FormatException ex)
        {
            ShowError(ex.Message);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            ShowError("Someone else changed this deployment after you opened it (409 Conflict). Click Reload to get the current version, then make your edit again.");
        }
        catch (Exception ex)
        {
            ShowError(KubernetesExplorer.Describe(ex));
        }
    }

    private async Task ReloadAsync()
    {
        if (_editor.Text != _original
            && MessageBox.Show(this, "Reload from the cluster and discard your edits?", "Reload", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _original = await _explorer.ReadYamlAsync(_ns, _name, CancellationToken.None);
            _editor.Text = _original;
            _status.Text = "Reloaded.";
        }
        catch (Exception ex)
        {
            ShowError(KubernetesExplorer.Describe(ex));
        }
    }

    private void ShowError(string message)
    {
        _status.Foreground = Brushes.IndianRed;
        _status.Text = message;
    }
}
