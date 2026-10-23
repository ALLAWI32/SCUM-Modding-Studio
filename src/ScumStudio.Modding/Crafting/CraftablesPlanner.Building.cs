using System.Globalization;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// How a built craftable is placed, whether it needs a flag and what a repair costs (owner, 2026-10-17, his "Base Cabin 03"):
/// values of its base element (<c>UBaseBuildingComponent</c>, SDK build 149664) written into each element the craftable
/// clones. Where the placement ghost appears (its distance from the player, Shift + mouse wheel) is the game's code: no
/// element value moves it, and no game element stores a mesh offset (<c>RelativeLocation</c>: 0 of 333).
/// <para>
/// "Place in front" (experimental) is a value of the recipe instead: an element is one <c>UStaticMeshComponent</c> (its
/// Blueprint has no component of its own; snap markers, ladder, destruction zones are arrays of its class default), and the
/// ghost is another actor (<c>BP_PlaceableActorBaseBuilding</c>: its own root, box and mesh components) whose
/// <c>Server_Place(Location, Rotation)</c> the element is built at. So no element offset can move the ghost with it, and
/// <c>PlacementMeshOffset</c> (47 recipes, 1-5 cm on upgrades) moves only the ghost's picture. <c>PlacementLocationOffset</c>
/// moves the placement point itself; 2 recipes of the game store it (the Woodland pack's curtains, same ghost actor). The
/// owner's test (3.5.0): the game adds it in world directions after finding the ground, so the ghost floated and did not
/// turn with him. What makes a big building placeable is the game's own large-building recipe
/// (<see cref="LargeBuildingRecipe"/>) with its placement check drawn in (<see cref="AddPlacementShrink"/>), so "Place in
/// front" is left out wherever that recipe is used.
/// </para>
/// </summary>
public static partial class CraftablesPlanner
{
    /// <summary>
    /// From this size (metres, the largest extent) a furniture or station craftable is placed like the game's large buildings
    /// (its cabins, walls and frames): on the highest ground point under it, upright, turned only around its vertical axis.
    /// The chair template stores no height rule (the C++ default, most likely LowestPointHeight = 0; no file says), so a big
    /// mesh sinks into a slope, and it may tilt.
    /// </summary>
    public const float LargeBuildingMeters = 3;

    /// <summary>The value the game's large buildings store for <c>_placementHeightType</c> (65 elements, none stores another).</summary>
    public const string HighestPointHeight = "EConZPlacementHeightType::HighestPointHeight";

    /// <summary>What the automatic "Place in front" distance adds to half the craftable's size (metres).</summary>
    public const float InFrontMarginMeters = 1;

    /// <summary>The largest automatic "Place in front" distance (metres).</summary>
    public const float InFrontCapMeters = 15;

    /// <summary>The largest "Place in front" distance the metres box takes.</summary>
    public const float InFrontMaxMeters = 30;

    /// <summary>What a big building's placement check keeps around its pivot, cm: the player stands 2-3 m from the point he aims at.</summary>
    public const float PlacementCheckKeepCm = 150;

    private const string WithoutFlag = "_canBePlacedWithoutFlag";
    private const string HealthPerRepair = "_repairableParams.HealthRepairedPerUse";
    private const string PlacementOffset = "PlacementLocationOffset";
    private const string CollisionScale = "CollisionMeshScale";
    private const string BoundsInsetMin = "BoundsExtensionWhilePlacingMin";
    private const string BoundsInsetMax = "BoundsExtensionWhilePlacingMax";

    /// <summary>
    /// The automatic "Place in front" distance of a craftable of <paramref name="sizeMeters"/> (its largest extent): 0 below
    /// <see cref="LargeBuildingMeters"/> (a table or a chair stays where the game puts it), else half its size plus
    /// <see cref="InFrontMarginMeters"/>, rounded to 0.1 m, at most <see cref="InFrontCapMeters"/>: the 15 m cabin 8.5 m.
    /// </summary>
    public static float AutoInFrontMeters(float sizeMeters) =>
        !float.IsFinite(sizeMeters) || sizeMeters < LargeBuildingMeters ? 0 : MathF.Min(MathF.Round(sizeMeters / 2 + InFrontMarginMeters, 1), InFrontCapMeters);

    /// <summary>How far <paramref name="craftable"/> is placed in front, in metres (0: the game's placement).</summary>
    public static float InFrontMeters(Craftable craftable)
    {
        ArgumentNullException.ThrowIfNull(craftable);
        return craftable.InFront switch
        {
            null => 0,
            { Meters: { } meters } => float.IsFinite(meters) ? Math.Clamp(meters, 0, InFrontMaxMeters) : 0,
            _ => AutoInFrontMeters(craftable.SizeMeters),
        };
    }

    /// <summary>The <c>PlacementLocationOffset</c> (cm, no height) of <paramref name="meters"/> along <paramref name="axis"/>.</summary>
    public static (float X, float Y) InFrontOffset(InFrontAxis axis, float meters) => axis switch
    {
        InFrontAxis.MinusX => (-meters * 100, 0),
        InFrontAxis.PlusY => (0, meters * 100),
        InFrontAxis.MinusY => (0, -meters * 100),
        _ => (meters * 100, 0),
    };

    /// <summary>
    /// The base elements a built craftable clones, each as (stock element, its clone): every level of an element family,
    /// the chair's or the workbench's clone for furniture or a station; none for an item or power.
    /// </summary>
    internal static IReadOnlyList<(string Stock, string Clone)> BuiltElements(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages)
    {
        var map = plan.ToMap();
        return craftable.Kind switch
        {
            CraftKind.Element => ElementFamilies.Of(catalog, ElementSource(craftable)) is { } family
                ? family.Levels.Select(l => (l.Element, map.Map(l.Element)!)).Where(p => p.Item2 is not null).ToList()
                : [],
            CraftKind.Furniture or CraftKind.Station => [(plan.Packages.First(p => p.Value == packages.Product).Key, packages.Product)],
            _ => [],
        };
    }

    /// <summary>
    /// The flag, repair and (for a large furniture or station) placement values of the craftable's elements; only a value that
    /// differs from what the element has is written, and a value the data cannot hold is a warning.
    /// </summary>
    private static void AddBuildingEdits(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, Dictionary<string, IReadOnlyList<TunableEdit>> edits, List<string> warnings,
        string? projectDirectory = null)
    {
        var large = craftable.Kind is CraftKind.Furniture or CraftKind.Station && craftable.SizeMeters >= LargeBuildingMeters;
        var percent = craftable.RepairCostPercent;
        var shed = UsesLargeBuildingRecipe(catalog, craftable);
        var building = shed || DrawsPlacementIn(catalog, craftable); // a big station keeps the workbench's recipe, drawn in too
        if (building)
        {
            AddPlacementShrink(catalog, craftable, plan, packages, edits, warnings, projectDirectory);
        }

        if (craftable.Kind is CraftKind.Furniture or CraftKind.Station or CraftKind.Element && InFrontMeters(craftable) is > 0 and var ahead)
        {
            // Placed as a building with its check drawn in, the player may stand inside the ghost: no offset is needed, and
            // the game adds one in world directions after finding the ground (3.5.0: the ghost floated and did not turn with him).
            if (building)
            {
                warnings.Add(shed
                    ? $"{craftable.Name}: \"Place in front\" is not written: it is placed with the game's Large Log Shed recipe and its placement check drawn in, so you may stand inside the ghost."
                    : $"{craftable.Name}: \"Place in front\" is not written: it is placed like the game's large buildings with its placement check drawn in, so you may stand inside the ghost.");
            }
            else
            {
                AddInFront(catalog, craftable, plan, packages, ahead, edits, warnings);
            }
        }

        if (craftable.NeedsFlag is null && percent is null && !large)
        {
            return;
        }

        if (percent is { } p && (!float.IsFinite(p) || p <= 0))
        {
            warnings.Add($"{craftable.Name}: a repair cost of {p.ToString(CultureInfo.InvariantCulture)} % is not valid; the game's repair is kept.");
            percent = null;
        }

        var map = plan.ToMap();
        foreach (var (stock, clone) in BuiltElements(catalog, craftable, plan, packages))
        {
            var cdo = $"Default__{PackageMap.Leaf(stock)}_C";
            var stored = TunableReader.Read(ModdableAssets.ReadPackage(catalog, stock)).Where(t => t.Export == cdo).ToDictionary(t => t.Path, StringComparer.Ordinal);
            void Set(string property, string value, string gameValue)
            {
                var kind = stored.TryGetValue(property, out var known) ? known.Kind : property == "_placementHeightType" ? TunableKind.Enum : TunableKind.Bool;
                if (gameValue.Length > 0 && TunableValue.AreEqual(kind, gameValue, value))
                {
                    return; // the element has it already
                }

                var export = map.RemapObject(cdo);
                var list = edits.TryGetValue(clone, out var existing) ? existing.Where(e => !(e.Export == export && e.Path == property)).ToList() : [];
                list.Add(new TunableEdit(export, property, value));
                edits[clone] = list;
            }

            if (craftable.NeedsFlag is { } needs)
            {
                // Not stored: the C++ default, false (31 game elements store true, none false).
                Set(WithoutFlag, needs ? "false" : "true", stored.TryGetValue(WithoutFlag, out var flag) ? flag.Value : "false");
            }

            if (percent is { } cost && Math.Abs(cost - 100) > 0.001f)
            {
                if (stored.TryGetValue(HealthPerRepair, out var perUse))
                {
                    Set(HealthPerRepair, TunableValue.Format(TunableValue.ParseFloat(perUse.Value) * 100 / cost), perUse.Value);
                }
                else
                {
                    warnings.Add($"{craftable.Name}: the repair cost is not written for {PackageMap.Leaf(stock)} (it stores no HealthRepairedPerUse; its repair keeps the game's code default).");
                }
            }

            if (large)
            {
                Set("_placementHeightType", HighestPointHeight, stored.TryGetValue("_placementHeightType", out var height) ? height.Value : string.Empty);
                Set("_mustBePlacedUpright", "true", stored.TryGetValue("_mustBePlacedUpright", out var upright) ? upright.Value : "false");
                if (stored.TryGetValue("_maxRotationWhilePlacing", out var rotation))
                {
                    Set("_maxRotationWhilePlacing", "0, 360, 0", rotation.Value);
                }
            }
        }
    }

    /// <summary>
    /// "Place in front": the craftable's recipe gets <c>PlacementLocationOffset</c> (added to the template recipe's own, which the
    /// chair's does not store) along its axis. Experimental: the game decides whether the offset turns with the player's view
    /// and whether the ground under it is traced there; only a game test shows the ghost where the element is built.
    /// </summary>
    private static void AddInFront(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, float meters, Dictionary<string, IReadOnlyList<TunableEdit>> edits, List<string> warnings)
    {
        var stock = plan.Packages.First(p => p.Value == packages.Recipe).Key;
        var export = PackageMap.Leaf(stock);
        if (!catalog.GetExports(stock).Any(e => e.Name == export && e.ClassName == "PlaceableCraftingRecipe"))
        {
            // Another recipe class has no such value: written, it would make every edit of the recipe fail.
            warnings.Add($"{craftable.Name}: \"Place in front\" is not written ({export} is not a placement recipe).");
            return;
        }

        var game = TunableReader.Read(ModdableAssets.ReadPackage(catalog, stock)).FirstOrDefault(t => t.Export == export && t.Path == PlacementOffset) is { } stored
            ? TunableValue.ParseFloats(stored.Value, 3)
            : [0f, 0f, 0f];
        var axis = craftable.InFront?.Axis ?? InFrontAxis.PlusX;
        var (x, y) = InFrontOffset(axis, meters);
        var renamed = plan.ToMap().RemapObject(export);
        var list = edits.TryGetValue(packages.Recipe, out var existing) ? existing.Where(e => !(e.Export == renamed && e.Path == PlacementOffset)).ToList() : [];
        list.Add(new TunableEdit(renamed, PlacementOffset, TunableValue.Format(game[0] + x, game[1] + y, game[2])));
        edits[packages.Recipe] = list;
        var sign = axis is InFrontAxis.MinusX or InFrontAxis.MinusY ? "-" : "+";
        warnings.Add($"{craftable.Name}: placed {meters.ToString("0.#", CultureInfo.InvariantCulture)} m along {sign}{(axis is InFrontAxis.PlusY or InFrontAxis.MinusY ? "Y" : "X")} (experimental: its recipe's PlacementLocationOffset); check in game that the ghost shows where it is built.");
    }

    /// <summary>
    /// A big building's placement check, drawn in to its middle (owner, 3.5.0: "always red, Overlapping character, I am
    /// standing inside it"). The game centres the ghost on the aimed point, 2-3 m ahead, so inside a 15 m cabin the player's
    /// feet meet its floor and the ghost's mesh overlaps him; every placeable recipe of the game checks mesh against mesh
    /// (<c>BoxOverlapFlags</c> 2, the chair's too). The game's own cure for a false overlap is to shrink what is checked: its
    /// wall lamps scale the collision mesh to 0.6, the workbench to 0.9 in Z, the chair and the bed draw their box in 5 cm.
    /// So the copied recipe gets <c>CollisionMeshScale</c> (about the pivot) and <c>BoundsExtensionWhilePlacingMin</c>/<c>Max</c>
    /// that keep <see cref="PlacementCheckKeepCm"/> around the pivot: the ghost turns green with the player inside it and
    /// still refuses a rock or a tree at its middle; the ground traces keep the whole footprint, and the built element keeps
    /// its full collision. Only a game test shows the ghost green: the warning says so.
    /// <para>
    /// Per side and per axis (review 3.5.1: one inset from the longest reach crossed the box's faces over on the shorter axis
    /// of the owner's 11 x 15 m cabin): each face of the box moves in to <see cref="PlacementCheckKeepCm"/> from the pivot (never
    /// past it; a face already that close keeps the recipe's own value), and the collision mesh is scaled per axis so that it
    /// reaches no further. The template recipe's own values are the start (the workbench's 0.9 height scale stays). A model
    /// imported from a 3D file is read from its own folder in <paramref name="projectDirectory"/> (<see cref="Craftable.Imported"/>).
    /// </para>
    /// </summary>
    private static void AddPlacementShrink(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, Dictionary<string, IReadOnlyList<TunableEdit>> edits, List<string> warnings,
        string? projectDirectory)
    {
        var stock = plan.Packages.First(p => p.Value == packages.Recipe).Key;
        var export = PackageMap.Leaf(stock);
        if (!catalog.GetExports(stock).Any(e => e.Name == export && e.ClassName == "PlaceableCraftingRecipe"))
        {
            return;
        }

        string NotDrawnIn(string why) =>
            $"{craftable.Name}: its placement check is not drawn in ({why}); the ghost may stay red with you inside it.";
        (float X, float Y, float Z) min, max;
        AssetCatalog? own = null;
        try
        {
            // A model imported from a 3D file is not in the game's files (review 3.5.1): its own cooked files are read.
            if (craftable.Imported is { } imported)
            {
                if (ImportedMeshFolder(projectDirectory, imported) is not { } folder)
                {
                    warnings.Add(NotDrawnIn($"its imported model's files are not in the project's {imported} folder"));
                    return;
                }

                own = AssetCatalog.OpenLoose(folder);
            }

            if ((own ?? catalog).LoadFirstExport<UStaticMesh>(PackageMap.Normalize(craftable.Mesh)) is not { } mesh)
            {
                warnings.Add(NotDrawnIn($"its mesh {PackageMap.Leaf(craftable.Mesh)} could not be read"));
                return;
            }

            var bounds = MeshExtractor.DescribeStaticMesh(mesh).Bounds;
            (min, max) = ((bounds.Min.X, bounds.Min.Y, bounds.Min.Z), (bounds.Max.X, bounds.Max.Y, bounds.Max.Z));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add(NotDrawnIn($"its mesh {PackageMap.Leaf(craftable.Mesh)} could not be read: {ex.GetType().Name}"));
            return;
        }
        finally
        {
            own?.Dispose();
        }

        var reachX = MathF.Max(-min.X, max.X); // cm, horizontally from the pivot
        var reachY = MathF.Max(-min.Y, max.Y);
        if (!float.IsFinite(reachX) || !float.IsFinite(reachY) || (reachX <= PlacementCheckKeepCm && reachY <= PlacementCheckKeepCm))
        {
            return; // small enough: the player aims from outside it
        }

        // The template recipe's own values (the shed stores none: 1, 1, 1 and no extension; the workbench scales Z to 0.9).
        var stored = TunableReader.Read(ModdableAssets.ReadPackage(catalog, stock)).Where(t => t.Export == export).ToDictionary(t => t.Path, StringComparer.Ordinal);
        float[] Stored(string property, float fallback) => stored.TryGetValue(property, out var value) ? TunableValue.ParseFloats(value.Value, 3) : [fallback, fallback, fallback];
        var gameScale = Stored(CollisionScale, 1);
        var gameMin = Stored(BoundsInsetMin, 0);
        var gameMax = Stored(BoundsInsetMax, 0);

        // A face this far from the pivot (cm) with the recipe's own extension moves in to the kept distance.
        static float Face(float side, float game) => side + game > PlacementCheckKeepCm ? MathF.Round(PlacementCheckKeepCm - side, 1) : game;
        static float Scale(float reach, float game) => reach * game > PlacementCheckKeepCm ? MathF.Round(PlacementCheckKeepCm / reach, 2) : game;
        var scaleX = Scale(reachX, gameScale[0]);
        var scaleY = Scale(reachY, gameScale[1]);
        var renamed = plan.ToMap().RemapObject(export);
        var list = edits.TryGetValue(packages.Recipe, out var existing)
            ? existing.Where(e => !(e.Export == renamed && e.Path is CollisionScale or BoundsInsetMin or BoundsInsetMax)).ToList()
            : [];
        list.Add(new TunableEdit(renamed, CollisionScale, TunableValue.Format(scaleX, scaleY, gameScale[2])));
        list.Add(new TunableEdit(renamed, BoundsInsetMin, TunableValue.Format(Face(-min.X, gameMin[0]), Face(-min.Y, gameMin[1]), gameMin[2])));
        list.Add(new TunableEdit(renamed, BoundsInsetMax, TunableValue.Format(Face(max.X, gameMax[0]), Face(max.Y, gameMax[1]), gameMax[2])));
        edits[packages.Recipe] = list;
        string Text(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        warnings.Add($"{craftable.Name}: its placement check is drawn in to {(PlacementCheckKeepCm / 100).ToString("0.#", CultureInfo.InvariantCulture)} m around its middle "
            + $"(collision mesh x{Text(scaleX)} by x{Text(scaleY)}, each side of its box moved in to {(PlacementCheckKeepCm / 100).ToString("0.#", CultureInfo.InvariantCulture)} m from its pivot), the way the game's wall lamps are, "
            + "so the ghost can turn green with you standing inside it; what is built keeps its full collision. Check in game that it turns green.");
    }

    /// <summary>
    /// The folder of a model imported from a 3D file (<paramref name="imported"/>, relative to <paramref name="projectDirectory"/>),
    /// or null when there is no project folder or it is not inside it.
    /// </summary>
    private static string? ImportedMeshFolder(string? projectDirectory, string imported)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var root = Path.GetFullPath(projectDirectory);
        var folder = Path.GetFullPath(Path.Combine(root, imported));
        return folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder) ? folder : null;
    }

    /// <summary>The health one use of a repair tool restores at <paramref name="percent"/> % of the game's repair cost.</summary>
    /// <param name="gameHealthPerUse">The element's <c>_repairableParams.HealthRepairedPerUse</c>.</param>
    /// <param name="percent">The repair cost in percent of the game's (100 = the game's).</param>
    public static float HealthPerRepairUse(float gameHealthPerUse, float percent) => percent > 0 ? gameHealthPerUse * 100 / percent : gameHealthPerUse;
}
