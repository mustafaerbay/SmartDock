using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SmartDock.App.Services;
using SmartDock.App.ViewModels;
using SmartDock.App.Views;
using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Infrastructure.Configuration;
using SmartDock.Infrastructure.Persistence;
using SmartDock.Widgets;
using SmartDock.Windows.Media;
using SmartDock.Windows.SystemIntegration;
using SmartDock.Windows.Taskbar;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SmartDock.App;

/// <summary>Composition root and lifetime owner: DI, logging, dock window, tray, taskbar mode and shutdown.</summary>
public partial class App : Application
{
    private const string SingleInstanceName = @"Local\SmartDock.SingleInstance";

    /// <summary>Signalled by <c>SmartDock.exe --shutdown</c> (used by the installer) to ask this instance to exit cleanly.</summary>
    public const string ShutdownEventName = @"Local\SmartDock.Shutdown";
    private EventWaitHandle? _shutdownSignal;
    private RegisteredWaitHandle? _shutdownWait;

    private readonly AppPaths _paths;
    private readonly List<DateTime> _recentUiErrors = [];
    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private ILogger? _logger;
    private DockWindow? _dock;
    private TrayIconHost? _tray;
    private TaskbarController? _taskbar;
    private ISettingsService? _settings;
    private SettingsWindow? _settingsWindow;
    private WidgetManagerWindow? _managerWindow;
    private NotesWindow? _notesWindow;
    private FileSearchWindow? _fileSearchWindow;
    private BookmarkSearchWindow? _bookmarkSearchWindow;
    private MergeRequestSearchWindow? _mergeRequestSearchWindow;
    private readonly Dictionary<string, KubernetesExplorerWindow> _kubernetesWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WidgetConfigWindow> _configWindows = new(StringComparer.Ordinal);
    private Process? _watchdog;
    private bool _shuttingDown;

    public App()
        : this(new AppPaths())
    {
    }

    public App(AppPaths paths)
    {
        _paths = paths;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths.EnsureCreated();
        ConfigureLogging();

        _singleInstance = new Mutex(true, SingleInstanceName, out var createdNew);
        if (!createdNew)
        {
            Log.Information("SmartDock is already running; exiting");
            Shutdown();
            return;
        }

        // Installer/upgrade support: exit cleanly (restoring the taskbar) when asked via the named event.
        _shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _shutdownWait = ThreadPool.RegisterWaitForSingleObject(_shutdownSignal, (_, _) =>
        {
            Log.Information("Shutdown requested by another process (installer)");
            Dispatcher.BeginInvoke(() => Shutdown());
        }, null, Timeout.Infinite, executeOnlyOnce: true);

        // A previous run that crashed while in replacement mode left the taskbar hidden: fix that first.
        if (TaskbarController.ReadRecovery(_paths.TaskbarRecoveryFile) is not null)
        {
            Log.Warning("Found taskbar recovery record from a previous run; restoring taskbar");
            TaskbarController.RestoreFromRecoveryFile(_paths.TaskbarRecoveryFile);
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            _services = ConfigureServices();
            _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("SmartDock");
            _settings = _services.GetRequiredService<ISettingsService>();
            _taskbar = _services.GetRequiredService<TaskbarController>();

            var theme = _services.GetRequiredService<ThemeService>();
            theme.Apply(_settings.Current.Appearance);

            AppCommands.ShowSettings = ShowSettings;
            AppCommands.ShowWidgetManager = ShowWidgetManager;
            AppCommands.ConfigureWidget = ConfigureWidget;
            _services.GetRequiredService<IEventBus>().Subscribe<OpenNotesRequested>(e => Dispatcher.BeginInvoke(() => ShowNotes(e.NoteId == "new")));
            _services.GetRequiredService<IEventBus>().Subscribe<OpenFileSearchRequested>(e => Dispatcher.BeginInvoke(() => ShowFileSearch(e)));
            _services.GetRequiredService<IEventBus>().Subscribe<OpenBookmarkSearchRequested>(e => Dispatcher.BeginInvoke(() => ShowBookmarkSearch(e)));
            _services.GetRequiredService<IEventBus>().Subscribe<SmartDock.Widgets.CodeRepo.OpenMergeRequestSearchRequested>(e => Dispatcher.BeginInvoke(() => ShowMergeRequestSearch(e)));
            _services.GetRequiredService<IEventBus>().Subscribe<SmartDock.Widgets.Cluster.OpenKubernetesExplorerRequested>(e => Dispatcher.BeginInvoke(() => ShowKubernetesExplorer(e)));

            var dockVm = _services.GetRequiredService<DockViewModel>();
            _dock = _services.GetRequiredService<DockWindow>();
            _dock.ExplorerRestarted += (_, _) => OnExplorerRestarted();
            _dock.WatchTick += (_, _) => EnforceReplacementMode();
            _dock.Show();

            var notifications = _services.GetRequiredService<INotificationService>();
            notifications.Posted += (_, n) => Dispatcher.BeginInvoke(() => _dock?.ShowToast(n));
            notifications.HistoryChanged += (_, _) => Dispatcher.BeginInvoke(dockVm.SyncNotifications);

            _settings.Changed += OnSettingsChanged;

            _tray = _services.GetRequiredService<TrayIconHost>();
            _tray.ShowDockRequested += ShowDock;
            _tray.WidgetManagerRequested += ShowWidgetManager;
            _tray.SettingsRequested += ShowSettings;
            _tray.ModeRequested += mode => _settings.Update(s => s.Behavior.Mode = mode);
            _tray.RestartRequested += Restart;
            _tray.ExitRequested += () => Shutdown();
            var updates = _services.GetRequiredService<UpdateCoordinator>();
            _tray.CheckUpdatesRequested += () => _ = updates.CheckNowAsync();
            _tray.InstallUpdateRequested += () => _ = updates.InstallAsync();
            updates.AvailabilityChanged += version => _tray?.SetUpdateAvailable(version);
            _tray.Start(_settings.Current.Behavior.Mode);
            updates.Start();

            StartWatchdog();
            ApplyTaskbarMode(_settings.Current.Behavior.Mode);
            SyncStartupRegistration(_settings.Current.Behavior.StartWithWindows);

            await dockVm.LoadAsync();
            _services.GetRequiredService<WidgetHost>().Start();
            _logger.LogInformation("SmartDock started (mode {Mode})", _settings.Current.Behavior.Mode);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            _taskbar?.Restore();
            MessageBox.Show($"SmartDock could not start:\n\n{ex.Message}\n\nDetails were written to {_paths.LogsDirectory}.", "SmartDock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;

        // Restore first: everything after this is best effort.
        _taskbar?.Restore();
        _dock?.ReleaseScreenSpace();

        try
        {
            _services?.GetService<ILayoutService>()?.Flush();
            _tray?.Dispose();

            // Async disposal (WidgetHost is IAsyncDisposable); bounded so a stuck widget cannot block exit.
            _services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during shutdown");
        }
        finally
        {
            StopWatchdog();
            _shutdownWait?.Unregister(null);
            _shutdownSignal?.Dispose();
            if (_singleInstance is not null)
            {
                try
                {
                    _singleInstance.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _singleInstance.Dispose();
            }

            Log.Information("SmartDock exited");
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton(_paths);
        services.AddSingleton(TimeProvider.System);

        services.AddBuiltInWidgets();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ILayoutService, LayoutService>();
        services.AddSingleton<WidgetHost>();

        services.AddSingleton<ISystemMetrics, WindowsSystemMetrics>();
        services.AddSingleton<IVolumeService, CoreAudioVolumeService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IFileSearchService, EverythingSearchService>();
        services.AddSingleton<SmartDock.Core.Updates.IUpdateService, SmartDock.Windows.Updates.UpdateService>();
        services.AddSingleton<UpdateCoordinator>();
        services.AddSingleton<INotesStore, JsonNotesStore>();
        services.AddSingleton<IMediaService, GsmtcMediaService>();
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(_paths.SecretsDirectory));
        services.AddSingleton(sp => new TaskbarController(_paths.TaskbarRecoveryFile, sp.GetRequiredService<ILogger<TaskbarController>>()));

        services.AddSingleton<ThemeService>();
        services.AddSingleton<TrayIconHost>();
        services.AddSingleton<AppsViewModel>();
        services.AddSingleton<DockViewModel>();
        services.AddSingleton<DockWindow>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(_paths.LogsDirectory, "smartdock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // UI-thread exceptions: log and keep running. If they repeat rapidly the app is in a crash loop;
        // restore the taskbar and exit cleanly rather than leave a broken desktop.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            var now = DateTime.UtcNow;
            _recentUiErrors.Add(now);
            _recentUiErrors.RemoveAll(t => now - t > TimeSpan.FromSeconds(10));
            args.Handled = true;
            if (_recentUiErrors.Count >= 8 && !_shuttingDown)
            {
                Log.Fatal("Repeated UI exceptions; shutting down safely");
                _taskbar?.Restore();
                Shutdown(2);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Fatal unhandled exception");
            try
            {
                _taskbar?.Restore();
                _dock?.ReleaseScreenSpace();
            }
            finally
            {
                Log.CloseAndFlush();
            }
        };
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var previous = e.Previous;
            var current = e.Current;
            _services!.GetRequiredService<ThemeService>().Apply(current.Appearance);
            _dock?.ApplySettings(current);

            if (previous.Behavior.Mode != current.Behavior.Mode)
            {
                ApplyTaskbarMode(current.Behavior.Mode);
            }

            if (previous.Behavior.StartWithWindows != current.Behavior.StartWithWindows)
            {
                SyncStartupRegistration(current.Behavior.StartWithWindows);
            }
        });
    }

    private void ApplyTaskbarMode(TaskbarMode mode)
    {
        if (_taskbar is null || _settings is null)
        {
            return;
        }

        if (mode == TaskbarMode.Replacement)
        {
            if (!_taskbar.Hide())
            {
                // Could not hide reliably: stay usable in companion mode and tell the user.
                FallBackToCompanion();
                return;
            }
        }
        else
        {
            _taskbar.Restore();
        }

        _tray?.SetMode(mode);
        _dock?.Reposition();
    }

    private void EnforceReplacementMode()
    {
        if (_taskbar is null || _settings?.Current.Behavior.Mode != TaskbarMode.Replacement)
        {
            return;
        }

        if (!_taskbar.Reapply())
        {
            FallBackToCompanion();
        }
    }

    private void FallBackToCompanion()
    {
        _services!.GetRequiredService<INotificationService>().Post(
            "Replacement mode unavailable",
            "The Windows taskbar could not be hidden safely on this system, so SmartDock stays in companion mode.",
            NotificationSeverity.Warning,
            dedupeKey: "replacement-failed",
            dedupeWindow: TimeSpan.FromMinutes(1));
        _settings?.Update(s => s.Behavior.Mode = TaskbarMode.Companion);
    }

    private void OnExplorerRestarted()
    {
        if (_settings?.Current.Behavior.Mode == TaskbarMode.Replacement)
        {
            // Explorer recreated a visible taskbar; give it a moment to finish, then hide it again.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                EnforceReplacementMode();
                _dock?.Reposition();
            };
            timer.Start();
        }
        else
        {
            _dock?.Reposition();
        }
    }

    private void SyncStartupRegistration(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && StartupRegistration.IsEnabled() != enabled)
            {
                StartupRegistration.Set(enabled, exe);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger?.LogWarning(ex, "Could not update the startup registration");
        }
    }

    /// <summary>Launches "SmartDock.exe --watchdog PID" which restores the taskbar if this process dies.</summary>
    private void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            _watchdog = Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Could not start the taskbar watchdog");
        }
    }

    private void StopWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false })
            {
                // Normal exit already restored the taskbar; the watchdog is no longer needed.
                _watchdog.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            _watchdog?.Dispose();
        }
    }

    private void ShowDock()
    {
        if (_dock is null)
        {
            return;
        }

        _dock.Show();
        _dock.ApplySettings(_settings!.Current);
    }

    private void ShowSettings() => OpenWindowSafely("Settings", () =>
    {
        if (_settingsWindow is not { IsLoaded: true })
        {
            var window = new SettingsWindow(_services!.GetRequiredService<SettingsViewModel>(), _paths.SettingsFile);
            window.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow = window;
        }

        WindowStyling.BringToFront(_settingsWindow);
    });

    /// <summary>
    /// Opens one of the app's windows. A failure is logged and shown as a dock notification instead of
    /// being swallowed by the global handler, so a button never just "does nothing".
    /// </summary>
    private void OpenWindowSafely(string name, Action open)
    {
        try
        {
            // The popup that asked for this window (e.g. CodeHub › Search) would otherwise stay open behind it.
            SmartDock.App.Controls.WidgetCard.CloseAllPopups();
            open();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the {Window} window failed", name);
            _services?.GetService<INotificationService>()?.Post(
                $"Couldn't open {name}",
                $"{ex.GetType().Name}: {ex.Message} — details in {_paths.LogsDirectory}",
                NotificationSeverity.Error);
        }
    }

    private void ShowWidgetManager() => OpenWindowSafely("Widgets", () =>
    {
        if (_managerWindow is not { IsLoaded: true })
        {
            // Created outside the container so the disposable view model is not retained by the root provider.
            var window = new WidgetManagerWindow(ActivatorUtilities.CreateInstance<WidgetManagerViewModel>(_services!));
            window.Closed += (_, _) => _managerWindow = null;
            _managerWindow = window;
        }

        WindowStyling.BringToFront(_managerWindow);
    });

    private void ConfigureWidget(string instanceId)
    {
        if (_configWindows.TryGetValue(instanceId, out var open))
        {
            WindowStyling.BringToFront(open);
            return;
        }

        var dock = _services!.GetRequiredService<DockViewModel>();
        if (dock.Find(instanceId, out _) is not { } widget || widget is not IConfigurableWidget configurable || dock.GetSettings(instanceId) is not { } settings)
        {
            return;
        }

        var window = new WidgetConfigWindow(widget, configurable, settings, _services!.GetRequiredService<ISecretStore>(), () => dock.RefreshWidget(instanceId));
        window.Closed += (_, _) => _configWindows.Remove(instanceId);
        _configWindows[instanceId] = window;
        WindowStyling.BringToFront(window);
    }

    private void ShowFileSearch(OpenFileSearchRequested request) => OpenWindowSafely("file search", () =>
    {
        if (_fileSearchWindow is not { IsLoaded: true })
        {
            var window = new FileSearchWindow(_services!.GetRequiredService<IFileSearchService>(), request.MaxResults, request.MatchPath);
            window.Closed += (_, _) => _fileSearchWindow = null;
            _fileSearchWindow = window;
        }

        WindowStyling.BringToFront(_fileSearchWindow);
        _fileSearchWindow.Prepare(request.Query, request.MaxResults, request.MatchPath);
    });

    private void ShowBookmarkSearch(OpenBookmarkSearchRequested request) => OpenWindowSafely("bookmark search", () =>
    {
        if (_bookmarkSearchWindow is not { IsLoaded: true })
        {
            var window = new BookmarkSearchWindow();
            window.Closed += (_, _) => _bookmarkSearchWindow = null;
            _bookmarkSearchWindow = window;
        }

        WindowStyling.BringToFront(_bookmarkSearchWindow);
        _bookmarkSearchWindow.Show(request);
    });

    private void ShowMergeRequestSearch(SmartDock.Widgets.CodeRepo.OpenMergeRequestSearchRequested request) => OpenWindowSafely("merge request search", () =>
    {
        if (_mergeRequestSearchWindow is not { IsLoaded: true })
        {
            var window = new MergeRequestSearchWindow();
            window.Closed += (_, _) => _mergeRequestSearchWindow = null;
            _mergeRequestSearchWindow = window;
        }

        WindowStyling.BringToFront(_mergeRequestSearchWindow);
        _mergeRequestSearchWindow.Show(request);
    });

    /// <summary>One explorer window per cluster; asking again focuses it (and jumps to the requested pod).</summary>
    private void ShowKubernetesExplorer(SmartDock.Widgets.Cluster.OpenKubernetesExplorerRequested request) => OpenWindowSafely("Kubernetes explorer", () =>
    {
        var key = request.Explorer.Context + "|" + request.Title;
        if (_kubernetesWindows.TryGetValue(key, out var existing) && existing.IsLoaded)
        {
            existing.ShowRequest(request);
            WindowStyling.BringToFront(existing);
            return;
        }

        var window = new KubernetesExplorerWindow(request);
        window.Closed += (_, _) => _kubernetesWindows.Remove(key);
        _kubernetesWindows[key] = window;
        WindowStyling.BringToFront(window);
    });

    private void ShowNotes(bool createNew)
    {
        if (_notesWindow is not { IsLoaded: true })
        {
            _notesWindow = new NotesWindow(_services!.GetRequiredService<INotesStore>());
            _notesWindow.Closed += (_, _) => _notesWindow = null;
        }

        WindowStyling.BringToFront(_notesWindow);
        if (createNew)
        {
            _notesWindow.NewNote();
        }
    }

    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
        }

        Shutdown();
    }
}
