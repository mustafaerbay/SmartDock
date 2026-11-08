using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Clock;

/// <summary>Time and date with 12/24h, optional seconds and a configurable time zone.</summary>
public sealed class ClockWidget : WidgetBase
{
    public const string TypeId = "clock";

    private readonly TimeProvider _time;
    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private string? _zoneLabel;

    public ClockWidget()
        : this(TimeProvider.System)
    {
    }

    public ClockWidget(TimeProvider time)
    {
        _time = time;
    }

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Clock",
        "Current time and date. 12/24-hour, seconds and time zone are configurable.",
        WidgetCategory.Time,
        "",
        DockRegion.Right,
        _ => new ClockWidget())
    {
        AllowMultiple = true,
    };

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => SetProperty(ref _dateText, value);
    }

    /// <summary>Short zone label when a non-local zone is configured (e.g. "UTC", "New York").</summary>
    public string? ZoneLabel
    {
        get => _zoneLabel;
        private set => SetProperty(ref _zoneLabel, value);
    }

    public bool Use24Hour => Settings.Get("use24Hour", !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t'));

    public bool ShowSeconds => Settings.Get("showSeconds", false);

    public bool ShowDate => Settings.Get("showDate", true);

    public string? TimeZoneId => Settings.Get<string?>("timeZoneId", null);

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("24-hour time", new RelayCommand(() => Settings.Set("use24Hour", !Use24Hour)), Use24Hour, true),
        new("Show seconds", new RelayCommand(() => Settings.Set("showSeconds", !ShowSeconds)), ShowSeconds, true),
        new("Show date", new RelayCommand(() => Settings.Set("showDate", !ShowDate)), ShowDate, true),
        new("Use local time zone", new RelayCommand(() => Settings.Remove("timeZoneId")), TimeZoneId is null, true),
        new("Use UTC", new RelayCommand(() => Settings.Set("timeZoneId", "UTC")), TimeZoneId == "UTC", true),
    ];

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        Render();
        return Task.CompletedTask;
    }

    protected override void OnSettingChanged(string key)
    {
        Render();
        OnPropertyChanged(nameof(ShowDate));
        base.OnSettingChanged(key);
    }

    /// <summary>Formats the given instant; public for tests.</summary>
    public (string Time, string Date) FormatAt(DateTimeOffset utcNow)
    {
        var zone = ResolveZone(TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var pattern = (Use24Hour, ShowSeconds) switch
        {
            (true, true) => "HH:mm:ss",
            (true, false) => "HH:mm",
            (false, true) => "h:mm:ss tt",
            (false, false) => "h:mm tt",
        };

        // tt is empty in cultures without AM/PM designators; force English designators for 12h then.
        var culture = CultureInfo.CurrentCulture;
        if (!Use24Hour && string.IsNullOrEmpty(culture.DateTimeFormat.AMDesignator))
        {
            culture = CultureInfo.InvariantCulture;
        }

        var time = local.ToString(pattern, culture).Trim();
        var date = local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        return (time, date);
    }

    private void Render()
    {
        var (time, date) = FormatAt(_time.GetUtcNow());
        TimeText = time;
        DateText = ShowDate ? date : string.Empty;
        var id = TimeZoneId;
        ZoneLabel = id is null ? null : ResolveZone(id).Id == TimeZoneInfo.Local.Id ? null : ShortZoneName(id);
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string ShortZoneName(string id)
    {
        var slash = id.LastIndexOf('/');
        return (slash >= 0 ? id[(slash + 1)..] : id).Replace('_', ' ');
    }
}
