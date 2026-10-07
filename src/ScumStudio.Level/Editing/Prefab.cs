using System.Text.Json;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Editing;

/// <summary>What one part of a prefab is made from.</summary>
public enum PrefabPartKind
{
    /// <summary>An actor the game placed in a level (a building with everything it stores); placed as a copy of it.</summary>
    StockActor,

    /// <summary>A plain mesh (a prop, a copied tree, a part of a building); placed as a new StaticMeshActor.</summary>
    StaticMesh,

    /// <summary>A Blueprint the project placed by class (copied from the game's instance in <see cref="PrefabPart.Level"/>).</summary>
    Blueprint,

    /// <summary>One tree, rock or bush of a foliage component; placed as a new instance of that component when its level is loaded, else as a mesh.</summary>
    FoliageInstance,
}

/// <summary>
/// One object of a prefab. Every path is the game's own object path, so the file reads like a bill of materials:
/// what the object is, which level it came from and where it stands relative to the prefab's pivot.
/// </summary>
public sealed record PrefabPart
{
    /// <summary>What the part is.</summary>
    public PrefabPartKind Kind { get; init; }

    /// <summary>Source level package (stock actors, Blueprints and foliage instances).</summary>
    public string? Level { get; init; }

    /// <summary>Source actor name in <see cref="Level"/>.</summary>
    public string? Actor { get; init; }

    /// <summary>Foliage instances: the component holding the instance.</summary>
    public string? Component { get; init; }

    /// <summary>Class object path (<c>/Game/X/BP_House.BP_House_C</c>, <c>/Script/Engine.StaticMeshActor</c>).</summary>
    public string? Class { get; init; }

    /// <summary>Mesh object path (<c>/Game/X/SM_Crate.SM_Crate</c>) when the part draws one mesh.</summary>
    public string? Mesh { get; init; }

    /// <summary>For a copied item spawner: the item it spawns.</summary>
    public string? Item { get; init; }

    /// <summary>Collision profile the copy keeps (a tree's <c>SCUM_TreeStump</c>), or null for the mesh's default.</summary>
    public string? Collision { get; init; }

    /// <summary>Where the part stands relative to <see cref="Prefab.Pivot"/>: location in cm, rotation in degrees, scale.</summary>
    public TransformValue Transform { get; init; } = TransformValue.Identity;

    /// <summary>The part's bend (see <see cref="BendActorOp"/>), or null when straight.</summary>
    public BendValue? Bend { get; init; }

    /// <summary>Short name for lists: the mesh, the class or the actor.</summary>
    public string DisplayName
    {
        get
        {
            var path = Mesh ?? Class ?? Actor ?? Kind.ToString();
            var name = path[(path.LastIndexOf('/') + 1)..];
            var dot = name.IndexOf('.');
            return dot < 0 ? name : path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) ? name[(dot + 1)..] : name[..dot];
        }
    }
}

/// <summary>
/// A saved selection: a building with its props, a camp, a bridge, kept as plain indented JSON (<c>.ssprefab</c>) so it
/// can be read, edited and shared. Positions are relative to <see cref="Pivot"/> (the selection's centre on the ground),
/// so the prefab is placed anywhere by putting the pivot where the camera aims.
/// </summary>
public sealed record Prefab
{
    /// <summary>File extension, with the dot.</summary>
    public const string Extension = ".ssprefab";

    /// <summary>Format identifier written into every file.</summary>
    public const string FormatId = "scumstudio.prefab/1";

    /// <summary>The format (see <see cref="FormatId"/>).</summary>
    public string Format { get; init; } = FormatId;

    /// <summary>The name the user gave it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>When it was saved (UTC).</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>ScumStudio version that wrote it.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Game build the source objects came from, when known.</summary>
    public string? GameVersion { get; init; }

    /// <summary>The selection's centre on the ground (UE world cm) when it was saved; every part's transform is relative to it.</summary>
    public FVector Pivot { get; init; }

    /// <summary>The objects.</summary>
    public IReadOnlyList<PrefabPart> Parts { get; init; } = [];

    /// <summary>Indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, LevelJson.Indented);

    /// <summary>Parses a file written by <see cref="ToJson"/>.</summary>
    /// <exception cref="InvalidDataException">Not a prefab file, or it has no parts.</exception>
    public static Prefab FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Prefab? prefab;
        try
        {
            prefab = JsonSerializer.Deserialize<Prefab>(json, LevelJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a prefab file: " + ex.Message, ex);
        }

        if (prefab is null || !string.Equals(prefab.Format, FormatId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Not a prefab file (expected \"format\": \"{FormatId}\").");
        }

        // A null list or a null element would surface later as a crash (List(), Place); refused here like other bad files.
        if (prefab.Parts is not { Count: > 0 } parts)
        {
            throw new InvalidDataException("The prefab has no parts.");
        }

        if (parts.Any(p => p is null))
        {
            throw new InvalidDataException("A part of the prefab is empty (null).");
        }

        return prefab;
    }

    /// <summary>Reads a <c>.ssprefab</c> file.</summary>
    /// <exception cref="InvalidDataException">Not a prefab file.</exception>
    public static Prefab Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Writes the file (indented JSON, UTF-8 without BOM).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>A file name stem for <paramref name="name"/> (invalid characters replaced, "Prefab" when empty).</summary>
    public static string SafeFileName(string name)
    {
        var stem = string.Concat((name ?? string.Empty).Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        return stem.Length == 0 ? "Prefab" : stem;
    }
}
