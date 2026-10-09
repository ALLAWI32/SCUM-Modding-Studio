using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.Crafting;

/// <summary>What a craftable becomes in game (see <see cref="CraftablesPlanner"/> for the template each one is cloned from).</summary>
public enum CraftKind
{
    /// <summary>A base-building element placed like the game's furniture (template: the improvised wooden chair).</summary>
    Furniture,

    /// <summary>A crafting station other recipes can require nearby (template: the improvised workbench and its item).</summary>
    Station,

    /// <summary>A power object: an item that gives electricity inside a radius (template: the electricity generator).</summary>
    Power,
}

/// <summary>The main material of a craftable; picks the ingredients of its suggested recipe.</summary>
public enum CraftMaterial
{
    /// <summary>Planks, logs, nails; cut with a blade.</summary>
    Wood,

    /// <summary>Metal scrap, bolts, a toolbox.</summary>
    Metal,

    /// <summary>Bricks, cement, gravel, sand, water.</summary>
    Brick,

    /// <summary>Cement, gravel, sand, metal scrap, water.</summary>
    Cement,

    /// <summary>Stones and rope.</summary>
    Stone,

    /// <summary>Planks and rags (sofas, beds, rugs).</summary>
    Fabric,
}

/// <summary>One ingredient slot of a recipe.</summary>
/// <param name="Tag">The game's ingredient tag (<c>CI_Plank</c>, <c>CI_Group_Toolbox</c>, a station's <c>CI_SS_*</c>).</param>
/// <param name="Amount">Amount without skill (the higher skill levels need less, as in the game's recipes).</param>
/// <param name="IsTool">A tool (used, not consumed) instead of a material.</param>
public sealed record CraftIngredient(string Tag, int Amount, bool IsTool = false);

/// <summary>Power settings of a <see cref="CraftKind.Power"/> craftable (the generator's entity setup values).</summary>
/// <param name="RadiusMeters">Power radius, the green circle (game: 10 m, the small generator 5 m).</param>
/// <param name="Output">Electricity it gives (game: 500, the small generator 250).</param>
/// <param name="FuelPerMinute">Fuel use (game: 0.13889); 0 = it never needs fuel.</param>
/// <param name="DayOnly">Solar: works only by day. Not a value of the game's data (kept, reported at export).</param>
public sealed record PowerSettings(float RadiusMeters = 10, float Output = 500, float FuelPerMinute = 0.13889f, bool DayOnly = false);

/// <summary>A craftable of the project (one entry of <c>craftables.json</c>).</summary>
public sealed record Craftable
{
    /// <summary>Name shown in game (recipe caption and element name).</summary>
    public required string Name { get; init; }

    /// <summary>The asset it was made from (a static mesh or a Blueprint, package path).</summary>
    public required string Source { get; init; }

    /// <summary>The static mesh it shows (package path; the Blueprint's mesh for a Blueprint source).</summary>
    public required string Mesh { get; init; }

    /// <summary>What it becomes.</summary>
    public CraftKind Kind { get; init; }

    /// <summary>Main material (for the suggested recipe).</summary>
    public CraftMaterial Material { get; init; }

    /// <summary>Largest extent of the mesh in metres (for the suggested recipe).</summary>
    public float SizeMeters { get; init; } = 1;

    /// <summary>The recipe.</summary>
    public IReadOnlyList<CraftIngredient> Ingredients { get; init; } = [];

    /// <summary>Name of a <see cref="CraftKind.Station"/> craftable the player must stand next to, or null.</summary>
    public string? Station { get; init; }

    /// <summary>The station must have electricity (not a value of the game's data: kept, reported at export).</summary>
    public bool NeedsPower { get; init; }

    /// <summary>Power settings (used when <see cref="Kind"/> is <see cref="CraftKind.Power"/>).</summary>
    public PowerSettings Power { get; init; } = new();

    /// <summary>The token of its new assets: <c>SS_</c> + the name in letters, digits and '_'.</summary>
    [JsonIgnore]
    public string Token => CraftablesFile.TokenOf(Name);
}

/// <summary>The project's craftables, stored as <c>craftables.json</c> in the project folder.</summary>
public sealed record CraftablesFile
{
    /// <summary>File name in the project folder.</summary>
    public const string FileName = "craftables.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The craftables, in list order.</summary>
    public IReadOnlyList<Craftable> Items { get; init; } = [];

    /// <summary>The file of <paramref name="projectDirectory"/>, or an empty list when there is none.</summary>
    public static CraftablesFile Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new CraftablesFile();
    }

    /// <summary>Reads the JSON text of the file.</summary>
    public static CraftablesFile Parse(string json) => JsonSerializer.Deserialize<CraftablesFile>(json, Json) ?? new CraftablesFile();

    /// <summary>The JSON text of the file.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Writes the file into <paramref name="projectDirectory"/> (deletes it when the list is empty).</summary>
    public void Save(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        if (Items.Count == 0)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(path, ToJson());
    }

    /// <summary><c>SS_</c> + <paramref name="name"/> with every other character as '_' (asset names of the craftable).</summary>
    public static string TokenOf(string name) =>
        "SS_" + Regex.Replace(name.Trim(), "[^A-Za-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');
}

/// <summary>
/// The suggested recipe of a craftable, from its material and size, scaled from the game's own recipes (read from the
/// paks, NoSkill amounts): a modular wall (about 3 m) of wood = 9 planks, 4 logs, a blade (6); of metal = 8 metal scrap,
/// 4 bolts, 6 toolbox uses, a blunt tool (4); of brick = 31 bricks, 9 cement, 7 gravel, 40 sand, 12 water, 28 toolbox
/// uses, a blunt tool (19); of cement = 28 metal scrap, 28 cement, 14 gravel, 81 sand, 28 water, 25 toolbox uses, a blunt
/// tool (19); a 1 m leather sofa = 4 planks, 4 rags, 2 fasteners, a blunt tool. The improvised table (3 planks, a blade)
/// and wardrobe (8 planks, 10 fasteners) land on the same scale.
/// </summary>
public static class RecipeRules
{
    private static readonly Dictionary<CraftMaterial, (float Reference, CraftIngredient[] Slots)> Bases = new()
    {
        [CraftMaterial.Wood] = (3, [new("CI_Plank", 9), new("CI_Group_Logs", 4), new("CI_Group_Fastener", 3), new("CI_Group_Blade", 6, true)]),
        [CraftMaterial.Metal] = (3, [new("CI_Metal_Scrap", 8), new("CI_Bolts", 4), new("CI_Group_Toolbox", 6), new("CI_Group_BluntTool", 4, true)]),
        [CraftMaterial.Brick] = (3, [new("CI_Brick", 31), new("CI_Cement_Bag", 9), new("CI_Gravel_Bag", 7), new("CI_Sand_Bag", 40), new("CI_Group_Liquid", 12), new("CI_Group_Toolbox", 28), new("CI_Group_BluntTool", 19, true)]),
        [CraftMaterial.Cement] = (3, [new("CI_Metal_Scrap", 28), new("CI_Cement_Bag", 28), new("CI_Gravel_Bag", 14), new("CI_Sand_Bag", 81), new("CI_Group_Liquid", 28), new("CI_Group_Toolbox", 25), new("CI_Group_BluntTool", 19, true)]),
        [CraftMaterial.Stone] = (3, [new("CI_Stone", 10), new("CI_Group_Rope", 2), new("CI_Group_BluntTool", 3, true)]),
        [CraftMaterial.Fabric] = (1, [new("CI_Plank", 4), new("CI_Group_Rags", 4), new("CI_Group_Fastener", 2), new("CI_Group_BluntTool", 1, true)]),
    };

    /// <summary>The ingredient tags the editor offers (the suggestions' tags plus common extras).</summary>
    public static IReadOnlyList<string> CommonTags { get; } = Bases.Values.SelectMany(b => b.Slots).Select(s => s.Tag)
        .Concat(["CI_Nails", "CI_Wire", "CI_Group_Rope", "CI_Group_Glass", "CI_Duct_Tape", "CI_Lead_Plate", "CI_Metal_Pipe", "CI_Glue", "CI_Group_Hammers", "CI_Group_Machinery"])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The suggested ingredients: the material's base recipe times the size against its reference (clamped to a quarter
    /// and three times, so a stool is not free and a house is not absurd), each at least 1; tools stay at most the base.
    /// </summary>
    public static IReadOnlyList<CraftIngredient> Suggest(CraftMaterial material, float sizeMeters)
    {
        var (reference, slots) = Bases[material];
        var factor = Math.Clamp(sizeMeters / reference, 0.25f, 3f);
        return slots.Select(s => s with
        {
            Amount = Math.Max(1, (int)MathF.Round(s.IsTool ? Math.Min(s.Amount, s.Amount * factor) : s.Amount * factor)),
        }).ToList();
    }

    /// <summary>The material named by the asset's path and material names (wood when nothing matches).</summary>
    public static CraftMaterial GuessMaterial(IEnumerable<string> hints)
    {
        var text = string.Join(' ', hints).ToLowerInvariant();
        (CraftMaterial Material, string[] Words)[] rules =
        [
            (CraftMaterial.Brick, ["brick"]),
            (CraftMaterial.Cement, ["concrete", "cement"]),
            (CraftMaterial.Fabric, ["fabric", "cloth", "leather", "sofa", "couch", "mattress", "curtain", "rug", "carpet", "pillow", "bed_"]),
            (CraftMaterial.Metal, ["metal", "steel", "iron", "rust", "alumin", "chrome", "pipe", "container", "generator", "solar", "pole", "pump", "locker"]),
            (CraftMaterial.Stone, ["stone", "rock", "marble", "granite", "boulder"]),
            (CraftMaterial.Wood, ["wood", "plank", "log", "timber", "oak", "pine", "tree", "crate"]),
        ];
        return rules.FirstOrDefault(r => r.Words.Any(text.Contains)) is { Words: not null } hit ? hit.Material : CraftMaterial.Wood;
    }

    /// <summary>The kind an asset suggests: power poles, generators and solar panels give power; benches and workbenches are stations.</summary>
    public static CraftKind GuessKind(string path)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return leaf switch
        {
            _ when new[] { "generator", "solar", "powerpole", "power_pole", "electric_pole", "transformer" }.Any(leaf.Contains) => CraftKind.Power,
            _ when new[] { "workbench", "work_bench", "anvil", "lathe", "workshop" }.Any(leaf.Contains) => CraftKind.Station,
            _ => CraftKind.Furniture,
        };
    }

    /// <summary>
    /// True for what can become a craftable: a static mesh or a Blueprint that is not a character, an animal, a vehicle or
    /// anything else that moves on its own (owner's rule).
    /// </summary>
    public static bool IsAllowedSource(string packagePath, string? className)
    {
        if (className is not ("StaticMesh" or "Blueprint" or "BlueprintGeneratedClass"))
        {
            return false;
        }

        string[] moving = ["/Characters/", "/NPCs/", "/NPC/", "/Animals/", "/Zombies/", "/Puppets/", "/Vehicles/", "/Drones/", "/Weapons/"];
        return !moving.Any(m => packagePath.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
