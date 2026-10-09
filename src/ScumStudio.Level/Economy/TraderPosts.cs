using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Economy;

/// <summary>A kind of trade post that can be placed: one the game placed in a stock outpost, with what it is.</summary>
/// <param name="Type">Trader type (<c>Armorer</c>, <c>GeneralGoods</c> …) or <c>Bank</c>.</param>
/// <param name="ClassPath">The trade post's class.</param>
/// <param name="Source">The stock post that is copied (it stores its trader on the instance, so the copy carries it).</param>
/// <param name="Personality">Object path of that post's trader personality; null for a bank.</param>
/// <param name="NpcClass">The NPC standing there (for the stand-in), or empty.</param>
public sealed record TraderKind(string Type, string ClassPath, ActorRef Source, string? Personality, string NpcClass);

/// <summary>
/// A mechanic's lift that can be copied (<c>BP_CarLift</c>, <c>BP_BikeLift</c>): a lift is an actor with
/// <c>_assignedTradePost</c>, the mechanic's trade post whose menu repairs and upgrades the vehicle on it.
/// </summary>
/// <param name="Kind"><c>CarLift</c> or <c>BikeLift</c> (the class name without <c>BP_</c> and <c>_C</c>).</param>
/// <param name="ClassPath">Blueprint class object path.</param>
/// <param name="Source">The stock lift copied.</param>
public sealed record LiftKind(string Kind, string ClassPath, ActorRef Source);

/// <summary>
/// Facts about the game's trade posts (read from the stock outposts, verified on SCUM 1.x): a trade post is an actor
/// with <c>_outpost</c> (<c>TradeOutpostRef.OutpostName</c>, e.g. <c>Outpost_B_4</c>) and either <c>_traderMarkers</c>
/// (<c>TraderMarker</c>: <c>TraderPersonality</c>, <c>SedentaryNPCClass</c>, <c>SpawnTransform</c>, depot transforms) or,
/// for the bank, <c>_sedentaryNPCMarkers</c>. Its outpost's <c>BP_TradeOutpostManager</c> (<c>_outpostName</c>,
/// <c>_outpostDescription</c> → <c>TradingOutpostDescriptionDataAsset.TradeOutpostPersistentId</c>,
/// <c>_assignedTradePosts</c>) lists the outpost's traders of its own level; the banks and the DLC hunters (their grotto is
/// another level) are in no list and belong to their outpost by <c>_outpost.OutpostName</c> only. A trader's economy section
/// is its personality's <c>HumanReadableTraderName</c> (<c>A_0_Armory</c>); its <c>TraderPersistentId</c> keeps its state.
/// </summary>
public static partial class TraderPosts
{
    /// <summary>The outpost manager class.</summary>
    public const string ManagerClass = "/Game/ConZ_Files/Economy/BP_TradeOutpostManager.BP_TradeOutpostManager_C";

    /// <summary>Folder of the game's trader personalities (new ones go under it, so the game's scan finds them).</summary>
    public const string PersonalitiesFolder = "/Game/ConZ_Files/Economy/TraderPersonalities";

    /// <summary>Folder of the outposts' description data assets.</summary>
    public const string DescriptionsFolder = "/Game/ConZ_Files/Economy/OutpostDescriptions";

    /// <summary>The bank's "type" (it has no trader: a banker NPC and no economy section).</summary>
    public const string BankType = "Bank";

    /// <summary>
    /// True for one of the game's trader characters (<c>/Characters/NPCs/Vendors/…/BP_ArmsDealer_01</c>, the banker, the
    /// mechanic …): placing one means placing a whole trader of its type (trade post, NPC, economy section), never a bare
    /// character, which the game only spawns from a trade post.
    /// </summary>
    public static bool IsTraderNpcPackage(string package) =>
        package.Contains("/Characters/NPCs/Vendors/", StringComparison.OrdinalIgnoreCase)
        && !package.EndsWith("/BP_Master_Trader", StringComparison.OrdinalIgnoreCase)
        && !package.Contains("/BackgroundInteractions/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mechanic's trader type (the lifts serve its trade post).</summary>
    public const string MechanicType = "Mechanic";

    /// <summary>The lift kinds the Add object box offers before the outposts were read.</summary>
    public static readonly string[] KnownLiftKinds = ["CarLift", "BikeLift"];

    /// <summary>The mechanic's lifts that can be copied, one per kind, from the stock outposts (read levels).</summary>
    public static IReadOnlyList<LiftKind> FindLifts(IEnumerable<LevelDocument> outposts)
    {
        ArgumentNullException.ThrowIfNull(outposts);
        var kinds = new List<LiftKind>();
        foreach (var level in outposts)
        {
            foreach (var actor in level.Actors.Where(a => a.PropertyNames.Contains("_assignedTradePost")))
            {
                var kind = LiftKindOf(actor.ClassPath);
                if (!kinds.Any(k => k.Kind == kind))
                {
                    kinds.Add(new LiftKind(kind, actor.ClassPath, new ActorRef(level.PackagePath, actor.Name)));
                }
            }
        }

        return kinds;
    }

    /// <summary><c>/Game/.../BP_CarLift.BP_CarLift_C</c> → <c>CarLift</c>.</summary>
    public static string LiftKindOf(string classPath)
    {
        ArgumentNullException.ThrowIfNull(classPath);
        var name = classPath[(classPath.LastIndexOf('.') + 1)..];
        if (name.EndsWith("_C", StringComparison.Ordinal))
        {
            name = name[..^2];
        }

        return name.StartsWith("BP_", StringComparison.Ordinal) ? name[3..] : name;
    }

    /// <summary>The suffix the game's trader names use per type (<c>A_0_Armory</c>, <c>A_0_Saloon</c>, <c>A_0_Master_Hunter</c>).</summary>
    public static string Suffix(string type) => type switch
    {
        "Armorer" => "Armory",
        "GeneralGoods" => "Trader",
        "Doctor" => "Hospital",
        "Bartender" => "Saloon",
        "Harbourmaster" => "BoatShop",
        "MasterHunter" => "Master_Hunter",
        _ => type,
    };

    /// <summary>
    /// A new trader's default name (owner: "numbered per sector and type"): <c>&lt;cell&gt;_&lt;Suffix&gt;_&lt;n&gt;</c>, where n
    /// counts the traders of <paramref name="type"/> in <paramref name="cell"/> that <paramref name="existing"/> lists (the
    /// game's own and the placed ones) plus one, then skips names that are taken. B_4 has the game's armory, so a placed one
    /// is <c>B_4_Armory_2</c>; the first in a cell without one is <c>Z_3_Armory_1</c>.
    /// </summary>
    /// <param name="cell">Map cell (<c>B_4</c>).</param>
    /// <param name="type">Trader type (<c>Armorer</c>).</param>
    /// <param name="existing">Every trader there is: the game's (<see cref="EconomyDefaults.Traders"/>) and the project's.</param>
    public static string DefaultName(string cell, string type, IEnumerable<(string Name, string Type)> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cell);
        ArgumentNullException.ThrowIfNull(existing);
        var known = existing.ToList();
        var taken = known.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = known.Count(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(CellOf(t.Name), cell, StringComparison.OrdinalIgnoreCase)) + 1;
        var name = $"{cell}_{Suffix(type)}_{n}";
        while (taken.Contains(name))
        {
            name = $"{cell}_{Suffix(type)}_{++n}";
        }

        return name;
    }

    /// <summary>The map cell a name starts with (<c>B_4_Armory</c>, <c>/Game/.../A_3_Farm_01</c> → <c>B_4</c>, <c>A_3</c>), or null.</summary>
    public static string? CellOf(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var leaf = name[(name.LastIndexOf('/') + 1)..];
        return CellPrefix().Match(leaf) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>True for a stock outpost's level (<c>B_4_Outpost</c>, <c>A_0_Outpost_HuntersGrotto</c>): where the game's trade posts are.</summary>
    public static bool IsOutpostLevel(string levelPath) =>
        OutpostLevel().IsMatch(levelPath[(levelPath.LastIndexOf('/') + 1)..]);

    /// <summary>
    /// The game's traders the project took off the island: every trade post that carries them (in <paramref name="outposts"/>,
    /// the read outpost levels with deletions) is deleted and no copy of one is left. Their economy sections are left out of
    /// the exported file (and come back when the delete is undone). A bank's NPC has no personality and no section.
    /// </summary>
    public static IReadOnlySet<string> RemovedStockTraders(EditState state, IEnumerable<LevelDocument> outposts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outposts);
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var levels = outposts.ToList();
        foreach (var level in levels)
        {
            foreach (var actor in level.Actors)
            {
                foreach (var marker in actor.TraderMarkers.Where(m => m.Name.Length > 0 && m.PersonalityPath.Length > 0))
                {
                    (state.IsDeleted(new ActorRef(level.PackagePath, actor.Name)) ? gone : kept).Add(marker.Name);
                }
            }
        }

        // A copy of a stock post (Duplicate) carries its trader along: the trader is still on the island.
        foreach (var (copy, op) in state.AddedActors)
        {
            if (op is DuplicateActorOp duplicate && !state.IsDeleted(copy)
                && levels.FirstOrDefault(l => string.Equals(l.PackagePath, duplicate.Source.Level, StringComparison.OrdinalIgnoreCase))?.FindActor(duplicate.Source.Actor) is { } source)
            {
                kept.UnionWith(source.TraderMarkers.Select(m => m.Name));
            }
        }

        gone.ExceptWith(kept);
        return gone;
    }

    /// <summary>A cell's outpost name (<c>A_0</c> → <c>Outpost_A_0</c>, the stock outposts' own naming).</summary>
    public static string DefaultOutpost(string cell) => $"Outpost_{cell}";

    /// <summary>Package of the description of <paramref name="outpost"/> (<c>Outpost_A_0</c> → <c>…/A_0_TradeOutpostDescription</c>).</summary>
    public static string DescriptionPackage(string outpost) =>
        $"{DescriptionsFolder}/{(outpost.StartsWith("Outpost_", StringComparison.OrdinalIgnoreCase) ? outpost[8..] : outpost)}_TradeOutpostDescription";

    /// <summary>
    /// Package of a new trader's personality (<c>…/TraderPersonalities/ScumStudio/A_3_Armory_Personality</c>). No path
    /// segment ends in <c>_&lt;digits&gt;</c>: such a name is a numbered FName, which the asset registry would not match.
    /// </summary>
    public static string PersonalityPackage(string name) => $"{PersonalitiesFolder}/ScumStudio/{name}_Personality";

    /// <summary>The object path of a package's main asset (<c>/Game/X/Y</c> → <c>/Game/X/Y.Y</c>).</summary>
    public static string ObjectPath(string package) => package + "." + package[(package.LastIndexOf('/') + 1)..];

    /// <summary>True for one of the game's outposts (its description is a stock asset).</summary>
    public static bool IsStockOutpost(AssetCatalog catalog, string outpost)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.PackageExists(DescriptionPackage(outpost));
    }

    /// <summary>
    /// A stable persistent id for a new trader or outpost (the game keeps a trader's funds and stock by it): the same name
    /// gives the same id on every export, so a re-exported mod keeps its traders' state.
    /// </summary>
    public static byte[] PersistentId(string kind, string name) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"ScumStudio.{kind}:{name.ToUpperInvariant()}"));

    /// <summary>True for a usable trader or outpost name: letters, digits and <c>_</c> (an FName and a JSON key).</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 64 } && NamePattern().IsMatch(name);

    /// <summary>The placed traders of a project (live ones: added and not deleted again), with their actors.</summary>
    public static IReadOnlyList<(ActorRef Actor, AddBlueprintActorOp Op)> Placed(EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.AddedActors
            .Where(a => a.Value is AddBlueprintActorOp { Trader: not null } && !state.IsDeleted(a.Key))
            .Select(a => (a.Key, (AddBlueprintActorOp)a.Value))
            .OrderBy(a => a.Key.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key.Actor, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The trade posts that can be copied, one per type: from the stock outposts (<paramref name="outposts"/>, read
    /// levels), the first post of each type that stores its trader (or the bank's NPC) on the instance, so a copy carries it.
    /// </summary>
    public static IReadOnlyList<TraderKind> FindKinds(IEnumerable<LevelDocument> outposts)
    {
        ArgumentNullException.ThrowIfNull(outposts);
        var kinds = new List<TraderKind>();
        foreach (var level in outposts)
        {
            foreach (var actor in level.Actors.Where(a => a.PropertyNames.Contains("_outpost")))
            {
                TraderKind? kind = null;
                if (actor.TraderMarkers.Count > 0 && actor.PropertyNames.Contains("_traderMarkers") && actor.TraderMarkers[0] is { Type.Length: > 0, PersonalityPath.Length: > 0 } marker)
                {
                    kind = new TraderKind(marker.Type, actor.ClassPath, new ActorRef(level.PackagePath, actor.Name), marker.PersonalityPath, marker.NpcClass);
                }
                else if (!actor.PropertyNames.Contains("_traderMarkers") && actor.PropertyNames.Contains("_sedentaryNPCMarkers")) // the bank: its NPC marker has no personality
                {
                    kind = new TraderKind(BankType, actor.ClassPath, new ActorRef(level.PackagePath, actor.Name), null, actor.TraderMarkers.FirstOrDefault()?.NpcClass ?? string.Empty);
                }

                if (kind is not null && !kinds.Any(k => k.Type == kind.Type))
                {
                    kinds.Add(kind);
                }
            }
        }

        return kinds;
    }

    /// <summary>The stock outposts' levels in a world (<c>B_4_Outpost</c> first: its posts store their traders), hunters' grottos after.</summary>
    public static IReadOnlyList<string> OutpostLevels(IEnumerable<string> levelPaths) =>
        levelPaths
            .Where(p => OutpostLevel().IsMatch(p[(p.LastIndexOf('/') + 1)..]))
            .OrderBy(p => p.Contains("_HuntersGrotto", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Contains("/A_0_", StringComparison.OrdinalIgnoreCase)) // A_0's posts use their classes' traders
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Z]_[0-9]_Outpost(_HuntersGrotto)?$")]
    private static partial Regex OutpostLevel();

    [GeneratedRegex("^([A-Z]_[0-9])(_|$)")]
    private static partial Regex CellPrefix();
}
