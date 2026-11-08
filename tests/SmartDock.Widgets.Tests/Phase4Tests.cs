using SmartDock.Widgets.Calendar;
using SmartDock.Widgets.Productivity;
using SmartDock.Widgets.Weather;

namespace SmartDock.Widgets.Tests;

public class IcsParserTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Join("\r\n", events) + "\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Parses_utc_event_with_folded_description_and_teams_link()
    {
        var ics = Calendar(
            "BEGIN:VEVENT",
            "UID:1",
            "DTSTART:20261103T150000Z",
            "DTEND:20261103T153000Z",
            "SUMMARY:Sprint review",
            "DESCRIPTION:Join here https://teams.microsoft.com/l/meetup-join/abc",
            " def\\nthanks",
            "END:VEVENT");

        var e = Assert.Single(IcsParser.Parse(ics, WindowStart, WindowEnd));

        Assert.Equal("Sprint review", e.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero), e.Start);
        Assert.Equal(TimeSpan.FromMinutes(30), e.End - e.Start);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/abcdef", e.JoinUrl);
    }

    [Fact]
    public void Expands_weekly_rule_with_byday_count_and_exdate()
    {
        var ics = Calendar(
            "BEGIN:VEVENT",
            "UID:standup",
            "DTSTART:20261102T090000",
            "DURATION:PT15M",
            "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=4",
            "EXDATE:20261104T090000",
            "SUMMARY:Standup",
            "END:VEVENT");

        var events = IcsParser.Parse(ics, WindowStart, WindowEnd);

        // Mon 2, (Wed 4 excluded), Mon 9, Wed 11 => COUNT=4 includes the excluded occurrence.
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(TimeSpan.FromMinutes(15), e.End - e.Start));
        Assert.DoesNotContain(events, e => e.Start.LocalDateTime.Day == 4);
    }

    [Fact]
    public void Cancelled_override_removes_occurrence()
    {
        var ics = Calendar(
            "BEGIN:VEVENT",
            "UID:daily",
            "DTSTART:20261110T080000Z",
            "DTEND:20261110T083000Z",
            "RRULE:FREQ=DAILY;COUNT=3",
            "SUMMARY:Daily",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:daily",
            "RECURRENCE-ID:20261111T080000Z",
            "DTSTART:20261111T080000Z",
            "STATUS:CANCELLED",
            "SUMMARY:Daily",
            "END:VEVENT");

        Assert.Equal(2, IcsParser.Parse(ics, WindowStart, WindowEnd).Count);
    }

    [Fact]
    public void Alarm_properties_do_not_override_event_properties()
    {
        var ics = Calendar(
            "BEGIN:VEVENT",
            "UID:2",
            "DTSTART:20261105T090000Z",
            "DTEND:20261105T100000Z",
            "BEGIN:VALARM",
            "ACTION:DISPLAY",
            "DESCRIPTION:Reminder text",
            "TRIGGER:-PT10M",
            "END:VALARM",
            "SUMMARY:Design sync",
            "END:VEVENT");

        var e = Assert.Single(IcsParser.Parse(ics, WindowStart, WindowEnd));
        Assert.Equal("Design sync", e.Title);
        Assert.Null(e.Description);
    }

    [Theory]
    [InlineData("PT1H30M", 90)]
    [InlineData("P1D", 1440)]
    [InlineData("PT15M", 15)]
    public void Parses_durations(string value, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), IcsParser.ParseDuration(value));

    [Fact]
    public void Join_url_ignores_non_https_text() =>
        Assert.Null(IcsParser.FindJoinUrl("Room 4.12", "ftp://example.com", null));
}

public class QuickActionTests
{
    [Fact]
    public void Parses_builtins_urls_and_apps_and_rejects_scripts()
    {
        var actions = QuickActionsWidget.Parse(
            "Lock | builtin:lock\n" +
            "Docs | https://learn.microsoft.com\n" +
            "# comment\n" +
            "Evil | C:\\temp\\run.ps1\n" +
            "Unknown | builtin:format-disk\n" +
            "Notepad | C:\\Windows\\notepad.exe");

        Assert.Collection(
            actions,
            a => Assert.Equal((QuickActionKind.Builtin, "lock"), (a.Kind, a.Target)),
            a => Assert.Equal(QuickActionKind.Url, a.Kind),
            a => Assert.Equal(("Notepad", QuickActionKind.App), (a.Label, a.Kind)));
    }
}

public class WeatherCodeTests
{
    [Theory]
    [InlineData(0, "Clear")]
    [InlineData(63, "Rain")]
    [InlineData(95, "Thunderstorm")]
    [InlineData(12345, "Unknown")]
    public void Maps_wmo_codes(int code, string text) => Assert.Equal(text, WeatherCodes.Describe(code).Text);
}

public class IcsRecurrenceTests
{
    [Fact]
    public void Monthly_ordinal_byday_expands_to_nth_weekday()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:m\r\nDTSTART:20260908T100000\r\nDURATION:PT1H\r\n" +
                  "RRULE:FREQ=MONTHLY;BYDAY=2TU\r\nSUMMARY:Monthly\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var e = Assert.Single(IcsParser.Parse(ics, start, start.AddDays(29)));

        Assert.Equal(new DateTime(2026, 11, 10), e.Start.LocalDateTime.Date); // Second Tuesday of November 2026.
    }

    [Fact]
    public void Old_daily_series_still_produces_current_occurrences()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:d\r\nDTSTART:20150101T090000\r\nDURATION:PT15M\r\n" +
                  "RRULE:FREQ=DAILY\r\nSUMMARY:Daily\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var start = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(7, IcsParser.Parse(ics, start, start.AddDays(7)).Count);
    }

    [Fact]
    public void Unsupported_bysetpos_series_is_skipped_not_misdated()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:s\r\nDTSTART:20261102T090000\r\nDURATION:PT15M\r\n" +
                  "RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1\r\nSUMMARY:Last weekday\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(IcsParser.Parse(ics, start, start.AddDays(60)));
    }
}
