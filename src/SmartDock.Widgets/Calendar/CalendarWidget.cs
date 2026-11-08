using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Calendar;

public sealed class CalendarItem
{
    public CalendarItem(CalendarEvent e, DateTimeOffset now)
    {
        Event = e;
        IsNow = e.Start <= now && e.End > now;
        var day = e.Start.Date == now.Date ? string.Empty : e.Start.Date == now.Date.AddDays(1) ? "Tomorrow " : e.Start.ToString("ddd ");
        TimeText = e.IsAllDay ? (day.Length == 0 ? "All day" : day + "· all day") : $"{day}{e.Start:HH:mm}–{e.End:HH:mm}";
        JoinCommand = new RelayCommand(() => SafeLauncher.OpenWebUrl(e.JoinUrl), () => e.JoinUrl is not null);
    }

    public CalendarEvent Event { get; }

    public string Title => Event.Title;

    public string? Location => Event.Location;

    public string TimeText { get; }

    public bool IsNow { get; }

    public bool HasJoin => Event.JoinUrl is not null;

    public RelayCommand JoinCommand { get; }
}

/// <summary>
/// Next meeting with countdown, popup agenda and a Join button for Teams/Zoom/Meet/Webex links.
/// Sources are local .ics files or iCal URLs (Outlook "publish calendar", Google "secret address").
/// The source list is stored encrypted because published calendar URLs grant read access.
/// </summary>
public sealed class CalendarWidget(ISecretStore secrets, HttpClient http, TimeProvider? time = null)
    : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "calendar";
    public const string SourcesKey = "sources";

    private static readonly TimeSpan UrlRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (string Content, DateTimeOffset FetchedAt)> _cache = new(StringComparer.Ordinal);
    private IReadOnlyList<CalendarEvent> _events = [];
    private IReadOnlyList<CalendarItem> _agenda = [];
    private string _headline = "Calendar";
    private string _subline = string.Empty;
    private bool _hasUpcoming;
    private bool _isSoon;
    private string? _nextJoinUrl;
    private RelayCommand? _joinNext;

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Calendar",
        "Next meeting with countdown, agenda popup, reminders and a Join button. Reads .ics files or iCal URLs.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        sp => new CalendarWidget(sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<HttpClient>()));

    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public string Subline
    {
        get => _subline;
        private set => SetProperty(ref _subline, value);
    }

    public bool HasUpcoming
    {
        get => _hasUpcoming;
        private set => SetProperty(ref _hasUpcoming, value);
    }

    /// <summary>True when the next event starts within the reminder window or is in progress.</summary>
    public bool IsSoon
    {
        get => _isSoon;
        private set => SetProperty(ref _isSoon, value);
    }

    public IReadOnlyList<CalendarItem> Agenda
    {
        get => _agenda;
        private set => SetProperty(ref _agenda, value);
    }

    public RelayCommand JoinNextCommand => _joinNext ??= new(() => SafeLauncher.OpenWebUrl(_nextJoinUrl), () => _nextJoinUrl is not null);

    public bool CanJoinNext => _nextJoinUrl is not null;

    public int ReminderMinutes => (int)Math.Clamp(Settings.Get("reminderMinutes", 5.0), 0, 120);

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new(SourcesKey, "Calendar sources", SettingFieldKind.Secret,
            "One or more .ics file paths or iCal https URLs, separated by spaces or ';'. Stored encrypted."),
        new("reminderMinutes", "Reminder (minutes before)", SettingFieldKind.Number, "0 turns reminders off.", DefaultNumber: 5),
    ];

    public void OnPopupOpened() => Render(_time.GetLocalNow());

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var sources = (secrets.Get(WidgetSecrets.Name(InstanceId, SourcesKey)) ?? string.Empty)
            .Split([' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (sources.Length == 0)
        {
            _events = [];
            Render(_time.GetLocalNow());
            SetUnavailable("Right-click › Configure… to add an .ics file or calendar URL");
            return;
        }

        var now = _time.GetLocalNow();
        var events = new List<CalendarEvent>();
        var failures = new List<string>();
        foreach (var source in sources)
        {
            var content = await LoadAsync(source, now, failures, cancellationToken).ConfigureAwait(false);
            if (content is not null)
            {
                events.AddRange(IcsParser.Parse(content, now.Date.AddDays(-1), now.AddDays(8)));
            }
        }

        _events = events.OrderBy(e => e.Start).ToList();
        Render(now);
        Remind(now);
        Context.Events.Publish(new CalendarEventsUpdated(
            InstanceId,
            _events.Where(e => e.End > now && e.Start < now.AddHours(24))
                .Select(e => new CalendarEventInfo(e.Uid, e.Title, e.Start, e.End, e.Location, e.JoinUrl))
                .ToList()));

        if (failures.Count > 0)
        {
            SetDisconnected($"Could not read: {string.Join(", ", failures)}" + (_cache.Count > 0 ? " (showing cached data)" : string.Empty));
        }
        else
        {
            SetReady();
        }
    }

    private async Task<string?> LoadAsync(string source, DateTimeOffset now, List<string> failures, CancellationToken ct)
    {
        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http" or "webcal");
        if (isUrl && _cache.TryGetValue(source, out var cached) && now - cached.FetchedAt < UrlRefreshInterval)
        {
            return cached.Content;
        }

        try
        {
            string content;
            if (isUrl)
            {
                var url = uri!.Scheme == "webcal" ? "https" + uri.AbsoluteUri[6..] : uri.AbsoluteUri;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Clear();
                request.Headers.TryAddWithoutValidation("Accept", "text/calendar, */*");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            else
            {
                content = await File.ReadAllTextAsync(Environment.ExpandEnvironmentVariables(source), ct).ConfigureAwait(false);
            }

            _cache[source] = (content, now);
            return content;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            failures.Add(isUrl ? uri!.Host : Path.GetFileName(source));
            return _cache.TryGetValue(source, out var stale) ? stale.Content : null;
        }
    }

    private void Render(DateTimeOffset now)
    {
        var upcoming = _events.Where(e => e.End > now).ToList();
        Agenda = upcoming.Where(e => e.Start < now.Date.AddDays(2)).Take(12).Select(e => new CalendarItem(e, now)).ToList();

        var next = upcoming.FirstOrDefault(e => !e.IsAllDay) ?? upcoming.FirstOrDefault();
        HasUpcoming = next is not null;
        _nextJoinUrl = next?.JoinUrl;
        OnPropertyChanged(nameof(CanJoinNext));
        PostToUi(() => _joinNext?.NotifyCanExecuteChanged());

        if (next is null)
        {
            Headline = "No upcoming events";
            Subline = now.ToString("ddd d MMM");
            IsSoon = false;
            return;
        }

        Headline = next.Title;
        if (next.Start <= now)
        {
            var left = next.End - now;
            Subline = next.IsAllDay ? "Today" : $"Now · {(int)Math.Ceiling(left.TotalMinutes)} min left";
            IsSoon = !next.IsAllDay;
        }
        else
        {
            var until = next.Start - now;
            Subline = until < TimeSpan.FromHours(12)
                ? RelativeTime.Until(until)
                : next.Start.Date == now.Date.AddDays(1) ? $"Tomorrow {next.Start:HH:mm}" : next.Start.ToString("ddd HH:mm");
            IsSoon = until <= TimeSpan.FromMinutes(Math.Max(ReminderMinutes, 1));
        }
    }

    private void Remind(DateTimeOffset now)
    {
        var minutes = ReminderMinutes;
        if (minutes <= 0)
        {
            return;
        }

        foreach (var e in _events.Where(e => !e.IsAllDay && e.Start > now && e.Start - now <= TimeSpan.FromMinutes(minutes)))
        {
            var mins = (int)Math.Ceiling((e.Start - now).TotalMinutes);
            Context.Notifications.Post(
                e.Title,
                $"Starts in {mins} min" + (e.Location is { Length: > 0 } l ? $" · {l}" : string.Empty) + (e.JoinUrl is not null ? " — click to join" : string.Empty),
                NotificationSeverity.Info,
                InstanceId,
                e.JoinUrl,
                dedupeKey: $"cal:{e.Uid}:{e.Start.UtcTicks}",
                dedupeWindow: TimeSpan.FromHours(6));
        }
    }
}
