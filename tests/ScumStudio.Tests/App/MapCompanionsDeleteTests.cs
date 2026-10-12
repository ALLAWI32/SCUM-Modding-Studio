using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// "Delete with everything attached" from the map page (the owner: "If I delete a fire place, the fire stays burning in
/// game ... When I delete it, everything of it must be deleted"): every delete asks what belongs to the object, lists it,
/// and journals it all as one step. Synthetic camp level, no GL.
/// </summary>
public sealed class MapCompanionsDeleteTests
{
    private static readonly string[] FireplaceCompanions = ["Ash_Decal_1", "FireLight_1", "Fire_Sound_1", "Glow_1", "P_Fire_1", "Rock_1"];

    [Fact]
    public async Task DeletingTheFireplaceTakesItsFireInOneUndoStep()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var project = ctx.Services.Projects.Current!;
        map.SelectedActor = Actor(map, "SM_Fireplace_1");

        var clock = Stopwatch.StartNew();
        map.DeleteSelectedCommand.Execute(null);
        clock.Stop();
        var preview = Assert.IsType<CompanionsPreviewViewModel>(map.Companions);
        Assert.Empty(project.Journal.Applied); // nothing goes before the list is answered
        Assert.Equal(FireplaceCompanions, preview.Rows.Where(r => !r.IsGreyed).Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(preview.Rows.Where(r => !r.IsGreyed), r => Assert.True(r.IsChecked, r.Name));
        Assert.False(Row(preview, "Glow_1").CanToggle); // attached and mesh-less: it would hang in the air
        var ambience = Assert.Single(preview.Rows, r => r.IsGreyed);
        Assert.Equal("Camp_Ambience", ambience.Name); // nearer to the house: the house's
        Assert.False(ambience.IsChecked);
        Assert.Contains("BP_House_1", ambience.RuleText, StringComparison.Ordinal);
        Assert.True(preview.HasShared);
        Assert.DoesNotContain(preview.Rows, r => r.Name is "Lamp_1" or "DirectionalLight_1" or "WorldSettings" or "Smoke_Volume");
        Assert.Contains(Actor(map, "FireLight_1").SelectableId, map.HighlightIds); // shown in the view while the list is open
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms"); // the UI-thread search, with the first-call JIT

        preview.DeleteAllCommand.Execute(null);
        Assert.Null(map.Companions);
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Contains(batch.Ops, o => o is DeleteActorOp { Target.Actor: "SM_Fireplace_1" });
        var state = project.State;
        foreach (var name in FireplaceCompanions.Append("SM_Fireplace_1"))
        {
            Assert.True(state.IsDeleted(Ref(name)), name);
        }

        Assert.False(state.IsDeleted(Ref("Camp_Ambience")));
        Assert.False(state.IsDeleted(Ref("BP_House_1")));

        // One Ctrl+Z brings them all back.
        ctx.Services.Projects.Undo();
        Assert.All(FireplaceCompanions.Append("SM_Fireplace_1"), name => Assert.False(project.State.IsDeleted(Ref(name)), name));
    }

    [Fact]
    public async Task ShiftDeleteAndTheSettingOffDeleteTheObjectAlone()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var project = ctx.Services.Projects.Current!;
        map.SelectedActor = Actor(map, "SM_Fireplace_1");

        Assert.True(map.DeleteSelectedAloneCommand.CanExecute(null));
        map.DeleteSelectedAloneCommand.Execute(null);
        Assert.Null(map.Companions);
        Assert.Equal(Ref("SM_Fireplace_1"), Assert.IsType<DeleteActorOp>(Assert.Single(project.Journal.Applied).Op).Target);
        Assert.False(project.State.IsDeleted(Ref("FireLight_1")));
        ctx.Services.Projects.Undo();

        // The Settings switch off: Delete is the old delete of the object alone.
        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { DeleteWithCompanions = false } });
        map.DeleteSelectedCommand.Execute(null);
        Assert.Null(map.Companions);
        Assert.IsType<DeleteActorOp>(project.Journal.Applied[^1].Op);
        Assert.False(project.State.IsDeleted(Ref("P_Fire_1")));

        // "Only this object" in the list does the same.
        ctx.Services.Projects.Undo();
        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { DeleteWithCompanions = true } });
        map.DeleteSelectedCommand.Execute(null);
        map.Companions!.OnlyThisCommand.Execute(null);
        Assert.IsType<DeleteActorOp>(project.Journal.Applied[^1].Op);
        Assert.False(project.State.IsDeleted(Ref("Fire_Sound_1")));

        // Esc in the list deletes nothing.
        ctx.Services.Projects.Undo();
        var applied = project.Journal.Applied.Count;
        map.DeleteSelectedCommand.Execute(null);
        map.Companions!.CancelCommand.Execute(null);
        Assert.Null(map.Companions);
        Assert.Equal(applied, project.Journal.Applied.Count);
    }

    [Fact]
    public async Task DeletingTheHousesFireplacePartNeutralizesItsOwnFire()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var project = ctx.Services.Projects.Current!;
        map.PickParts = true;
        var house = Actor(map, "BP_House_1");
        map.SelectedInstanceKey = InstanceKey.Of(house.SelectableId, "Fireplace", InstanceKey.Part); // the viewport sets the part first
        map.SelectedActorId = house.SelectableId;
        Assert.True(map.HasSelectedPart);

        map.DeleteSelectedCommand.Execute(null);
        var preview = Assert.IsType<CompanionsPreviewViewModel>(map.Companions);
        Assert.Equal(["BP_House_1.Fire", "BP_House_1.FireLight", "BP_House_1.FireSound"], preview.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        preview.DeleteAllCommand.Execute(null);

        // The fireplace is scaled to nothing as before; its own fire, light and sound are deleted components of the kept house.
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal(2, batch.Ops.Count);
        var part = Assert.IsType<SetTransformOp>(batch.Ops[0]);
        Assert.Equal("Fireplace", part.Component);
        Assert.Equal(FVector.Zero, part.New.Scale);
        var components = Assert.IsType<DeleteComponentsOp>(batch.Ops[1]);
        Assert.Equal(Ref("BP_House_1"), components.Target);
        Assert.Equal(["Fire", "FireLight", "FireSound"], components.Components.Order(StringComparer.Ordinal));
        var state = project.State;
        Assert.False(state.IsDeleted(Ref("BP_House_1")));
        Assert.True(state.IsDeleted(Ref("BP_House_1"), "Fire"));
        Assert.False(state.IsDeleted(Ref("BP_House_1"), "PorchLight"));
        Assert.False(state.IsDeleted(Ref("BP_House_1"), "Walls"));

        // History: Go to finds the house's first deleted component.
        Assert.Equal((Ref("BP_House_1"), "Fireplace", InstanceKey.Part), MapPageViewModel.TargetOf(batch));
        Assert.Equal((Ref("BP_House_1"), "Fire", InstanceKey.Part), MapPageViewModel.TargetOf(components));

        ctx.Services.Projects.Undo();
        Assert.False(project.State.IsDeleted(Ref("BP_House_1"), "Fire"));
    }

    [Fact]
    public async Task BrushAndCtrlClickDeleteTakeCompanionsToo()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var project = ctx.Services.Projects.Current!;

        // Ctrl+click: the fireplace and the far lamp.
        map.SelectedActor = Actor(map, "SM_Fireplace_1");
        map.ToggleGroup(Actor(map, "Lamp_1").SelectableId, null);
        Assert.True(map.HasGroup);
        await map.DeleteKindSelectionAsync();
        var preview = Assert.IsType<CompanionsPreviewViewModel>(map.Companions);
        Assert.Equal(FireplaceCompanions, preview.Rows.Where(r => !r.IsGreyed).Select(r => r.Name).Order(StringComparer.Ordinal));
        preview.DeleteAllCommand.Execute(null);
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal(8, batch.Ops.OfType<DeleteActorOp>().Count()); // the two picked and the six that belong to the fireplace
        Assert.All(FireplaceCompanions.Append("Lamp_1"), name => Assert.True(project.State.IsDeleted(Ref(name)), name));
        Assert.False(map.HasGroup);
        ctx.Services.Projects.Undo();

        // Shift+Delete on a group: the members alone.
        map.SelectedActor = Actor(map, "SM_Fireplace_1");
        map.ToggleGroup(Actor(map, "Lamp_1").SelectableId, null);
        await map.DeleteKindSelectionAsync(alone: true);
        Assert.Null(map.Companions);
        Assert.Equal(2, Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op).Ops.Count);
        Assert.False(project.State.IsDeleted(Ref("FireLight_1")));
        ctx.Services.Projects.Undo();

        // The brush over the fireplace: what the circle missed still goes with it.
        map.BrushRadius = 1;
        map.BrushAt(new FVector(0f, 0f, 0f));
        Assert.True(map.HasGroup);
        await map.DeleteKindSelectionAsync();
        map.Companions?.DeleteAllCommand.Execute(null);
        Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op);
        Assert.All(FireplaceCompanions.Append("SM_Fireplace_1"), name => Assert.True(project.State.IsDeleted(Ref(name)), name));
        Assert.False(project.State.IsDeleted(Ref("Camp_Ambience")));
    }

    [Fact]
    public async Task ALargeSelectionIsSearchedOnAWorker()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var project = ctx.Services.Projects.Current!;

        // Nine objects (more than the UI thread searches at once): what is picked is never listed again.
        string[] picked = ["SM_Fireplace_1", "Lamp_1", "BP_House_1", "Camp_Ambience", "FireLight_1", "P_Fire_1", "Ash_Decal_1", "DirectionalLight_1", "Smoke_Volume"];
        map.SelectedActor = Actor(map, picked[0]);
        foreach (var name in picked.Skip(1))
        {
            map.ToggleGroup(Actor(map, name).SelectableId, null);
        }

        await map.DeleteKindSelectionAsync();
        Assert.Equal(string.Empty, map.LoadStatus);
        var preview = Assert.IsType<CompanionsPreviewViewModel>(map.Companions);
        Assert.Equal(["Fire_Sound_1", "Glow_1", "Rock_1"], preview.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        preview.DeleteAllCommand.Execute(null);
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal(12, batch.Ops.Count);
        Assert.All(picked.Concat(["Fire_Sound_1", "Glow_1", "Rock_1"]), name => Assert.True(project.State.IsDeleted(Ref(name)), name));
    }

    [Fact]
    public async Task AHiddenOrHelperMeshNoLongerBlocksTheWholeDelete()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        WriteBarrelLevel(game);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Barrel");
        await map.LoadLevelsAsync([BarrelLevelPath]);
        map.PickParts = true;
        var barrel = map.AllActors.Single(a => a.Name == "BP_Barrel_1");
        map.SelectedInstanceKey = InstanceKey.Of(barrel.SelectableId, "Barrel", InstanceKey.Part);
        map.SelectedActorId = barrel.SelectableId;
        Assert.True(map.HasSelectedPart);

        // Its only drawn part: the collision box (hidden) and the editor sphere (a helper) are never drawn, so they keep
        // nothing standing; the whole actor goes, its flames with it.
        map.DeleteSelectedCommand.Execute(null);
        map.Companions?.DeleteAllCommand.Execute(null);
        var op = ctx.Services.Projects.Current!.Journal.Applied[^1].Op;
        Assert.True(op is DeleteActorOp { Target.Actor: "BP_Barrel_1" }, op.Describe());
    }

    [Fact]
    public async Task WaterOverKeepsMeshlessSoundActorsOverLoadedTiles()
    {
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        SyntheticCampLevel.WriteCampContent(content);
        var tile = new WorldTileInfo((0, 0, 0), new FVector(-500, -500, -100), new FVector(900, 900, 500), true,
            new WorldTileLayer("Landscape", 0, (0, 0), 20000, true), false, "None", [], 0);
        await SyntheticLevels.BuildLevel(tile).WriteAsync(Path.Combine([content, .. (SyntheticLevels.MapsFolder + "/Landscape_A_0_1").Split('/')]), ".umap");
        using var catalog = AssetCatalog.OpenLoose(content);
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        var landscape = world.Packages.Single(p => p.Name == "Landscape_A_0_1").PackagePath;
        var water = LevelDocument.Load(new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), NullLogger.Instance), SyntheticCampLevel.LevelPath);

        var shown = MapPageViewModel.WaterOver(water, world, [landscape]);
        Assert.NotNull(shown);
        var names = shown.Actors.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SM_Fireplace_1", names); // a mesh over the tile, as before
        Assert.Contains("Fire_Sound_1", names); // a lake's sound has no mesh: it is shown, picked and deleted with its water
        Assert.Contains("Camp_Ambience", names);
        Assert.Contains("P_Fire_1", names);
        Assert.DoesNotContain("Lamp_1", names); // 10 m out, past the tile
        Assert.DoesNotContain("WorldSettings", names);
        Assert.Null(MapPageViewModel.WaterOver(water, world, ["/Game/ConZ_Files/Maps/The_Island/Elsewhere"]));
    }

    [Fact]
    public async Task DeletedBuildingsChildActorsAreHiddenOnTheMap()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenCampAsync(ctx);
        var glow = Actor(map, "Glow_1");
        var rock = Actor(map, "Rock_1");

        // Deleted alone (an old journal, MCP, Shift+Delete): the export takes the mesh-less light attached to it along, so
        // the map hides it too; the rock with a mesh of its own stays where it stands, shown.
        map.SelectedActor = Actor(map, "SM_Fireplace_1");
        map.DeleteSelectedAloneCommand.Execute(null);
        Assert.Contains(glow.SelectableId, map.HiddenActorIds);
        Assert.DoesNotContain(rock.SelectableId, map.HiddenActorIds);
        Assert.False(glow.IsDeleted); // hidden, not deleted by name

        ctx.Services.Projects.Undo();
        map.RefreshHiddenIds();
        Assert.DoesNotContain(glow.SelectableId, map.HiddenActorIds);
    }

    [Fact]
    public async Task WhatHangsUnderAPartsFireNeverStopsItsDelete()
    {
        // The fireplace part's light carries an emitter actor, and a box hangs under its fire (a Blueprint's damage box).
        // Before, the light could not go while the emitter was not deleted yet, nor the fire with its box: "Delete failed",
        // and not even the part was deleted.
        using var ctx = AppTestContext.Create();
        using var map = await OpenHouseAsync(ctx);
        var project = ctx.Services.Projects.Current!;
        map.PickParts = true;
        var house = Actor(map, "BP_House_1");
        map.SelectedInstanceKey = InstanceKey.Of(house.SelectableId, "Fireplace", InstanceKey.Part);
        map.SelectedActorId = house.SelectableId;

        map.DeleteSelectedCommand.Execute(null);
        var preview = Assert.IsType<CompanionsPreviewViewModel>(map.Companions);
        Assert.Equal(["BP_House_1.Fire", "BP_House_1.FireLight", "BP_House_1.FireSound", "Sparks_1"], preview.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        preview.DeleteAllCommand.Execute(null);

        // The part, the emitter first, then the light and the sound; the fire stays with the box that hangs on it.
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal(3, batch.Ops.Count);
        Assert.Equal("Fireplace", Assert.IsType<SetTransformOp>(batch.Ops[0]).Component);
        Assert.Equal(HouseRef("Sparks_1"), Assert.IsType<DeleteActorOp>(batch.Ops[1]).Target);
        Assert.Equal(["FireLight", "FireSound"], Assert.IsType<DeleteComponentsOp>(batch.Ops[2]).Components.Order(StringComparer.Ordinal));
        Assert.False(project.State.IsDeleted(HouseRef("BP_House_1"), "Fire"));
        Assert.False(project.State.IsDeleted(HouseRef("BP_House_1"), "FireBox"));
        Assert.Contains("3", batch.Title, StringComparison.Ordinal); // the emitter, the light and the sound
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message.Contains("BP_House_1.Fire", StringComparison.Ordinal)); // the fire kept is said
        Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Title == "Delete failed");

        ctx.Services.Projects.Undo();
        Assert.False(project.State.IsDeleted(HouseRef("Sparks_1")));
        Assert.False(project.State.IsDeleted(HouseRef("BP_House_1"), "FireLight"));
    }

    [Fact]
    public async Task ADeletedBuildingsChildActorIsLeftToTheExport()
    {
        // A stored child actor (the house's door) is spawned by its building: the export takes it along with the building,
        // and deleting it by name would keep it deleted if the building were ever brought back on its own.
        using var ctx = AppTestContext.Create();
        using var map = await OpenHouseAsync(ctx);
        var project = ctx.Services.Projects.Current!;
        map.SelectedActor = Actor(map, "BP_House_1");

        map.DeleteSelectedCommand.Execute(null);
        Assert.Null(map.Companions); // only structural things go: at once
        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal([HouseRef("BP_House_1"), HouseRef("Sparks_1")], batch.Ops.Select(o => Assert.IsType<DeleteActorOp>(o).Target));
        Assert.False(project.State.IsDeleted(HouseRef("Door_1")));
        Assert.Contains(Actor(map, "Door_1").SelectableId, map.HiddenActorIds); // the map shows what the game will have

        ctx.Services.Projects.Undo();
        map.RefreshHiddenIds();
        Assert.DoesNotContain(Actor(map, "Door_1").SelectableId, map.HiddenActorIds);
    }

    [Fact]
    public void ARepaintedEditorOnlyPartIsDrawn()
    {
        // A WorldGrid part the placement repaints is drawn by the map as a variant: it keeps its actor standing.
        var b = new FakeLevelBuilder(BenchLevel(0));
        var actor = b.Actor("BP_Hut_1", "/Game/BP_Hut.BP_Hut_C", blueprint: true);
        var root = b.Component(actor, "DefaultSceneRoot");
        b.Root(actor, root);
        b.Component(actor, "Floor", "StaticMeshComponent", attachTo: root, mesh: GridMesh);
        var painted = b.Component(actor, "Roof", "StaticMeshComponent", attachTo: root, mesh: GridMesh);
        var data = b.Build();
        var hut = LevelDocument.FromData(data with { Exports = data.Exports.Select(e => e.Index == painted ? e with { OverrideMaterials = ["/Game/M_Roof.M_Roof"] } : e).ToList() })
            .FindActor("BP_Hut_1")!;
        float[] positions = [0, 0, 0, 100, 0, 0, 0, 100, 0];
        var editorOnly = new PreparedMeshAsset(GridMesh, MeshData.Create("Grid", positions, [0, 1, 2], sections: [new MeshSection("/Engine/M_WorldGrid.M_WorldGrid", 0, 3)]), null)
        {
            IsEditorOnly = true,
        };

        Assert.False(MapPageViewModel.DrawnByMap(hut, hut.FindComponent("Floor")!, _ => editorOnly));
        Assert.True(MapPageViewModel.DrawnByMap(hut, hut.FindComponent("Roof")!, _ => editorOnly));
        Assert.True(MapPageViewModel.DrawnByMap(hut, hut.FindComponent("Floor")!, _ => null)); // not loaded: it may be drawn
        Assert.False(MapPageViewModel.DrawnByMap(hut, hut.FindComponent("DefaultSceneRoot")!, _ => null));
    }

    [Fact]
    public void OnALargeWorldTheSearchAndTheHiddenRefreshStayWithinAFrame()
    {
        // 100 levels of 500 actors (meshes, Blueprints with a light and a fire, lights, Wwise sounds): 50,000 actors.
        var levels = Enumerable.Range(0, 100).Select(BenchDocument).ToList();
        var seed = new[] { new DeleteSeed(new ActorRef(BenchLevel(0), "Mesh_5")) };
        var state = new EditState();
        static double Fastest(Action run)
        {
            run(); // JIT
            var best = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                run();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        // One delete: up to InlineActors loaded actors the search runs on the UI thread, inside a 60 Hz frame; more go to a worker.
        var inline = levels.Take(MapPageViewModel.InlineActors / 500).ToList();
        Assert.True(MapPageViewModel.SearchesInline(1, inline));
        Assert.False(MapPageViewModel.SearchesInline(1, levels));
        Assert.False(MapPageViewModel.SearchesInline(9, [levels[0]]));
        var found = Companions.Find(inline, seed, state, null);
        Assert.Contains(found.Companions, c => c.Actor.Actor == "Light_5");
        var inlineMs = Fastest(() => Companions.Find(inline, seed, state, null));
        var wholeMs = Fastest(() => Companions.Find(levels, seed, state, null));
        _output.WriteLine($"Companions.Find, one seed: {inlineMs:0.00} ms over {MapPageViewModel.InlineActors:N0} actors, {wholeMs:0.00} ms over 50,000 (worker)");
        Assert.True(inlineMs < 16, $"{inlineMs:0.00} ms for {MapPageViewModel.InlineActors:N0} actors");

        // The map's hidden refresh: a deleted Blueprint in every level hides the lamp attached to it. After an edit, only the
        // edited level is walked again.
        var cache = new ConditionalWeakTable<LevelDocument, MapPageViewModel.HiddenByDeletions>();
        Dictionary<LevelDocument, MapPageViewModel.LevelDeletions> Deletions(bool secondInFirstLevel)
        {
            var result = new Dictionary<LevelDocument, MapPageViewModel.LevelDeletions>(ReferenceEqualityComparer.Instance);
            foreach (var level in levels)
            {
                var gone = new MapPageViewModel.LevelDeletions();
                gone.Actors.Add("BP_0");
                if (secondInFirstLevel && ReferenceEquals(level, levels[0]))
                {
                    gone.Actors.Add("BP_1");
                }

                result[level] = gone;
            }

            return result;
        }

        var clock = Stopwatch.StartNew();
        var hidden = MapPageViewModel.HiddenWith(Deletions(false), cache).ToList();
        var coldMs = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(100, hidden.Count);
        Assert.All(hidden, h => Assert.Equal("Lamp_0", h.Actor));
        Assert.Equal(101, MapPageViewModel.HiddenWith(Deletions(true), cache).Count());
        var edited = false;
        var editMs = Fastest(() =>
        {
            edited = !edited;
            _ = MapPageViewModel.HiddenWith(Deletions(edited), cache).Count();
        });
        _output.WriteLine($"Hidden refresh over 100 levels with deletions: {coldMs:0.00} ms cold, {editMs:0.00} ms after an edit");
        Assert.True(editMs < 16, $"{editMs:0.00} ms after one edit");
    }

    private const string BarrelLevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_Barrel";
    private const string HouseLevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_House";
    private const string GridMesh = "/Game/ConZ_Files/Editor/SM_Grid.SM_Grid";
    private const string BenchRock = "/Game/ConZ_Files/Models/Rocks/SM_Rock.SM_Rock";

    private readonly ITestOutputHelper _output;

    public MapCompanionsDeleteTests(ITestOutputHelper output) => _output = output;

    private static string BenchLevel(int i) => $"/Game/ConZ_Files/Maps/The_Island/Bench_{i}";

    /// <summary>
    /// 500 actors 3 m apart in a 100 m square of its own: 300 rocks, 100 Blueprints (two parts, a light and a fire), 10 lamps
    /// attached to the first Blueprints, 40 lights and 50 Wwise sounds by the rocks.
    /// </summary>
    private static LevelDocument BenchDocument(int index)
    {
        var b = new FakeLevelBuilder(BenchLevel(index));
        var origin = new FVector(index % 10 * 10000, index / 10 * 10000, 0);
        FVector At(int n) => origin + new FVector(n % 33 * 300, n / 33 * 300, 0);
        for (var i = 0; i < 300; i++)
        {
            var mesh = b.Actor($"Mesh_{i}", "/Script/Engine.StaticMeshActor");
            b.Root(mesh, b.Component(mesh, "StaticMeshComponent0", "StaticMeshComponent", At(i), mesh: BenchRock));
        }

        for (var i = 0; i < 100; i++)
        {
            var bp = b.Actor($"BP_{i}", "/Game/BP_Shed.BP_Shed_C", blueprint: true);
            var root = b.Component(bp, "DefaultSceneRoot", location: At(300 + i));
            b.Root(bp, root);
            b.Component(bp, "Walls", "StaticMeshComponent", attachTo: root, mesh: BenchRock);
            b.Component(bp, "Roof", "StaticMeshComponent", new FVector(0, 0, 300), attachTo: root, mesh: BenchRock);
            b.Component(bp, "Light", "PointLightComponent", new FVector(0, 0, 200), attachTo: root);
            b.Component(bp, "Fire", "ParticleSystemComponent", attachTo: root);
            if (i < 10)
            {
                var lamp = b.Actor($"Lamp_{i}", "/Script/Engine.PointLight");
                b.Root(lamp, b.Component(lamp, "LightComponent0", "PointLightComponent", new FVector(0, 0, 250), attachTo: root));
            }
        }

        for (var i = 0; i < 40; i++)
        {
            var light = b.Actor($"Light_{i}", "/Script/Engine.PointLight");
            b.Root(light, b.Component(light, "LightComponent0", "PointLightComponent", At(i) + new FVector(0, 0, 60)));
        }

        for (var i = 0; i < 50; i++)
        {
            var sound = b.Actor($"Sound_{i}", "/Script/AkAudio.AkAmbientSound");
            b.Root(sound, b.Component(sound, "AkComponent0", "AkComponent", At(i) + new FVector(80, 0, 0)));
        }

        return LevelDocument.FromData(b.Build());
    }

    private static ActorRef HouseRef(string actor) => new(HouseLevelPath, actor);

    private static async Task<MapPageViewModel> OpenHouseAsync(AppTestContext ctx)
    {
        var game = ctx.Combine("game");
        WriteHouseLevel(game);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "House");
        await map.LoadLevelsAsync([HouseLevelPath]);
        return map;
    }

    /// <summary>
    /// A house Blueprint at the origin: walls, a fireplace part (y 300) with its fire under it and a box under the fire, its
    /// light and sound beside it, a door it spawns as a stored child actor, and an emitter actor hung on the fire's light.
    /// </summary>
    private static void WriteHouseLevel(string root)
    {
        const string engine = "/Script/Engine";
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_House", p.ScriptClass(engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(engine, "Level"), world);
        var fireplaceMesh = p.ObjectImport(SyntheticCampLevel.FireplaceMesh, engine, "StaticMesh", "SM_Fireplace_01");
        var wallsMesh = p.ObjectImport(SyntheticCampLevel.WallsMesh, engine, "StaticMesh", "SM_House_Walls");
        var meshClass = p.ScriptClass(engine, "StaticMeshComponent");
        var lightClass = p.ScriptClass(engine, "PointLightComponent");
        var particleClass = p.ScriptClass(engine, "ParticleSystemComponent");
        var house = p.Export("BP_House_1", p.ScriptClass(engine, "Actor"), level);
        var houseRoot = p.Export("DefaultSceneRoot", p.ScriptClass(engine, "SceneComponent"), house);
        var walls = p.Export("Walls", meshClass, house);
        var fireplace = p.Export("Fireplace", meshClass, house);
        var fire = p.Export("Fire", particleClass, house);
        var fireBox = p.Export("FireBox", p.ScriptClass(engine, "BoxComponent"), house);
        var fireLight = p.Export("FireLight", lightClass, house);
        var fireSound = p.Export("FireSound", p.ScriptClass("/Script/AkAudio", "AkComponent"), house);
        var doorSlot = p.Export("DoorSlot", p.ScriptClass(engine, "ChildActorComponent"), house);
        var door = p.Export("Door_1", p.ScriptClass(engine, "Actor"), level);
        var doorRoot = p.Export("Leaf", meshClass, door);
        var sparks = p.Export("Sparks_1", p.ScriptClass(engine, "Emitter"), level);
        var sparksRoot = p.Export("ParticleSystemComponent0", particleClass, sparks);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, "A_0_House", [house, door, sparks])));
        p.SetPayload(house, p.Properties(t => t.Object("RootComponent", houseRoot)));
        p.SetPayload(houseRoot, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 0)));
        void Part(int component, int parent, Action<SyntheticLevelPackage.TagWriter> tags, bool mesh = false) =>
            p.SetPayload(component, p.Properties(t =>
            {
                t.Object("AttachParent", parent);
                tags(t);
            }, mesh ? w => w.I32(0) : null));
        Part(walls, houseRoot, t => t.Object("StaticMesh", wallsMesh), mesh: true);
        Part(fireplace, houseRoot, t =>
        {
            t.Object("StaticMesh", fireplaceMesh);
            t.Vector("RelativeLocation", 0, 300, 0);
        }, mesh: true);
        Part(fire, fireplace, _ => { });
        Part(fireBox, fire, t => t.Vector("RelativeLocation", 0, 0, 10));
        Part(fireLight, houseRoot, t => t.Vector("RelativeLocation", 0, 300, 60));
        Part(fireSound, houseRoot, t => t.Vector("RelativeLocation", 0, 300, 0));
        Part(doorSlot, houseRoot, t =>
        {
            t.Object("ChildActor", door);
            t.Vector("RelativeLocation", 0, -100, 0);
        });
        p.SetPayload(door, p.Properties(t =>
        {
            t.Object("RootComponent", doorRoot);
            t.Object("ParentComponent", doorSlot);
        }));
        Part(doorRoot, doorSlot, t => t.Object("StaticMesh", wallsMesh), mesh: true);
        p.SetPayload(sparks, p.Properties(t => t.Object("RootComponent", sparksRoot)));
        Part(sparksRoot, fireLight, t => t.Vector("RelativeLocation", 0, 0, 20));
        p.Build().WriteAsync(Path.Combine([root, .. (SyntheticLevels.MapsFolder + "/A_0_House").Split('/')]), ".umap").GetAwaiter().GetResult();
    }

    private static async Task<MapPageViewModel> OpenCampAsync(AppTestContext ctx)
    {
        var game = ctx.Combine("game");
        SyntheticCampLevel.WriteCampContent(game);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Camp");
        await map.LoadLevelsAsync([SyntheticCampLevel.LevelPath]);
        return map;
    }

    private static ActorItemViewModel Actor(MapPageViewModel map, string name) => map.AllActors.Single(a => a.Name == name);

    private static CompanionRowViewModel Row(CompanionsPreviewViewModel preview, string name) => preview.Rows.Single(r => r.Name == name);

    private static ActorRef Ref(string actor) => new(SyntheticCampLevel.LevelPath, actor);

    /// <summary>A fire barrel Blueprint: one drawn mesh, a hidden collision mesh and an editor sphere (both never drawn).</summary>
    private static void WriteBarrelLevel(string root)
    {
        const string engine = "/Script/Engine";
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Barrel", p.ScriptClass(engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(engine, "Level"), world);
        var rock = p.ObjectImport(SyntheticLevels.RockPackage, engine, "StaticMesh", "SM_Rock");
        var sphere = p.ObjectImport("/Engine/EditorMeshes/EditorSphere", engine, "StaticMesh", "EditorSphere");
        var meshClass = p.ScriptClass(engine, "StaticMeshComponent");
        var barrel = p.Export("BP_Barrel_1", p.ScriptClass(engine, "Actor"), level);
        var root_ = p.Export("DefaultSceneRoot", p.ScriptClass(engine, "SceneComponent"), barrel);
        var drawn = p.Export("Barrel", meshClass, barrel);
        var collision = p.Export("Collision", meshClass, barrel);
        var editorSphere = p.Export("EditorSphere", meshClass, barrel);
        var flames = p.Export("Flames", p.ScriptClass(engine, "ParticleSystemComponent"), barrel);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, "A_0_Barrel", [barrel])));
        p.SetPayload(barrel, p.Properties(t => t.Object("RootComponent", root_)));
        p.SetPayload(root_, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 0)));
        p.SetPayload(drawn, p.Properties(t =>
        {
            t.Object("AttachParent", root_);
            t.Object("StaticMesh", rock);
        }, w => w.I32(0)));
        p.SetPayload(collision, p.Properties(t =>
        {
            t.Object("AttachParent", root_);
            t.Object("StaticMesh", rock);
            t.Bool("bVisible", false);
        }, w => w.I32(0)));
        p.SetPayload(editorSphere, p.Properties(t =>
        {
            t.Object("AttachParent", root_);
            t.Object("StaticMesh", sphere);
        }, w => w.I32(0)));
        p.SetPayload(flames, p.Properties(t => t.Object("AttachParent", root_)));
        p.Build().WriteAsync(Path.Combine([root, .. (SyntheticLevels.MapsFolder + "/A_0_Barrel").Split('/')]), ".umap").GetAwaiter().GetResult();
    }

    /// <summary>ULevel native data after the tagged properties (as <see cref="SyntheticLevels"/> writes it).</summary>
    private static void LevelNative(ByteWriter w, string levelName, IReadOnlyList<int> actors)
    {
        w.I32(actors.Count);
        foreach (var a in actors)
        {
            w.I32(a);
        }

        w.FString("unreal"); // FURL
        w.FString(string.Empty);
        w.FString(levelName);
        w.FString(string.Empty);
        w.I32(0); // Op
        w.I32(7777); // Port
        w.I32(1); // Valid
        for (var i = 0; i < 5; i++)
        {
            w.I32(0); // Model, ModelComponents, LevelScriptActor, NavListStart, NavListEnd
        }

        for (var i = 0; i < 4; i++)
        {
            w.F32(0);
        }

        w.I32(0);
        w.I32(0);
        w.I32(0);
        for (var i = 0; i < 7; i++)
        {
            w.F32(0);
        }

        w.U8(0);
        for (var i = 0; i < 4; i++)
        {
            w.I32(0);
        }
    }
}
