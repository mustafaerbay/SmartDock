using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartDock.Widgets.Calendar;

public sealed record CalendarEvent(
    string Uid,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string? Location,
    string? Description,
    string? JoinUrl);

/// <summary>
/// Minimal iCalendar (RFC 5545) reader for VEVENTs: line unfolding, DTSTART/DTEND/DURATION with
/// UTC, floating and TZID times, all-day dates, EXDATE, cancelled events, and the common RRULE
/// subset (DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL; BYDAY for weekly, ordinal BYDAY
/// such as 2TU or -1FR for monthly). BYSETPOS and YEARLY BYDAY/BYMONTH series are skipped rather than
/// shown on wrong dates. Recurrence
/// overrides (RECURRENCE-ID) replace the matching generated occurrence.
/// </summary>
public static partial class IcsParser
{
    private const int MaxOccurrencesPerEvent = 2000;
    private const int MaxIterations = 200_000; // ~550 years of daily occurrences; bounds pathological rules.

    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public static IReadOnlyList<CalendarEvent> Parse(string ics, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var results = new List<CalendarEvent>();
        var overrides = new List<(string Uid, DateTimeOffset RecurrenceId, CalendarEvent? Event)>();

        foreach (var block in ReadEvents(Unfold(ics)))
        {
            try
            {
                if (Get(block, "STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true && Get(block, "RECURRENCE-ID") is null)
                {
                    continue;
                }

                var start = Get(block, "DTSTART");
                if (start is null)
                {
                    continue;
                }

                var (startTime, allDay) = ParseDate(start.Value.Value, start.Value.Params);
                var endProp = Get(block, "DTEND");
                var duration = endProp is { } e
                    ? ParseDate(e.Value, e.Params).Time - startTime
                    : Get(block, "DURATION") is { } d ? ParseDuration(d.Value) : (allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                var uid = Get(block, "UID")?.Value ?? Guid.NewGuid().ToString("N");
                var title = Unescape(Get(block, "SUMMARY")?.Value) ?? "(no title)";
                var location = Unescape(Get(block, "LOCATION")?.Value);
                var description = Unescape(Get(block, "DESCRIPTION")?.Value);
                var join = FindJoinUrl(Get(block, "URL")?.Value, location, description, Get(block, "X-GOOGLE-CONFERENCE")?.Value);

                CalendarEvent Make(DateTimeOffset s) => new(uid, title, s, s + duration, allDay, location, description, join);

                if (Get(block, "RECURRENCE-ID") is { } rid)
                {
                    var cancelled = Get(block, "STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;
                    overrides.Add((uid, ParseDate(rid.Value, rid.Params).Time, cancelled ? null : Make(startTime)));
                    continue;
                }

                var exdates = block.Where(p => p.Name == "EXDATE")
                    .SelectMany(p => p.Value.Split(',').Select(v => ParseDate(v, p.Params).Time))
                    .ToHashSet();

                var rrule = Get(block, "RRULE")?.Value;
                IEnumerable<DateTimeOffset> starts = rrule is null ? [startTime] : ExpandRule(rrule, startTime, windowStart - duration, windowEnd);
                foreach (var s in starts)
                {
                    if (exdates.Contains(s))
                    {
                        continue;
                    }

                    if (s + duration >= windowStart && s < windowEnd)
                    {
                        results.Add(Make(s));
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                // Skip malformed events rather than failing the whole calendar.
            }
        }

        foreach (var (uid, recurrenceId, replacement) in overrides)
        {
            results.RemoveAll(r => r.Uid == uid && r.Start == recurrenceId);
            if (replacement is not null && replacement.End >= windowStart && replacement.Start < windowEnd)
            {
                results.Add(replacement);
            }
        }

        return results.OrderBy(r => r.Start).ToList();
    }

    /// <summary>Finds a Teams/Zoom/Meet/Webex/https link that can be offered as "Join".</summary>
    public static string? FindJoinUrl(params string?[] sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            var meeting = MeetingUrlRegex().Match(source);
            if (meeting.Success)
            {
                return meeting.Value.TrimEnd('>', ')', '.', ',');
            }
        }

        foreach (var source in sources)
        {
            if (!string.IsNullOrEmpty(source) && Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    internal static IEnumerable<DateTimeOffset> ExpandRule(string rule, DateTimeOffset start, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);

        var freq = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTimeOffset? until = parts.TryGetValue("UNTIL", out var u) ? ParseDate(u, NoParams).Time : null;
        var limit = until is { } un && un < windowEnd ? un : windowEnd;

        var byDay = parts.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(ParseWeekday).Where(d => d is not null).Select(d => d!.Value).ToHashSet()
            : [];

        // Ordinal weekdays for MONTHLY (e.g. 2TU = second Tuesday, -1FR = last Friday).
        var ordinalDays = parts.TryGetValue("BYDAY", out var obd)
            ? obd.Split(',')
                .Select(t => (Ord: int.TryParse(t.Length > 2 ? t[..^2] : "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o) ? o : 0, Day: ParseWeekday(t)))
                .Where(x => x.Day is not null)
                .Select(x => (x.Ord, Day: x.Day!.Value))
                .ToList()
            : [];

        if (parts.ContainsKey("BYSETPOS") || (freq == "YEARLY" && (parts.ContainsKey("BYDAY") || parts.ContainsKey("BYMONTH"))))
        {
            // Unsupported combination: skip the series rather than show wrong dates.
            yield break;
        }

        var produced = 0;   // occurrences counted toward COUNT
        var inWindow = 0;   // occurrences counted toward the safety cap
        var iterations = 0;
        var startLocal = start.DateTime;

        IEnumerable<DateTime> Candidates()
        {
            switch (freq)
            {
                case "DAILY":
                    for (var n = 0; ; n++)
                    {
                        yield return startLocal.AddDays(n * interval);
                    }

                case "WEEKLY":
                    var weekStart = startLocal.Date.AddDays(-(int)startLocal.DayOfWeek);
                    for (var w = 0; ; w++)
                    {
                        var week = weekStart.AddDays(7 * w * interval);
                        if (byDay.Count == 0)
                        {
                            yield return startLocal.AddDays(7 * w * interval);
                            continue;
                        }

                        for (var day = 0; day < 7; day++)
                        {
                            var date = week.AddDays(day);
                            if (byDay.Contains(date.DayOfWeek) && date >= startLocal.Date)
                            {
                                yield return date + startLocal.TimeOfDay;
                            }
                        }
                    }

                case "MONTHLY":
                    for (var m = 0; ; m++)
                    {
                        var monthStart = new DateTime(startLocal.Year, startLocal.Month, 1).AddMonths(m * interval);
                        if (ordinalDays.Count == 0)
                        {
                            var candidate = startLocal.AddMonths(m * interval);
                            if (candidate.Day == startLocal.Day)
                            {
                                yield return candidate;
                            }

                            continue;
                        }

                        var dates = new SortedSet<DateTime>();
                        foreach (var (ord, day) in ordinalDays)
                        {
                            var all = Enumerable.Range(0, DateTime.DaysInMonth(monthStart.Year, monthStart.Month))
                                .Select(n => monthStart.AddDays(n))
                                .Where(d => d.DayOfWeek == day)
                                .ToList();
                            if (ord == 0)
                            {
                                dates.UnionWith(all);
                            }
                            else if (Math.Abs(ord) <= all.Count)
                            {
                                dates.Add(ord > 0 ? all[ord - 1] : all[^-ord]);
                            }
                        }

                        foreach (var d in dates)
                        {
                            if (d >= startLocal.Date)
                            {
                                yield return d + startLocal.TimeOfDay;
                            }
                        }
                    }

                case "YEARLY":
                    for (var y = 0; ; y++)
                    {
                        yield return startLocal.AddYears(y * interval);
                    }

                default:
                    yield return startLocal;
                    yield break;
            }
        }

        foreach (var local in Candidates())
        {
            if (++iterations > MaxIterations || inWindow >= MaxOccurrencesPerEvent || (count is { } max && produced >= max))
            {
                yield break;
            }

            // Offset per occurrence so a weekly 09:00 meeting stays at 09:00 across DST changes.
            var occurrence = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            if (occurrence > limit)
            {
                yield break;
            }

            if (occurrence < start)
            {
                continue;
            }

            produced++;
            if (occurrence >= windowStart)
            {
                inWindow++;
            }

            yield return occurrence;
        }
    }

    internal static (DateTimeOffset Time, bool AllDay) ParseDate(string value, IReadOnlyDictionary<string, string> parameters)
    {
        value = value.Trim();
        if (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8)
        {
            var date = DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
            return (new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)), true);
        }

        var utc = value.EndsWith('Z');
        var local = DateTime.ParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc)
        {
            return (new DateTimeOffset(local, TimeSpan.Zero).ToLocalTime(), false);
        }

        var zone = TimeZoneInfo.Local;
        if (parameters.TryGetValue("TZID", out var tzid))
        {
            zone = ResolveZone(tzid.Trim('"'));
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(unspecified) ? zone.BaseUtcOffset : zone.GetUtcOffset(unspecified);
        return (new DateTimeOffset(unspecified, offset).ToLocalTime(), false);
    }

    internal static TimeSpan ParseDuration(string value)
    {
        // e.g. PT1H30M, P1D, -PT15M
        var m = DurationRegex().Match(value.Trim());
        if (!m.Success)
        {
            throw new FormatException("Bad DURATION");
        }

        double Part(string name) => m.Groups[name].Success ? double.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        var span = TimeSpan.FromDays((Part("w") * 7) + Part("d")) + TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) + TimeSpan.FromSeconds(Part("s"));
        return m.Groups["neg"].Success ? -span : span;
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try
        {
            // .NET resolves both Windows ("W. Europe Standard Time") and IANA ("Europe/Istanbul") ids on Windows 10+.
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DayOfWeek? ParseWeekday(string token)
    {
        var code = token.Length >= 2 ? token[^2..].ToUpperInvariant() : token;
        return code switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday,
            _ => null,
        };
    }

    private static string Unfold(string ics)
    {
        // RFC 5545 §3.1: a CRLF followed by a single space or tab continues the previous line.
        return ics.Replace("\r\n", "\n").Replace("\n ", string.Empty).Replace("\n\t", string.Empty);
    }

    private static IEnumerable<List<Property>> ReadEvents(string ics)
    {
        List<Property>? current = null;
        var nested = 0; // Depth inside VALARM etc.; nested component properties must not shadow the event's.
        foreach (var raw in ics.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                nested = 0;
            }
            else if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = null;
            }
            else if (current is not null && line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                nested++;
            }
            else if (current is not null && line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                nested = Math.Max(0, nested - 1);
            }
            else if (current is not null && nested == 0 && TryParseProperty(line, out var property))
            {
                current.Add(property);
            }
        }
    }

    private static bool TryParseProperty(string line, out Property property)
    {
        property = default;
        var colon = IndexOfUnquoted(line, ':');
        if (colon <= 0)
        {
            return false;
        }

        var head = line[..colon];
        var value = line[(colon + 1)..];
        var segments = head.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments.Skip(1))
        {
            var eq = seg.IndexOf('=');
            if (eq > 0)
            {
                parameters[seg[..eq]] = seg[(eq + 1)..];
            }
        }

        property = new Property(segments[0].ToUpperInvariant(), parameters, value);
        return true;
    }

    private static int IndexOfUnquoted(string s, char target)
    {
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                quoted = !quoted;
            }
            else if (s[i] == target && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static Property? Get(List<Property> block, string name)
    {
        foreach (var p in block)
        {
            if (p.Name == name)
            {
                return p;
            }
        }

        return null;
    }

    private static string? Unescape(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"https://[^\s""<>]*(teams\.microsoft\.com|teams\.live\.com|zoom\.us|meet\.google\.com|webex\.com|whereby\.com|gotomeeting\.com|chime\.aws)[^\s""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingUrlRegex();

    [GeneratedRegex(@"^(?<neg>-)?\+?P(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    private readonly record struct Property(string Name, IReadOnlyDictionary<string, string> Params, string Value);
}
