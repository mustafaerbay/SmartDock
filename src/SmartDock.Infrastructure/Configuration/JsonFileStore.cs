using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartDock.Infrastructure.Configuration;

/// <summary>
/// Reads and writes a JSON document atomically. A file that fails to parse is moved aside as
/// "name.corrupt-yyyyMMddHHmmss.json" and replaced by defaults, so a bad file never blocks startup.
/// </summary>
public sealed class JsonFileStore<T>(string path, Func<T> createDefault, ILogger? logger = null)
    where T : class
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Path { get; } = path;

    public LoadResult Load()
    {
        if (!File.Exists(Path))
        {
            return new LoadResult(createDefault(), LoadOutcome.Missing);
        }

        try
        {
            var json = File.ReadAllText(Path);
            var value = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            if (value is null)
            {
                throw new JsonException("Document is empty or null.");
            }

            return new LoadResult(value, LoadOutcome.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var backup = QuarantineCorruptFile();
            _logger.LogWarning(ex, "Configuration file {Path} is corrupt; moved to {Backup} and using defaults", Path, backup);
            return new LoadResult(createDefault(), LoadOutcome.RecoveredFromCorruption);
        }
    }

    public void Save(T value)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        File.WriteAllText(temp, json);

        // Write-then-rename keeps the previous file intact if we crash mid-write.
        File.Move(temp, Path, overwrite: true);
    }

    private string? QuarantineCorruptFile()
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(Path);
            var dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            var backup = System.IO.Path.Combine(dir, $"{name}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(Path, backup, overwrite: true);
            return backup;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not move corrupt file {Path} aside", Path);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not move corrupt file {Path} aside", Path);
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public enum LoadOutcome
    {
        Missing,
        Loaded,
        RecoveredFromCorruption,
    }

    public readonly record struct LoadResult(T Value, LoadOutcome Outcome);
}
