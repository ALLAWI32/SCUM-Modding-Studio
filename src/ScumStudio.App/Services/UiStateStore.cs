using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScumStudio.Core.IO;
using ScumStudio.Core.Settings;

namespace ScumStudio.App.Services;

/// <summary>Editor-only UI state kept next to <c>settings.json</c> in <c>ui-state.json</c>.</summary>
public sealed record UiState
{
    /// <summary>Accent colour as <c>#RRGGBB</c>.</summary>
    public string Accent { get; init; } = AccentPalette.DefaultHex;

    /// <summary>Whether the log panel is open.</summary>
    public bool LogPanelOpen { get; init; }

    /// <summary>Key of the last page shown.</summary>
    public string? LastPage { get; init; }

    /// <summary>True once the first-run setup was completed or skipped.</summary>
    public bool SetupCompleted { get; init; }

    /// <summary>Assets page shows tiles (true) or rows.</summary>
    public bool AssetsGridView { get; init; } = true;
}

/// <summary>Loads and saves <see cref="UiState"/>; failures fall back to defaults and are logged.</summary>
public sealed class UiStateStore
{
    /// <summary>File name inside the data folder.</summary>
    public const string FileName = "ui-state.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;

    /// <summary>Creates the store in <paramref name="directory"/>.</summary>
    public UiStateStore(string directory, ILogger logger)
    {
        FilePath = Path.Combine(directory, FileName);
        _logger = logger;
        Current = Load();
    }

    /// <summary>Full path of the file.</summary>
    public string FilePath { get; }

    /// <summary>Last loaded/saved state.</summary>
    public UiState Current { get; private set; }

    /// <summary>Applies <paramref name="change"/> and saves.</summary>
    public UiState Update(Func<UiState, UiState> change)
    {
        Current = change(Current);
        try
        {
            StudioHome.EnsureDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(Current, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save {File}: {Message}", FilePath, ex.Message);
        }

        return Current;
    }

    private UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<UiState>(File.ReadAllBytes(FilePath), Options) ?? new UiState();

                // One-time migration: the pre-FIELD MANUAL default blue becomes Blaze (saved on the next Update).
                return state with { Accent = AccentPalette.Migrate(state.Accent) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not read {File}; using defaults: {Message}", FilePath, ex.Message);
        }

        return new UiState();
    }
}
