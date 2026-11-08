using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Models;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.Weather;

public sealed record WeatherDay(DateOnly Date, int Code, double Min, double Max);

public sealed record WeatherSnapshot(string Location, double Temperature, int Code, bool IsDay, IReadOnlyList<WeatherDay> Days, DateTimeOffset FetchedAt);

/// <summary>Pluggable weather source ("configurable weather provider").</summary>
public interface IWeatherProvider
{
    string Name { get; }

    Task<WeatherSnapshot> GetAsync(string location, bool fahrenheit, CancellationToken cancellationToken);
}

/// <summary>Open-Meteo: free, no API key, no account. Location names are geocoded with its geocoding API.</summary>
public sealed class OpenMeteoProvider(HttpClient http) : IWeatherProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Lat, double Lon, string Label)> _geocache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Open-Meteo";

    public async Task<WeatherSnapshot> GetAsync(string location, bool fahrenheit, CancellationToken cancellationToken)
    {
        var (lat, lon, label) = await ResolveAsync(location, cancellationToken).ConfigureAwait(false);
        var unit = fahrenheit ? "&temperature_unit=fahrenheit" : string.Empty;
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=4{unit}");

        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var times = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var max = daily.GetProperty("temperature_2m_max");
        var min = daily.GetProperty("temperature_2m_min");

        var days = new List<WeatherDay>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (min[i].ValueKind != JsonValueKind.Number || max[i].ValueKind != JsonValueKind.Number)
            {
                continue; // Missing values: skip the day rather than store NaN (which JSON can't persist).
            }

            days.Add(new WeatherDay(
                DateOnly.ParseExact(times[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                codes[i].ValueKind == JsonValueKind.Number ? codes[i].GetInt32() : 0,
                min[i].GetDouble(),
                max[i].GetDouble()));
        }

        return new WeatherSnapshot(
            label,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.TryGetProperty("is_day", out var isDay) && isDay.GetInt32() == 1,
            days,
            DateTimeOffset.Now);
    }

    private async Task<(double Lat, double Lon, string Label)> ResolveAsync(string location, CancellationToken ct)
    {
        // "41.01,28.97" is used as coordinates directly.
        var parts = location.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
        {
            return (la, lo, location);
        }

        if (_geocache.TryGetValue(location, out var cached))
        {
            return cached;
        }

        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(location)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Location \"{location}\" not found");
        }

        var r = results[0];
        var name = r.GetProperty("name").GetString() ?? location;
        var resolved = (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
        _geocache[location] = resolved;
        return resolved;
    }
}

public static class WeatherCodes
{
    /// <summary>WMO weather interpretation codes → (description, day glyph, night glyph) in Segoe Fluent Icons.</summary>
    public static (string Text, string Glyph) Describe(int code, bool isDay = true) => code switch
    {
        0 => ("Clear", isDay ? "" : ""),
        1 => ("Mostly clear", isDay ? "" : ""),
        2 => ("Partly cloudy", isDay ? "" : ""),
        3 => ("Overcast", ""),
        45 or 48 => ("Fog", ""),
        51 or 53 or 55 or 56 or 57 => ("Drizzle", ""),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("Rain", ""),
        71 or 73 or 75 or 77 or 85 or 86 => ("Snow", ""),
        95 or 96 or 99 => ("Thunderstorm", ""),
        _ => ("Unknown", ""),
    };
}

public sealed record ForecastItem(string Day, string Glyph, string Range, string Text);

/// <summary>Current temperature, condition and a short forecast; keeps showing the last data when offline.</summary>
public sealed class WeatherWidget(IWeatherProvider provider) : WidgetBase, IPopupWidget, IConfigurableWidget
{
    public const string TypeId = "weather";
    private const string CacheKey = "lastSnapshot";

    private WeatherSnapshot? _snapshot;
    private string _temperatureText = "–";
    private string _conditionText = string.Empty;
    private string _glyph = "";
    private string _updatedText = string.Empty;
    private IReadOnlyList<ForecastItem> _forecast = [];

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Weather",
        "Current temperature, conditions and a 3-day forecast (Open-Meteo, no account needed).",
        WidgetCategory.Other,
        "",
        DockRegion.Center,
        sp => new WeatherWidget(sp.GetRequiredService<IWeatherProvider>()))
    {
        AllowMultiple = true,
    };

    public string TemperatureText
    {
        get => _temperatureText;
        private set => SetProperty(ref _temperatureText, value);
    }

    public string ConditionText
    {
        get => _conditionText;
        private set => SetProperty(ref _conditionText, value);
    }

    public string Glyph
    {
        get => _glyph;
        private set => SetProperty(ref _glyph, value);
    }

    public string LocationText => _snapshot?.Location ?? Location;

    /// <summary>"Updated 5 min ago · Open-Meteo".</summary>
    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    public IReadOnlyList<ForecastItem> Forecast
    {
        get => _forecast;
        private set => SetProperty(ref _forecast, value);
    }

    public string Location => Settings.Get("location", string.Empty);

    public bool Fahrenheit => Settings.Get("units", "Celsius") == "Fahrenheit";

    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Settings.Get("refreshMinutes", 20.0), 5, 180));

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new("location", "Location", SettingFieldKind.Text, "City name (e.g. Istanbul) or coordinates \"41.01,28.97\"."),
        new("units", "Units", SettingFieldKind.Choice, Choices: ["Celsius", "Fahrenheit"], DefaultText: "Celsius"),
        new("refreshMinutes", "Refresh every (minutes)", SettingFieldKind.Number, DefaultNumber: 20),
    ];

    public void OnPopupOpened() => RenderUpdated();

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        // Show the last known weather immediately (also when starting offline).
        _snapshot = Settings.Get<WeatherSnapshot?>(CacheKey, null);
        if (_snapshot is not null)
        {
            Render(_snapshot);
        }

        return Task.CompletedTask;
    }

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Location))
        {
            SetUnavailable("Right-click › Configure… to set your location");
            return;
        }

        try
        {
            var snapshot = await provider.GetAsync(Location, Fahrenheit, cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            Settings.Set(CacheKey, snapshot);
            Render(snapshot);
            SetReady();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested && ex is TaskCanceledException)
            {
                throw;
            }

            RenderUpdated();
            SetDisconnected(_snapshot is null
                ? $"Weather unavailable: {ex.Message}"
                : $"Offline – showing data from {RelativeTime.Ago(_snapshot.FetchedAt, DateTimeOffset.Now)}");
        }
    }

    protected override void OnSettingChanged(string key)
    {
        if (key == CacheKey)
        {
            return; // Our own cache write: no refresh loop.
        }

        OnPropertyChanged(nameof(LocationText));
        base.OnSettingChanged(key);
    }

    private void Render(WeatherSnapshot s)
    {
        var (text, glyph) = WeatherCodes.Describe(s.Code, s.IsDay);
        TemperatureText = $"{Math.Round(s.Temperature):0}°";
        ConditionText = text;
        Glyph = glyph;
        OnPropertyChanged(nameof(LocationText));
        Forecast = s.Days.Take(4).Select((d, i) =>
        {
            var (dt, dg) = WeatherCodes.Describe(d.Code);
            var day = i == 0 ? "Today" : d.Date.ToString("ddd", CultureInfo.CurrentCulture);
            return new ForecastItem(day, dg, $"{Math.Round(d.Min):0}° / {Math.Round(d.Max):0}°", dt);
        }).ToList();
        RenderUpdated();
    }

    private void RenderUpdated()
    {
        UpdatedText = _snapshot is null ? string.Empty : $"Updated {RelativeTime.Ago(_snapshot.FetchedAt, DateTimeOffset.Now)} · {provider.Name}";
    }
}
