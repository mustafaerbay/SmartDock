using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Media;

/// <summary>Now-playing card with transport controls for whichever app owns the current OS media session.</summary>
public sealed class MediaWidget : WidgetBase
{
    public const string TypeId = "media";

    // Concurrent execution keeps the toolkit from raising CanExecuteChanged from a background
    // continuation when a command finishes (WPF requires it on the UI thread).
    private const AsyncRelayCommandOptions Concurrent = AsyncRelayCommandOptions.AllowConcurrentExecutions;

    private readonly IMediaService _media;
    private MediaSnapshot? _snapshot;

    public MediaWidget(IMediaService media)
    {
        _media = media;
        PlayPauseCommand = new AsyncRelayCommand(() => _media.TogglePlayPauseAsync(), () => _snapshot?.CanPlayPause ?? false, Concurrent);
        NextCommand = new AsyncRelayCommand(() => _media.NextAsync(), () => _snapshot?.CanGoNext ?? false, Concurrent);
        PreviousCommand = new AsyncRelayCommand(() => _media.PreviousAsync(), () => _snapshot?.CanGoPrevious ?? false, Concurrent);
    }

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Music",
        "Now playing, artwork and play/pause/next/previous for any app that exposes a Windows media session.",
        WidgetCategory.Media,
        "",
        DockRegion.Center,
        sp => new MediaWidget(sp.GetRequiredService<IMediaService>()));

    public AsyncRelayCommand PlayPauseCommand { get; }

    public AsyncRelayCommand NextCommand { get; }

    public AsyncRelayCommand PreviousCommand { get; }

    public bool HasSession => _snapshot is not null;

    public string Title => _snapshot?.Title is { Length: > 0 } t ? t : (HasSession ? "Unknown title" : "Nothing playing");

    public string Subtitle => _snapshot is null ? "Start music in any app" : (_snapshot.Artist is { Length: > 0 } a ? a : FriendlySource(_snapshot.SourceAppId));

    public string SourceApp => FriendlySource(_snapshot?.SourceAppId);

    public bool IsPlaying => _snapshot?.IsPlaying ?? false;

    /// <summary>Encoded artwork (PNG/JPEG) or null; the view converts it to an image.</summary>
    public byte[]? Thumbnail => _snapshot?.Thumbnail;

    /// <summary>Event-driven; the long interval is only a safety net for missed session events.</summary>
    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _media.Changed += OnMediaChanged;
        await _media.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        if (!_media.IsSupported)
        {
            SetUnavailable("Windows media sessions are not available on this system");
            return Task.CompletedTask;
        }

        Apply(_media.Current);
        SetReady();
        return Task.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync()
    {
        _media.Changed -= OnMediaChanged;
        return ValueTask.CompletedTask;
    }

    private void OnMediaChanged(object? sender, EventArgs e) => Apply(_media.Current);

    private void Apply(MediaSnapshot? snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SourceApp));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Thumbnail));

        // CanExecuteChanged must be raised on the UI thread or WPF throws when re-querying CanExecute.
        PostToUi(() =>
        {
            PlayPauseCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
        });
    }

    private static string FriendlySource(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return string.Empty;
        }

        // AUMIDs look like "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" or "chrome.exe".
        var name = appId;
        var bang = name.LastIndexOf('!');
        if (bang >= 0)
        {
            name = name[(bang + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }
}
