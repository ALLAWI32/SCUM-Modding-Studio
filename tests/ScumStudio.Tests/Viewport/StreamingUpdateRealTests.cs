using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Tests.App;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "it stutters while I fly over the map". A streaming step must not throw the scene away: the levels that stay keep
/// their nodes (and what was done to them), only the new level's meshes go to the GPU (staged beforehand), and the batches
/// of the meshes it does not touch are kept. Real game files (<c>SCUM_PAKS</c>) and OpenGL.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class StreamingUpdateRealTests
{
    [GlFact]
    public void AStreamingStepKeepsTheLevelsThatStayAndOnlyAddsTheNewOne()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        LevelDocument Load(string name) => LevelDocument.Load(reader, SpawnPartsRealTests.Maps + name);
        var (outpost, saloon, exterior) = (Load("A_0_Outpost"), Load("A_0_Outpost_Ext_Saloon"), Load("A_0_Outpost_Exterior"));
        var prepareCache = new LevelPrepareCache();
        var preparer = new LevelScenePreparer(catalog);
        PreparedLevelScene Prepare(LevelDocument[] documents, PreparedLevelScene? shown) =>
            preparer.Prepare(documents, new LevelSceneOptions { TextureSize = 64, DocumentSlots = LevelScenePreparer.SlotsAfter(documents, shown) }, cache: prepareCache);

        using var harness = GlHarness.Create(256, 256);
        var gpu = new GpuMeshCache();
        var first = Prepare([outpost, saloon], null);
        Stage(gpu, harness, first, null);
        using var level = LevelSceneUploader.Upload(harness.Renderer, first, cache: gpu);
        Assert.Equal(0, level.UploadTimings!.MeshesUploaded); // staged beforehand: the upload itself sends nothing
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 1_000_000f);
        camera.Orbit(level.Bounds.Center, -60f, -35f, level.Bounds.Extent.Length() * 2f);
        harness.Renderer.Render(harness.Target, level.Scene, camera);

        // A saloon actor moved and selected; the outpost's ids noted.
        var moved = first.Placements.First(p => p.DocumentIndex == 1 && p.Component is not null).SelectableId;
        var movedNodes = level.Scene.Nodes.Where(n => n.SelectableId == moved).ToList();
        var to = level.PlacementsById[moved][0].Actor.WorldTransform is var w ? w with { Translation = w.Translation + new FVector(0f, 0f, 500f) } : default;
        level.SetActorTransform(moved, to);
        level.SetSelection([moved]);
        var outpostIds = first.Placements.Where(p => p.DocumentIndex == 0).Select(p => p.SelectableId).ToHashSet();

        // The camera flies on: the outpost goes, the exterior comes, the saloon stays (in its slot, so with its ids).
        var second = Prepare([exterior, saloon], first);
        Assert.Equal([0, 1], second.Slots);
        Stage(gpu, harness, second, level);
        level.Update(second);
        gpu.Trim(harness.Renderer);
        Assert.Equal(0, level.UploadTimings!.MeshesUploaded);
        Assert.All(movedNodes, n => Assert.NotNull(n.Parent)); // the very same nodes, still in the scene
        Assert.All(movedNodes, n => Assert.True(n.Selected));
        Assert.Contains(moved, level.MovedActors);
        Assert.DoesNotContain(level.Scene.Nodes, n => n.Tag is ScenePlacement p && p.DocumentIndex == 0 && outpostIds.Contains(p.SelectableId) && ReferenceEquals(p.Actor, outpost.Actors.FirstOrDefault(a => a.Name == p.Actor.Name)));
        Assert.Same(second, level.Prepared);

        // What is drawn is what a fresh upload of the second scene draws (the moved actor aside).
        using var fresh = LevelSceneUploader.Upload(harness.Renderer, second);
        Assert.Equal(Drawn(fresh, moved), Drawn(level, moved));
        Assert.Equal(fresh.PlacedCount, level.PlacedCount);

        // The batches of the meshes neither level draws were kept.
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        Assert.True(harness.Renderer.LastRebuild is { Partial: true }, $"{harness.Renderer.LastRebuild}");

        static List<string> Drawn(LevelScene scene, uint except) => scene.Scene.Nodes
            .Where(n => n.Tag is ScenePlacement p && p.SelectableId != except)
            .Select(n => (ScenePlacement)n.Tag!)
            .Select(p => $"{p.SelectableId}|{p.MeshPath}|{p.Name}|{p.GlModel}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // As the viewport does: a few milliseconds a frame until the meshes are on the GPU and the new levels' nodes are built.
    private static void Stage(GpuMeshCache gpu, GlHarness harness, PreparedLevelScene scene, LevelScene? level)
    {
        for (var frame = 0; !(gpu.Stage(harness.Renderer, scene, 4.0) && (level?.Prebuild(scene) ?? true)); frame++)
        {
            Assert.True(frame < 100_000, "staging never finished");
            Thread.Sleep(1);
        }
    }
}
