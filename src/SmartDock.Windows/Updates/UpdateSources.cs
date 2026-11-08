using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using SmartDock.Core.Models;
using SmartDock.Core.Updates;

namespace SmartDock.Windows.Updates;

/// <summary>Creates the right <see cref="IUpdateSource"/> for the configured feed location.</summary>
public static class UpdateSourceFactory
{
    public static IUpdateSource Create(UpdateSettings settings, HttpClient http)
    {
        var feed = settings.FeedLocation.Trim();
        if (feed.Length == 0)
        {
            throw new UpdateException("No update location is set (Settings › Updates).");
        }

        if (feed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return SshUpdateSource.Parse(feed, settings.SshKeyPath);
        }

        if (feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpUpdateSource(http, feed);
        }

        if (feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return settings.AllowUnencryptedHttp
                ? new HttpUpdateSource(http, feed)
                : throw new UpdateException("http:// isn't encrypted, so a download could be tampered with on the way. Use https:// or ssh://, " +
                    "or tick \"Allow unencrypted http\" if this is a trusted network.");
        }

        if (Path.IsPathFullyQualified(feed))
        {
            return new FolderUpdateSource(feed);
        }

        throw new UpdateException($"\"{feed}\" isn't a supported location. Use https://…, ssh://user@host/path, \\\\server\\share or a folder path.");
    }
}

/// <summary>Release folder served over HTTP(S), e.g. nginx or <c>python3 -m http.server</c> on the Linux machine.</summary>
public sealed class HttpUpdateSource(HttpClient http, string baseUrl) : IUpdateSource
{
    private readonly Uri _base = new(baseUrl.TrimEnd('/') + "/");

    public string Display => _base.ToString();

    public async Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(fileName, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string fileName, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(fileName, cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destinationPath);
        await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string fileName, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, Uri.EscapeDataString(fileName)));
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Can't reach {_base.Host}: {ex.Message}", ex);
        }
        finally
        {
            request.Dispose();
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateException($"{fileName}: the server answered {code}.");
        }

        return response;
    }
}

/// <summary>Release folder on a network share or local disk.</summary>
public sealed class FolderUpdateSource(string folder) : IUpdateSource
{
    public string Display => folder;

    public async Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, fileName);
        return await Task.Run(() => File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : throw new UpdateException($"{fileName} not found in {folder}."), cancellationToken).ConfigureAwait(false);
    }

    public Task DownloadAsync(string fileName, string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() => File.Copy(Path.Combine(folder, fileName), destinationPath, overwrite: true), cancellationToken);
}

/// <summary>
/// Release folder on a Linux machine reached over SSH with Windows' built-in OpenSSH client (scp.exe) and the
/// user's normal SSH key/agent. Nothing needs to be installed on the Linux side besides sshd.
/// Runs non-interactively (BatchMode) with strict host-key checking, so the host must already be in
/// known_hosts (connect once with <c>ssh user@host</c>) and no password prompt can block.
/// </summary>
public sealed class SshUpdateSource : IUpdateSource
{
    private static readonly Regex SafeRemotePath = new(@"^[A-Za-z0-9._/~-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeUserHost = new(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    private readonly string _user;
    private readonly string _host;
    private readonly int _port;
    private readonly string _path;
    private readonly string? _keyPath;

    private SshUpdateSource(string user, string host, int port, string path, string? keyPath)
    {
        _user = user;
        _host = host;
        _port = port;
        _path = path.TrimEnd('/');
        _keyPath = keyPath;
    }

    public string Display => $"ssh://{_user}@{_host}{(_port == 22 ? string.Empty : ":" + _port)}{_path}";

    /// <summary>Parses ssh://user@host[:port]/absolute/path (or ~/relative). Strict characters only: no shell tricks.</summary>
    public static SshUpdateSource Parse(string feed, string? keyPath)
    {
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
        {
            throw new UpdateException("SSH locations look like ssh://user@host/srv/smartdock (optionally ssh://user@host:2222/…).");
        }

        var user = Uri.UnescapeDataString(uri.UserInfo);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.StartsWith("/~", StringComparison.Ordinal))
        {
            path = path[1..]; // ssh://user@host/~/releases -> ~/releases
        }

        if (!SafeUserHost.IsMatch(user) || !SafeUserHost.IsMatch(uri.Host) || path.Length < 2 || !SafeRemotePath.IsMatch(path) || path.Contains("..", StringComparison.Ordinal))
        {
            throw new UpdateException("The SSH location may only contain letters, digits, '.', '_', '-' and '/' (no spaces).");
        }

        var key = string.IsNullOrWhiteSpace(keyPath) ? null : Environment.ExpandEnvironmentVariables(keyPath.Trim().Trim('"'));
        if (key is not null && !File.Exists(key))
        {
            throw new UpdateException($"SSH key not found: {key}");
        }

        return new SshUpdateSource(user, uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? 22 : uri.Port, path, key);
    }

    /// <summary>scp arguments (exposed for tests).</summary>
    public IReadOnlyList<string> BuildArguments(string fileName, string destinationPath)
    {
        var args = new List<string>
        {
            "-B",
            "-q",
            "-P", _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=15",
        };
        if (_keyPath is not null)
        {
            args.Add("-i");
            args.Add(_keyPath);
        }

        args.Add($"{_user}@{_host}:{_path}/{fileName}");
        args.Add(destinationPath);
        return args;
    }

    public async Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"smartdock-{Guid.NewGuid():N}-{fileName}");
        try
        {
            await DownloadAsync(fileName, temp, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllTextAsync(temp, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    public async Task DownloadAsync(string fileName, string destinationPath, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(fileName, @"^[A-Za-z0-9._-]+$"))
        {
            throw new UpdateException("Unexpected file name: " + fileName);
        }

        var scp = FindScp();
        var start = new ProcessStartInfo(scp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in BuildArguments(fileName, destinationPath))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new UpdateException("Couldn't start scp.exe: " + ex.Message, ex);
        }

        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new UpdateException("The SSH download took too long and was stopped.");
        }

        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new UpdateException(ExplainScpError(error, fileName));
        }
    }

    private string ExplainScpError(string error, string fileName)
    {
        if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No ED25519 host key is known", StringComparison.OrdinalIgnoreCase)
            || error.Contains("host key is known", StringComparison.OrdinalIgnoreCase))
        {
            return $"{_host} isn't a known host yet. Open a terminal, run  ssh {(_port == 22 ? string.Empty : "-p " + _port + " ")}{_user}@{_host}  once and accept the host key.";
        }

        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"SSH login to {_user}@{_host} was refused. SmartDock can't type passwords: set up key login (ssh-copy-id) or choose the key in Settings › Updates.";
        }

        if (error.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return $"{fileName} not found in {_path} on {_host}.";
        }

        return string.IsNullOrEmpty(error) ? "scp failed." : "scp: " + error;
    }

    private static string FindScp()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "scp.exe");
        if (File.Exists(system))
        {
            return system;
        }

        // 32-bit process on 64-bit Windows sees System32 through WOW64; Sysnative maps back.
        var sysnative = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "OpenSSH", "scp.exe");
        return File.Exists(sysnative)
            ? sysnative
            : throw new UpdateException("Windows' OpenSSH client isn't installed (Settings › System › Optional features › OpenSSH Client).");
    }
}
