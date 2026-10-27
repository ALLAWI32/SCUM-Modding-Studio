using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>
/// The area a loot override applies to: a rectangle of the map (UE cm). The game wants its corners as the top left (the larger X and
/// the larger Y) and the bottom right (the smaller ones); <see cref="Of"/> puts any two corners in that order.
/// </summary>
/// <param name="MaxX">The larger X.</param>
/// <param name="MaxY">The larger Y.</param>
/// <param name="MinX">The smaller X.</param>
/// <param name="MinY">The smaller Y.</param>
public readonly record struct LootZone(double MaxX, double MaxY, double MinX, double MinY)
{
    /// <summary>The rectangle between two corners given in any order.</summary>
    public static LootZone Of(double x1, double y1, double x2, double y2) =>
        new(Math.Max(x1, x2), Math.Max(y1, y2), Math.Min(x1, x2), Math.Min(y1, y2));

    /// <summary>The rectangle of the points grown by <paramref name="margin"/> cm on every side.</summary>
    public static LootZone Around(IEnumerable<(double X, double Y)> points, double margin)
    {
        var list = points.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one point is needed.", nameof(points));
        }

        return Of(list.Min(p => p.X) - margin, list.Min(p => p.Y) - margin, list.Max(p => p.X) + margin, list.Max(p => p.Y) + margin);
    }

    /// <summary>True for a rectangle with a size.</summary>
    public bool IsValid => MaxX > MinX && MaxY > MinY;
}

/// <summary>
/// The server's loot customization (<c>SCUM\Saved\Config\WindowsServer\Loot\Spawners\Presets</c>): the presets the game exported
/// (<c>Default</c>, a reference) and the owner's changes in <c>Override\&lt;zone&gt;</c>, each zone folder with the
/// <c>Zones.json</c> rectangle it applies to. A preset file is named after the preset's path with dashes
/// (<c>Street-Military-Examine_Crate.json</c> is <c>Street/Military/Examine_Crate</c>). The server reads the changes on
/// <c>#ReloadLootCustomizationsAndResetSpawners</c>.
/// </summary>
public sealed partial class LootOverrideStore
{
    /// <summary>The chat command that makes the server read the changes (it also lets examine spawners roll again).</summary>
    public const string ReloadCommand = "#ReloadLootCustomizationsAndResetSpawners";

    /// <summary>The chat command that writes the game's presets (the reference <c>Default</c> folder).</summary>
    public const string ExportCommand = "#ExportDefaultItemSpawnerPresets";

    private const string ZonesFile = "Zones.json";

    private readonly ServerInstall _server;

    /// <summary>Creates the store over <paramref name="server"/>.</summary>
    public LootOverrideStore(ServerInstall server) => _server = server ?? throw new ArgumentNullException(nameof(server));

    /// <summary>The <c>Presets</c> folder.</summary>
    public string PresetsFolder => Path.Combine(_server.ConfigFolder, "Loot", "Spawners", "Presets");

    /// <summary>Where the game exports its own presets.</summary>
    public string DefaultFolder => Path.Combine(PresetsFolder, "Default");

    /// <summary>Where the owner's changes live.</summary>
    public string OverrideFolder => Path.Combine(PresetsFolder, "Override");

    /// <summary>True when the game's presets were exported (see <see cref="ExportCommand"/>).</summary>
    public bool HasDefaults => Directory.Exists(DefaultFolder) && Directory.EnumerateFiles(DefaultFolder, "*.json").Any();

    /// <summary>True for a zone name that is a folder name the game accepts (letters, digits, _ and -, at most 40).</summary>
    public static bool IsValidZoneName(string? name) => name is { Length: > 0 and <= 40 } && ZoneName().IsMatch(name);

    /// <summary>True for a preset file name (without <c>.json</c>): letters, digits, _, - and ., no folders.</summary>
    public static bool IsValidPresetName(string? name) => name is { Length: > 0 and <= 200 } && PresetName().IsMatch(name);

    /// <summary>The game's presets (file names without <c>.json</c>), sorted.</summary>
    public IReadOnlyList<string> DefaultPresets() => Names(DefaultFolder);

    /// <summary>The zone folders of the changes.</summary>
    public IReadOnlyList<string> Zones() =>
        Directory.Exists(OverrideFolder)
            ? [.. Directory.EnumerateDirectories(OverrideFolder).Select(d => Path.GetFileName(d)).Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>The presets changed in a zone (file names without <c>.json</c>).</summary>
    public IReadOnlyList<string> PresetsOf(string zone) => IsValidZoneName(zone) ? Names(Path.Combine(OverrideFolder, zone)) : [];

    /// <summary>The game's own preset, or null.</summary>
    public LootPresetFile? ReadDefault(string preset) => IsValidPresetName(preset) ? Read(Path.Combine(DefaultFolder, preset + ".json")) : null;

    /// <summary>A zone's changed preset, or null.</summary>
    public LootPresetFile? ReadOverride(string zone, string preset) =>
        IsValidZoneName(zone) && IsValidPresetName(preset) ? Read(Path.Combine(OverrideFolder, zone, preset + ".json")) : null;

    /// <summary>The rectangle of a zone (its <c>Zones.json</c>), or null.</summary>
    public LootZone? ReadZone(string zone)
    {
        var path = Path.Combine(OverrideFolder, zone, ZonesFile);
        if (!IsValidZoneName(zone) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var first = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["Zones"] is JsonArray { Count: > 0 } list ? list[0] as JsonObject : null;
            var a = Corner(first?["TopLeft"]?.GetValue<string>());
            var b = Corner(first?["BottomRight"]?.GetValue<string>());
            return a is { } p && b is { } q ? LootZone.Of(p.X, p.Y, q.X, q.Y) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes a zone's rectangle (<c>Zones.json</c>), creating the zone folder.</summary>
    /// <exception cref="ArgumentException">The name or the rectangle is not valid.</exception>
    public void WriteZone(string zone, LootZone area)
    {
        if (!IsValidZoneName(zone))
        {
            throw new ArgumentException("A zone name is letters, digits, _ and - (at most 40).", nameof(zone));
        }

        if (!area.IsValid)
        {
            throw new ArgumentException("The zone's corners must make a rectangle with a size.", nameof(area));
        }

        var folder = Path.Combine(OverrideFolder, zone);
        Directory.CreateDirectory(folder);
        var corner = (double x, double y) => string.Create(CultureInfo.InvariantCulture, $"X={x:0.000} Y={y:0.000}");
        var json = new JsonObject
        {
            ["Zones"] = new JsonArray(new JsonObject { ["TopLeft"] = corner(area.MaxX, area.MaxY), ["BottomRight"] = corner(area.MinX, area.MinY) }),
        };
        File.WriteAllText(Path.Combine(folder, ZonesFile), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", new System.Text.UTF8Encoding(false));
    }

    /// <summary>Writes a changed preset into a zone (the zone folder and its rectangle must be written first or with it).</summary>
    /// <exception cref="ArgumentException">A name is not valid, or the preset has problems.</exception>
    public void WritePreset(string zone, string preset, LootPresetFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsValidZoneName(zone) || !IsValidPresetName(preset))
        {
            throw new ArgumentException("Not a valid zone or preset name.");
        }

        if (file.Problems() is { Count: > 0 } problems)
        {
            throw new ArgumentException($"The preset has problems: {string.Join(", ", problems.Select(p => p.Code + (p.Detail.Length > 0 ? " " + p.Detail : string.Empty)))}.");
        }

        var folder = Path.Combine(OverrideFolder, zone);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, preset + ".json"), file.ToJson(), new System.Text.UTF8Encoding(false));
    }

    /// <summary>Deletes a zone's changed preset (the game's own applies there again).</summary>
    public void RemovePreset(string zone, string preset)
    {
        if (IsValidZoneName(zone) && IsValidPresetName(preset))
        {
            var path = Path.Combine(OverrideFolder, zone, preset + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Deletes a zone folder with every changed preset in it.</summary>
    public void RemoveZone(string zone)
    {
        if (IsValidZoneName(zone) && Directory.Exists(Path.Combine(OverrideFolder, zone)))
        {
            Directory.Delete(Path.Combine(OverrideFolder, zone), recursive: true);
        }
    }

    private static List<string> Names(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !string.Equals(n, "Zones", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    private static LootPresetFile? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return LootPresetFile.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return null;
        }
    }

    private static (double X, double Y)? Corner(string? text)
    {
        if (text is null || CornerText().Match(text) is not { Success: true } m)
        {
            return null;
        }

        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ZoneName();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex PresetName();

    [GeneratedRegex(@"X=(-?\d+(?:\.\d+)?)\s+Y=(-?\d+(?:\.\d+)?)")]
    private static partial Regex CornerText();
}
