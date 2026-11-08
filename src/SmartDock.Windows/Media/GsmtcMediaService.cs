using Microsoft.Extensions.Logging;
using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using Windows.Media.Control;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace SmartDock.Windows.Media;

/// <summary>
/// Media control through Windows' Global System Media Transport Controls: works with any app that
/// publishes a media session (Spotify, browsers, Groove/Media Player, VLC 4, ...), not one hard-coded player.
/// </summary>
public sealed class GsmtcMediaService(IEventBus events, ILogger<GsmtcMediaService> logger) : IMediaService, IDisposable
{
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private GsmtcManager? _manager;
    private GsmtcSession? _session;
    private MediaSnapshot? _current;
    private bool _wasPlaying;

    public bool IsSupported { get; private set; }

    public MediaSnapshot? Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
        {
            return;
        }

        try
        {
            _manager = await GsmtcManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            IsSupported = true;
            await AttachAsync(_manager.GetCurrentSession()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IsSupported = false;
            logger.LogWarning(ex, "Media session manager unavailable");
        }
    }

    public Task<bool> TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task<bool> PauseAsync() => Run(s => s.TryPauseAsync().AsTask());

    public Task<bool> NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());

    public Task<bool> PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        Detach();
        _updateGate.Dispose();
    }

    private async Task<bool> Run(Func<GsmtcSession, Task<bool>> action)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Media command failed");
            return false;
        }
    }

    private async void OnCurrentSessionChanged(GsmtcManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            await AttachAsync(sender.GetCurrentSession()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to switch media session");
        }
    }

    private async Task AttachAsync(GsmtcSession? session)
    {
        Detach();
        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        await UpdateAsync().ConfigureAwait(false);
    }

    private void Detach()
    {
        var old = _session;
        if (old is null)
        {
            return;
        }

        old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session = null;
    }

    private async void OnMediaPropertiesChanged(GsmtcSession sender, MediaPropertiesChangedEventArgs args) => await SafeUpdateAsync().ConfigureAwait(false);

    private async void OnPlaybackInfoChanged(GsmtcSession sender, PlaybackInfoChangedEventArgs args) => await SafeUpdateAsync().ConfigureAwait(false);

    private async Task SafeUpdateAsync()
    {
        try
        {
            await UpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Media update failed");
        }
    }

    private async Task UpdateAsync()
    {
        await _updateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            MediaSnapshot? snapshot = null;
            if (session is not null)
            {
                var playback = session.GetPlaybackInfo();
                var controls = playback?.Controls;
                var isPlaying = playback?.PlaybackStatus == PlaybackStatus.Playing;

                string? title = null;
                string? artist = null;
                string? album = null;
                byte[]? thumbnail = null;
                try
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    title = props?.Title;
                    artist = props?.Artist;
                    album = props?.AlbumTitle;
                    thumbnail = await ReadThumbnailAsync(props).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Some players throw while switching tracks; keep playback state anyway.
                    logger.LogDebug(ex, "Could not read media properties");
                }

                snapshot = new MediaSnapshot(
                    title,
                    artist,
                    album,
                    session.SourceAppUserModelId,
                    isPlaying,
                    controls?.IsNextEnabled ?? false,
                    controls?.IsPreviousEnabled ?? false,
                    (controls?.IsPlayPauseToggleEnabled ?? false) || (controls?.IsPlayEnabled ?? false) || (controls?.IsPauseEnabled ?? false),
                    thumbnail);
            }

            Volatile.Write(ref _current, snapshot);
            var playingNow = snapshot?.IsPlaying ?? false;
            if (playingNow != _wasPlaying)
            {
                _wasPlaying = playingNow;
                events.Publish(new MediaPlaybackChanged(playingNow, snapshot?.Title, snapshot?.Artist));
            }
        }
        finally
        {
            _updateGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props?.Thumbnail is null)
        {
            return null;
        }

        using var stream = await props.Thumbnail.OpenReadAsync();
        using var managed = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await managed.CopyToAsync(buffer).ConfigureAwait(false);

        // Guard against unexpectedly large artwork.
        return buffer.Length is > 0 and < 4 * 1024 * 1024 ? buffer.ToArray() : null;
    }
}
