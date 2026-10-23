using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "click a single vehicle spawn point on the 2D map, drag it, rotate it ... a Show on the Map button ... real-file
/// tests on a real vehicle spawn point: move it, export, read back the new position". The Spawns page in the main window
/// over the real game (<c>SCUM_PAKS</c>, the key from this PC's store; pictures to <c>SCUMSTUDIO_SCREENSHOTS</c>): a real
/// world vehicle point picked with the Select point tool, dragged with the mouse onto the real terrain, turned with Q,
/// exported and read back; then Show on the Map loads its sector and selects it there.
/// </summary>
public sealed class SpawnsPointRealTests(ITestOutputHelper output)
{
    private const string PlaceName = "VehicleSpawn_00100";

    [AvaloniaFact]
    public async Task ARealVehiclePointIsDraggedTurnedExportedAndShownOnTheMap()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "SpawnMove");
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        var dir = ctx.Combine("export");
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var spawns = (SpawnsPageViewModel)vm.NavigateTo("spawns")!;
            Assert.True(HeadlessUi.PumpUntil(() => spawns.Markers.Count > 25_000 && spawns.MarkerOf(PlaceName) is not null, TimeSpan.FromSeconds(180)));
            var id = spawns.MarkerOf(PlaceName)!.Value;
            spawns.IsSelectTool = true;
            spawns.SelectedMarkerId = id;
            var stock = spawns.Point!.Value;
            Assert.Equal(PlaceName, spawns.Point.Name);
            spawns.ZoomToPointCommand.Execute(null); // 20 m around it
            HeadlessUi.Pump();
            var map = HeadlessUi.FindNamed<SpawnMap>(window, "SpawnMap")!;
            HeadlessUi.SaveScreenshot(window, "spawns-point-picked");

            // Dragged 8 m left and 5 m up on the map (+X, +Y: the map is turned like the game's, D4 top left), released: one
            // move, onto the real terrain.
            var from = map.TranslatePoint(map.ScreenOf(id)!.Value, window)!.Value; // the mouse works in the window's pixels
            var to = from - new Vector(8 * map.PixelsPerMetre, 5 * map.PixelsPerMetre);
            var history = ctx.Services.Projects.History.Count;
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + ((to - from) / 2));
            window.MouseMove(to);
            HeadlessUi.SaveScreenshot(window, "spawns-point-dragging");
            window.MouseUp(to, MouseButton.Left);
            await spawns.PointEdit;
            Assert.Equal(history + 1, ctx.Services.Projects.History.Count);

            var state = ctx.Services.Projects.Current!.State;
            var actor = new ActorRef(SpawnPlaces.StaticDataPath, PlaceName);
            var moved = state.GetTransformOverride(actor)!.Value;
            var pixel = 100f / (float)map.PixelsPerMetre; // a drag is as exact as one pixel
            Assert.Equal(stock.Location.X + 800f, moved.Location.X, pixel);
            Assert.Equal(stock.Location.Y + 500f, moved.Location.Y, pixel);
            var ground = new GroundHeights(ctx.Services.Workspace.Catalog!).At(moved.Location.X, moved.Location.Y);
            Assert.NotNull(ground);
            Assert.Equal(ground!.Value + SpawnGroundCheck.VehicleLift, moved.Location.Z, 0.5f);
            output.WriteLine($"{PlaceName}: {stock.Location} -> {moved.Location} (ground {ground:0.0})");

            // Q turns it 15 degrees the other way (one more undo step).
            Assert.True(HeadlessUi.PumpUntil(() => spawns.Point?.Value.Location == moved.Location));
            map.Focus();
            window.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Q, RawInputModifiers.None);
            Assert.Equal(history + 2, ctx.Services.Projects.History.Count);
            var turned = state.GetTransformOverride(actor)!.Value;
            Assert.Equal(UeMath.NormalizeAxis(stock.Rotation.Yaw - 15f), turned.Rotation.Yaw, 1);
            Assert.True(HeadlessUi.PumpUntil(() => spawns.Point?.Value.Rotation.Yaw == turned.Rotation.Yaw));
            HeadlessUi.SaveScreenshot(window, "spawns-point-moved");

            // Export mod writes it: the island data read back has the point where it was put, facing the new way.
            var result = await new ProjectExporter().ExportAsync(state, "SpawnMove", ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = dir, WritePak = false }, ScumStudio.Level.Projects.ProjectSourceRole.Client);
            var stem = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "The_Island_LevelStaticData");
            var written = SpawnPlaces.Read(CookedPackage.Parse(File.ReadAllBytes(stem + ".uasset"), File.ReadAllBytes(stem + ".uexp"), null, SpawnPlaces.StaticDataPath));
            var point = written.Single(p => p.Kind == SpawnPlaceKind.Vehicle && p.Index == 100);
            Assert.Equal(turned.Location, point.Transform.Translation);
            Assert.Equal(turned.Rotation.Yaw, TransformValue.FromTransform(point.Transform).Rotation.Yaw, 1);

            // Show on the Map: the Map page loads the point's sector and selects it.
            spawns.ShowOnMapCommand.Execute(null);
            var mapPage = Assert.IsType<MapPageViewModel>(vm.CurrentPage);
            var focus = mapPage.SpawnFocus;
            Assert.True(HeadlessUi.PumpUntil(() => focus.IsCompleted, TimeSpan.FromMinutes(5)));
            await focus;
            Assert.Equal(PlaceName, mapPage.SelectedActor?.Name);
            Assert.True(mapPage.ShowSpawns);
            output.WriteLine($"Map: {mapPage.WorldStatus}");
            HeadlessUi.SaveScreenshot(window, "map-spawn-focus");

            // Back on the Spawns page: a threat zone picked whole, turned with E held (previewed), one more step on release.
            Assert.Same(spawns, vm.NavigateTo("spawns"));
            spawns.IsZombiesTab = true;
            HeadlessUi.Pump(); // the page's view is laid out again
            Assert.Null(spawns.Point); // the vehicle point is not shown with the zombies
            var zoneId = spawns.MarkerOf("ThreatZone_00003")!.Value;
            spawns.SelectedMarkerId = zoneId;
            Assert.Equal(SpawnPlaceKind.Zone, spawns.Point!.Place.Kind);
            spawns.ZoomToPointCommand.Execute(null);
            HeadlessUi.Pump();
            map = HeadlessUi.FindNamed<SpawnMap>(window, "SpawnMap")!;
            map.Focus();
            var zoneYaw = spawns.Point.Value.Rotation.Yaw;
            var before = ctx.Services.Projects.History.Count;
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            HeadlessUi.SaveScreenshot(window, "spawns-zone-turning");
            window.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.None);
            Assert.Equal(before + 1, ctx.Services.Projects.History.Count);
            Assert.Equal(UeMath.NormalizeAxis(zoneYaw + 30f), state.GetTransformOverride(new ActorRef(SpawnPlaces.StaticDataPath, "ThreatZone_00003"))!.Value.Rotation.Yaw, 1);
            Assert.True(HeadlessUi.PumpUntil(() => spawns.Point?.Value.Rotation.Yaw != zoneYaw));
            HeadlessUi.SaveScreenshot(window, "spawns-zone-turned");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
