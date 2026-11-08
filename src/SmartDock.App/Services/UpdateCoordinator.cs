using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SmartDock.Core.Services;
using SmartDock.Core.Updates;

namespace SmartDock.App.Services;

/// <summary>
/// Schedules update checks (when a feed is set and automatic checks are on), announces new versions once,
/// and exposes check/install commands for Settings and the tray menu. Installing always needs a click.
/// </summary>
public sealed class UpdateCoordinator : ObservableObject, IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SoonDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinGapAfterWake = TimeSpan.FromMinutes(15);

    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly ILogger<UpdateCoordinator> _logger;
    private readonly DispatcherTimer _timer = new();
    private string? _announcedVersion;
    private string? _reportedFailure;
    private string _scheduledFeed = string.Empty;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private bool _busy;
    private string _statusText;

    public UpdateCoordinator(IUpdateService updates, ISettingsService settings, INotificationService notifications, ILogger<UpdateCoordinator> logger)
    {
        _updates = updates;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        _statusText = updates.Last.Message;
        CheckNowCommand = new AsyncRelayCommand(CheckNowAsync, () => !_busy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !_busy && AvailableVersion is not null);
        _timer.Tick += async (_, _) => await ScheduledCheckAsync();
        _updates.StatusChanged += (_, result) => Application.Current?.Dispatcher.BeginInvoke(() => Apply(result));
        _settings.Changed += (_, _) => Reschedule();

        // After sleep or when the PC is unlocked the 60-minute timer may be far off: check again (not too often).
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Application.Current?.Dispatcher.BeginInvoke(CheckSoonAfterWake);
            }
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
            {
                Application.Current?.Dispatcher.BeginInvoke(CheckSoonAfterWake);
            }
        };
    }

    /// <summary>Raised on the UI thread when an update becomes (un)available, e.g. for the tray menu.</summary>
    public event Action<string?>? AvailabilityChanged;

    public AsyncRelayCommand CheckNowCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    /// <summary>"1.13.0" when a newer version is available, otherwise null.</summary>
    public string? AvailableVersion => _updates.Last is { Status: UpdateStatus.Available, Manifest: { } m } ? m.Version.ToString(3) : null;

    public bool IsUpdateAvailable => AvailableVersion is not null;

    public string ReleaseNotes => _updates.Last.Manifest?.Notes ?? string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Start() => Reschedule(initial: true);

    public void Dispose() => _timer.Stop();

    public async Task CheckNowAsync()
    {
        SetBusy(true);
        StatusText = "Checking…";
        try
        {
            await _updates.CheckAsync(CancellationToken.None);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task InstallAsync()
    {
        if (_updates.Last.Manifest is not { } manifest || AvailableVersion is null)
        {
            return;
        }

        SetBusy(true);
        StatusText = $"Downloading v{AvailableVersion}…";
        try
        {
            await _updates.InstallAsync(manifest, CancellationToken.None);
            StatusText = $"Installing v{AvailableVersion}… SmartDock will close and restart automatically.";
        }
        catch (UpdateException ex)
        {
            StatusText = ex.Message;
            _notifications.Post("Update not installed", ex.Message, NotificationSeverity.Warning, "updates");
            SetBusy(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Update install failed");
            StatusText = "Update failed: " + ex.Message;
            SetBusy(false);
        }
    }

    private async Task ScheduledCheckAsync()
    {
        _timer.Interval = TimeSpan.FromMinutes(_settings.Current.Updates.CheckIntervalMinutes);
        if (!_busy)
        {
            await CheckNowAsync();
        }
    }

    private void Reschedule(bool initial = false)
    {
        var config = _settings.Current.Updates;
        if (!config.CheckAutomatically || config.FeedLocation.Length == 0)
        {
            _timer.Stop();
            _scheduledFeed = config.FeedLocation;
            return;
        }

        // First run after start-up, automatic checks just switched on, or a new/corrected location: check soon.
        var feedChanged = !string.Equals(_scheduledFeed, config.FeedLocation, StringComparison.Ordinal);
        _scheduledFeed = config.FeedLocation;
        if (initial || !_timer.IsEnabled || feedChanged)
        {
            _timer.Stop();
            _timer.Interval = initial ? FirstCheckDelay : SoonDelay;
            _timer.Start();
        }
    }

    private void CheckSoonAfterWake()
    {
        var config = _settings.Current.Updates;
        if (!config.CheckAutomatically || config.FeedLocation.Length == 0 || DateTimeOffset.Now - _lastCheck < MinGapAfterWake)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = SoonDelay; // give the network a moment to come back
        _timer.Start();
    }

    private void Apply(UpdateCheckResult result)
    {
        _lastCheck = DateTimeOffset.Now;
        StatusText = result.Message;

        // A failing automatic check used to be visible only in Settings; say it once (until it works again).
        if (result.Status == UpdateStatus.Failed)
        {
            if (result.Message != _reportedFailure)
            {
                _reportedFailure = result.Message;
                _notifications.Post("Update check failed", result.Message + "\nSee Settings › Updates.", NotificationSeverity.Warning, "updates");
            }
        }
        else
        {
            _reportedFailure = null;
        }

        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(ReleaseNotes));
        InstallCommand.NotifyCanExecuteChanged();
        AvailabilityChanged?.Invoke(AvailableVersion);

        if (AvailableVersion is { } version && version != _announcedVersion)
        {
            _announcedVersion = version;
            var notes = string.IsNullOrWhiteSpace(ReleaseNotes) ? string.Empty : "\n" + ReleaseNotes;
            _notifications.Post($"SmartDock v{version} is available",
                "Install it from the tray menu or Settings › Updates." + notes, NotificationSeverity.Info, "updates",
                dedupeKey: "update:" + version, dedupeWindow: TimeSpan.FromDays(1));
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckNowCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
}
