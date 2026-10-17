using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// The decals of one vehicle clone, kept in the project as <c>decals/&lt;clone leaf&gt;.json</c>. Fields this version does not
/// know are skipped on reading, and the version it was written with is kept.
/// </summary>
/// <param name="CloneLeaf">The clone's package leaf (<c>BPC_MyRager</c>).</param>
/// <param name="Layers">The decals, any order (<see cref="DecalLayer.Order"/> decides what is on top).</param>
public sealed record DecalSet(string CloneLeaf, IReadOnlyList<DecalLayer> Layers)
{
    /// <summary>The format this build writes.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Format version of the file.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The layers bottom first.</summary>
    public IReadOnlyList<DecalLayer> Ordered => [.. Layers.OrderBy(l => l.Order)];

    /// <summary><c>&lt;project&gt;/decals/&lt;clone&gt;.json</c>.</summary>
    public static string PathOf(string projectDirectory, string clone) =>
        System.IO.Path.Combine(projectDirectory, "decals", string.Concat(clone.Split(System.IO.Path.GetInvalidFileNameChars())) + ".json");

    /// <summary>The set as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A set from JSON (unknown fields skipped).</summary>
    public static DecalSet FromJson(string json) =>
        JsonSerializer.Deserialize<DecalSet>(json, Json) is { } set ? set with { Layers = set.Layers ?? [] } : throw new JsonException("No decal set.");

    /// <summary>The clone's decals saved in <paramref name="projectDirectory"/>, or an empty set.</summary>
    public static DecalSet Load(string projectDirectory, string clone)
    {
        var path = PathOf(projectDirectory, clone);
        return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new DecalSet(clone, []);
    }

    /// <summary>Writes the set to <c>decals/&lt;clone&gt;.json</c> in <paramref name="projectDirectory"/> (through a temporary file).</summary>
    public void Save(string projectDirectory) => Write(PathOf(projectDirectory, CloneLeaf));

    /// <summary><c>&lt;project&gt;/decals/presets/&lt;name&gt;.json</c>.</summary>
    public static string PresetPathOf(string projectDirectory, string name) =>
        System.IO.Path.Combine(projectDirectory, "decals", "presets", string.Concat(name.Trim().Split(System.IO.Path.GetInvalidFileNameChars())) + ".json");

    /// <summary>The project's saved presets by name, sorted.</summary>
    public static IReadOnlyList<string> Presets(string projectDirectory) => Names(System.IO.Path.Combine(projectDirectory, "decals", "presets"));

    /// <summary>The project's clones that have decals (their <c>decals/&lt;clone&gt;.json</c>), sorted.</summary>
    public static IReadOnlyList<string> Clones(string projectDirectory) => Names(System.IO.Path.Combine(projectDirectory, "decals"));

    /// <summary>Saves these decals as the preset <paramref name="name"/> of the project (replacing one of that name).</summary>
    public void SavePreset(string projectDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        (this with { CloneLeaf = name.Trim() }).Write(PresetPathOf(projectDirectory, name));
    }

    /// <summary>The preset <paramref name="name"/> of the project.</summary>
    public static DecalSet LoadPreset(string projectDirectory, string name) => FromJson(File.ReadAllText(PresetPathOf(projectDirectory, name)));

    /// <summary>
    /// <paramref name="target"/> with these decals put on top of its own: each a new layer (its own id) in the same order,
    /// at the same place, size and colours, so a clone of the same vehicle bakes them alike.
    /// </summary>
    public DecalSet AddTo(DecalSet target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var above = target.Layers.Count == 0 ? 0 : target.Layers.Max(l => l.Order) + 1;
        var copies = Ordered.Select((l, i) => l with { Id = new DecalLayer().Id, Order = above + i });
        return target with { Layers = [.. target.Layers, .. copies] };
    }

    private void Write(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }

    private static IReadOnlyList<string> Names(string folder) => Directory.Exists(folder)
        ? [.. Directory.EnumerateFiles(folder, "*.json").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).Order(StringComparer.OrdinalIgnoreCase)]
        : [];
}
