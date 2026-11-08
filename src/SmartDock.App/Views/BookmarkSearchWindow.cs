using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartDock.App.Services;
using SmartDock.Core.Events;
using SmartDock.Widgets.Bookmarks;

namespace SmartDock.App.Views;

/// <summary>
/// Type-to-filter window for the Bookmarks widget (the dock can't take keyboard input).
/// Matches name, URL and folder; Enter opens the selected link, Esc closes.
/// </summary>
public sealed class BookmarkSearchWindow : Window
{
    private const int MaxShown = 200;
    private readonly TextBox _query = new() { FontSize = 16, Padding = new Thickness(10, 6, 10, 6) };
    private readonly ListBox _results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Opacity = 0.65, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
    private IReadOnlyList<BookmarkLink> _links = [];
    private string? _browser;

    public BookmarkSearchWindow()
    {
        WindowStyling.ApplyFluent(this);
        Width = 680;
        Height = 480;
        MinWidth = 400;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _results.ItemTemplate = BuildTemplate();
        _results.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_results, (DependencyObject)e.OriginalSource) is ListBoxItem)
            {
                OpenSelected();
            }
        };

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(_query, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _results.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_query);
        root.Children.Add(_status);
        root.Children.Add(_results);
        Content = root;

        _query.TextChanged += (_, _) => Filter();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => _query.Focus();
    }

    public void Show(OpenBookmarkSearchRequested request)
    {
        Title = $"Search bookmarks – {request.Title}";
        _links = request.Links;
        _browser = request.BrowserPath;
        _query.Text = string.Empty;
        Filter();
        _query.Focus();
    }

    /// <summary>All words must match somewhere in name, URL or folder (case-insensitive); name matches first.</summary>
    public static IReadOnlyList<BookmarkLink> Match(IEnumerable<BookmarkLink> links, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return links.ToList();
        }

        return links
            .Where(l => words.All(w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || l.Url.Contains(w, StringComparison.OrdinalIgnoreCase)
                || l.Folder.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(l => words.Count(w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Filter()
    {
        var matches = Match(_links, _query.Text);
        _results.ItemsSource = matches.Take(MaxShown).ToList();
        if (_results.Items.Count > 0)
        {
            _results.SelectedIndex = 0;
        }

        _status.Text = matches.Count > MaxShown
            ? $"Showing {MaxShown} of {matches.Count:N0} · Enter: open · Esc: close"
            : $"{matches.Count:N0} of {_links.Count:N0} bookmarks · Enter: open · Esc: close";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _query.IsKeyboardFocusWithin && _results.Items.Count > 0:
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OpenSelected()
    {
        if (_results.SelectedItem is BookmarkLink link && BookmarksWidget.Launch(link.Url, _browser))
        {
            Close();
        }
    }

    private static DataTemplate BuildTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BookmarkLink.Name)));
        name.SetValue(TextBlock.FontSizeProperty, 13.5);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var url = new FrameworkElementFactory(typeof(TextBlock));
        url.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BookmarkLink.Url)));
        url.SetValue(TextBlock.FontSizeProperty, 11.5);
        url.SetValue(TextBlock.OpacityProperty, 0.65);
        url.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var folder = new FrameworkElementFactory(typeof(TextBlock));
        folder.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BookmarkLink.Folder)));
        folder.SetValue(TextBlock.FontSizeProperty, 11.0);
        folder.SetValue(TextBlock.OpacityProperty, 0.5);
        folder.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        stack.AppendChild(name);
        stack.AppendChild(url);
        stack.AppendChild(folder);
        return new DataTemplate(typeof(BookmarkLink)) { VisualTree = stack };
    }
}
