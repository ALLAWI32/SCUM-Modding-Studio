using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The transform gizmo drawn over a selected building of the outpost, as the Map page draws it (the owner wants it to
/// read like the FiveM map editors: a globe of three rings, arrows with heads, plane squares, scale cubes). Real game
/// files and OpenGL; the pictures go to <c>SCUMSTUDIO_SCREENSHOTS</c> (else the temp folder) as <c>gizmo-*.png</c>.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class GizmoViewportRealTests
{
    [GlFact]
    public async Task TheGizmoOfASelectedBuildingIsDrawnReadably()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var documents = new[] { SpawnPartsRealTests.Maps + "A_0_Outpost", SpawnPartsRealTests.Maps + "A_0_Outpost_Ext_Saloon" }.Select(l => LevelDocument.Load(reader, l)).ToList();
        var prepared = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256 });
        const int w = 1280, h = 800;
        using var harness = GlHarness.Create(w, h);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);

        // The saloon (the biggest Blueprint building that is not a spawner), selected: the gizmo stands in its middle.
        var (building, id, bounds) = documents
            .SelectMany((d, i) => d.Actors.Where(a => a.Kind == ActorKind.Blueprint && !a.Name.Contains("Spawn", StringComparison.OrdinalIgnoreCase)).Select(a => (Actor: a, Id: LevelScenePreparer.SelectableIdOf(i, a))))
            .Select(p => (p.Actor, p.Id, Bounds: Bounds(level, p.Id)))
            .Where(p => !p.Bounds.IsEmpty)
            .MaxBy(p => p.Bounds.Extent.Length());
        level.SetSelection([id]);
        var root = building.WorldTransform;
        var camera = new FlyCamera();
        camera.SetClipRange(5f, 1_000_000f);
        var pivot = Pivot(level, root, (id, null), camera);
        Assert.True(bounds.Contains(pivot), $"the gizmo stands inside the building ({pivot}), not at its root");
        var folder = Folder();

        var distance = bounds.Extent.Length() * 1.4f;
        camera.Orbit(pivot, -60f, -28f, distance);
        var frame = Frame(root, pivot, camera, localAxes: true);
        var lines = harness.Renderer.Overlay;
        var triangles = harness.Renderer.OverlayTriangles;

        // 1. At rest, local axes: the X arrow red, the spin ring purple.
        await ShotAsync("gizmo-building", GizmoAxis.None, GizmoAxis.None, dragging: false);
        var arrowMid = frame.Origin + (frame.X * (frame.Length * 0.55f));
        Assert.True(IsReddish(harness, camera, arrowMid, w, h), "the X arrow is drawn red where it should be");
        Assert.True(SpinRingShows(harness, camera, frame, w, h), "the spin ring is drawn purple");
        Assert.True(lines.Count > 300 && triangles.Count > 100, $"lines {lines.Count}, triangles {triangles.Count}");

        // 2. The Z ring hovered (yellow, thicker).
        await ShotAsync("gizmo-hover-ring", GizmoAxis.RotateZ, GizmoAxis.None, dragging: false);

        // 3. The X arrow dragged: it stays yellow, the rest fades.
        await ShotAsync("gizmo-drag-x", GizmoAxis.None, GizmoAxis.X, dragging: true);

        // 4. From above on the other side, world axes.
        camera.Orbit(pivot, 35f, -50f, distance);
        frame = Frame(root, pivot, camera, localAxes: false);
        await ShotAsync("gizmo-close", GizmoAxis.None, GizmoAxis.None, dragging: false);

        Task ShotAsync(string name, GizmoAxis hover, GizmoAxis active, bool dragging) =>
            Shot(harness, level, camera, frame, Path.Combine(folder, name + ".png"), hover, active, dragging, scale: true);
    }

    [GlFact]
    public async Task TheGizmoOfATraderStandsOnTheTraderNotAmongTheShopsVehicles()
    {
        // Owner screenshot: the selected mechanic of the car shop at the left, the globe 10-15 m away at the right (the
        // middle of the trader and the shop's four vehicle boxes, which pick on their own).
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var documents = new[] { "A_0_Outpost", "A_0_Outpost_Ext_Mechanic", "A_0_Outpost_Mechanic" }.Select(l => LevelDocument.Load(reader, SpawnPartsRealTests.Maps + l)).ToList();
        var prepared = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256 });
        const int w = 1280, h = 800;
        using var harness = GlHarness.Create(w, h);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);

        // A click on the mechanic selects the car shop actor (its trader picks as it).
        var shop = documents[0].Actors.Single(a => a.Name == "BP_Outpost_CarShop_NPC_and_VehicleSpawner_5");
        var id = LevelScenePreparer.SelectableIdOf(0, shop);
        level.SetSelection([id]);
        var trader = prepared.Placements.Single(p => p.SelectableId == id && SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Trader);
        var boxes = prepared.Placements.Where(p => p.SelectableId == id && SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Vehicle).ToList();
        Assert.Equal(4, boxes.Count);
        var standing = UeToGl.Point(trader.World.Translation);
        Assert.True(Vector3.Distance(Bounds(level, id).Center, standing) > 500f, "the whole actor's middle is far from the trader (the old place)");

        var camera = new FlyCamera();
        camera.SetClipRange(5f, 1_000_000f);
        var root = shop.WorldTransform;
        var pivot = Pivot(level, root, (id, null), camera);
        var model = level.Scene.Nodes.Single(n => ReferenceEquals(n.Tag, trader));
        var drawn = LevelSceneUploader.TransformBounds(model.Mesh!.Bounds, model.WorldTransform);
        Assert.True(Vector3.Distance(pivot, drawn.Center) < 1f && Vector3.Distance(pivot, standing) < 150f, $"the gizmo stands on the trader's model: {pivot - standing}, model middle {drawn.Center - standing}");
        Assert.InRange(pivot.Y - standing.Y, 40f, 140f); // his middle, not his feet (GL Y is up)
        Assert.All(boxes, b => Assert.True(Vector3.Distance(pivot, UeToGl.Point(b.World.Translation)) > 400f));

        // From the shop floor (where its vehicles stand), looking at the mechanic.
        var floor = boxes.Aggregate(Vector3.Zero, (sum, b) => sum + UeToGl.Point(b.World.Translation)) / boxes.Count;
        camera.Orbit(pivot, MathF.Atan2(pivot.Z - floor.Z, pivot.X - floor.X) * (180f / MathF.PI), -12f, 450f);
        var frame = Frame(root, pivot, camera, localAxes: true);
        await Shot(harness, level, camera, frame, Path.Combine(Folder(), "gizmo-trader.png"), GizmoAxis.None, GizmoAxis.None, dragging: false, scale: true);
        Assert.True(SpinRingShows(harness, camera, frame, w, h), "the spin ring is drawn purple around the trader");
    }

    [GlFact]
    public async Task TheGizmoOfALongRoadPieceStandsWhereTheCameraLooks()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var documents = new[] { LevelDocument.Load(reader, SpawnPartsRealTests.Maps + "Landscape_A_0_4") };
        var prepared = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256, LandscapeStep = 4 });
        const int w = 1280, h = 800;
        using var harness = GlHarness.Create(w, h);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);

        // The longest road piece (a landscape spline segment, selected alone).
        var camera = new FlyCamera();
        camera.SetClipRange(5f, 1_000_000f);
        var (piece, box) = prepared.Placements
            .Where(p => p.InstanceKey is { InstanceIndex: InstanceKey.Segment })
            .Select(p => (Piece: p, Box: GizmoMath.LocalBox(GizmoMath.PivotParts(level.Scene.Nodes, [(p.SelectableId, p.InstanceKey)]), p.World)))
            .Where(p => !p.Box.IsEmpty && GizmoMath.IsLong(p.Box.Size.X, p.Box.Size.Y, p.Piece.MeshPath))
            .MaxBy(p => MathF.Max(p.Box.Size.X, p.Box.Size.Y));
        level.SelectInstance(piece.InstanceKey!.Value);
        var root = piece.World;
        var alongX = box.Size.X >= box.Size.Y;
        var length = MathF.Max(box.Size.X, box.Size.Y);
        FVector At(float t) => alongX
            ? new FVector(box.Min.X + (box.Size.X * t), box.Center.Y, box.Center.Z)
            : new FVector(box.Center.X, box.Min.Y + (box.Size.Y * t), box.Center.Z);

        // The camera looks at a point three quarters along it: the gizmo stands there, not in the piece's middle.
        var target = UeToGl.Point(root.TransformPosition(At(0.75f)));
        camera.Orbit(target, -50f, -35f, MathF.Max(1500f, length * 0.4f));
        var pivot = Pivot(level, root, (piece.SelectableId, piece.InstanceKey), camera);
        Assert.True(Vector3.Distance(pivot, target) < 50f, $"{piece.MeshPath} ({length / 100f:0} m): the gizmo stands where the camera looks, {Vector3.Distance(pivot, target):0} cm off");
        Assert.True(Vector3.Distance(pivot, UeToGl.Point(root.TransformPosition(At(0.5f)))) > length * 0.2f, "not in the middle of the piece");

        var frame = Frame(root, pivot, camera, localAxes: true);
        await Shot(harness, level, camera, frame, Path.Combine(Folder(), "gizmo-road.png"), GizmoAxis.None, GizmoAxis.None, dragging: false, scale: false);
        Assert.True(SpinRingShows(harness, camera, frame, w, h), "the spin ring is drawn purple on the road");
    }

    [Fact]
    public async Task AScaleDragJournalsTheNewScaleAndAMoveKeepsIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gizmo");
        await map.LoadLevelsAsync([SpawnPartsRealTests.Maps + "A_0_Outpost"]);

        var building = map.AllActors.First(a => a.Actor.Kind == ActorKind.Blueprint && a.HasMesh);
        map.SelectedActorId = building.SelectableId;
        Assert.True(map.GizmoCanScale, "a building shows the scale cubes");
        var root = map.SelectedRootWorld!.Value;

        // A cube drag: the journaled transform carries the new scale.
        map.ApplyDraggedTransform(building.SelectableId, root with { Scale3D = root.Scale3D * 1.2f }, scaled: true);
        var scaled = Assert.IsType<SetTransformOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal(building.Name, scaled.Target.Actor);
        Assert.Equal(root.Scale3D.X * 1.2f, scaled.New.Scale.X, 1e-3f);
        Assert.Equal(root.Scale3D.Z * 1.2f, scaled.New.Scale.Z, 1e-3f);
        Assert.Equal(root.Scale3D.X * 1.2f, map.SelectedRootWorld!.Value.Scale3D.X, 1e-3f);

        // An arrow drag afterwards moves and keeps that scale.
        var moved = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(building.SelectableId, moved with { Translation = moved.Translation + new FVector(100f, 0f, 0f) }, scaled: false);
        var shifted = Assert.IsType<SetTransformOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal(scaled.New.Scale, shifted.New.Scale);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, moved.Translation) is > 99f and < 101f);

        // A road piece cannot be scaled: no cubes.
        var piece = map.PreparedScene!.Placements.FirstOrDefault(p => p.InstanceKey is { InstanceIndex: InstanceKey.Segment });
        if (piece is not null)
        {
            map.SelectedInstanceKey = piece.InstanceKey;
            map.SelectedActorId = piece.SelectableId;
            Assert.False(map.GizmoCanScale);
        }
    }

    private static GizmoFrame Frame(FTransform root, Vector3 pivot, FlyCamera camera, bool localAxes)
    {
        Vector3 Axis(GizmoAxis axis) => UeToGl.Direction(localAxes ? root.Rotation.RotateVector(GizmoMath.UeDirection(axis)) : GizmoMath.UeDirection(axis));
        return new GizmoFrame(pivot, Axis(GizmoAxis.X), Axis(GizmoAxis.Y), Axis(GizmoAxis.Z), camera.Position, camera.Forward,
            GizmoMath.HandleLength(Vector3.Distance(camera.Position, pivot)));
    }

    /// <summary>The gizmo's place (GL) for a selection, the way the Map page puts it (<see cref="GizmoMath.PivotParts"/>, <see cref="GizmoMath.PivotLocal"/>).</summary>
    private static Vector3 Pivot(LevelScene level, FTransform root, (uint Id, InstanceKey? Instance) selection, FlyCamera camera)
    {
        var parts = GizmoMath.PivotParts(level.Scene.Nodes, [selection]);
        var (local, _) = GizmoMath.PivotLocal(GizmoMath.LocalBox(parts, root), root, parts.Count == 1 ? parts[0].Mesh : null, camera.Position, camera.Forward);
        return UeToGl.Point(root.TransformPosition(local));
    }

    private static string Folder()
    {
        var folder = Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } shots ? shots : Path.Combine(Path.GetTempPath(), "scumstudio-gizmo");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task Shot(GlHarness harness, LevelScene level, FlyCamera camera, GizmoFrame frame, string path, GizmoAxis hover, GizmoAxis active, bool dragging, bool scale)
    {
        harness.Renderer.Overlay.Clear();
        harness.Renderer.OverlayTriangles.Clear();
        GizmoOverlay.Draw(frame, hover, active, dragging, scale, harness.Renderer.Overlay, harness.Renderer.OverlayTriangles);
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        await ImageExport.SavePngAsync(harness.Target.ReadColorRgba(), harness.Target.Width, harness.Target.Height, path);
        Assert.True(new FileInfo(path).Length > 1000, path);
    }

    /// <summary>True when the spin ring shows purple on screen: a clearly purple pixel near most of its points.</summary>
    private static bool SpinRingShows(GlHarness harness, FlyCamera camera, GizmoFrame frame, int w, int h)
    {
        var points = GizmoOverlay.RingPoints(frame, GizmoAxis.RotateZ);
        var purple = points.Count(p => Near(harness, camera, p, w, h, c => c[2] > 150 && c[0] > c[1] + 50 && c[2] > c[0] + 20));
        return purple > points.Length / 2;
    }

    private static BoundingBox Bounds(LevelScene level, uint selectableId)
    {
        var bounds = BoundingBox.Empty;
        foreach (var node in level.Scene.Nodes)
        {
            if (node.SelectableId == selectableId && node.Mesh is { } mesh)
            {
                bounds = bounds.Union(LevelSceneUploader.TransformBounds(mesh.Bounds, node.WorldTransform));
            }
        }

        return bounds;
    }

    /// <summary>True when a pixel within 2 px of where <paramref name="gl"/> lands on screen is clearly red.</summary>
    private static bool IsReddish(GlHarness harness, FlyCamera camera, Vector3 gl, int w, int h) =>
        Near(harness, camera, gl, w, h, p => p[0] > 150 && p[0] > p[1] + 60 && p[0] > p[2] + 60);

    /// <summary>True when a pixel within 2 px of where <paramref name="gl"/> lands on screen matches <paramref name="colour"/>.</summary>
    private static bool Near(GlHarness harness, FlyCamera camera, Vector3 gl, int w, int h, Func<byte[], bool> colour)
    {
        var clip = Vector4.Transform(new Vector4(gl, 1f), camera.GetViewProjection((float)w / h));
        var x = (int)Math.Round(((clip.X / clip.W * 0.5f) + 0.5f) * w);
        var y = (int)Math.Round((0.5f - (clip.Y / clip.W * 0.5f)) * h);
        var rgba = harness.Target.ReadColorRgba();
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                if (colour(harness.Pixel(rgba, Math.Clamp(x + dx, 0, w - 1), Math.Clamp(y + dy, 0, h - 1))))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
