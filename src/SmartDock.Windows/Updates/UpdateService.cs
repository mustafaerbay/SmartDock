using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SmartDock.Core;
using SmartDock.Core.Services;
using SmartDock.Core.Updates;

namespace SmartDock.Windows.Updates;

/// <summary>
/// Checks the configured release folder for a newer latest.json and installs the MSI on request:
/// download → SHA-256 check → <c>msiexec /i … /passive RELAUNCH=1</c>. The MSI then closes this instance
/// cleanly (SmartDock.exe --shutdown restores the taskbar), upgrades and restarts SmartDock.
/// Nothing is downloaded or run unless the user set a feed location; installing always needs a click.
/// </summary>
public sealed class UpdateService(ISettingsService settings, HttpClient http, ILogger<UpdateService> logger) : IUpdateService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCheckResult Last { get; private set; } = new(UpdateStatus.NotConfigured, "Updates aren't set up.");

    public event EventHandler<UpdateCheckResult>? StatusChanged;

    /// <summary>%LOCALAPPDATA%\SmartDock\updates – downloaded installers and msiexec logs.</summary>
    public static string DownloadFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartDock", "updates");

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = settings.Current.Updates;
            if (config.FeedLocation.Length == 0)
            {
                return Publish(new UpdateCheckResult(UpdateStatus.NotConfigured, "Updates aren't set up. Enter the release location below."));
            }

            try
            {
                var source = UpdateSourceFactory.Create(config, http);
                var json = await source.ReadTextAsync(UpdateManifest.FileName, cancellationToken).ConfigureAwait(false);
                var manifest = UpdateManifest.Parse(json);
                var now = DateTimeOffset.Now;
                return Publish(UpdateManifest.IsNewer(manifest.Version, AppInfo.Version)
                    ? new UpdateCheckResult(UpdateStatus.Available, $"SmartDock v{manifest.Version.ToString(3)} is available (you have {AppInfo.DisplayVersion}).", manifest, now)
                    : new UpdateCheckResult(UpdateStatus.UpToDate, $"You're up to date ({AppInfo.DisplayVersion}). Checked {source.Display} at {now:HH:mm}.", manifest, now));
            }
            catch (UpdateException ex)
            {
                logger.LogWarning("Update check failed: {Message}", ex.Message);
                return Publish(new UpdateCheckResult(UpdateStatus.Failed, ex.Message, CheckedAt: DateTimeOffset.Now));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Update check failed");
                return Publish(new UpdateCheckResult(UpdateStatus.Failed, "Update check failed: " + ex.Message, CheckedAt: DateTimeOffset.Now));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InstallAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (!UpdateManifest.IsNewer(manifest.Version, AppInfo.Version))
        {
            throw new UpdateException("That version isn't newer than the one running.");
        }

        var source = UpdateSourceFactory.Create(settings.Current.Updates, http);
        Directory.CreateDirectory(DownloadFolder);
        var msiPath = Path.Combine(DownloadFolder, manifest.Msi);
        var partial = msiPath + ".download";

        logger.LogInformation("Downloading update {Version} from {Source}", manifest.Version, source.Display);
        await source.DownloadAsync(manifest.Msi, partial, cancellationToken).ConfigureAwait(false);

        string actual;
        await using (var stream = File.OpenRead(partial))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new UpdateException("The downloaded installer doesn't match the checksum in latest.json, so it was deleted and not installed.");
        }

        File.Move(partial, msiPath, overwrite: true);
        CleanOldDownloads(keep: msiPath);

        var log = Path.Combine(DownloadFolder, $"install-{manifest.Version.ToString(3)}.log");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
        {
            UseShellExecute = false,
            ArgumentList = { "/i", msiPath, "/passive", "/norestart", "RELAUNCH=1", "/l*v", log },
        };

        logger.LogInformation("Starting update installer {Msi}", msiPath);
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UpdateException("Couldn't start the installer: " + ex.Message, ex);
        }

        // The installer now asks this instance to shut down (--shutdown) before replacing files.
    }

    private UpdateCheckResult Publish(UpdateCheckResult result)
    {
        Last = result;
        StatusChanged?.Invoke(this, result);
        return result;
    }

    private static void CleanOldDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(DownloadFolder, "*.msi"))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
