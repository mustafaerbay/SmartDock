namespace SmartDock.Core.Interfaces;

public readonly record struct VolumeState(float Level, bool IsMuted, string DeviceName);

/// <summary>Master volume of the default output device. Thread-safe.</summary>
public interface IVolumeService
{
    /// <summary>Null when no output device exists or the audio service is unavailable.</summary>
    VolumeState? GetState();

    bool SetLevel(float level);

    bool SetMute(bool muted);
}

public sealed record ClipboardEntry(string Id, string Text, DateTimeOffset CopiedAt)
{
    public string Preview => Text.Length <= 120 ? Text.ReplaceLineEndings(" ") : Text[..117].ReplaceLineEndings(" ") + "…";
}

/// <summary>
/// Clipboard text history. It only listens while <see cref="Start"/> is active (explicit opt-in), keeps
/// entries in memory only, and skips content marked as sensitive by the source app.
/// </summary>
public interface IClipboardService
{
    event EventHandler<ClipboardEntry>? TextCopied;

    bool IsListening { get; }

    bool Start();

    void Stop();

    /// <summary>Puts text back on the clipboard without recording it again.</summary>
    void SetText(string text);
}

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string Title
    {
        get
        {
            var line = Text.Split('\n', 2)[0].Trim();
            return line.Length == 0 ? "(empty note)" : line.Length > 60 ? line[..57] + "…" : line;
        }
    }
}

/// <summary>Local notes storage (notes.json). Thread-safe.</summary>
public interface INotesStore
{
    event EventHandler? Changed;

    /// <summary>Most recently updated first.</summary>
    IReadOnlyList<Note> GetAll();

    Note Save(Note note);

    bool Delete(string id);
}

// ---------------------------------------------------------------- file search (Everything)

public sealed record FileSearchHit(string Name, string Directory, bool IsFolder)
{
    public string FullPath => string.IsNullOrEmpty(Directory) ? Name : System.IO.Path.Combine(Directory, Name);
}

public sealed record FileSearchResult(IReadOnlyList<FileSearchHit> Hits, int TotalCount);

public enum FileSearchAvailability
{
    /// <summary>The search engine is running and accepts queries.</summary>
    Ready,

    /// <summary>Installed (executable found) but not running.</summary>
    NotRunning,

    /// <summary>Neither running nor found on disk.</summary>
    NotInstalled,
}

/// <summary>Instant file-name search backed by an external indexer (voidtools Everything).</summary>
public interface IFileSearchService
{
    string ProviderName { get; }

    /// <summary>Optional user-supplied path to the engine's executable; null/empty = auto-detect.</summary>
    string? ExecutableOverride { get; set; }

    FileSearchAvailability GetAvailability();

    /// <summary>Runs a query; throws <see cref="InvalidOperationException"/> when the engine isn't running.</summary>
    Task<FileSearchResult> SearchAsync(string query, int maxResults, bool matchPath, CancellationToken cancellationToken);

    /// <summary>Opens the engine's own search window, optionally pre-filled. Returns false if it can't be found.</summary>
    bool OpenProviderWindow(string? query);

    /// <summary>Starts the engine in the background. Returns false if the executable can't be found.</summary>
    bool StartProvider();
}
