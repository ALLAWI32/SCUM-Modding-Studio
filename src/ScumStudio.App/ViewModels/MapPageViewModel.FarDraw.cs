using System.Globalization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Export;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// How far the game draws a placed object (cwhedgehog, Discord 2026-10-19: "the bridge is only visible up close and
/// disappears when viewed from a distance"): its properties say which level the game loads it with and from how far,
/// worked out as the export does (<see cref="FarPlacement"/>).
/// </summary>
public sealed partial class MapPageViewModel
{
    private (WorldIndex? World, IReadOnlyList<FarPlacement.Tile> Tiles) _terrainTiles = (null, []);

    /// <summary>The island's terrain tiles with their stock records (worked out once per world index).</summary>
    private IReadOnlyList<FarPlacement.Tile> TerrainTiles
    {
        get
        {
            if (!ReferenceEquals(_terrainTiles.World, World))
            {
                _terrainTiles = (World, TerrainTilesOf(World));
            }

            return _terrainTiles.Tiles;
        }
    }

    /// <summary>The terrain tiles of <paramref name="world"/> that carry a tile record.</summary>
    internal static IReadOnlyList<FarPlacement.Tile> TerrainTilesOf(WorldIndex? world) =>
        world?.Packages.Where(p => p.Kind == WorldPackageKind.Landscape && p.IsMap && p.Tile is { BoundsValid: true })
            .Select(p => new FarPlacement.Tile(p.PackagePath, p.Tile!)).ToList() ?? [];

    /// <summary>
    /// The "In the game" row of a mesh the project placed at <paramref name="at"/> into <paramref name="level"/>: the level it
    /// goes into and from how far the game loads it (whether the game also hides small pieces sooner by their size is not
    /// checked in the game, and the row says so); null when the level has no streaming record (nothing to say).
    /// <paramref name="partCullCm"/> is the cull distance an added instance keeps when it stays an instance of its part.
    /// </summary>
    internal static PropertyRow? InGameRow(WorldIndex? world, IReadOnlyList<FarPlacement.Tile> tiles, string level, FVector at, int partCullCm = 0)
    {
        if (world?.Find(level) is not { Tile: { } own }
            || FarPlacement.HomeOf(at, level, own, tiles) is not { ReachCm: > 0 } home)
        {
            return null;
        }

        var metres = Metres(home.ReachCm);
        var text = home.Moved
            ? Localization.Loc.F("Map.Row.InGame.Tile", metres, Short(level), Short(home.Level))
            : partCullCm > 0
                ? Localization.Loc.F("Map.Row.InGame.Part", metres, Short(level), Metres(partCullCm))
                : Localization.Loc.F("Map.Row.InGame.Level", metres, Short(level));
        return new PropertyRow(Localization.Loc.T("Map.Row.InGame"), text);

        static string Short(string path) => path[(path.LastIndexOf('/') + 1)..];
        static string Metres(int cm) => (cm / 100f).ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The "In the game" row of an added instance (Ctrl+D or Paste of a building's or a bridge's instanced part) of
    /// <paramref name="component"/> of <paramref name="actor"/> standing at <paramref name="at"/>: far out the export writes it
    /// into the terrain tile under it as a mesh of its own (<see cref="FarPlacement.Apply"/>); otherwise it stays an instance
    /// of its part in the level and keeps the part's cull distance. Null for a tree, bush or grass of the foliage (it stays an
    /// instance of its foliage) and for what is not an instanced part.
    /// </summary>
    internal static PropertyRow? InstanceInGameRow(WorldIndex? world, IReadOnlyList<FarPlacement.Tile> tiles, ScumStudio.Level.Editing.EditState state,
        ScumStudio.Level.Editing.ActorRef owner, ScumStudio.Level.Model.ActorRecord actor, ScumStudio.Level.Model.ComponentRecord component, FVector at)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (component is not { IsInstanced: true, IsSynthesized: false } || FarPlacement.IsFoliage(actor, component))
        {
            return null;
        }

        // As the export decides: only an instance of a stock actor's part that the project did not move can leave its level.
        var movable = !state.IsAdded(owner) && state.GetTransformOverride(owner) is null && state.GetTransformOverride(owner, component.Name) is null;
        return InGameRow(world, movable ? tiles : [], owner.Level, at, component.InstanceEndCullDistance);
    }

    /// <summary>
    /// The selection's properties plus, for a mesh the project placed (the meshes the export may move; a copied building stays
    /// in its level), the <see cref="InGameRow"/>.
    /// </summary>
    private IReadOnlyList<PropertyRow> WithInGameRow(ActorItemViewModel item, IReadOnlyList<PropertyRow> rows, FVector at) =>
        item is { IsAdded: true, IsDeleted: false }
        && _services.Projects.Current?.State.AddedActors.GetValueOrDefault(item.Reference) is ScumStudio.Level.Editing.AddStaticMeshActorOp
        && InGameRow(World, TerrainTiles, item.Level.PackagePath, at) is { } row
            ? [.. rows, row]
            : rows;
}
