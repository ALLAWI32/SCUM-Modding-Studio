using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Modding;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>
/// How the placed traders of a project are exported (facts in <see cref="TraderPosts"/>): each trade post copy gets its
/// outpost name and a personality of its own (a renamed copy of its source's, see <see cref="DataAssetCopy"/>); a trader of
/// a stock outpost joins that outpost (listed by the outpost's manager when it is in the same level); a new outpost gets a
/// manager in the level of its first trader (a copy of a stock manager with its own description and persistent id) that
/// lists the outpost's traders of that level.
/// </summary>
/// <param name="Managers">New outposts → the manager made for them.</param>
/// <param name="DataAssets">Personalities and descriptions to create.</param>
public sealed record TraderExportPlan(IReadOnlyDictionary<string, OutpostManagerPlan> Managers, IReadOnlyList<DataAssetCopy> DataAssets)
{
    /// <summary>No traders placed.</summary>
    public static TraderExportPlan Empty { get; } = new(new Dictionary<string, OutpostManagerPlan>(), []);
}

/// <summary>A new outpost's manager: where it goes, what it is copied from and the traders it lists.</summary>
/// <param name="Outpost">Outpost name.</param>
/// <param name="Level">The level it is created in (its first trader's).</param>
/// <param name="Source">The stock manager copied.</param>
/// <param name="SourceRoot">That manager's root component name (for the transform), or null.</param>
/// <param name="Description">Object path of the stock manager's outpost description (copied with a new id), or null.</param>
/// <param name="Posts">The trade posts of <paramref name="Level"/> it lists.</param>
/// <param name="Transform">Where it stands (at its first trader).</param>
public sealed record OutpostManagerPlan(string Outpost, string Level, ActorRef Source, string? SourceRoot, string? Description, IReadOnlyList<string> Posts, TransformValue Transform)
{
    /// <summary>The manager's actor name.</summary>
    public string ActorName => "BP_TradeOutpostManager_" + Outpost;
}

public sealed partial class ProjectExporter
{
    /// <summary>The project's placed traders: new managers for new outposts, and the data assets to create.</summary>
    public static TraderExportPlan PlanTraders(
        EditState state, AssetCatalog catalog, Func<string, CookedPackage?> packages, Func<string, LevelDocument?> documents, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var placed = TraderPosts.Placed(state);
        if (placed.Count == 0)
        {
            return TraderExportPlan.Empty;
        }

        var assets = new List<DataAssetCopy>();
        foreach (var (_, op) in placed)
        {
            var trader = op.Trader!;
            if (trader.Name.Length > 0 && trader.Personality is { } personality)
            {
                assets.Add(new DataAssetCopy(personality, TraderPosts.PersonalityPackage(trader.Name), TraderPosts.PersistentId("Trader", trader.Name))
                {
                    TraderName = trader.Name,
                });
            }
        }

        var managers = new Dictionary<string, OutpostManagerPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var outpost in placed.GroupBy(p => p.Op.Trader!.Outpost, StringComparer.OrdinalIgnoreCase))
        {
            if (TraderPosts.IsStockOutpost(catalog, outpost.Key))
            {
                continue;
            }

            // The first trader's level holds the new outpost's manager; a stock manager is copied (its source outpost's).
            var home = outpost.First();
            var candidates = outpost.Select(p => p.Op.Source.Level)
                .Concat(TraderPosts.OutpostLevels(catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            OutpostManagerPlan? plan = null;
            foreach (var level in candidates)
            {
                if (packages(level) is { } package && FindManager(package) is { } found)
                {
                    var root = documents(level)?.FindActor(found.Name)?.Root is { IsSynthesized: false } r ? r.Name : null;
                    var posts = outpost.Where(p => p.Op.Trader!.Name.Length > 0 && string.Equals(p.Actor.Level, home.Actor.Level, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Actor.Actor).ToList();
                    var at = state.GetAddedTransform(home.Actor) ?? home.Op.Transform;
                    plan = new OutpostManagerPlan(outpost.Key, home.Actor.Level, new ActorRef(level, found.Name), root, found.Description, posts, at with { Scale = Core.Mathematics.FVector.One });
                    break;
                }
            }

            if (plan is null)
            {
                warnings.Add($"{outpost.Key}: no stock outpost manager to copy was found; its traders are linked by outpost name only.");
                continue;
            }

            managers[outpost.Key] = plan;
            if (plan.Description is { } description)
            {
                assets.Add(new DataAssetCopy(description, TraderPosts.DescriptionPackage(outpost.Key), TraderPosts.PersistentId("Outpost", outpost.Key)));
            }
        }

        return new TraderExportPlan(managers, assets);
    }

    /// <summary>A level's outpost manager: its actor name and its description's object path.</summary>
    private static (string Name, string? Description)? FindManager(CookedPackage package)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i).Contains("TradeOutpostManager", StringComparison.Ordinal))
            {
                continue;
            }

            string? description = null;
            if (package.ReadProperties(i).Find("_outpostDescription")?.Value is ObjectValue { Index: < 0 } d)
            {
                var import = package.Imports[-d.Index - 1];
                description = import.OuterIndex < 0
                    ? package.ResolveName(package.Imports[-import.OuterIndex - 1].ObjectName) + "." + package.ResolveName(import.ObjectName)
                    : null;
            }

            return (package.ResolveName(package.Exports[i].ObjectName), description);
        }

        return null;
    }

    /// <summary>The trade copy of a placed trader: its outpost, and its personality pointed at its own copy.</summary>
    private static TradeCopy TradeCopyOf(TraderPost trader) => new(trader.Outpost)
    {
        Renames = trader.Name.Length > 0 && trader.Personality is { } personality
            ? [(personality, TraderPosts.ObjectPath(TraderPosts.PersonalityPackage(trader.Name)))]
            : [],
    };

    /// <summary>
    /// The new outposts' managers that go into <paramref name="level"/>, after its trade posts (they list them by name).
    /// </summary>
    private static void AddManagers(
        TraderExportPlan traders, string level, Func<string, CookedPackage?> sourcePackages, LevelDocument? document,
        List<ForeignActorCopy> foreignCopies, List<string> warnings)
    {
        foreach (var plan in traders.Managers.Values.Where(m => string.Equals(m.Level, level, StringComparison.OrdinalIgnoreCase)))
        {
            if (sourcePackages(plan.Source.Level) is not { } package)
            {
                warnings.Add($"{plan.Outpost}: the outpost manager's source level {plan.Source.Level} could not be read; its traders are linked by outpost name only.");
                continue;
            }

            if (document?.FindActor(plan.ActorName) is not null)
            {
                warnings.Add($"{plan.Outpost}: the level already has an actor named {plan.ActorName}; no manager was added.");
                continue;
            }

            foreignCopies.Add(new ForeignActorCopy(package, plan.Source.Actor, plan.ActorName, plan.Transform, plan.SourceRoot)
            {
                Trade = new TradeCopy(plan.Outpost)
                {
                    Renames = plan.Description is { } d ? [(d, TraderPosts.ObjectPath(TraderPosts.DescriptionPackage(plan.Outpost)))] : [],
                    Posts = plan.Posts,
                },
            });
        }
    }

    /// <summary>
    /// The economy written next to a pak: the project's <c>EconomyOverride.json</c> (or the game's defaults) with a section
    /// for each placed trader (an empty one sells the game's stock of its type) and without the sections of the game's
    /// traders the project deleted (<paramref name="removedStock"/>, see <see cref="TraderPosts.RemovedStockTraders"/>);
    /// null when the project has neither an economy nor a placed trader.
    /// </summary>
    public static EconomyOverride? EconomyFor(EditState state, EconomyOverride? economy, IEnumerable<string>? removedStock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var traders = TraderPosts.Placed(state).Select(p => p.Op.Trader!).Where(t => t.Name.Length > 0).ToList();
        if (economy is null && traders.Count == 0)
        {
            return null;
        }

        var result = economy?.Clone() ?? EconomyOverride.CreateDefault();
        foreach (var name in removedStock ?? [])
        {
            if (!traders.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                result.RemoveSection(name);
            }
        }

        foreach (var trader in traders)
        {
            result.EnsureSection(trader.Name);
        }

        return result;
    }

    /// <summary>One report line per trader placed in <paramref name="level"/>: who, which outpost, and how the outpost knows it.</summary>
    private static IEnumerable<string> DescribeTraders(EditState state, string level, TraderExportPlan traders, LevelEditReport report)
    {
        foreach (var (actor, op) in TraderPosts.Placed(state).Where(p => SameLevel(p.Actor.Level, level)))
        {
            if (!report.AddedActors.Contains(actor.Actor, StringComparer.OrdinalIgnoreCase))
            {
                continue; // not created (see the warnings)
            }

            var trader = op.Trader!;
            var who = trader.Name.Length > 0 ? $"{trader.Name} ({trader.Type})" : trader.Type;
            var joined = report.JoinedTradePosts.FirstOrDefault(j => string.Equals(j.Post, actor.Actor, StringComparison.OrdinalIgnoreCase));
            var how = trader.Name.Length == 0
                ? "linked by outpost name (a bank is in no manager's list, as the game's are)"
                : joined.Manager is not null
                ? $"listed in _assignedTradePosts of the outpost's manager {joined.Manager}"
                : traders.Managers.TryGetValue(trader.Outpost, out var plan) && SameLevel(plan.Level, level) && report.AddedActors.Contains(plan.ActorName, StringComparer.OrdinalIgnoreCase)
                    ? $"listed in _assignedTradePosts of the new outpost manager {plan.ActorName} (description {TraderPosts.DescriptionPackage(trader.Outpost)})"
                    : "linked by outpost name only (its manager is in another level, as the game's banks and hunters are)";
            var personality = trader.Name.Length > 0 && trader.Personality is not null ? $"; personality {TraderPosts.PersonalityPackage(trader.Name)}" : string.Empty;
            yield return $"{who}: {actor.Actor} in {level}, outpost {trader.Outpost}, {how}{personality}";
        }
    }

    /// <summary>The report's traders and economy sections (what the game needs, client and server).</summary>
    private static void AppendTraderReport(StringBuilder sb, ExportResult result)
    {
        if (result.Traders.Count == 0 && result.EconomyPath is null)
        {
            return;
        }

        if (result.Traders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Traders");
            sb.AppendLine();
            foreach (var line in result.Traders)
            {
                sb.Append("- ").AppendLine(line);
            }

            sb.AppendLine();
            sb.AppendLine("The game needs, on the server and on every client: this pak (the levels with the trade posts, the traders' " +
                "personalities under " + TraderPosts.PersonalitiesFolder + ", new outposts' descriptions under " + TraderPosts.DescriptionsFolder +
                " and the AssetRegistry.bin that lists the personalities). A dedicated server loads the Server export's pak from its Paks " +
                "folder (start it with -fileopenlog, as for every mod pak). A trader is a server object: without the server pak nobody sees it.");
        }

        if (result.EconomyPath is { } economy)
        {
            sb.AppendLine();
            sb.AppendLine("## Economy");
            sb.AppendLine();
            sb.Append("- File: ").AppendLine(economy);
            if (result.RemovedTraders.Count > 0)
            {
                sb.Append("- Left out (their trade posts are deleted): ").AppendLine(string.Join(", ", result.RemovedTraders));
            }

            sb.AppendLine("- Dedicated server: copy it to <server>\\SCUM\\Saved\\Config\\WindowsServer\\EconomyOverride.json (the server reads it when it starts; a section per trader, named as in the trade menu's economy: A_0_Armory, your placed traders by their own names).");
            sb.AppendLine("- Single player / local game: %LOCALAPPDATA%\\SCUM\\Saved\\Config\\WindowsNoEditor\\EconomyOverride.json.");
        }
    }
}
