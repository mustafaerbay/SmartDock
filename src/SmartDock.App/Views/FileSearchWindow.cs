using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartDock.App.Services;
using SmartDock.Core.Interfaces;

namespace SmartDock.App.Views;

/// <summary>
/// Type-to-search window for the Everything widget (a normal, focusable window: the dock itself can't take
/// keyboard input). Enter opens the selected result, Ctrl+Enter opens its folder, Esc closes.
/// </summary>
public sealed class FileSearchWindow : Window
{
    private readonly IFileSearchService _search;
    private readonly TextBox _query = new() { FontSize = 16, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Opacity = 0.65, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _startButton = new() { Content = "Start Everything", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _inFlight;
    private int _maxResults;
    private bool _matchPath;

    public FileSearchWindow(IFileSearchService search, int maxResults, bool matchPath)
    {
        _search = search;
        _maxResults = maxResults;
        _matchPath = matchPath;
        WindowStyling.ApplyFluent(this);
        Title = "Search files – Everything";
        Width = 720;
        Height = 480;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _results.ItemTemplate = BuildItemTemplate();
        _results.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_results, (DependencyObject)e.OriginalSource) is ListBoxItem)
            {
                OpenSelected(folder: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            }
        };
        _results.ContextMenu = BuildContextMenu();

        var openEverything = new Button { Content = "Open in Everything", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        openEverything.Click += (_, _) =>
        {
            if (!_search.OpenProviderWindow(_query.Text))
            {
                _status.Text = "Everything.exe wasn't found. Set its location in the widget's Configure… dialog.";
            }
        };
        _startButton.Click += (_, _) =>
        {
            _status.Text = _search.StartProvider() ? "Starting Everything…" : "Everything.exe wasn't found.";
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                Search();
            });
        };

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        DockPanel.SetDock(openEverything, Dock.Right);
        DockPanel.SetDock(_startButton, Dock.Right);
        footer.Children.Add(openEverything);
        footer.Children.Add(_startButton);
        footer.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(_query, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        _results.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_query);
        root.Children.Add(footer);
        root.Children.Add(_results);
        Content = root;

        _query.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Search();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            _query.Focus();
            ShowIdleStatus();
        };
        Closed += (_, _) => _inFlight?.Cancel();
    }

    /// <summary>Re-used window: apply the widget's current options and select the query text for a new search.</summary>
    public void Prepare(string? query, int maxResults, bool matchPath)
    {
        _maxResults = maxResults;
        _matchPath = matchPath;
        if (query is not null)
        {
            _query.Text = query;
        }

        _query.Focus();
        _query.SelectAll();
        if (string.IsNullOrWhiteSpace(_query.Text))
        {
            ShowIdleStatus();
        }
    }

    private void ShowIdleStatus()
    {
        var availability = _search.GetAvailability();
        _startButton.Visibility = availability == FileSearchAvailability.NotRunning ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = availability switch
        {
            FileSearchAvailability.Ready => "Type to search · Enter: open · Ctrl+Enter: open folder · Esc: close",
            FileSearchAvailability.NotRunning => "Everything is installed but not running.",
            _ => "Everything isn't installed (free from voidtools.com), or set its location in the widget's Configure… dialog.",
        };
    }

    private async void Search()
    {
        _inFlight?.Cancel();
        var text = _query.Text.Trim();
        if (text.Length == 0)
        {
            _results.ItemsSource = null;
            ShowIdleStatus();
            return;
        }

        var cts = new CancellationTokenSource();
        _inFlight = cts;
        try
        {
            var result = await _search.SearchAsync(text, _maxResults, _matchPath, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _startButton.Visibility = Visibility.Collapsed;
            _results.ItemsSource = result.Hits;
            if (result.Hits.Count > 0)
            {
                _results.SelectedIndex = 0;
                _results.ScrollIntoView(_results.SelectedItem);
            }

            _status.Text = result.TotalCount > result.Hits.Count
                ? $"Showing {result.Hits.Count:N0} of {result.TotalCount:N0} results"
                : $"{result.Hits.Count:N0} result{(result.Hits.Count == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (!cts.IsCancellationRequested)
            {
                _results.ItemsSource = null;
                ShowIdleStatus();
                _status.Text = ex.Message + " " + _status.Text;
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected(folder: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _query.IsKeyboardFocusWithin && _results.Items.Count > 0:
                var next = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.SelectedIndex = next;
                _results.ScrollIntoView(_results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OpenSelected(bool folder)
    {
        if (_results.SelectedItem is not FileSearchHit hit)
        {
            return;
        }

        try
        {
            if (folder)
            {
                // Explorer with the item selected. Windows paths can't contain quotes, so quoting is safe.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{hit.FullPath}\"") { UseShellExecute = false })?.Dispose();
            }
            else
            {
                // Same as double-clicking it in Explorer: the user explicitly chose this item.
                Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true })?.Dispose();
            }

            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = $"Couldn't open “{hit.Name}”: {ex.Message}";
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenSelected(folder: false);
        var folder = new MenuItem { Header = "Open containing folder" };
        folder.Click += (_, _) => OpenSelected(folder: true);
        var copy = new MenuItem { Header = "Copy full path" };
        copy.Click += (_, _) =>
        {
            if (_results.SelectedItem is FileSearchHit hit)
            {
                try
                {
                    Clipboard.SetText(hit.FullPath);
                    _status.Text = "Path copied";
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    _status.Text = "Clipboard is busy; try again.";
                }
            }
        };
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        return menu;
    }

    private static DataTemplate BuildItemTemplate()
    {
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 16.0);
        icon.SetValue(TextBlock.WidthProperty, 26.0);
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FileSearchHit.IsFolder))
        {
            Converter = new GlyphConverter(),
        });

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FileSearchHit.Name)));
        name.SetValue(TextBlock.FontSizeProperty, 13.5);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var path = new FrameworkElementFactory(typeof(TextBlock));
        path.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FileSearchHit.Directory)));
        path.SetValue(TextBlock.FontSizeProperty, 11.5);
        path.SetValue(TextBlock.OpacityProperty, 0.65);
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.AppendChild(name);
        text.AppendChild(path);

        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        row.AppendChild(icon);
        row.AppendChild(text);

        return new DataTemplate(typeof(FileSearchHit)) { VisualTree = row };
    }

    private sealed class GlyphConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is true ? "" : ""; // Folder / Document

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }
}
