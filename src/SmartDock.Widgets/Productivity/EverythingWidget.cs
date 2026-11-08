using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Productivity;

/// <summary>
/// Instant file search through voidtools Everything (must be installed separately; it's free).
/// Click opens SmartDock's search window, which queries the running Everything instance as you type.
/// When Everything isn't running the widget says so and offers to start it - it never pretends to search.
/// </summary>
public sealed class EverythingWidget(IFileSearchService search) : WidgetBase, IClickableWidget, IConfigurableWidget
{
    public const string TypeId = "everything";

    private const string ClickActionSearch = "SmartDock search window";
    private const string ClickActionEverything = "Everything's own window";

    private string _statusText = "Checking…";
    private FileSearchAvailability _availability = FileSearchAvailability.NotInstalled;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Everything search",
        "Instant file and folder search powered by voidtools Everything (free, installed separately). Click to search.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        sp => new EverythingWidget(sp.GetRequiredService<IFileSearchService>()));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(15);

    public override IReadOnlyList<WidgetMenuItem> MenuItems
    {
        get
        {
            var items = new List<WidgetMenuItem>
            {
                new("Search files…", new RelayCommand(OpenSearch)),
            };

            if (_availability == FileSearchAvailability.Ready)
            {
                items.Add(new("Open Everything", new RelayCommand(() => search.OpenProviderWindow(null))));
            }
            else if (_availability == FileSearchAvailability.NotRunning)
            {
                items.Add(new("Start Everything", new RelayCommand(StartEverything)));
            }

            return items;
        }
    }

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("clickAction", "When I click the widget, open", SettingFieldKind.Choice,
            Choices: [ClickActionSearch, ClickActionEverything], DefaultText: ClickActionSearch),
        new("maxResults", "Results to show", SettingFieldKind.Number, "1–500.", DefaultNumber: 100),
        new("matchPath", "Match the whole path, not just the name", SettingFieldKind.Toggle),
        new("executablePath", "Everything.exe location (optional)", SettingFieldKind.Text,
            "Leave empty to detect it automatically (running instance, then Program Files)."),
    ];

    public void OnClick()
    {
        if (Settings.Get("clickAction", ClickActionSearch) == ClickActionEverything && search.OpenProviderWindow(null))
        {
            return;
        }

        OpenSearch();
    }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        ApplyExecutableOverride();
        return Task.CompletedTask;
    }

    protected override void OnSettingChanged(string key)
    {
        if (key == "executablePath")
        {
            ApplyExecutableOverride();
        }

        base.OnSettingChanged(key);
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var availability = search.GetAvailability();
        var changed = availability != _availability;
        _availability = availability;
        switch (availability)
        {
            case FileSearchAvailability.Ready:
                StatusText = "Search files";
                SetReady();
                break;
            case FileSearchAvailability.NotRunning:
                StatusText = "Not running";
                SetUnavailable("Everything is installed but not running. Right-click › Start Everything.");
                break;
            default:
                StatusText = "Not installed";
                SetUnavailable("Everything wasn't found. Install it from voidtools.com, or set its location in Configure….");
                break;
        }

        if (changed)
        {
            PostToUi(() => OnPropertyChanged(nameof(MenuItems)));
        }

        return Task.CompletedTask;
    }

    private void OpenSearch() => Context.Events.Publish(new OpenFileSearchRequested(
        null,
        (int)Math.Clamp(Settings.Get("maxResults", 100.0), 1, 500),
        Settings.Get("matchPath", false)));

    private void StartEverything()
    {
        if (!search.StartProvider())
        {
            SetUnavailable("Couldn't start Everything. Check its location in Configure….");
            return;
        }

        StatusText = "Starting…";

        // Everything needs a moment to create its IPC window; check again shortly.
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Context.RequestRefresh?.Invoke(), TaskScheduler.Default);
    }

    private void ApplyExecutableOverride() => search.ExecutableOverride = Settings.Get<string?>("executablePath", null);
}
