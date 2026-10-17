using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>Why an asset name or a folder a user typed cannot be a craftable's (<see cref="CraftableNames"/>).</summary>
public enum CraftNameProblem
{
    /// <summary>It can be used.</summary>
    None,

    /// <summary>Not a letter first, then letters A-Z, digits and '_' (a folder: such names separated by '/').</summary>
    Characters,

    /// <summary>Longer than <see cref="CraftableNames.MaxToken"/> (a folder: more than 3 levels or 32 characters a level).</summary>
    Length,

    /// <summary>Ends in '_' and a number, which the engine reads as another name with a number (FName rule).</summary>
    Number,

    /// <summary>Another craftable of the project has it (or one of its packages would).</summary>
    Craftable,

    /// <summary>The game (or a mod the studio reads) has a package of one of its names.</summary>
    Game,
}

/// <summary>
/// The asset name (<see cref="Craftable.Token"/>) and folder (<see cref="Craftable.Folder"/>) a user chooses for a craftable
/// before its first export (HexTer, 2026-10-12: "customize the names of crafting recipes and furniture items ... before
/// exporting the mod"). Its packages are <c>CR_</c>/<c>BP_</c> + the name (a station or a chest's item the name and its
/// <c>_ES</c>, an element's upgrades <c>CR_</c>name<c>_Up</c>n and <c>BP_</c>name<c>_L</c>n). The game's asset manager
/// tells primary assets apart by that last name only, whatever their folder, so none may exist anywhere in the game.
/// </summary>
public static class CraftableNames
{
    /// <summary>Longest asset name.</summary>
    public const int MaxToken = 64;

    private const int MaxFolderLevels = 3;
    private const int MaxFolderSegment = 32;
    private const int MaxUpgrades = 8;

    private static readonly ConditionalWeakTable<AssetCatalog, Lazy<IReadOnlyDictionary<string, string>>> Leaves = new();

    /// <summary>Folders the page suggests (any other of valid names can be typed).</summary>
    public static IReadOnlyList<string> SuggestedFolders { get; } =
        ["Furniture", "Lights", "Storage", "Beds", "Kitchen", "Decoration", "Stations", "Power", "Weapons", "Ammunition", "Clothes", "Food", "Medical", "Tools", "Items"];

    /// <summary>
    /// <paramref name="folder"/> as stored: trimmed, '\' as '/', no '/' at either end or twice in a row; null when empty.
    /// </summary>
    public static string? NormalizeFolder(string? folder)
    {
        if (folder is null)
        {
            return null;
        }

        var parts = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join('/', parts);
    }

    /// <summary>True for a folder the planner writes: null, or 1 to 3 levels of a letter then letters, digits and '_'.</summary>
    public static bool IsValidFolder(string? folder) => CheckFolder(folder) == CraftNameProblem.None;

    /// <summary>Why <paramref name="folder"/> (normalized) cannot be used, or <see cref="CraftNameProblem.None"/>.</summary>
    public static CraftNameProblem CheckFolder(string? folder)
    {
        if (folder is null)
        {
            return CraftNameProblem.None;
        }

        var parts = folder.Split('/');
        if (parts.Length > MaxFolderLevels || parts.Any(p => p.Length > MaxFolderSegment))
        {
            return CraftNameProblem.Length;
        }

        if (parts.Any(p => !Regex.IsMatch(p, "^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)))
        {
            return CraftNameProblem.Characters;
        }

        // A folder is a part of the package path the registry stores as one name: 'Lights_2' would be read as 'Lights' #2.
        return parts.Any(p => FNameRef.Split(p).Number != 0) ? CraftNameProblem.Number : CraftNameProblem.None;
    }

    /// <summary>
    /// Why <paramref name="token"/> cannot be a new asset name, or <see cref="CraftNameProblem.None"/>; the detail names the
    /// craftable or the game package that has it, or the name to write instead of one ending in a number.
    /// <paramref name="gameLeaves"/> (<see cref="GameLeaves"/>) may be null while it is read: the export checks again.
    /// </summary>
    public static (CraftNameProblem Problem, string? Detail) Check(string token, IEnumerable<Craftable> others, IReadOnlyDictionary<string, string>? gameLeaves)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(others);
        if (token.Length > MaxToken)
        {
            return (CraftNameProblem.Length, MaxToken.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!CraftablesFile.IsValidToken(token))
        {
            return (CraftNameProblem.Characters, null);
        }

        if (FNameRef.Split(token).Number != 0)
        {
            return (CraftNameProblem.Number, token.Insert(token.LastIndexOf('_') + 1, "v"));
        }

        var mine = LeavesOf(token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        {
            if (other.Token is { } theirs && LeavesOf(theirs).Any(mine.Contains))
            {
                return (CraftNameProblem.Craftable, other.Name);
            }
        }

        if (gameLeaves is not null)
        {
            foreach (var leaf in mine)
            {
                if (gameLeaves.TryGetValue(leaf, out var path))
                {
                    return (CraftNameProblem.Game, path);
                }
            }
        }

        return (CraftNameProblem.None, null);
    }

    /// <summary>Every package name a craftable of asset name <paramref name="token"/> may get (any kind, up to 8 upgrades).</summary>
    public static IReadOnlyList<string> LeavesOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        List<string> leaves = ["CR_" + token, "BP_" + token, token, token + "_ES", "CI_" + token, "CR_" + token + "_Item", "ICO_" + token]; // ICO_: its picture (CraftPictures.PackageFor)
        for (var n = 1; n <= MaxUpgrades; n++)
        {
            leaves.Add($"CR_{token}_Up{n}");
            leaves.Add($"BP_{token}_L{n}");
        }

        return leaves;
    }

    /// <summary>
    /// Every package name of <paramref name="catalog"/> (case-insensitive) with one package path that has it: read once per
    /// catalog from its package list (a worker; the first call builds the index when there is none).
    /// </summary>
    public static IReadOnlyDictionary<string, string> GameLeaves(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Leaves.GetValue(catalog, c => new Lazy<IReadOnlyDictionary<string, string>>(() =>
        {
            var leaves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (c.Index ?? c.BuildIndex(resolveClasses: false)).Entries)
            {
                leaves.TryAdd(PackageMap.Leaf(entry.PackagePath), entry.PackagePath);
            }

            return leaves;
        })).Value;
    }

    /// <summary>
    /// The folder a new craftable gets: by what it is (furniture, lights, storage, beds, a station), or null for one made by an
    /// item recipe (an item, power): the game keeps its own sub-folders of placeable recipes (<c>Placeables/Skins</c>,
    /// <c>Placeables/AsianDecorDLC/Upgrade</c>) but none of item recipes, so those stay beside the game's unless the user
    /// chooses one. The user can change it until the first export.
    /// </summary>
    public static string? SuggestFolder(Craftable craftable)
    {
        ArgumentNullException.ThrowIfNull(craftable);
        var path = craftable.Source;
        bool Has(params string[] words) => words.Any(w => path.Contains(w, StringComparison.OrdinalIgnoreCase));
        return craftable.Kind switch
        {
            CraftKind.Station => "Stations",
            CraftKind.Power or CraftKind.Item => null,
            _ when craftable.Element?.Storage is not null || Has("Chest", "Cabinet", "Wardrobe", "Locker", "Shelf", "Fridge", "Refrigerator", "Rack") => "Storage",
            _ when Has("Lamp", "Light", "Chandelier", "Torch", "Candle") => "Lights",
            _ when Has("Bed", "Shelter", "Mattress") => "Beds",
            _ when Has("Kitchen", "Oven", "Stove") => "Kitchen",
            _ => "Furniture",
        };
    }
}
