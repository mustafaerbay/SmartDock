using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Web;

public enum SiteHealth
{
    Pending,
    Up,
    Slow,
    Down,
}

public sealed record SiteDefinition(string Name, Uri Url);

/// <summary>One monitored page as shown in the popup.</summary>
public sealed class SiteStatus : ObservableObject
{
    private SiteHealth _health = SiteHealth.Pending;
    private string _detail = "Not checked yet";
    private string _lastChecked = string.Empty;
    private string _uptime = string.Empty;

    public SiteStatus(SiteDefinition definition)
    {
        Definition = definition;
        OpenCommand = new RelayCommand(() => SafeLauncher.OpenWebUrl(definition.Url.AbsoluteUri));
    }

    public SiteDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Host => Definition.Url.Host;

    public RelayCommand OpenCommand { get; }

    public SiteHealth Health
    {
        get => _health;
        set => SetProperty(ref _health, value);
    }

    /// <summary>"200 OK · 142 ms", "Timed out after 10 s", "DNS lookup failed", ...</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public string LastChecked
    {
        get => _lastChecked;
        set => SetProperty(ref _lastChecked, value);
    }

    /// <summary>Share of successful checks since SmartDock started, e.g. "99.2% up".</summary>
    public string Uptime
    {
        get => _uptime;
        set => SetProperty(ref _uptime, value);
    }

    internal int ConsecutiveFailures { get; set; }

    internal int Checks { get; set; }

    internal int Successes { get; set; }

    /// <summary>Last state the user was notified about (Up/Down), so recoveries and outages notify once.</summary>
    internal SiteHealth? NotifiedHealth { get; set; }
}

/// <summary>
/// Checks a list of web pages on an interval and shows whether they're up, slow or down, with the HTTP status
/// and response time. Only the URLs the user enters are requested (plain GET, no credentials sent), and an
/// outage notification needs two failed checks in a row so a single blip doesn't alert.
/// </summary>
public sealed class WebStatusWidget(HttpClient http) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "web-status";
    public const int MaxSites = 20;

    private const string StrictMode = "2xx–3xx only";
    private const string LenientMode = "Any response below 500 (e.g. a login page's 401)";
    private const int FailuresBeforeAlert = 2;

    private IReadOnlyList<SiteStatus> _sites = [];
    private string _summary = "No sites";
    private SiteHealth _overall = SiteHealth.Pending;
    private RelayCommand? _checkNow;
    private TimeSpan _interval = TimeSpan.FromSeconds(60);

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Web page status",
        "Watches web pages or services and shows if they're up, slow or down, with response times. Alerts on outages and recoveries.",
        WidgetCategory.Developer,
        "",
        DockRegion.Right,
        sp => new WebStatusWidget(sp.GetRequiredService<HttpClient>()))
    {
        AllowMultiple = true, // e.g. one per team or environment.
    };

    public IReadOnlyList<SiteStatus> Sites
    {
        get => _sites;
        private set => SetProperty(ref _sites, value);
    }

    /// <summary>Card text: "3/3 up", "1 down", "2 slow".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Worst state across all sites, used for the card's status colour.</summary>
    public SiteHealth Overall
    {
        get => _overall;
        private set => SetProperty(ref _overall, value);
    }

    public RelayCommand CheckNowCommand => _checkNow ??= new RelayCommand(() => Context.RequestRefresh?.Invoke());

    /// <summary>Updated from the settings on every refresh (the host may read it before the first one).</summary>
    public override TimeSpan RefreshInterval => _interval;

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Check now", CheckNowCommand),
    ];

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("sites", "Pages to watch (one per line: Name | URL)", SettingFieldKind.MultilineText,
            $"http:// or https:// only, up to {MaxSites}. The name is optional, e.g. \"Wiki | https://wiki.example.com\".",
            DefaultText: string.Empty),
        new("intervalSeconds", "Check every (seconds)", SettingFieldKind.Number, "30–3600.", DefaultNumber: 60),
        new("timeoutSeconds", "Time out after (seconds)", SettingFieldKind.Number, "3–15.", DefaultNumber: 10),
        new("slowMs", "Mark as slow above (milliseconds)", SettingFieldKind.Number, DefaultNumber: 2000),
        new("upCodes", "Count as up", SettingFieldKind.Choice, Choices: [StrictMode, LenientMode], DefaultText: StrictMode),
        new("notify", "Notify me when a page goes down or recovers", SettingFieldKind.Toggle, DefaultToggle: true),
    ];

    public void OnPopupOpened()
    {
    }

    /// <summary>Parses the "Name | URL" list; public for tests.</summary>
    public static IReadOnlyList<SiteDefinition> Parse(string? text)
    {
        var list = new List<SiteDefinition>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var bar = line.LastIndexOf('|');
            var name = bar >= 0 ? line[..bar].Trim() : string.Empty;
            var target = bar >= 0 ? line[(bar + 1)..].Trim() : line;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var site = new SiteDefinition(name.Length > 0 ? name : url.Host, url);
            if (list.Contains(site))
            {
                continue;
            }

            list.Add(site);
            if (list.Count == MaxSites)
            {
                break;
            }
        }

        return list;
    }

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        _interval = TimeSpan.FromSeconds(Math.Clamp(Settings.Get("intervalSeconds", 60.0), 30, 3600));
        var definitions = Parse(Settings.Get("sites", string.Empty));
        if (definitions.Count == 0)
        {
            Sites = [];
            Summary = "No sites";
            Overall = SiteHealth.Pending;
            SetUnavailable("Add the pages to watch: right-click › Configure….");
            return;
        }

        // Keep existing rows (and their uptime counters) for unchanged entries.
        var previous = Sites.GroupBy(s => s.Definition).ToDictionary(g => g.Key, g => g.First());
        var sites = definitions.Select(d => previous.GetValueOrDefault(d) ?? new SiteStatus(d)).ToList();
        if (!sites.SequenceEqual(Sites))
        {
            Sites = sites;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(Settings.Get("timeoutSeconds", 10.0), 3, 15));
        var slow = TimeSpan.FromMilliseconds(Math.Max(100, Settings.Get("slowMs", 2000.0)));
        var lenient = Settings.Get("upCodes", StrictMode) == LenientMode;

        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(sites.Select(async site =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckAsync(site, timeout, slow, lenient, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var down = sites.Count(s => s.Health == SiteHealth.Down);
        var slowCount = sites.Count(s => s.Health == SiteHealth.Slow);
        Overall = down > 0 ? SiteHealth.Down : slowCount > 0 ? SiteHealth.Slow : SiteHealth.Up;
        Summary = down > 0 ? $"{down} down" : slowCount > 0 ? $"{slowCount} slow" : $"{sites.Count}/{sites.Count} up";
        SetReady();
        LastUpdated = DateTimeOffset.Now;
    }

    private async Task CheckAsync(SiteStatus site, TimeSpan timeout, TimeSpan slow, bool lenient, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        SiteHealth health;
        string detail;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, site.Definition.Url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            // Headers are enough to judge the status; the body is not downloaded.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var code = (int)response.StatusCode;
            var up = code is >= 200 and < 400 || (lenient && code < 500);
            var ms = stopwatch.Elapsed.TotalMilliseconds;
            health = !up ? SiteHealth.Down : stopwatch.Elapsed > slow ? SiteHealth.Slow : SiteHealth.Up;
            detail = $"{code} {Reason(response)} · {ms:0} ms";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            health = SiteHealth.Down;
            detail = $"No answer within {timeout.TotalSeconds:0} s";
        }
        catch (HttpRequestException ex)
        {
            health = SiteHealth.Down;
            detail = Describe(ex);
        }

        site.Checks++;
        if (health != SiteHealth.Down)
        {
            site.Successes++;
            site.ConsecutiveFailures = 0;
        }
        else
        {
            site.ConsecutiveFailures++;
        }

        // A single failed check shows as Down but only alerts after FailuresBeforeAlert in a row.
        site.Health = health;
        site.Detail = detail;
        site.LastChecked = "Checked " + DateTimeOffset.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        site.Uptime = $"{100.0 * site.Successes / site.Checks:0.#}% up";
        Notify(site);
    }

    private void Notify(SiteStatus site)
    {
        var state = site.Health == SiteHealth.Down
            ? site.ConsecutiveFailures >= FailuresBeforeAlert ? SiteHealth.Down : (SiteHealth?)null
            : SiteHealth.Up;
        if (state is null || state == site.NotifiedHealth)
        {
            return;
        }

        var first = site.NotifiedHealth is null;
        site.NotifiedHealth = state;
        if (!Settings.Get("notify", true) || (first && state == SiteHealth.Up))
        {
            return; // Don't announce "up" for sites that were simply fine at start-up.
        }

        if (state == SiteHealth.Down)
        {
            Context.Notifications.Post($"{site.Name} is down", site.Detail, NotificationSeverity.Error, InstanceId,
                site.Definition.Url.AbsoluteUri, dedupeKey: $"web:{InstanceId}:{site.Definition.Url}:down", dedupeWindow: TimeSpan.FromMinutes(5));
        }
        else
        {
            Context.Notifications.Post($"{site.Name} is back up", site.Detail, NotificationSeverity.Success, InstanceId,
                site.Definition.Url.AbsoluteUri, dedupeKey: $"web:{InstanceId}:{site.Definition.Url}:up", dedupeWindow: TimeSpan.FromMinutes(5));
        }
    }

    private static string Reason(HttpResponseMessage response) =>
        string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase!;

    private static string Describe(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "DNS lookup failed",
        HttpRequestError.ConnectionError => "Couldn't connect",
        HttpRequestError.SecureConnectionError => "TLS/certificate error",
        HttpRequestError.ProxyTunnelError => "Proxy refused the connection",
        _ => ex.StatusCode is HttpStatusCode code ? $"HTTP {(int)code}" : ex.Message,
    };
}
