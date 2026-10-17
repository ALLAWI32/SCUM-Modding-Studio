using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScumStudio.Formats;

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

    /// <summary>
    /// An inventory item of the game (weapon, tool, food, ammunition, clothing …) made by a new item recipe (template: a
    /// stock item recipe of the same kind of item, for its crafting menu category).
    /// </summary>
    Item,

    /// <summary>A base element of the game cloned with its own family: a bed stays a bed (<see cref="Craftable.Element"/>).</summary>
    Element,
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
/// <param name="Item">
/// An item of the game picked as the ingredient (package path), or null for <paramref name="Tag"/>: the planner finds the
/// item's own tag (<see cref="IngredientIndex.Resolve"/>).
/// </param>
/// <param name="AddTag">The user allows a new tag on the item's stock entity setup when the item has no tag of its own.</param>
/// <param name="Levels">
/// The amount at each skill level, set by the user; null asks the higher levels for less, as the game's recipes do
/// (<see cref="RecipeIngredients.PerSkill"/> of <paramref name="Amount"/>). When set it is what the recipe gets, its no-skill
/// amount included.
/// </param>
public sealed record CraftIngredient(string Tag, int Amount, bool IsTool = false, string? Item = null, bool AddTag = false, LevelAmounts? Levels = null)
{
    /// <summary>The amounts the recipe gets per skill level (<see cref="Levels"/>, else the automatic ones), each at least 1.</summary>
    [JsonIgnore]
    public LevelAmounts PerLevel => (Levels ?? LevelAmounts.Auto(Amount)).Clamped();
}

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

    /// <summary>
    /// The token of its new assets (<c>CR_</c>/<c>BP_</c> + it), stored the first time the craftable is saved and kept by a
    /// rename: objects players placed are saved by class path. A file without it gets it from <see cref="CraftablesFile.Parse"/>.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>The asset it was made from (a static mesh or a Blueprint, package path).</summary>
    public required string Source { get; init; }

    /// <summary>The static mesh it shows (package path; the Blueprint's mesh for a Blueprint source).</summary>
    public required string Mesh { get; init; }

    /// <summary>
    /// For a model imported from a 3D file (Level <c>UeCook</c>): its cooked files, relative to the project folder
    /// (<c>imports/&lt;Token&gt;</c>); the Craftables pak carries them. Null for the game's own meshes.
    /// </summary>
    public string? Imported { get; init; }

    /// <summary>What it becomes.</summary>
    public CraftKind Kind { get; init; }

    /// <summary>Main material (for the suggested recipe).</summary>
    public CraftMaterial Material { get; init; }

    /// <summary>
    /// The crafting menu section it shows in: a gameplay tag stock recipes of its recipe class use
    /// (<see cref="CraftCategories"/>; furniture and stations are building recipes, power and items crafting ones). Null
    /// shows it in its default section (<see cref="CraftCategories.DefaultFor"/>).
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// The picture its recipe shows in the crafting menu: a Texture2D the game ships (package path), or null for its own
    /// (<see cref="CraftablesPlanner.OwnIcon"/>: an item's inventory icon, a base element's recipe icon).
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Largest extent of the mesh in metres (for the suggested recipe).</summary>
    public float SizeMeters { get; init; } = 1;

    /// <summary>The recipe.</summary>
    public IReadOnlyList<CraftIngredient> Ingredients { get; init; } = [];

    /// <summary>Name of a <see cref="CraftKind.Station"/> craftable the player must stand next to, or null.</summary>
    public string? Station { get; init; }

    /// <summary>The base element settings (used when <see cref="Kind"/> is <see cref="CraftKind.Element"/>), or null.</summary>
    public ElementSettings? Element { get; init; }

    /// <summary>Where it goes once made, or null for its kind's own: items and power craft into the bag, the others build.</summary>
    public CraftUse? Use { get; init; }

    /// <summary>The station must have electricity (not a value of the game's data: kept, reported at export).</summary>
    public bool NeedsPower { get; init; }

    /// <summary>Power settings (used when <see cref="Kind"/> is <see cref="CraftKind.Power"/>).</summary>
    public PowerSettings Power { get; init; } = new();

    /// <summary>
    /// The folder its new packages go in, inside each template's folder (HexTer: "organize things neatly"): <c>Furniture</c>
    /// puts its recipe at <c>…/Recipes/Placeables/Furniture/CR_SS_Chair</c> and its element at
    /// <c>…/BaseElements/Furniture/BP_SS_Chair</c> (a station's ingredient tag stays in the game's tag folder). Null: beside
    /// the template, as every craftable before 3.3 (<see cref="CraftableNames.IsValidFolder"/>). Chosen until the first
    /// export, like <see cref="Token"/>.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>
    /// True once an export wrote it into a Craftables pak; false while it never was; null for one stored before the studio
    /// kept track (it may have been exported). Only a false one may change its <see cref="Token"/> and <see cref="Folder"/>:
    /// the game saves what players built by the asset path.
    /// </summary>
    public bool? Exported { get; init; }

    /// <summary>Its asset name and folder are fixed (<see cref="Exported"/> is not false).</summary>
    [JsonIgnore]
    public bool IsLocked => Exported != false;
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

    /// <summary>
    /// Recipes of the game whose amounts per skill level the user changed (an explicit override: the Craftables pak ships
    /// them at the game's own paths; another mod changing the same recipe conflicts).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<StockRecipeOverride> RecipeOverrides { get; init; } = [];

    // Stored only when there is one: a file without overrides stays as every earlier version wrote it.
    [JsonInclude]
    [JsonPropertyName(nameof(RecipeOverrides))]
    private IReadOnlyList<StockRecipeOverride>? StoredOverrides
    {
        get => RecipeOverrides.Count == 0 ? null : RecipeOverrides;
        init => RecipeOverrides = value ?? [];
    }

    /// <summary>Nothing to export: no craftable and no recipe override.</summary>
    [JsonIgnore]
    public bool IsEmpty => Items.Count == 0 && RecipeOverrides.Count == 0;

    /// <summary>The project folder it was loaded from (where <see cref="Craftable.Imported"/> folders are), or null.</summary>
    [JsonIgnore]
    public string? ProjectDirectory { get; init; }

    /// <summary><see cref="Parse"/> gave tokens to craftables stored without one: saving the file keeps them.</summary>
    [JsonIgnore]
    public bool TokensAdded { get; init; }

    /// <summary>The file of <paramref name="projectDirectory"/>, or an empty list when there is none.</summary>
    public static CraftablesFile Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        return (File.Exists(path) ? Parse(File.ReadAllText(path)) : new CraftablesFile()) with { ProjectDirectory = projectDirectory };
    }

    /// <summary>Reads the JSON text of the file; every craftable gets its token (<see cref="WithTokens"/>).</summary>
    public static CraftablesFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CraftablesFile>(json, Json) ?? new CraftablesFile();
        var items = WithTokens(file.Items);
        return ReferenceEquals(items, file.Items) ? file : file with { Items = items, TokensAdded = true };
    }

    /// <summary>The JSON text of the file.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Writes the file into <paramref name="projectDirectory"/>; an emptied list is kept too, so the next export still runs
    /// and removes the Craftables pak an earlier one left.
    /// </summary>
    public void Save(string projectDirectory) => File.WriteAllText(Path.Combine(projectDirectory, FileName), KeepExported(projectDirectory).ToJson());

    /// <summary>
    /// This file with every craftable the file in <paramref name="projectDirectory"/> marks exported marked too: an export
    /// marks the file on disk (<see cref="MarkExported"/>), and a page that read it before must not take the mark back.
    /// </summary>
    public CraftablesFile KeepExported(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        HashSet<string> exported;
        try
        {
            exported = File.Exists(path)
                ? Parse(File.ReadAllText(path)).Items.Where(i => i.Exported == true && i.Token is not null).Select(i => i.Token!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (JsonException)
        {
            return this; // a broken file is replaced as before
        }

        return Items.Any(i => i.Exported != true && i.Token is not null && exported.Contains(i.Token))
            ? this with { Items = Items.Select(i => i.Exported != true && i.Token is not null && exported.Contains(i.Token) ? i with { Exported = true } : i).ToList() }
            : this;
    }

    /// <summary>
    /// Marks the craftables of <paramref name="tokens"/> exported in the file of <paramref name="projectDirectory"/> (a
    /// Craftables pak now has them, so their asset names and folders stay); returns the tokens newly marked.
    /// </summary>
    public static IReadOnlyList<string> MarkExported(string projectDirectory, IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var wanted = tokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var file = Load(projectDirectory);
        var marked = file.Items.Where(i => i.Exported != true && i.Token is not null && wanted.Contains(i.Token)).Select(i => i.Token!).ToList();
        if (marked.Count > 0 || file.TokensAdded)
        {
            var set = marked.ToHashSet(StringComparer.OrdinalIgnoreCase);
            (file with { Items = file.Items.Select(i => i.Token is not null && set.Contains(i.Token) ? i with { Exported = true } : i).ToList() }).Save(projectDirectory);
        }

        return marked;
    }

    /// <summary>
    /// The token every export before 3.2 gave a craftable, computed from its name: <c>SS_</c> + the Latin letters and digits
    /// of <paramref name="name"/> (other runs as '_'); a name with no Latin letter or digit, or more than 61, gets at most 40
    /// of them and the first 8 hex digits of its SHA-256. Kept unchanged for files stored without a token.
    /// </summary>
    public static string LegacyTokenOf(string name)
    {
        var trimmed = name.Trim();
        var latin = Regex.Replace(trimmed, "[^A-Za-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');
        if (latin.Length is > 0 and <= 61)
        {
            return "SS_" + latin;
        }

        latin = latin[..Math.Min(latin.Length, 40)].TrimEnd('_');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8];
        return latin.Length > 0 ? $"SS_{latin}_{hash}" : "SS_" + hash;
    }

    /// <summary>
    /// The token of a new craftable named <paramref name="name"/>: <c>SS_</c> + its Latin letters and digits (spaces as '_').
    /// A name with other letters or punctuation, or one whose token <paramref name="taken"/> already has (in any case), gets
    /// <c>_h</c> + 8 hex digits of its SHA-256 too, so 'كرسي 01' and 'طاولة 01' differ. It never ends in '_' and a number,
    /// which the engine would read as an FName number: 'Shelf 1' gives <c>SS_Shelf_v1</c>.
    /// </summary>
    public static string NewTokenFor(string name, IEnumerable<string?> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var used = taken.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trimmed = name.Trim();
        var latin = Regex.Replace(trimmed, "[^A-Za-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');
        var plain = latin.Length is > 0 and <= 60 && !Regex.IsMatch(trimmed, @"[^A-Za-z0-9_\s]", RegexOptions.CultureInvariant) ? Unsplit("SS_" + latin) : null;
        if (plain is not null && !used.Contains(plain))
        {
            return plain;
        }

        var stem = latin[..Math.Min(latin.Length, 40)].TrimEnd('_');
        var hashed = "SS_" + (stem.Length > 0 ? stem + "_" : string.Empty) + "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8];
        var token = hashed;
        for (var n = 2; used.Contains(token); n++)
        {
            token = $"{hashed}_v{n}"; // the same name twice
        }

        return token;
    }

    /// <summary>
    /// True for a token the planner accepts: a letter, then letters, digits or '_', at most <see cref="CraftableNames.MaxToken"/>
    /// (every <c>SS_</c> token the studio gave; a stored one is never renamed, so one from before the FName number rule still is).
    /// </summary>
    public static bool IsValidToken(string? token) => token is not null && Regex.IsMatch(token, "^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="items"/> with every token set (the same list when none was missing). A missing token is the one every
    /// earlier export gave the craftable (<see cref="LegacyTokenOf"/>) unless a stored token or a craftable before it in the
    /// planner's build order has it: that one was left out of every export (the planner keeps the first), so it gets a new one.
    /// </summary>
    public static IReadOnlyList<Craftable> WithTokens(IReadOnlyList<Craftable> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.All(i => i.Token is not null))
        {
            return items;
        }

        var taken = items.Select(i => i.Token).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = new Dictionary<Craftable, string>(ReferenceEqualityComparer.Instance);
        foreach (var craftable in CraftablesPlanner.BuildOrder(items).Where(i => i.Token is null))
        {
            var legacy = LegacyTokenOf(craftable.Name);
            var token = taken.Contains(legacy) ? NewTokenFor(craftable.Name, taken) : legacy;
            taken.Add(token);
            tokens[craftable] = token;
        }

        return items.Select(i => i.Token is null ? i with { Token = tokens[i] } : i).ToList();
    }

    /// <summary><paramref name="token"/>, or with 'v' after its last '_' when it ends in '_' and a number (<c>SS_Chair_01_v2</c>).</summary>
    private static string Unsplit(string token) =>
        FNameRef.Split(token).Number == 0 ? token : token.Insert(token.LastIndexOf('_') + 1, "v");
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
            (CraftMaterial.Fabric, ["fabric", "cloth", "leather", "sofa", "couch", "mattress", "curtain", "rug", "carpet", "pillow", "bed_", "clothes"]),
            (CraftMaterial.Metal, ["metal", "steel", "iron", "rust", "alumin", "chrome", "pipe", "container", "generator", "solar", "pole", "pump", "locker", "weapon", "ammunition"]),
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
