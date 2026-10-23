using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// cwhedgehog (Discord, 2026-10-19): a skybridge laid across the map from one piece stood in one small level and showed only
/// up close. A placed mesh out of its level's reach is written into the terrain tile under it (<see cref="FarPlacement"/>).
/// </summary>
public sealed class FarPlacementTests
{
    private const string Town = SyntheticLevels.LevelPath;
    private const string TerrainA = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1";
    private const string TerrainB = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_2";
    private const string Rock = SyntheticLevels.RockPackage + ".SM_Rock";

    // A town 20 m square streaming within 200 m; two terrain tiles side by side (1,000 m), the town in the first.
    private static readonly WorldTileInfo TownTile = Tile(-1000, -1000, 1000, 1000, "City", 20000);
    private static readonly FarPlacement.Tile[] Tiles =
    [
        new(TerrainA, Tile(-50000, -50000, 50000, 50000, "Landscape_0_5", 100000)),
        new(TerrainB, Tile(50000, -50000, 150000, 50000, "Landscape_0_5", 100000)),
    ];

    [Fact]
    public void AMeshWithinItsLevelsReachStaysThere()
    {
        Assert.Equal(new FarPlacement.Home(Town, 20000, false, 0f, 20000), FarPlacement.HomeOf(new FVector(500, 500, 0), Town, TownTile, Tiles));
        var near = FarPlacement.HomeOf(new FVector(15000, 0, 9000), Town, TownTile, Tiles)!; // 140 m out, high up: height does not count
        Assert.False(near.Moved);
        Assert.Equal(14000f, near.OutsideCm, 1f);
    }

    [Fact]
    public void AMeshOutOfItsLevelsReachGoesIntoTheTerrainTileUnderIt()
    {
        var out1 = FarPlacement.HomeOf(new FVector(30000, 0, 0), Town, TownTile, Tiles)!; // 290 m out of a 200 m reach
        Assert.Equal(new FarPlacement.Home(TerrainA, 100000, true, 29000f, 20000), out1);
        Assert.Equal(TerrainB, FarPlacement.HomeOf(new FVector(120000, 3000, 0), Town, TownTile, Tiles)!.Level);
        Assert.Equal(TerrainB, FarPlacement.HomeOf(new FVector(50000.5f, 0, 0), Town, TownTile, Tiles)!.Level); // on the seam: the nearer centre

        // Over the open sea past the terrain there is no tile: it stays (its level's area grows over it, as before).
        Assert.False(FarPlacement.HomeOf(new FVector(900000, 0, 0), Town, TownTile, Tiles)!.Moved);

        // A level with no tile record (the persistent level) or one the game does not stream by distance keeps its meshes.
        Assert.Null(FarPlacement.HomeOf(new FVector(30000, 0, 0), Town, null, Tiles));
        Assert.False(FarPlacement.HomeOf(new FVector(30000, 0, 0), Town, Tile(-1000, -1000, 1000, 1000, "Off", 20000, streams: false), Tiles)!.Moved);

        // A mesh a project put into a terrain tile itself stays there within that tile's 1,000 m.
        Assert.False(FarPlacement.HomeOf(new FVector(140000, 0, 0), TerrainA, Tiles[0].Info, Tiles)!.Moved); // 900 m out
    }

    [Fact]
    public void ThePlacedMeshGoesWithEverythingDoneToItAndTheJournalKeepsIt()
    {
        var state = new EditState();
        var far = new AddStaticMeshActorOp(Town, "SM_Rock_Added", Rock, TransformValue.At(1000, 0, 0)) { Loot = ["/Game/Loot/World_Shelf.World_Shelf_C"] };
        state.Apply(far);
        var moved = TransformValue.At(120000, 2000, 8000); // dragged 1.2 km east, 80 m up
        state.Apply(new SetTransformOp(far.Created, far.Transform, moved));
        state.Apply(new SetClearGrassOp(far.Created, null, false));
        state.Apply(new BendActorOp(far.Created, 0f, 30f));
        var near = new AddStaticMeshActorOp(Town, "SM_Rock_Added2", Rock, TransformValue.At(1500, 0, 0));
        state.Apply(near);
        var gone = new AddStaticMeshActorOp(Town, "SM_Rock_Added3", Rock, TransformValue.At(60000, 0, 0));
        state.Apply(gone);
        state.Apply(new DeleteActorOp(gone.Created));

        // The tile already holds a stock actor of that name: the moved one takes the next free name.
        var (result, moves) = FarPlacement.Apply(state, Tiles, l => l == Town ? TownTile : null, _ => null, l => l == TerrainB ? ["SM_Rock_Added"] : []);

        var move = Assert.Single(moves);
        var to = new ActorRef(TerrainB, "SM_Rock_Added2");
        Assert.Equal(far.Created, move.From);
        Assert.Equal(to, move.To);
        Assert.Contains("Landscape_A_0_2", move.Describe(), StringComparison.Ordinal);
        Assert.Contains("1,000 m", move.Describe(), StringComparison.Ordinal);
        var add = Assert.IsType<AddStaticMeshActorOp>(result.AddedActors[to]);
        Assert.Equal((TerrainB, "SM_Rock_Added2", Rock), (add.Level, add.NewName, add.StaticMesh));
        Assert.Equal(far.Loot, add.Loot);
        Assert.Equal(moved, result.GetAddedTransform(to));
        Assert.False(result.GetClearGrass(to));
        Assert.Equal(30f, result.GetBend(to));
        Assert.False(result.IsAdded(far.Created));
        Assert.True(result.IsAdded(near.Created)); // 5 m out: within the town's 200 m
        Assert.True(result.IsDeleted(gone.Created)); // deleted: nothing to write, stays as it was
        Assert.Contains(TerrainB, result.ChangedLevels);

        // The project's own state is not touched.
        Assert.True(state.IsAdded(far.Created));
        Assert.False(state.IsAdded(to));
    }

    [Fact]
    public void AFarAddedInstanceOfAPartBecomesAMeshOfItsOwnAndATreeStaysAnInstance()
    {
        var b = new FakeLevelBuilder(Town);
        var bridge = b.Actor("Bridge_1", "/Script/Engine.Actor");
        var root = b.Component(bridge, "DefaultSceneRoot", location: new FVector(500, 0, 0));
        b.Root(bridge, root);
        b.Component(bridge, "Decks", "HierarchicalInstancedStaticMeshComponent", attachTo: root, mesh: Rock, instances: [FTransform.Identity]);
        var forest = b.Actor("InstancedFoliageActor_0", "/Script/Foliage.InstancedFoliageActor");
        b.Component(forest, "Pines", "FoliageInstancedStaticMeshComponent", mesh: Rock, instances: [FTransform.Identity]);
        var document = LevelDocument.FromData(b.Build());

        var state = new EditState();
        var deck = new AddInstanceOp(new InstanceRef(Town, "Bridge_1", "Decks", 1), TransformValue.At(99500, 0, 3000)); // world x 1,000 m
        var nearDeck = new AddInstanceOp(new InstanceRef(Town, "Bridge_1", "Decks", 2), TransformValue.At(400, 0, 0));
        var pine = new AddInstanceOp(new InstanceRef(Town, "InstancedFoliageActor_0", "Pines", 1), TransformValue.At(100000, 0, 0));
        state.Apply(deck);
        state.Apply(nearDeck);
        state.Apply(pine);

        var (result, moves) = FarPlacement.Apply(state, Tiles, l => l == Town ? TownTile : null, l => l == Town ? document : null, _ => []);

        var move = Assert.Single(moves);
        Assert.Equal(deck.Target, move.Instance);
        var add = Assert.IsType<AddStaticMeshActorOp>(result.AddedActors[move.To]);
        Assert.Equal((TerrainB, "SM_Rock_Added", Rock), (add.Level, add.NewName, add.StaticMesh));
        Assert.Equal(new FVector(100000, 0, 3000), add.Transform.Location);
        Assert.False(result.IsAdded(deck.Target));
        Assert.True(result.IsAdded(nearDeck.Target)); // near the town: an instance of its part, as before
        Assert.True(result.IsAdded(pine.Target)); // a tree stays choppable
    }

    [Fact]
    public void AFarAddedInstanceTakesTheProjectsLootOfItsPart()
    {
        const string Locker = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Locker.Examine_Locker_C";
        const string Cabinet = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Cabinet.Examine_Cabinet_C";
        var b = new FakeLevelBuilder(Town);
        var shed = b.Actor("Shed_1", "/Script/Engine.Actor");
        var root = b.Component(shed, "DefaultSceneRoot");
        b.Root(shed, root);
        b.Component(shed, "Lockers", "InstancedStaticMeshComponent", attachTo: root, mesh: Rock, instances: [FTransform.Identity]);
        var document = LevelDocument.FromData(b.Build());
        var owner = new ActorRef(Town, "Shed_1");

        LootSetting? Carried(LootSetting? edit)
        {
            var state = new EditState();
            if (edit is not null)
            {
                state.Apply(new SetLootOp(owner, "Lockers", new LootSetting(true, [Cabinet]), edit));
            }

            state.Apply(new AddInstanceOp(new InstanceRef(Town, "Shed_1", "Lockers", 1), TransformValue.At(100000, 0, 0)));
            var (result, moves) = FarPlacement.Apply(state, Tiles, l => l == Town ? TownTile : null, l => l == Town ? document : null, _ => []);
            var loot = Assert.IsType<AddStaticMeshActorOp>(result.AddedActors[Assert.Single(moves).To]).Loot;
            return loot is null ? null : new LootSetting(loot.Count > 0, loot);
        }

        // The Map's own copy of a part carries the part's loot as the project has it; so does the export's far one.
        Assert.Null(Carried(null)); // stock part, no presets: only what the mesh asset carries
        Assert.Equal(new LootSetting(true, [Locker]), Carried(new LootSetting(true, [Locker])));
        Assert.Equal(new LootSetting(false, []), Carried(new LootSetting(false, []))); // made decoration only: stays so
    }

    [Fact]
    public void WithoutTerrainTilesNothingMoves()
    {
        var state = new EditState();
        state.Apply(new AddStaticMeshActorOp(Town, "SM_Rock_Added", Rock, TransformValue.At(500000, 0, 0)));
        var (result, moves) = FarPlacement.Apply(state, [], _ => TownTile, _ => null, _ => []);
        Assert.Same(state, result);
        Assert.Empty(moves);
    }

    [Fact]
    public void OnlyPlacedMeshesCanBeMoved()
    {
        var state = new EditState();
        var copy = new DuplicateActorOp(new ActorRef(Town, "StaticMeshActor_1"), "StaticMeshActor_1_Copy", TransformValue.Identity);
        state.Apply(copy);
        Assert.Throws<ArgumentException>(() => state.WithPlacedMeshesMoved(new Dictionary<ActorRef, ActorRef> { [copy.Created] = new(TerrainA, "X") }, new Dictionary<InstanceRef, AddStaticMeshActorOp>()));
    }

    [Fact]
    public async Task TheExportWritesAFarMeshIntoTheTerrainTileAndKeepsANearOneInItsLevel()
    {
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        SyntheticLevels.WriteContent(content, withBlueprintPackage: true, TownTile);
        await SyntheticLevels.BuildLevel(Tiles[1].Info).WriteAsync(Path.Combine([content, .. (SyntheticLevels.MapsFolder + "/Landscape_A_0_2").Split('/')]), ".umap");
        using var catalog = AssetCatalog.OpenLoose(content);
        using var project = Project.Create(temp.Combine("Bridge.ssproj"), "Bridge");
        project.Apply(new AddStaticMeshActorOp(Town, "Deck_Near", Rock, TransformValue.At(1500, 0, 0)));
        project.Apply(new AddStaticMeshActorOp(Town, "Deck_Far", Rock, TransformValue.At(100000, 0, 3000)));
        // A copy of one of the town's instanced rocks 1.1 km away, as the Map journals it (in its component's space).
        var plain = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = true };
        var rocks = LevelDocument.Load(new Cue4ParseLevelReader(catalog, plain), Town).FindActor("Rocks_Actor")!.FindComponent("Rocks")!;
        var copyAt = new FVector(110000, 0, 100);
        project.Apply(new AddInstanceOp(new InstanceRef(Town, "Rocks_Actor", "Rocks", 2), TransformValue.FromTransform(new FTransform(copyAt).GetRelativeTransform(rocks.WorldTransform))));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        Assert.Equal(2, result.FarPlaced.Count);
        var town = Assert.Single(result.Levels, l => l.PackagePath == Town);
        Assert.Equal(["Deck_Near"], town.Report.AddedActors);
        var terrain = Assert.Single(result.Levels, l => l.PackagePath == TerrainB);
        Assert.Equal(["Deck_Far", "SM_Rock_Added"], terrain.Report.AddedActors.Order(StringComparer.Ordinal));

        // Read back as the game reads it: each where it was placed, the rocks' component no longer holds a third instance.
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var reader = new Cue4ParseLevelReader(written, plain);
        var tileDoc = LevelDocument.Load(reader, TerrainB);
        Assert.Equal(new FVector(100000, 0, 3000), tileDoc.FindActor("Deck_Far")!.WorldTransform.Translation);
        Assert.True(FVector.Distance(copyAt, tileDoc.FindActor("SM_Rock_Added")!.WorldTransform.Translation) < 1f, tileDoc.FindActor("SM_Rock_Added")!.WorldTransform.Translation.ToString());
        var townDoc = LevelDocument.Load(reader, Town);
        Assert.Equal(2, townDoc.FindActor("Rocks_Actor")!.FindComponent("Rocks")!.Instances.Count);
        Assert.NotNull(townDoc.FindActor("Deck_Near"));
        Assert.Null(townDoc.FindActor("Deck_Far"));

        // The town's area was not stretched over the far deck (it grows only over Deck_Near, 5 m out).
        var grown = WorldTileInfo.TryRead(CookedPackage.Load(Directory.EnumerateFiles(result.StagingDirectory, "A_0_TestLevel.umap", SearchOption.AllDirectories).Single()))!;
        Assert.True(grown.BoundsMax.X < 2000f, grown.BoundsMax.ToString());

        // The report says where each went and why.
        var report = await File.ReadAllTextAsync(result.ReportPath!);
        Assert.Contains("## Placed far from their level", report, StringComparison.Ordinal);
        Assert.Contains("A_0_TestLevel/Deck_Far stands", report, StringComparison.Ordinal);
    }

    private static WorldTileInfo Tile(float x0, float y0, float x1, float y1, string layer, int streaming, bool streams = true) =>
        new((0, 0, 0), new FVector(x0, y0, -10000), new FVector(x1, y1, 10000), true, new WorldTileLayer(layer, 0, (0, 0), streaming, streams), false, "None", [], 0);
}
