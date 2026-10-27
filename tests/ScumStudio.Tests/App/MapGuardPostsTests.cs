using Avalonia.Headless.XUnit;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-26): "a constructor for spawns like the dungeon's guards, placed on the 3D model of the map". The Map page's
/// guard tool puts posts where the 3D view's click lands (the floor height can be fixed for a building seen through its
/// roof), keeps them as journal edits (undo), draws them in the viewport and writes them as a server Lua mod.
/// </summary>
public sealed class MapGuardPostsTests
{
    private static async Task<(MapPageViewModel Map, ScumStudio.Level.Projects.Project Project)> OpenAsync(AppTestContext ctx)
    {
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Guards");
        return (new MapPageViewModel(ctx.Services), project);
    }


    private static async Task<(MapPageViewModel Map, ScumStudio.Level.Projects.Project Project)> OpenWithGroundAsync(AppTestContext ctx)
    {
        var game = ctx.Combine("game");
        ScumStudio.Tests.Level.SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Guards");
        await map.LoadLevelsAsync([ScumStudio.Tests.Level.SyntheticLevels.LevelPath]);
        // Flat ground 40 cm high around the origin.
        map.WorldBackdrop = new ScumStudio.Viewport.PreparedLevelScene([], [], new Dictionary<string, ScumStudio.Viewport.PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [], [], [], TimeSpan.Zero)
        {
            HeightField = new ScumStudio.Viewport.TerrainHeightField([(ScumStudio.Tests.Assets.LandscapeExtractorTests.Ramp(20, 100f, new System.Numerics.Vector2(-1000f, -1000f), (_, _) => 40f), null)]),
        };
        return (map, project);
    }

    [Fact]
    public async Task ClicksMakeASetOfPostsThatUndoes()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.GuardTool = true;

        map.GuardLevel = 5;
        map.PlaceGuardPost(new FVector(100f, 200f, 8301f));
        map.GuardLevel = 3;
        map.PlaceGuardPost(new FVector(400f, 500f, 8301f));

        var set = Assert.Single(project.State.GuardSets).Value;
        Assert.Equal("Guards", set.Name);
        Assert.Equal([5, 3], set.Posts.Select(p => p.Level));
        Assert.Equal(new FVector(400f, 500f, 8301f), set.Posts[1].Position);
        Assert.Equal(["Guards"], map.GuardSetNames);
        Assert.Equal(2, map.GuardPostRows.Count);
        Assert.Equal(1, map.SelectedGuardPost!.Index); // the last post is the picked one
        Assert.Equal("Posts: 2", map.GuardPostsText);

        ctx.Services.Projects.Undo();
        Assert.Single(project.State.GuardSets.Values.Single().Posts);
        Assert.Single(map.GuardPostRows);

        ctx.Services.Projects.Undo();
        Assert.Empty(project.State.GuardSets);
        Assert.Empty(map.GuardSetNames);
        Assert.Empty(map.GuardPostRows);
    }

    [Fact]
    public async Task ThePickedPostsLevelAndTheTimingsAreEdits()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.PlaceGuardPost(new FVector(0f, 0f, 100f));
        var before = project.History.Count;

        map.SelectedGuardPostLevel = 5;
        Assert.Equal(5, project.State.GuardSets["Guards"].Posts[0].Level);
        map.GuardReachMeters = 60;
        map.GuardIdleSeconds = 120;
        map.GuardRespawnSeconds = 45;

        var set = project.State.GuardSets["Guards"];
        Assert.Equal((6000f, 120, 45), (set.Reach, set.IdleSeconds, set.RespawnSeconds));
        Assert.Equal(before + 4, project.History.Count); // one undo step each

        map.GuardReachMeters = 60; // no change: no edit
        Assert.Equal(before + 4, project.History.Count);
    }

    [Fact]
    public async Task AFixedFloorHeightSetsEveryPostsZ()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.GuardFloorMode = GuardFloorMode.Fixed;
        map.GuardFloorMeters = 83;

        map.PlaceGuardPost(new FVector(10f, 20f, 8934f)); // the roof's height under the cursor

        Assert.Equal(new FVector(10f, 20f, 8300f), project.State.GuardSets["Guards"].Posts[0].Position);
    }

    [Fact]
    public async Task RemovingThePickedPostAndTheLastOneTakesTheSetAway()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.PlaceGuardPost(new FVector(1f, 1f, 1f));
        map.PlaceGuardPost(new FVector(2f, 2f, 2f));
        map.SelectedGuardPost = map.GuardPostRows[0];

        map.RemoveGuardPostCommand.Execute(null);
        Assert.Equal(new FVector(2f, 2f, 2f), Assert.Single(project.State.GuardSets["Guards"].Posts).Position);

        map.RemoveGuardPostCommand.Execute(null);
        Assert.Empty(project.State.GuardSets);

        map.PlaceGuardPost(new FVector(3f, 3f, 3f));
        map.NewGuardSetCommand.Execute(null);
        Assert.Equal("Guards2", map.SelectedGuardSet); // the next click starts it
        map.PlaceGuardPost(new FVector(4f, 4f, 4f));
        Assert.Equal(["Guards", "Guards2"], project.State.GuardSets.Keys.Order());

        map.DeleteGuardSetCommand.Execute(null);
        Assert.Equal(["Guards"], project.State.GuardSets.Keys);
    }

    [AvaloniaFact]
    public async Task TheToolTakesTheCursorFromTheOtherToolsAndDrawsThePosts()
    {
        using var ctx = AppTestContext.Create();
        var (map, _) = await OpenAsync(ctx);
        map.HoleTool = true;

        map.GuardTool = true;

        Assert.False(map.HoleTool);
        Assert.True(map.ViewportBrushMode);
        map.PlaceGuardPost(new FVector(0f, 0f, 100f));
        map.PlaceGuardPost(new FVector(1000f, 0f, 100f));
        map.HoverGuard(new FVector(500f, 0f, 100f));

        var overlay = map.GuardOverlay!;
        Assert.Equal([GuardMarkerKind.Normal, GuardMarkerKind.Selected, GuardMarkerKind.Ghost], overlay.Markers.Select(m => m.Kind));
        Assert.Equal(new FVector(500f, 0f, 100f), overlay.WakeCenter);
        Assert.Equal(500f + GuardSpawns.DefaultReach, overlay.WakeRadius, 1f);
        Assert.NotEmpty(LevelViewport.GuardLines(overlay));

        map.GuardTool = false;
        Assert.Null(map.GuardOverlay);
        Assert.False(map.ViewportBrushMode);
    }

    [Fact]
    public async Task ACursorDragPutsOnePostWhereItEnds()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.GuardTool = true;

        map.NoteGuardDab(new FVector(1f, 1f, 1f));
        map.NoteGuardDab(new FVector(2f, 2f, 2f));
        map.NoteGuardDab(new FVector(3f, 3f, 3f));
        map.EndGuardStroke();
        map.EndGuardStroke(); // nothing more to put

        Assert.Equal(new FVector(3f, 3f, 3f), Assert.Single(project.State.GuardSets["Guards"].Posts).Position);
    }

    [Fact]
    public async Task TheModGoesToTheServerOrTheProjectFolder()
    {
        using var ctx = AppTestContext.Create();
        var server = ctx.Combine("server");
        Directory.CreateDirectory(Path.Combine(server, "SCUM", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(server, "SCUM", "Binaries", "Win64", "SCUMServer.exe"), string.Empty);
        var (map, project) = await OpenAsync(ctx);
        map.PlaceGuardPost(new FVector(456300f, -234600f, 8301f));

        // No server folder set: nothing is written.
        map.InstallGuardModCommand.Execute(null);
        Assert.False(Directory.Exists(Path.Combine(server, "SCUM", "Binaries", "Win64", "ue4ss")));

        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = Path.Combine(server, "SCUM", "Content", "Paks") });
        map.InstallGuardModCommand.Execute(null);

        var mods = Path.Combine(server, "SCUM", "Binaries", "Win64", "ue4ss", "Mods");
        var lua = File.ReadAllText(Path.Combine(mods, GuardSpawns.ModName, "Scripts", "main.lua"));
        Assert.Contains("{ 456300, -234600, 8301, 4 },", lua, StringComparison.Ordinal);
        Assert.Contains("GuardSpawns : 1", File.ReadAllText(Path.Combine(mods, "mods.txt")), StringComparison.Ordinal);

        map.WriteGuardFileCommand.Execute(null);
        Assert.Equal(lua, File.ReadAllText(Path.Combine(project.DirectoryPath, GuardSpawns.ModName, "Scripts", "main.lua")));
    }

    /// <summary>The tool's button is on the Map page's tools; turning it on shows the bar over the viewport with the set, the posts and the buttons.</summary>
    [AvaloniaFact]
    public async Task TheBarShowsOverTheViewportWhileTheToolIsOn()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        ScumStudio.Tests.Level.SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Guards");
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await map.LoadLevelsAsync([ScumStudio.Tests.Level.SyntheticLevels.LevelPath]);
            HeadlessUi.Pump();
            var bar = HeadlessUi.FindNamed<Avalonia.Controls.Border>(window, "GuardBar")!;
            var toggle = HeadlessUi.FindNamed<Avalonia.Controls.Primitives.ToggleButton>(window, "GuardToolButton")!;
            Assert.False(bar.IsVisible);

            toggle.IsChecked = true;
            map.PlaceGuardPost(new FVector(456300f, -234600f, 8301f));
            map.PlaceGuardPost(new FVector(456800f, -234600f, 8301f));
            map.GuardLevel = 5;
            map.PlaceGuardPost(new FVector(456300f, -234100f, 8301f));
            HeadlessUi.Pump();

            Assert.True(map.GuardTool && bar.IsVisible);
            Assert.Equal(["Guards"], HeadlessUi.FindNamed<Avalonia.Controls.ComboBox>(window, "GuardSetBox")!.Items.Cast<string>());
            Assert.Equal(3, HeadlessUi.FindNamed<Avalonia.Controls.ListBox>(window, "GuardPostList")!.Items.Count);
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "GuardInstallButton")!.IsEffectivelyEnabled);
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "GuardRemovePostButton")!.IsEffectivelyEnabled);
            Assert.NotNull(HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!.GuardOverlay);
            HeadlessUi.SaveScreenshot(window, "page-map-guard-posts");

            toggle.IsChecked = false;
            HeadlessUi.Pump();
            Assert.False(bar.IsVisible);
            Assert.Null(HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!.GuardOverlay);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Fact]
    public void TheFloorsAreTheSurfacesWithRoomAboveThemAndTheTopCoveredOneIsTheDefault()
    {
        // The dungeon: a roof slab (top and underside), the floor under it, the terrain just under the floor.
        var (dungeon, pick) = GuardFloors.Choose([8935f, 8934f, 8301f, 8220f]);
        Assert.Equal([8935f, 8301f], dungeon);
        Assert.Equal(1, pick); // the floor: the topmost one with a ceiling over it

        // Open ground: the ground itself.
        var (open, openPick) = GuardFloors.Choose([40f]);
        Assert.Equal([40f], open);
        Assert.Equal(0, openPick);

        // A house of three storeys on the ground: the top floor is the default, the stepper reaches the others and the roof.
        var (house, top) = GuardFloors.Choose([1000f, 700f, 400f, 390f]);
        Assert.Equal([1000f, 700f, 400f], house);
        Assert.Equal(1, top);

        // A slab on the terrain with nothing over it, and nothing at all.
        var (slab, slabPick) = GuardFloors.Choose([8301f, 8220f]);
        Assert.Equal([8301f], slab);
        Assert.Equal(0, slabPick);
        var (none, nonePick) = GuardFloors.Choose([]);
        Assert.Empty(none);
        Assert.Equal(-1, nonePick);
    }

    [Fact]
    public async Task TheAutoFloorFindsTheHeightUnderTheClickAndTheOtherModesKeepTheirs()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenWithGroundAsync(ctx);

        // Auto (the default): the ground is 40 cm high, the click hit something at 500 cm.
        map.PlaceGuardPost(new FVector(0f, 0f, 500f));
        Assert.Equal(40f, project.State.GuardSets["Guards"].Posts[0].Position.Z);
        Assert.Equal("Floor 1 of 1: 0.4 m", map.GuardFloorText);

        map.GuardFloorMode = GuardFloorMode.Surface;
        map.PlaceGuardPost(new FVector(100f, 0f, 500f));
        Assert.Equal(500f, project.State.GuardSets["Guards"].Posts[1].Position.Z);

        map.GuardFloorMode = GuardFloorMode.Fixed;
        map.GuardFloorMeters = 12;
        map.PlaceGuardPost(new FVector(200f, 0f, 500f));
        Assert.Equal(1200f, project.State.GuardSets["Guards"].Posts[2].Position.Z);
        Assert.True(map.IsGuardFloorFixed && !map.IsGuardFloorAuto);

        // "Floor under it" sets the picked post back on the found floor: one edit.
        map.GuardFloorMode = GuardFloorMode.Auto;
        var before = project.History.Count;
        map.SnapGuardPostCommand.Execute(null);
        Assert.Equal(40f, project.State.GuardSets["Guards"].Posts[2].Position.Z);
        Assert.Equal(before + 1, project.History.Count);
    }

    [Fact]
    public async Task APostIsPickedByItsPoleDraggedAlongItsFloorAndSetByItsNumbers()
    {
        using var ctx = AppTestContext.Create();
        var (map, project) = await OpenAsync(ctx);
        map.GuardTool = true;
        map.PlaceGuardPost(new FVector(0f, 0f, 100f));
        map.PlaceGuardPost(new FVector(1000f, 0f, 100f));
        var before = project.History.Count;

        // A press on the first post's pole and a drag: it follows the cursor at its own height, and the edit comes when the button is let go.
        map.GrabGuardPost("Guards", 0);
        Assert.Equal(0, map.SelectedGuardPost!.Index);
        map.DragGuardPost(new FVector(300f, 400f, 100f));
        Assert.Equal(new FVector(300f, 400f, 100f), map.GuardOverlay!.Markers.Single(m => m.Index == 0).At); // drawn there already
        Assert.Equal(new FVector(0f, 0f, 100f), project.State.GuardSets["Guards"].Posts[0].Position); // not journaled yet
        map.EndGuardStroke();
        Assert.Equal(new FVector(300f, 400f, 100f), project.State.GuardSets["Guards"].Posts[0].Position);
        Assert.Equal(before + 1, project.History.Count);
        Assert.Equal(300.0, map.SelectedGuardPostX);

        // A press without a drag only picks the other post; a tiny move is no move either.
        map.GrabGuardPost("Guards", 1);
        map.DragGuardPost(new FVector(1005f, 0f, 100f));
        map.EndGuardStroke();
        Assert.Equal(1, map.SelectedGuardPost!.Index);
        Assert.Equal(before + 1, project.History.Count);
        Assert.Equal(2, project.State.GuardSets["Guards"].Posts.Count); // no post was added by the press

        // Typing a number moves the picked post: one undo step each.
        map.SelectedGuardPostX = 2000;
        map.SelectedGuardPostZ = 250;
        Assert.Equal(new FVector(2000f, 0f, 250f), project.State.GuardSets["Guards"].Posts[1].Position);
        Assert.Equal(before + 3, project.History.Count);
        ctx.Services.Projects.Undo();
        Assert.Equal(new FVector(2000f, 0f, 100f), project.State.GuardSets["Guards"].Posts[1].Position);
        Assert.Equal(2000.0, map.SelectedGuardPostX);
        Assert.Equal(100.0, map.SelectedGuardPostZ);
    }

    [Fact]
    public void ARayFindsThePoleItPassesNearest()
    {
        // A pole from (0,0,0) to (0,180,0) (GL: Y is up); a ray along +Z passing 50 cm beside it, 500 cm from its start.
        var (distance, along) = LevelViewport.RayToSegment(new System.Numerics.Vector3(50f, 90f, -500f), System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(0f, 180f, 0f));
        Assert.Equal(50f, distance, 0.01f);
        Assert.Equal(500f, along, 0.01f);

        // Above the pole's end the nearest point is its top.
        var (above, _) = LevelViewport.RayToSegment(new System.Numerics.Vector3(0f, 300f, -500f), System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(0f, 180f, 0f));
        Assert.Equal(120f, above, 0.01f);
    }

    [AvaloniaFact]
    public void AGuardsColourSaysHowStrongItIs()
    {
        var overlay = new GuardOverlay([new GuardMarker(FVector.Zero, 3, GuardMarkerKind.Normal), new GuardMarker(FVector.Zero, 5, GuardMarkerKind.Normal)]);

        var lines = LevelViewport.GuardLines(overlay).ToList();

        Assert.Equal(2, lines.Select(l => l.Color).Distinct().Count());
        Assert.NotEqual(LevelViewport.GuardColor(3, GuardMarkerKind.Normal), LevelViewport.GuardColor(5, GuardMarkerKind.Normal));
        Assert.Equal(1f, LevelViewport.GuardColor(4, GuardMarkerKind.Selected).X); // the picked post is white
        Assert.Equal(0.5f, LevelViewport.GuardColor(4, GuardMarkerKind.Ghost).W);
    }
}
