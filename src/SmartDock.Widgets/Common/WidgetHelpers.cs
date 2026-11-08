using System.Diagnostics;
using System.Net.Http.Headers;

namespace SmartDock.Widgets.Common;

public static class SafeLauncher
{
    /// <summary>Opens an http(s) URL in the default browser. Anything else is refused (no shell execution).</summary>
    public static bool OpenWebUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

public static class HttpClients
{
    /// <summary>Creates the shared HttpClient used by widgets (registered as a singleton).</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartDock/" + SmartDock.Core.AppInfo.Version);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

public static class RelativeTime
{
    /// <summary>"just now", "5 min ago", "2 h ago".</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min ago";
        }

        return span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours} h ago" : when.ToLocalTime().ToString("g");
    }

    /// <summary>"in 12 min", "in 2 h 5 min".</summary>
    public static string Until(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "in <1 min";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"in {(int)Math.Ceiling(span.TotalMinutes)} min";
        }

        return span.Minutes == 0 ? $"in {(int)span.TotalHours} h" : $"in {(int)span.TotalHours} h {span.Minutes} min";
    }
}
