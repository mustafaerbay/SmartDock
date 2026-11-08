using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartDock.Core.Updates;

/// <summary>Contents of latest.json in the release folder.</summary>
public sealed record UpdateManifest(Version Version, string Msi, string Sha256, string Notes)
{
    public const string FileName = "latest.json";

    private static readonly Regex MsiName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.msi$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses and validates latest.json. The MSI must be a plain file name in the same folder (no paths, no URLs),
    /// so a manipulated manifest can't point the updater somewhere else.
    /// </summary>
    public static UpdateManifest Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

            if (!System.Version.TryParse(Get("version"), out var version) || version.Major < 0)
            {
                throw new UpdateException("latest.json: \"version\" must look like 1.13.0.");
            }

            var msi = Get("msi");
            if (!MsiName.IsMatch(msi))
            {
                throw new UpdateException("latest.json: \"msi\" must be a plain .msi file name in the same folder.");
            }

            var sha = Get("sha256");
            if (!Hex64.IsMatch(sha))
            {
                throw new UpdateException("latest.json: \"sha256\" must be the 64-character SHA-256 of the MSI.");
            }

            return new UpdateManifest(Normalize(version), msi, sha.ToUpperInvariant(), Get("notes"));
        }
        catch (JsonException ex)
        {
            throw new UpdateException("latest.json isn't valid JSON: " + ex.Message);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        version = Version.ToString(3),
        msi = Msi,
        sha256 = Sha256,
        notes = Notes,
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>1.13 → 1.13.0 so comparisons don't treat a missing build number as "older".</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static bool IsNewer(Version candidate, string current) =>
        System.Version.TryParse(current, out var installed) && Normalize(candidate) > Normalize(installed);
}

public enum UpdateStatus
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

public sealed record UpdateCheckResult(UpdateStatus Status, string Message, UpdateManifest? Manifest = null, DateTimeOffset? CheckedAt = null);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A place releases are fetched from (https, ssh, network share or local folder).</summary>
public interface IUpdateSource
{
    /// <summary>Human-readable location for messages, e.g. "ssh://deploy@build01:/srv/smartdock".</summary>
    string Display { get; }

    Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken);

    Task DownloadAsync(string fileName, string destinationPath, CancellationToken cancellationToken);
}

public interface IUpdateService
{
    /// <summary>Last result (NotConfigured until a check ran).</summary>
    UpdateCheckResult Last { get; }

    event EventHandler<UpdateCheckResult>? StatusChanged;

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads, verifies (SHA-256) and starts the MSI upgrade; SmartDock is then closed by the installer.</summary>
    Task InstallAsync(UpdateManifest manifest, CancellationToken cancellationToken);
}
