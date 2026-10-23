using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.App;

/// <summary>
/// cwhedgehog (Discord): "after deleting the buildings I placed, the level's load/draw distance doesn't decrease - at least
/// that's the case in the Studio", and the Map's word on how far the game draws what was placed.
/// </summary>
public sealed class MapFarDrawTests
{
    private const string Town = SyntheticLevels.LevelPath;
    private const string Rock = SyntheticLevels.RockPackage + ".SM_Rock";

    [Fact]
    public void ADeletedPlacedBuildingNoLongerDrawsItsLevelOutToIt()
    {
        var state = new EditState();
        var far = new AddStaticMeshActorOp(Town, "SM_Building_Added", Rock, TransformValue.At(600000, -200000, 0));
        var near = new AddStaticMeshActorOp(Town, "SM_Shed_Added", Rock, TransformValue.At(60000, 0, 0));
        var moved = new ActorRef(Town, "StaticMeshActor_1");
        state.Apply(far);
        state.Apply(near);
        state.Apply(new SetTransformOp(moved, TransformValue.At(1000, 0, 0), TransformValue.At(-400000, 0, 0)));
        Assert.Equal((new FVector(-400000, -200000, 0), new FVector(600000, 0, 0)), MapPageViewModel.EditedBounds(state)[Town]);

        // Deleted (the journal keeps them, marked, for Ctrl+Z): the level's box shrinks to what is left.
        state.Apply(new DeleteActorOp(far.Created));
        state.Apply(new DeleteActorOp(moved));
        Assert.Equal((new FVector(60000, 0, 0), new FVector(60000, 0, 0)), MapPageViewModel.EditedBounds(state)[Town]);
        state.Apply(new DeleteActorOp(near.Created));
        Assert.False(MapPageViewModel.EditedBounds(state).ContainsKey(Town));

        // Undone: it is back, and so is its box.
        state.Apply(new RestoreActorOp(far.Created));
        Assert.Equal((new FVector(600000, -200000, 0), new FVector(600000, -200000, 0)), MapPageViewModel.EditedBounds(state)[Town]);
    }

    [Fact]
    public async Task APlacedMeshSaysFromHowFarTheGameLoadsIt()
    {
        using var temp = new LevelTempDirectory();
        var (catalog, world, tiles) = await TownAndTerrain(temp);
        using var files = catalog;

        var near = MapPageViewModel.InGameRow(world, tiles, Town, new FVector(1500, 0, 0))!;
        Assert.Equal(Loc.F("Map.Row.InGame.Level", "200", "A_0_TestLevel"), near.Value);
        Assert.DoesNotContain("Landscape_A_0_2", near.Value, StringComparison.Ordinal);

        var far = MapPageViewModel.InGameRow(world, tiles, Town, new FVector(100000, 0, 9000))!;
        Assert.Equal(Loc.F("Map.Row.InGame.Tile", 1000f.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), "A_0_TestLevel", "Landscape_A_0_2"), far.Value);

        // A level the island does not stream by distance (no tile record): nothing to say.
        Assert.Null(MapPageViewModel.InGameRow(world, tiles, "/Game/ConZ_Files/Maps/The_Island/The_Island", new FVector(0, 0, 0)));
    }

    [Fact]
    public async Task AnAddedInstanceOfAPartSaysWhereTheExportPutsItAndATreeSaysNothing()
    {
        using var temp = new LevelTempDirectory();
        var (catalog, world, tiles) = await TownAndTerrain(temp);
        using var files = catalog;
        var b = new FakeLevelBuilder(Town);
        var bridge = b.Actor("Bridge_1", "/Script/Engine.Actor");
        var root = b.Component(bridge, "DefaultSceneRoot");
        b.Root(bridge, root);
        b.Component(bridge, "Decks", "HierarchicalInstancedStaticMeshComponent", attachTo: root, mesh: Rock, instances: [FTransform.Identity]);
        var forest = b.Actor("InstancedFoliageActor_0", "/Script/Foliage.InstancedFoliageActor");
        b.Component(forest, "Pines", "FoliageInstancedStaticMeshComponent", mesh: Rock, instances: [FTransform.Identity]);
        var document = LevelDocument.FromData(b.Build());
        var owner = document.FindActor("Bridge_1")!;
        var decks = owner.FindComponent("Decks")! with { InstanceEndCullDistance = 10000 }; // a stock bridge part's 100 m
        var ownerRef = new ActorRef(Town, "Bridge_1");
        var state = new EditState();

        // Ctrl+D of a deck 1 km out: the export writes it into the terrain tile under it, as a mesh of its own.
        var far = MapPageViewModel.InstanceInGameRow(world, tiles, state, ownerRef, owner, decks, new FVector(100000, 0, 3000))!;
        Assert.Contains("Landscape_A_0_2", far.Value, StringComparison.Ordinal);

        // Near the town it stays an instance of its part, under the part's own cull distance.
        var near = MapPageViewModel.InstanceInGameRow(world, tiles, state, ownerRef, owner, decks, new FVector(1500, 0, 0))!;
        Assert.Equal(Loc.F("Map.Row.InGame.Part", "200", "A_0_TestLevel", "100"), near.Value);

        // A bridge the project moved keeps its instances in its level, wherever they stand (the export does the same).
        state.Apply(new SetTransformOp(ownerRef, TransformValue.Identity, TransformValue.At(500, 0, 0)));
        var stays = MapPageViewModel.InstanceInGameRow(world, tiles, state, ownerRef, owner, decks, new FVector(100000, 0, 3000))!;
        Assert.DoesNotContain("Landscape_A_0_2", stays.Value, StringComparison.Ordinal);
        Assert.Contains("A_0_TestLevel", stays.Value, StringComparison.Ordinal);

        // A tree of the foliage stays an instance of its foliage (choppable): no row.
        var trees = document.FindActor("InstancedFoliageActor_0")!;
        Assert.Null(MapPageViewModel.InstanceInGameRow(world, tiles, state, new ActorRef(Town, trees.Name), trees, trees.FindComponent("Pines")!, new FVector(100000, 0, 0)));
    }

    /// <summary>A 20 m town streaming within 200 m and, east of it, one terrain tile streaming within 1,000 m.</summary>
    private static async Task<(AssetCatalog Catalog, WorldIndex World, IReadOnlyList<ScumStudio.Level.Export.FarPlacement.Tile> Tiles)> TownAndTerrain(LevelTempDirectory temp)
    {
        var content = temp.Combine("game");
        var town = new WorldTileInfo((0, 0, 0), new FVector(-1000, -1000, -100), new FVector(1000, 1000, 500), true,
            new WorldTileLayer("City", 0, (0, 0), 20000, true), false, "None", [], 0);
        var terrain = new WorldTileInfo((0, 0, 0), new FVector(50000, -50000, -100), new FVector(150000, 50000, 500), true,
            new WorldTileLayer("Landscape_0_5", 0, (0, 0), 100000, true), false, "None", [], 0);
        SyntheticLevels.WriteContent(content, withBlueprintPackage: false, town);
        await SyntheticLevels.BuildLevel(terrain).WriteAsync(Path.Combine([content, .. (SyntheticLevels.MapsFolder + "/Landscape_A_0_2").Split('/')]), ".umap");
        var catalog = AssetCatalog.OpenLoose(content);
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        var tiles = MapPageViewModel.TerrainTilesOf(world);
        Assert.Single(tiles);
        return (catalog, world, tiles);
    }
}
