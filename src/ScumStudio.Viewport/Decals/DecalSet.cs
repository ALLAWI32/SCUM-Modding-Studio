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
    public void Save(string projectDirectory)
    {
        var path = PathOf(projectDirectory, CloneLeaf);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }
}
