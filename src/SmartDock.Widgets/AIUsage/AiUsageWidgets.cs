using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Common;

namespace SmartDock.Widgets.AIUsage;

/// <summary>Month-to-date organisation usage returned by a provider's official admin API.</summary>
public sealed record AiUsageSnapshot(decimal MonthCostUsd, long TodayInputTokens, long TodayOutputTokens, DateTimeOffset FetchedAt);

public interface IAiUsageProvider
{
    string ProviderName { get; }

    string KeyHelp { get; }

    Task<AiUsageSnapshot> FetchAsync(string adminKey, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class AiUsageException(string message) : Exception(message);

/// <summary>
/// Anthropic Usage &amp; Cost Admin API (organisation accounts with an Admin key, sk-ant-admin…).
/// Individual claude.ai subscription limits are not exposed by any public API, so they are not shown.
/// </summary>
public sealed class AnthropicUsageProvider(HttpClient http) : IAiUsageProvider
{
    public string ProviderName => "Claude API";

    public string KeyHelp => "Anthropic Admin API key (sk-ant-admin…). Organisation accounts only; claude.ai Pro/Max plan limits have no public API.";

    public async Task<AiUsageSnapshot> FetchAsync(string adminKey, DateTimeOffset now, CancellationToken ct)
    {
        var utcNow = now.ToUniversalTime();
        var monthStart = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var dayStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero);

        var costUrl = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={Rfc3339(monthStart)}&ending_at={Rfc3339(dayStart.AddDays(1))}&limit=31";
        decimal cents = 0;
        string? page = null;
        for (var guard = 0; guard < 10; guard++)
        {
            using var cost = await GetAsync(adminKey, page is null ? costUrl : $"{costUrl}&page={Uri.EscapeDataString(page)}", ct).ConfigureAwait(false);
            foreach (var bucket in cost.RootElement.GetProperty("data").EnumerateArray())
            {
                foreach (var result in bucket.GetProperty("results").EnumerateArray())
                {
                    // Amounts are decimal strings in the lowest currency unit (cents).
                    if (result.TryGetProperty("amount", out var amount))
                    {
                        cents += ReadDecimal(amount);
                    }
                }
            }

            page = cost.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                && cost.RootElement.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (page is null)
            {
                break;
            }
        }

        using var usage = await GetAsync(adminKey, $"https://api.anthropic.com/v1/organizations/usage_report/messages?starting_at={Rfc3339(dayStart)}&ending_at={Rfc3339(dayStart.AddDays(1))}&bucket_width=1d&limit=1", ct).ConfigureAwait(false);
        long input = 0;
        long output = 0;
        foreach (var bucket in usage.RootElement.GetProperty("data").EnumerateArray())
        {
            foreach (var r in bucket.GetProperty("results").EnumerateArray())
            {
                input += Long(r, "uncached_input_tokens") + Long(r, "cache_read_input_tokens");
                if (r.TryGetProperty("cache_creation", out var cc))
                {
                    input += Long(cc, "ephemeral_5m_input_tokens") + Long(cc, "ephemeral_1h_input_tokens");
                }

                output += Long(r, "output_tokens");
            }
        }

        return new AiUsageSnapshot(cents / 100m, input, output, DateTimeOffset.Now);
    }

    private async Task<JsonDocument> GetAsync(string key, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AiUsageException("Key rejected – an Admin API key (sk-ant-admin…) is required");
        }

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string Rfc3339(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    internal static decimal ReadDecimal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => 0,
    };

    internal static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}

/// <summary>
/// OpenAI organisation Costs/Usage API (requires an Admin key). ChatGPT Plus/Pro subscription usage
/// is not available through any public API, so it is not shown.
/// </summary>
public sealed class OpenAiUsageProvider(HttpClient http) : IAiUsageProvider
{
    public string ProviderName => "OpenAI API";

    public string KeyHelp => "OpenAI Admin key (organisation settings › Admin keys). ChatGPT Plus/Pro subscription usage has no public API.";

    public async Task<AiUsageSnapshot> FetchAsync(string adminKey, DateTimeOffset now, CancellationToken ct)
    {
        var utcNow = now.ToUniversalTime();
        var monthStart = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var dayStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        using var costs = await GetAsync(adminKey, $"https://api.openai.com/v1/organization/costs?start_time={monthStart}&bucket_width=1d&limit=31", ct).ConfigureAwait(false);
        decimal total = 0;
        foreach (var bucket in costs.RootElement.GetProperty("data").EnumerateArray())
        {
            foreach (var result in bucket.GetProperty("results").EnumerateArray())
            {
                if (result.TryGetProperty("amount", out var amount) && amount.TryGetProperty("value", out var value))
                {
                    total += AnthropicUsageProvider.ReadDecimal(value);
                }
            }
        }

        using var usage = await GetAsync(adminKey, $"https://api.openai.com/v1/organization/usage/completions?start_time={dayStart}&bucket_width=1d&limit=1", ct).ConfigureAwait(false);
        long input = 0;
        long output = 0;
        foreach (var bucket in usage.RootElement.GetProperty("data").EnumerateArray())
        {
            foreach (var r in bucket.GetProperty("results").EnumerateArray())
            {
                input += AnthropicUsageProvider.Long(r, "input_tokens");
                output += AnthropicUsageProvider.Long(r, "output_tokens");
            }
        }

        return new AiUsageSnapshot(total, input, output, DateTimeOffset.Now);
    }

    private async Task<JsonDocument> GetAsync(string key, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AiUsageException("Key rejected – an organisation Admin key with usage read access is required");
        }

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>
/// Shows month-to-date spend (optionally against a user-set monthly budget, which resets on the 1st)
/// and today's tokens. Without a key it shows a clear "unavailable" state — never invented numbers.
/// </summary>
public abstract class AiUsageWidgetBase(IAiUsageProvider provider, ISecretStore secrets) : WidgetBase, IConfigurableWidget
{
    public const string KeyName = "adminKey";

    private string _valueText = "–";
    private string _detailText = string.Empty;
    private double _budgetPercent;
    private bool _hasBudget;

    public string ProviderName => provider.ProviderName;

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public double BudgetPercent
    {
        get => _budgetPercent;
        private set => SetProperty(ref _budgetPercent, value);
    }

    public bool HasBudget
    {
        get => _hasBudget;
        private set => SetProperty(ref _hasBudget, value);
    }

    public double MonthlyBudget => Math.Max(0, Settings.Get("monthlyBudgetUsd", 0.0));

    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Settings.Get("refreshMinutes", 15.0), 5, 240));

    public IReadOnlyList<WidgetSettingField> SettingFields =>
    [
        new(KeyName, "Admin API key", SettingFieldKind.Secret, provider.KeyHelp + " Stored encrypted."),
        new("monthlyBudgetUsd", "Monthly budget (USD, optional)", SettingFieldKind.Number, "Shows spend as a percentage; resets on the 1st of each month. 0 = off."),
        new("refreshMinutes", "Refresh every (minutes)", SettingFieldKind.Number, DefaultNumber: 15),
    ];

    protected override async Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        var key = secrets.Get(WidgetSecrets.Name(InstanceId, KeyName));
        if (string.IsNullOrWhiteSpace(key))
        {
            ValueText = "–";
            DetailText = "No usage data source";
            HasBudget = false;
            SetUnavailable($"Usage is only available through the {provider.ProviderName} admin API. Right-click › Configure… to add an admin key.");
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var s = await provider.FetchAsync(key, now, cancellationToken).ConfigureAwait(false);
            ValueText = s.MonthCostUsd.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
            var resetsOn = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            DetailText = $"Today {Compact(s.TodayInputTokens)} in · {Compact(s.TodayOutputTokens)} out";
            var budget = MonthlyBudget;
            HasBudget = budget > 0;
            BudgetPercent = HasBudget ? Math.Min(100, (double)s.MonthCostUsd / budget * 100) : 0;
            SetReady();
            StatusMessage = $"{provider.ProviderName} · month to date (UTC)"
                + (HasBudget ? $" · {BudgetPercent:0}% of ${budget:0.##} · resets {resetsOn:d MMM}" : string.Empty)
                + $" · {RelativeTime.Ago(s.FetchedAt, DateTimeOffset.Now)}";
        }
        catch (AiUsageException ex)
        {
            SetDisconnected(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            SetDisconnected($"{provider.ProviderName} usage unavailable: {ex.Message}");
        }
    }

    private static string Compact(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };
}

public sealed class ClaudeUsageWidget(HttpClient http, ISecretStore secrets) : AiUsageWidgetBase(new AnthropicUsageProvider(http), secrets)
{
    public const string TypeId = "claude-usage";

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Claude usage",
        "Month-to-date Claude API spend and today's tokens via the Anthropic Admin API (organisation accounts).",
        WidgetCategory.AI,
        "",
        DockRegion.Center,
        sp => new ClaudeUsageWidget(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISecretStore>()));
}

public sealed class OpenAiUsageWidget(HttpClient http, ISecretStore secrets) : AiUsageWidgetBase(new OpenAiUsageProvider(http), secrets)
{
    public const string TypeId = "openai-usage";

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "ChatGPT / OpenAI usage",
        "Month-to-date OpenAI API spend and today's tokens via the organisation Usage/Costs API (admin key).",
        WidgetCategory.AI,
        "",
        DockRegion.Center,
        sp => new OpenAiUsageWidget(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISecretStore>()));
}
