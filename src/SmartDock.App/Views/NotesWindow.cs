using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartDock.App.Services;
using SmartDock.Core.Interfaces;
using SmartDock.Widgets.Notes;

namespace SmartDock.App.Views;

/// <summary>
/// Notes list + editor. Edits are saved automatically (debounced) to notes.json. A checker underneath the editor
/// validates JSON / XML / YAML (whole note or ``` code blocks) while you type; "Beautify" (Ctrl+Shift+F) formats
/// the note or the selection. Beautify is a normal edit, so Ctrl+Z undoes it.
/// </summary>
public sealed class NotesWindow : Window
{
    private readonly INotesStore _store;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(Note.Title), BorderThickness = new Thickness(0) };
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 14,
        Padding = new Thickness(8),
    };

    private readonly TextBlock _status = new() { Opacity = 0.6, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly TextBlock _check = new() { FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _codeFont = new() { Content = "Code font", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<NoteIssue> _issues = [];
    private Note? _current;
    private bool _loading;

    public NotesWindow(INotesStore store)
    {
        _store = store;
        WindowStyling.ApplyFluent(this);
        Title = "SmartDock Notes";
        Width = 820;
        Height = 560;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");

        var newButton = new Button { Content = "New note", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        newButton.Click += (_, _) => NewNote();
        var deleteButton = new Button { Content = "Delete", Padding = new Thickness(12, 4, 12, 4) };
        deleteButton.Click += (_, _) => DeleteCurrent();
        var beautifyButton = new Button
        {
            Content = "Beautify",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Format JSON/XML, check YAML and tidy lists, headings and spacing (Ctrl+Shift+F).\n" +
                      "Works on the selection if there is one. Ctrl+Z undoes it.",
        };
        beautifyButton.Click += (_, _) => Beautify();
        _codeFont.Checked += (_, _) => ApplyFont();
        _codeFont.Unchecked += (_, _) => ApplyFont();

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(newButton);
        left.Children.Add(deleteButton);
        left.Children.Add(beautifyButton);
        left.Children.Add(_codeFont);
        DockPanel.SetDock(left, Dock.Left);
        toolbar.Children.Add(left);
        _status.Margin = new Thickness(12, 0, 0, 0);
        toolbar.Children.Add(_status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Editor with the live check line underneath (click it to jump to the first problem).
        var editorPanel = new DockPanel();
        DockPanel.SetDock(_check, Dock.Bottom);
        editorPanel.Children.Add(_check);
        editorPanel.Children.Add(_editor);
        _check.Cursor = Cursors.Hand;
        _check.MouseLeftButtonUp += (_, _) => JumpToIssue(0);

        Grid.SetColumn(_list, 0);
        Grid.SetColumn(editorPanel, 2);
        grid.Children.Add(_list);
        grid.Children.Add(editorPanel);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(grid);
        Content = root;

        _list.SelectionChanged += (_, _) => OnSelected();
        _editor.TextChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _status.Text = "Editing…";
            _saveTimer.Stop();
            _saveTimer.Start();
            _checkTimer.Stop();
            _checkTimer.Start();
        };
        _saveTimer.Tick += (_, _) => SaveNow();
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            RunCheck();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                Beautify();
                e.Handled = true;
            }
        };
        Closing += (_, _) => SaveNow();

        Reload(selectId: null);
    }

    /// <summary>Opens a new empty note (used by the widget's "New note" menu item).</summary>
    public void NewNote()
    {
        SaveNow();
        var note = _store.Save(new Note());
        Reload(note.Id);
        _editor.Focus();
    }

    private void Reload(string? selectId)
    {
        var notes = _store.GetAll();
        _list.ItemsSource = notes;
        var select = notes.FirstOrDefault(n => n.Id == selectId) ?? notes.FirstOrDefault();
        _list.SelectedItem = select;
        if (select is null)
        {
            _current = null;
            _loading = true;
            _editor.Text = string.Empty;
            _loading = false;
            _editor.IsEnabled = false;
            _status.Text = "No notes yet – click New note.";
        }
    }

    private void OnSelected()
    {
        if (_list.SelectedItem is not Note note || ReferenceEquals(note, _current))
        {
            return;
        }

        SaveNow(reselect: false);
        _current = note;
        _loading = true;
        _editor.Text = note.Text;
        _loading = false;
        _editor.IsEnabled = true;
        _status.Text = $"Edited {note.UpdatedAt.LocalDateTime:g}";
        _codeFont.IsChecked = NoteFormatter.Detect(note.Text) != NoteContentKind.Text || note.Text.Contains("```", StringComparison.Ordinal);
        RunCheck();
    }

    // ------------------------------------------------------------------ check & beautify

    private void RunCheck()
    {
        var text = _editor.Text;
        if (text.Trim().Length == 0)
        {
            _issues = [];
            _check.Text = string.Empty;
            return;
        }

        _issues = NoteFormatter.Check(text);
        var kind = NoteFormatter.Detect(text.Replace("\r\n", "\n"));
        if (_issues.Count == 0)
        {
            _check.Text = kind switch
            {
                NoteContentKind.Json => "✓ Valid JSON",
                NoteContentKind.Xml => "✓ Valid XML",
                NoteContentKind.Yaml => "✓ Valid YAML",
                _ => text.Contains("```", StringComparison.Ordinal) ? "✓ Code blocks look fine" : string.Empty,
            };
            _check.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            _check.ToolTip = null;
            return;
        }

        var first = _issues[0];
        _check.Text = (_issues.Count == 1 ? "⚠ " : $"⚠ {_issues.Count} problems · ") + first + "  (click to go there)";
        _check.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        _check.ToolTip = string.Join("\n", _issues.Select(i => i.ToString()));
    }

    private void Beautify()
    {
        if (_current is null || !_editor.IsEnabled)
        {
            return;
        }

        var selectionOnly = _editor.SelectionLength > 0;
        var source = selectionOnly ? _editor.SelectedText : _editor.Text;
        var result = NoteFormatter.Beautify(source);

        if (!result.Changed)
        {
            _status.Text = result.Issues.Count > 0 ? "Nothing formatted – fix the problem shown below first." : "Already tidy.";
            RunCheck();
            return;
        }

        var formatted = result.Text.Replace("\n", Environment.NewLine);
        if (!selectionOnly)
        {
            _editor.SelectAll();
        }

        // Replacing the selection (instead of setting Text) keeps it on the undo stack: Ctrl+Z restores the original.
        var caret = _editor.SelectionStart;
        _editor.SelectedText = formatted;
        _editor.SelectionLength = 0;
        _editor.CaretIndex = Math.Min(caret, _editor.Text.Length);
        _status.Text = (result.Changes.Count > 0 ? string.Join(" · ", result.Changes) : "Formatted") + "  (Ctrl+Z to undo)";
        RunCheck();
    }

    private void JumpToIssue(int index)
    {
        if (index >= _issues.Count)
        {
            return;
        }

        // Line numbers count real lines (not wrapped ones), so walk the text instead of using TextBox line APIs.
        var issue = _issues[index];
        var text = _editor.Text;
        var position = 0;
        for (var line = 1; line < issue.Line && position < text.Length; line++)
        {
            var next = text.IndexOf('\n', position);
            if (next < 0)
            {
                break;
            }

            position = next + 1;
        }

        var lineEnd = text.IndexOf('\n', position);
        var lineLength = (lineEnd < 0 ? text.Length : lineEnd) - position;
        _editor.Focus();
        _editor.Select(position + Math.Clamp(issue.Column - 1, 0, Math.Max(0, lineLength - 1)), lineLength > 0 ? 1 : 0);
        var rect = _editor.GetRectFromCharacterIndex(_editor.SelectionStart);
        if (!rect.IsEmpty)
        {
            _editor.ScrollToVerticalOffset(Math.Max(0, _editor.VerticalOffset + rect.Top - (_editor.ViewportHeight / 3)));
        }
    }

    private void ApplyFont() =>
        _editor.FontFamily = _codeFont.IsChecked == true
            ? new FontFamily("Cascadia Mono, Cascadia Code, Consolas")
            : new FontFamily("Segoe UI Variable Text, Segoe UI");

    private void SaveNow(bool reselect = true)
    {
        _saveTimer.Stop();
        if (_current is null || _current.Text == _editor.Text)
        {
            return;
        }

        _current.Text = _editor.Text;
        _store.Save(_current);
        _status.Text = "Saved";

        // Refresh titles without losing the caret.
        var id = _current.Id;
        var selectedId = (_list.SelectedItem as Note)?.Id;
        var notes = _store.GetAll();
        _list.ItemsSource = notes;
        _current = notes.FirstOrDefault(n => n.Id == id);
        _list.SelectedItem = reselect ? _current : notes.FirstOrDefault(n => n.Id == selectedId);
    }

    private void DeleteCurrent()
    {
        if (_current is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"Delete \"{_current.Title}\"?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _store.Delete(_current.Id);
        _current = null;
        Reload(selectId: null);
    }
}
