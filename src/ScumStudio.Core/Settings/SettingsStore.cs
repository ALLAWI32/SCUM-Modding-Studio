using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.IO;

namespace ScumStudio.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as indented camelCase JSON in <c>settings.json</c> under
/// <see cref="StudioHome.GetDirectory"/> (or an explicit folder).
/// </summary>
/// <remarks>
/// Saving is atomic (temporary file + rename). Loading never throws for content problems: a missing file yields
/// defaults; an unreadable or corrupt file is renamed to <c>settings.json.corrupt-&lt;timestamp&gt;</c>, a warning is
/// logged and defaults are returned. Instances are thread-safe.
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>File name of the settings file.</summary>
    public const string FileName = "settings.json";

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a store for <paramref name="directory"/> (default: <see cref="StudioHome.GetDirectory"/>).</summary>
    public SettingsStore(string? directory = null, ILogger? logger = null)
    {
        Directory = Path.GetFullPath(directory ?? StudioHome.GetDirectory());
        FilePath = Path.Combine(Directory, FileName);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>JSON options used for the settings file (camelCase, indented, enums as strings, comments allowed).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Folder containing the settings file.</summary>
    public string Directory { get; }

    /// <summary>Full path of <c>settings.json</c>.</summary>
    public string FilePath { get; }

    /// <summary>Loads the settings (see remarks for error handling).</summary>
    public AppSettings Load()
    {
        _gate.Wait();
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read {Path}; using default settings.", FilePath);
                return new AppSettings();
            }

            return Parse(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the settings asynchronously (see remarks for error handling).</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read {Path}; using default settings.", FilePath);
                return new AppSettings();
            }

            return Parse(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically writes <paramref name="settings"/> to <see cref="FilePath"/>.</summary>
    public void Save(AppSettings settings)
    {
        var bytes = Serialize(settings);
        _gate.Wait();
        try
        {
            StudioHome.EnsureDirectory(Directory);
            AtomicFile.WriteAllBytes(FilePath, bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically writes <paramref name="settings"/> to <see cref="FilePath"/>.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var bytes = Serialize(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StudioHome.EnsureDirectory(Directory);
            await AtomicFile.WriteAllBytesAsync(FilePath, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads, applies <paramref name="change"/> and saves; returns the saved settings.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var updated = change(Load());
        Save(updated);
        return updated;
    }

    /// <summary>Serialises settings to the UTF-8 JSON written by <see cref="Save"/>.</summary>
    public static byte[] Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
    }

    /// <summary>
    /// Parses settings JSON, migrating older schema versions. Throws <see cref="JsonException"/> on malformed content.
    /// </summary>
    public static AppSettings Deserialize(ReadOnlySpan<byte> utf8Json, ILogger? logger = null)
    {
        var node = JsonNode.Parse(
            utf8Json,
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (node is not JsonObject root)
        {
            throw new JsonException("The settings file does not contain a JSON object.");
        }

        SettingsMigrator.Migrate(root, logger);
        var settings = root.Deserialize<AppSettings>(JsonOptions)
            ?? throw new JsonException("The settings file deserialised to null.");

        // Normalise nulls a hand-edited file may contain.
        return settings with
        {
            RecentProjects = settings.RecentProjects?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [],
            Ui = settings.Ui ?? new UiPreferences(),
        };
    }

    private AppSettings Parse(byte[] bytes)
    {
        try
        {
            return Deserialize(bytes, _logger);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            var backup = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(FilePath, backup, overwrite: true);
                _logger.LogWarning(ex, "{Path} is not valid settings JSON; moved it to {Backup} and using defaults.", FilePath, backup);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{Path} is not valid settings JSON; using defaults.", FilePath);
            }

            return new AppSettings();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
