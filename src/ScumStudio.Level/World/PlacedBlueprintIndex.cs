using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;

namespace ScumStudio.Level.World;

/// <summary>
/// One actor placed in a sublevel, found by <see cref="PlacedBlueprintIndex"/>.
/// </summary>
/// <param name="Level">Level package path.</param>
/// <param name="Actor">Actor object name.</param>
/// <param name="ClassPath">Class object path (<c>/Game/X/BP_Lamp.BP_Lamp_C</c>, or <see cref="PlacedBlueprintIndex.WorldItemSpawnerClass"/>).</param>
/// <param name="IsChildActor">True for an actor another actor's ChildActorComponent spawned (it hangs on its parent).</param>
/// <param name="LevelSize">Exports in the level (a smaller level reads faster).</param>
/// <param name="Item">For a world item spawner: the item class it spawns (its <c>_item</c>), or null.</param>
public sealed record PlacedActor(string Level, string Actor, string ClassPath, bool IsChildActor, int LevelSize, string? Item = null);

/// <summary>
/// Where each Blueprint class stands on the island, from the sublevels' headers alone (2,700 classes in 2,300 levels, well
/// under a second): a Blueprint is placed by copying one of these. Items (a drill press, a chest) are never placed as
/// actors; the game puts fixed ones down with native world item spawners, which are indexed with the item they spawn.
/// Discord user igor: "It would be preferable to create blueprints like these, with the option to install them on the map".
/// </summary>
public sealed class PlacedBlueprintIndex
{
    /// <summary>Class of the game's world item spawner actors (one fixed item where it stands).</summary>
    public const string WorldItemSpawnerClass = "/Script/SCUM.WorldItemSpawner";

    private readonly Dictionary<string, List<PlacedActor>> _byClass;

    private PlacedBlueprintIndex(Dictionary<string, List<PlacedActor>> byClass, IReadOnlyList<PlacedActor> spawners)
    {
        _byClass = byClass;
        Spawners = spawners;
    }

    /// <summary>Number of Blueprint classes placed somewhere.</summary>
    public int ClassCount => _byClass.Count;

    /// <summary>The world item spawners that are actors of their own (not child actors), with their items.</summary>
    public IReadOnlyList<PlacedActor> Spawners { get; }

    /// <summary>
    /// The placed instances of a Blueprint class (its package path or class path): actors of their own first, then those
    /// in the smallest levels. Empty when it is placed nowhere.
    /// </summary>
    public IReadOnlyList<PlacedActor> Of(string classPackage) =>
        _byClass.TryGetValue(AssetPaths.SplitObjectPath(classPackage).PackagePath, out var list) ? list : [];

    /// <summary>
    /// The world item spawners that spawn a fixed item (their copy has an <c>_item</c> to set), those that already spawn
    /// <paramref name="itemClass"/> first, then the smallest levels.
    /// </summary>
    public IReadOnlyList<PlacedActor> SpawnersFor(string itemClass) =>
        Spawners.Where(s => s.Item is not null)
            .OrderBy(s => string.Equals(s.Item, itemClass, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => s.LevelSize).ToList();

    /// <summary>
    /// Reads every sublevel header of <paramref name="world"/> from <paramref name="catalog"/>: each export directly under the
    /// level whose class is a Blueprint class (or a world item spawner) is a placed actor. Only the levels that hold world
    /// item spawners are read whole, for the items. Levels that cannot be read are skipped.
    /// </summary>
    public static PlacedBlueprintIndex Build(AssetCatalog catalog, WorldIndex world, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(world);
        var byClass = new Dictionary<string, List<PlacedActor>>(StringComparer.OrdinalIgnoreCase);
        var spawners = new List<PlacedActor>();
        foreach (var level in world.Sublevels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.TryGetPackageFile(level.PackagePath, out var file))
            {
                continue;
            }

            try
            {
                var package = CookedPackage.Parse(file.Read(), [], null, level.PackagePath);
                var found = Placed(package, level.PackagePath);
                foreach (var actor in found.Where(a => a.ClassPath != WorldItemSpawnerClass))
                {
                    var key = AssetPaths.SplitObjectPath(actor.ClassPath).PackagePath;
                    if (!byClass.TryGetValue(key, out var list))
                    {
                        byClass[key] = list = [];
                    }

                    list.Add(actor);
                }

                var levelSpawners = found.Where(a => a.ClassPath == WorldItemSpawnerClass && !a.IsChildActor).ToList();
                if (levelSpawners.Count > 0)
                {
                    spawners.AddRange(WithItems(catalog, file, level.PackagePath, levelSpawners));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or InvalidOperationException)
            {
                // ponytail: an unreadable level just has no placed actors here
            }
        }

        foreach (var list in byClass.Values)
        {
            list.Sort((a, b) => a.IsChildActor != b.IsChildActor ? a.IsChildActor.CompareTo(b.IsChildActor)
                : a.LevelSize != b.LevelSize ? a.LevelSize.CompareTo(b.LevelSize)
                : string.CompareOrdinal(a.Level + ":" + a.Actor, b.Level + ":" + b.Actor));
        }

        return new PlacedBlueprintIndex(byClass, spawners);
    }

    /// <summary>The Blueprint actors and world item spawners directly under the level export of a header-only package.</summary>
    private static List<PlacedActor> Placed(CookedPackage package, string level)
    {
        var result = new List<PlacedActor>();
        var levelIndex = -1;
        for (var i = 0; i < package.Exports.Count && levelIndex < 0; i++)
        {
            if (package.GetExportClassName(i) == "Level")
            {
                levelIndex = i + 1;
            }
        }

        foreach (var export in package.Exports)
        {
            if (export.OuterIndex != levelIndex || export.ClassIndex >= 0 || -export.ClassIndex - 1 >= package.Imports.Count)
            {
                continue;
            }

            var import = package.Imports[-export.ClassIndex - 1];
            var kind = package.ResolveName(import.ClassName);
            var classPath = kind is "BlueprintGeneratedClass" or "Class" ? package.GetFullPath(export.ClassIndex) : null;
            if (classPath is null || (kind == "Class" && classPath != WorldItemSpawnerClass))
            {
                continue;
            }

            var name = package.ResolveName(export.ObjectName);
            var child = name.Contains("_GEN_VARIABLE_", StringComparison.Ordinal) || name.Contains("_CAT", StringComparison.Ordinal);
            result.Add(new PlacedActor(level, name, classPath, child, package.Exports.Count));
        }

        return result;
    }

    /// <summary>The spawners of one level with the item each spawns (read from its components' <c>_item</c>).</summary>
    private static List<PlacedActor> WithItems(AssetCatalog catalog, CUE4Parse.FileProvider.Objects.GameFile file, string level, List<PlacedActor> spawners)
    {
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var package = CookedPackage.Parse(uasset, uexp, ubulk, level);
        return spawners.Select(spawner =>
        {
            var actor = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName) == spawner.Actor, -1);
            string? item = null;
            for (var i = 0; i < package.Exports.Count && item is null && actor >= 0; i++)
            {
                if (package.Exports[i].OuterIndex == actor + 1 && package.ReadProperties(i).Find("_item")?.Value is SoftObjectValue { AssetPath.Length: > 0 } soft)
                {
                    item = soft.AssetPath;
                }
            }

            return spawner with { Item = item };
        }).ToList();
    }
}
