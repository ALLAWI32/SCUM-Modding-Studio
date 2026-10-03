using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScumStudio.Core.Settings;

/// <summary>
/// User settings persisted as <c>settings.json</c> by <see cref="SettingsStore"/>. Immutable; change with
/// <c>with</c> expressions and save the result.
/// </summary>
/// <remarks>
/// <para>
/// Migration-friendly: <see cref="Version"/> records the schema; <see cref="SettingsMigrator"/> upgrades older files
/// before deserialisation, and properties this build does not know (written by a newer ScumStudio) are kept in
/// <see cref="AdditionalProperties"/> and written back unchanged. Add new properties with safe defaults; bump
/// <see cref="CurrentVersion"/> and add a migration step only when a property is renamed or reinterpreted.
/// </para>
/// <para>The AES key is never stored here; see <see cref="Security.ProtectedKeyStore"/>.</para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Maximum number of entries kept in <see cref="RecentProjects"/>.</summary>
    public const int MaxRecentProjects = 10;

    /// <summary>Schema version of this instance (see <see cref="CurrentVersion"/>).</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Game client Paks folder (<c>...\steamapps\common\SCUM\SCUM\Content\Paks</c>); null = not configured.</summary>
    public string? GamePaksFolder { get; init; }

    /// <summary>Dedicated server Paks folder (<c>...\SCUM\Content\Paks</c> next to SCUMServer.exe); null = not configured.</summary>
    public string? ServerPaksFolder { get; init; }

    /// <summary>Folder that receives built client mod paks (e.g. the launcher's mods folder); null = ask.</summary>
    public string? ClientModsOutputFolder { get; init; }

    /// <summary>Folder that receives built server-variant mod paks; null = ask.</summary>
    public string? ServerModsOutputFolder { get; init; }

    /// <summary>Optional user-supplied repak executable used instead of the built-in pak writer.</summary>
    public string? RepakExecutablePath { get; init; }

    /// <summary>Project reopened at start-up; null = none.</summary>
    public string? LastProjectPath { get; init; }

    /// <summary>Recently opened projects, most recent first (at most <see cref="MaxRecentProjects"/>).</summary>
    public IReadOnlyList<string> RecentProjects { get; init; } = [];

    /// <summary>User-interface preferences.</summary>
    public UiPreferences Ui { get; init; } = new();

    /// <summary>The built-in MCP server that lets AI assistants drive the editor (off by default).</summary>
    public McpServerSettings Mcp { get; init; } = new();

    /// <summary>
    /// Properties present in the file but unknown to this build; preserved on save. Not for application use.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    /// <summary>
    /// Returns a copy with <paramref name="projectPath"/> as <see cref="LastProjectPath"/> and at the head of
    /// <see cref="RecentProjects"/> (duplicates removed, list capped).
    /// </summary>
    public AppSettings WithRecentProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var recent = new List<string>(MaxRecentProjects) { projectPath };
        recent.AddRange(RecentProjects.Where(p => !comparer.Equals(p, projectPath)).Take(MaxRecentProjects - 1));
        return this with { LastProjectPath = projectPath, RecentProjects = recent };
    }

    /// <summary>Value equality, comparing <see cref="RecentProjects"/> and <see cref="AdditionalProperties"/> by content.</summary>
    public bool Equals(AppSettings? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null &&
               Version == other.Version &&
               GamePaksFolder == other.GamePaksFolder &&
               ServerPaksFolder == other.ServerPaksFolder &&
               ClientModsOutputFolder == other.ClientModsOutputFolder &&
               ServerModsOutputFolder == other.ServerModsOutputFolder &&
               RepakExecutablePath == other.RepakExecutablePath &&
               LastProjectPath == other.LastProjectPath &&
               RecentProjects.SequenceEqual(other.RecentProjects, StringComparer.Ordinal) &&
               Ui == other.Ui &&
               Mcp == other.Mcp &&
               AdditionalPropertiesEqual(AdditionalProperties, other.AdditionalProperties);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(GamePaksFolder);
        hash.Add(ServerPaksFolder);
        hash.Add(ClientModsOutputFolder);
        hash.Add(ServerModsOutputFolder);
        hash.Add(RepakExecutablePath);
        hash.Add(LastProjectPath);
        foreach (var project in RecentProjects)
        {
            hash.Add(project);
        }

        hash.Add(Ui);
        hash.Add(Mcp);
        hash.Add(AdditionalProperties?.Count ?? 0);
        return hash.ToHashCode();
    }

    private static bool AdditionalPropertiesEqual(Dictionary<string, JsonElement>? a, Dictionary<string, JsonElement>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (countA == 0)
        {
            return true;
        }

        foreach (var (key, value) in a!)
        {
            // Compare re-serialised (compact) text so formatting differences between files do not matter.
            if (!b!.TryGetValue(key, out var otherValue) ||
                JsonSerializer.Serialize(value) != JsonSerializer.Serialize(otherValue))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Colour theme of the editor.</summary>
public enum UiTheme
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Dark theme (default).</summary>
    Dark,

    /// <summary>Light theme.</summary>
    Light,
}

/// <summary>User-interface preferences stored in <see cref="AppSettings.Ui"/>.</summary>
public sealed record UiPreferences
{
    /// <summary>Colour theme.</summary>
    public UiTheme Theme { get; init; } = UiTheme.Dark;

    /// <summary>User-interface language code: <c>en</c> (default), <c>ar</c>, <c>ru</c> or <c>de</c>. Unknown codes show English.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Main window placement; null = let the OS decide.</summary>
    public WindowPlacement? MainWindow { get; init; }

    /// <summary>Viewport fly-camera speed in centimetres per second.</summary>
    public float CameraSpeed { get; init; } = 2000f;

    /// <summary>Viewport vertical field of view in degrees.</summary>
    public float FieldOfViewDegrees { get; init; } = 70f;

    /// <summary>Invert mouse Y in the viewport.</summary>
    public bool InvertMouseY { get; init; }

    /// <summary>Show the ground grid.</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Show frame statistics in the viewport.</summary>
    public bool ShowStatistics { get; init; }

    /// <summary>Translation snap step in centimetres (0 = off).</summary>
    public float TranslationSnap { get; init; } = 10f;

    /// <summary>Rotation snap step in degrees (0 = off).</summary>
    public float RotationSnapDegrees { get; init; } = 15f;

    /// <summary>3D view quality: how far levels stream in and objects draw, LOD detail and texture size.</summary>
    public RenderQuality RenderQuality { get; init; } = RenderQuality.Balanced;
}

/// <summary>3D view quality presets (faster on weaker PCs, more detail on strong ones).</summary>
public enum RenderQuality
{
    /// <summary>Fastest: levels within 200 m, objects to 300 m, coarse LODs, small textures.</summary>
    Performance,

    /// <summary>Default: levels within 300 m, objects to 600 m.</summary>
    Balanced,

    /// <summary>Levels within 600 m, objects to 1.5 km, full LODs, 1024 px textures.</summary>
    High,

    /// <summary>Levels within 1 km, objects as far as the game draws them, finer LODs.</summary>
    Ultra,
}

/// <summary>
/// Settings of the built-in MCP (Model Context Protocol) server stored in <see cref="AppSettings.Mcp"/>. The server only
/// listens on 127.0.0.1 and requires <see cref="Token"/> as a bearer token; it never exposes the AES key.
/// </summary>
public sealed record McpServerSettings
{
    /// <summary>Default TCP port.</summary>
    public const int DefaultPort = 47130;

    /// <summary>Start the server with the editor.</summary>
    public bool Enabled { get; init; }

    /// <summary>TCP port on 127.0.0.1.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Bearer token clients must send; null until the server is first enabled.</summary>
    public string? Token { get; init; }
}

/// <summary>Saved window position and size in device-independent pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="IsMaximized">True when the window was maximised.</param>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized);
