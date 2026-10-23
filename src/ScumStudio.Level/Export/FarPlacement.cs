using System.Globalization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>
/// The level a placed mesh is written into (cwhedgehog, Discord 2026-10-19: "all of my bridge segments are contained within
/// a single Actor, the bridge is only visible up close and disappears when viewed from a distance").
/// <para>
/// SCUM's map is a World Composition: the game loads a level only while the player is within its layer's streaming distance
/// of the level's area (the tile bounds in its header, height ignored) and drops it, with all it holds, farther away. The
/// distances are short for the places (20-50 m for interiors, 100-200 m for towns, farms and bridges, 500-600 m for a few
/// big ones) and 1,000 m for 399 of the 400 terrain tiles (<c>Landscape_*</c>, layer <c>Landscape_0_5</c>;
/// <c>Landscape_A_4_1</c> is in layer <c>Landscape</c>, 500 m); past a place's distance the game may draw a far model of
/// the place (most places have one), which never shows what a mod placed there. The stock island keeps
/// what stands between its places in the terrain tile under it: 16,757 standalone meshes (rocks, ruins, walls), 417
/// transmission towers and 1,186 power poles. Before this, every copy went into the level of what it was copied from
/// (Extend, Paste with that level selected, Ctrl+D of a building's instanced part), so a bridge laid across the map from one
/// piece stood in one small level, which the export then stretched over the whole bridge: drawn only within that level's
/// distance (100 m for the reporter's kind of place), and that whole place loaded wherever the bridge went.
/// </para>
/// <para>
/// Now a placed mesh that stands farther outside its level's area than that level's streaming distance (so the game does not
/// even load the level where the mesh stands) is written into the terrain tile under it instead (each bridge piece into the
/// tile under it), loaded with the terrain itself: within 1,000 m for almost every tile. An added instance of an instanced
/// part (a building's or a bridge's ISM/HISM part, not a tree, whose foliage component makes it choppable) standing that far
/// out becomes a mesh of its own there, with its part's mesh, materials, collision and loot (the project's loot edit of the
/// part when it has one): it no longer inherits its stock component's cull distance (60-100 m on most parts). 13,210 of the
/// tiles' 16,758 stock meshes carry no draw distance of their own (most large ones that do are drawn to 2 km), and neither
/// does a placed mesh, so the tile's streaming decides how far it is loaded; the game's own LODs still apply. Not checked in
/// the running game: whether the game also applies The_Island's <c>IslandCullDistanceVolume</c> when a level streams in (it
/// would hide a 4 m piece from about 200 m, as it would the stock meshes of that size). Meshes placed within reach of their
/// level stay in it, as before. Only the export changes: the project keeps them where they were placed.
/// </para>
/// </summary>
public static class FarPlacement
{
    /// <summary>A terrain tile: its level and its World Composition record.</summary>
    /// <param name="Level">Package path (<c>/Game/ConZ_Files/Maps/The_Island/Landscape_B_2_3</c>).</param>
    /// <param name="Info">Its tile record (bounds, layer).</param>
    public sealed record Tile(string Level, WorldTileInfo Info);

    /// <summary>Where a placed mesh is loaded in the game.</summary>
    /// <param name="Level">The level it is written into.</param>
    /// <param name="ReachCm">How near the player must be (cm) for the game to load it: that level's streaming distance.</param>
    /// <param name="Moved">True when that is the terrain tile under it rather than the level it was placed into.</param>
    /// <param name="OutsideCm">How far it stands outside the area of the level it was placed into (cm; 0 inside).</param>
    /// <param name="OwnReachCm">The streaming distance of the level it was placed into (cm).</param>
    public sealed record Home(string Level, int ReachCm, bool Moved, float OutsideCm, int OwnReachCm);

    /// <summary>One placed mesh the export writes into a terrain tile.</summary>
    /// <param name="From">Where the project has it (an added mesh actor, or the actor of an added instance).</param>
    /// <param name="To">The mesh actor written into the tile.</param>
    /// <param name="Instance">The added instance it stands for, or null for a placed mesh actor.</param>
    /// <param name="Location">Where it stands (world cm).</param>
    /// <param name="Home">The decision.</param>
    public sealed record Move(ActorRef From, ActorRef To, InstanceRef? Instance, FVector Location, Home Home)
    {
        /// <summary>One line for the export report.</summary>
        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"{(Instance is { } i ? $"{i} (an added instance)" : From.ToString())} stands {Home.OutsideCm / 100f:N0} m outside the area of {From.LevelName}, which the game loads only within {Home.OwnReachCm / 100f:N0} m of it: written into the terrain tile {To.LevelName} as {To.Actor}, loaded within {Home.ReachCm / 100f:N0} m.");
    }

    /// <summary>
    /// Where a mesh placed at <paramref name="location"/> into <paramref name="level"/> (its stock tile record
    /// <paramref name="levelTile"/>) is loaded: that level when it stands within the level's streaming distance of its area,
    /// else the terrain tile of <paramref name="tiles"/> under it. Null when the level is no World Composition tile (the
    /// persistent level, a spawn data asset): it stays where it is.
    /// </summary>
    public static Home? HomeOf(FVector location, string level, WorldTileInfo? levelTile, IReadOnlyList<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(tiles);
        if (levelTile is not { BoundsValid: true } own)
        {
            return null;
        }

        var outside = Outside(own, location);
        var reach = own.Layer.DistanceStreamingEnabled ? own.Layer.StreamingDistance : 0;
        var stay = new Home(level, reach, false, outside, reach);
        if (!own.Layer.DistanceStreamingEnabled || outside <= reach)
        {
            return stay;
        }

        return TileAt(location, tiles) is { } tile && !string.Equals(tile.Level, level, StringComparison.OrdinalIgnoreCase)
            ? new Home(tile.Level, tile.Info.Layer.StreamingDistance, true, outside, reach)
            : stay; // over the open sea past the terrain: the level's area grows over it, as before
    }

    /// <summary>The terrain tile whose area holds <paramref name="location"/> (the one whose centre is nearest when two touch there), or null.</summary>
    public static Tile? TileAt(FVector location, IReadOnlyList<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Tile? best = null;
        var bestDistance = float.MaxValue;
        foreach (var tile in tiles)
        {
            var info = tile.Info;
            if (!info.BoundsValid || Outside(info, location) > 0f)
            {
                continue;
            }

            var dx = info.Center.X - location.X;
            var dy = info.Center.Y - location.Y;
            var distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (tile, distance);
            }
        }

        return best;
    }

    /// <summary>How far (cm) <paramref name="location"/> is from the tile's area on the ground (height ignored, as the game streams): 0 inside.</summary>
    public static float Outside(WorldTileInfo tile, FVector location)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var shift = new FVector(tile.Position.X, tile.Position.Y, tile.Position.Z);
        var min = tile.BoundsMin + shift;
        var max = tile.BoundsMax + shift;
        var dx = MathF.Max(0f, MathF.Max(min.X - location.X, location.X - max.X));
        var dy = MathF.Max(0f, MathF.Max(min.Y - location.Y, location.Y - max.Y));
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The terrain tiles of the game files in <paramref name="catalog"/> with their stock records.</summary>
    public static IReadOnlyList<Tile> TilesOf(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return GroundHeights.LandscapeTiles(catalog).Select(t => new Tile(t.Path, t.Tile)).ToList();
    }

    /// <summary>
    /// The stock tile record of <paramref name="level"/> in <paramref name="catalog"/> (its header only), or null when it has
    /// none or cannot be read.
    /// </summary>
    public static WorldTileInfo? TileInfoOf(AssetCatalog catalog, string level)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            return catalog.TryGetPackageFile(level, out var file) ? WorldTileInfo.TryRead(CookedPackage.Parse(file.Read(), [], null, level)) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="state"/> with every placed mesh that stands out of its level's reach moved into the terrain tile under
    /// it (see the class remarks), and the moves. <paramref name="tileOf"/> gives a level's stock tile record,
    /// <paramref name="documentOf"/> a level as read (for the instanced parts; null when unreadable),
    /// <paramref name="namesOf"/> the names a level's package already uses. With no tiles nothing moves.
    /// </summary>
    public static (EditState State, IReadOnlyList<Move> Moves) Apply(
        EditState state, IReadOnlyList<Tile> tiles, Func<string, WorldTileInfo?> tileOf, Func<string, LevelDocument?> documentOf,
        Func<string, IEnumerable<string>> namesOf)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(tileOf);
        ArgumentNullException.ThrowIfNull(documentOf);
        ArgumentNullException.ThrowIfNull(namesOf);
        if (tiles.Count == 0)
        {
            return (state, []);
        }

        var levelTiles = new Dictionary<string, WorldTileInfo?>(StringComparer.OrdinalIgnoreCase);
        WorldTileInfo? LevelTile(string level) => levelTiles.TryGetValue(level, out var known) ? known : levelTiles[level] = tileOf(level);
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string Unique(string level, string name)
        {
            if (!taken.TryGetValue(level, out var names))
            {
                names = new HashSet<string>(namesOf(level), StringComparer.OrdinalIgnoreCase);
                names.UnionWith(state.AddedActors.Keys.Where(a => string.Equals(a.Level, level, StringComparison.OrdinalIgnoreCase)).Select(a => a.Actor));
                taken[level] = names;
            }

            var result = name;
            for (var i = 2; names.Contains(result); i++)
            {
                result = string.Create(CultureInfo.InvariantCulture, $"{name}{i}");
            }

            names.Add(result);
            return result;
        }

        var moves = new List<Move>();
        var actors = new Dictionary<ActorRef, ActorRef>(ActorRef.Comparer);
        foreach (var (actor, op) in state.AddedActors.OrderBy(a => a.Key.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key.Actor, StringComparer.OrdinalIgnoreCase))
        {
            if (op is not AddStaticMeshActorOp add || state.IsDeleted(actor))
            {
                continue;
            }

            var at = (state.GetAddedTransform(actor) ?? add.Transform).Location;
            if (HomeOf(at, actor.Level, LevelTile(actor.Level), tiles) is { Moved: true } home)
            {
                var to = new ActorRef(home.Level, Unique(home.Level, actor.Actor));
                actors[actor] = to;
                moves.Add(new Move(actor, to, null, at, home));
            }
        }

        var instances = new Dictionary<InstanceRef, AddStaticMeshActorOp>(InstanceRef.Comparer);
        foreach (var instance in state.AddedInstances.Keys.Where(i => !state.IsDeleted(i))
                     .OrderBy(i => i.Level, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Actor, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Component, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Index))
        {
            // A tree stays an instance of its foliage (chopped and harvested like the stock ones); an owner the project moved
            // is placed by the level's own export.
            if (documentOf(instance.Level)?.FindActor(instance.Actor) is not { } owner
                || owner.FindComponent(instance.Component) is not { IsInstanced: true, IsSynthesized: false } component
                || IsFoliage(owner, component)
                || state.GetTransformOverride(instance.ActorRef) is not null || state.GetTransformOverride(instance.ActorRef, component.Name) is not null
                || (state.GetMeshOverride(instance.ActorRef, component.Name) ?? component.StaticMeshPath) is not { Length: > 0 } mesh
                || state.GetAddedInstanceTransform(instance) is not { } local)
            {
                continue;
            }

            var world = local.ToTransform() * component.WorldTransform;
            if (HomeOf(world.Translation, instance.Level, LevelTile(instance.Level), tiles) is not { Moved: true } home)
            {
                continue;
            }

            var add = new AddStaticMeshActorOp(home.Level, Unique(home.Level, ShortName(mesh) + "_Added"), mesh, TransformValue.FromTransform(world))
            {
                CollisionProfile = component.CollisionProfile,
                Loot = LootOf(state, instance.ActorRef, owner, component),
                OverrideMaterials = component.OverrideMaterials,
            };
            instances[instance] = add;
            moves.Add(new Move(instance.ActorRef, add.Created, instance, world.Translation, home));
        }

        return moves.Count == 0 ? (state, []) : (state.WithPlacedMeshesMoved(actors, instances), moves);
    }

    /// <summary>True for a tree, bush or grass of the foliage (its component makes it choppable and harvestable).</summary>
    public static bool IsFoliage(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return string.Equals(actor.ClassName, "InstancedFoliageActor", StringComparison.Ordinal)
               || component.ClassName.Contains("Foliage", StringComparison.OrdinalIgnoreCase)
               || component.ClassPath.Contains("Foliage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The loot a far added instance of <paramref name="component"/> carries as a mesh of its own: the project's loot edit of
    /// that part when it has one (not lootable = none, as the Map's own copy of a part does), else the part's stock presets.
    /// </summary>
    private static IReadOnlyList<string>? LootOf(EditState state, ActorRef owner, ActorRecord actor, ComponentRecord component)
    {
        var key = actor.Root is { } root && string.Equals(root.Name, component.Name, StringComparison.Ordinal) ? null : component.Name;
        return state.GetLoot(owner, key) is { } set
            ? (set.Lootable ? (set.Presets.Count > 0 ? set.Presets : null) : [])
            : component.LootPresets.Count > 0 ? component.LootPresets : null;
    }

    private static string ShortName(string path)
    {
        var (package, name) = AssetPaths.SplitObjectPath(path);
        return string.IsNullOrEmpty(name) ? package[(package.LastIndexOf('/') + 1)..] : name;
    }
}
