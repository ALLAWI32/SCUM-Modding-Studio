using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// <see cref="LevelScene.SetBend"/> on a real outpost wall: the node draws a bent copy, then the straight mesh again.
/// Set <c>SCUMSTUDIO_SCREENSHOTS=&lt;folder&gt;</c> to also save the wall straight and bent both ways.
/// </summary>
public sealed class BendPreviewSliceTests
{
    [GlMapSliceFact]
    public async Task ABentActorDrawsABentCopyAndStraightensBack()
    {
        using var catalog = MapSlice.Open();
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "A_0_Outpost");
        var prepared = new LevelScenePreparer(catalog).Prepare([document], new LevelSceneOptions { IncludeInstances = false, TextureSize = 256 });
        using var harness = GlHarness.Create(512, 384);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);

        // The longest single-mesh wall or fence, so the bend shows.
        var (id, node, placement) = level.Scene.Nodes
            .Where(n => n.SelectableId != 0 && n.Mesh is not null && n.Tag is ScenePlacement)
            .GroupBy(n => n.SelectableId)
            .Where(g => g.Count() == 1)
            .Select(g => (g.Key, Node: g.Single(), Placement: (ScenePlacement)g.Single().Tag!))
            .Where(a => a.Placement.MeshPath.Contains("Wall", StringComparison.OrdinalIgnoreCase) || a.Placement.MeshPath.Contains("Fence", StringComparison.OrdinalIgnoreCase))
            .MaxBy(a => MathF.Max(prepared.Meshes[a.Placement.MeshPath].Mesh.Bounds.Size.X, prepared.Meshes[a.Placement.MeshPath].Mesh.Bounds.Size.Y));
        Console.WriteLine($"bending {placement.MeshPath}");
        var straight = node.Mesh!;
        var bounds = prepared.Meshes[placement.MeshPath].Mesh.Bounds;

        await SaveAsync("bend-0");
        level.SetBend(id, [BendShape.For(bounds, FVector.One, 90f)]);
        Assert.NotSame(straight, node.Mesh);
        Assert.NotEqual(straight.Bounds, node.Mesh!.Bounds);
        Assert.Contains(id, level.BentIds);
        await SaveAsync("bend-plus90");

        level.SetBend(id, [BendShape.For(bounds, FVector.One, -45f)]); // replaces the bent copy
        Assert.Single(level.BentIds);
        await SaveAsync("bend-minus45");

        level.SetBend(id, null);
        Assert.Same(straight, node.Mesh);
        Assert.Empty(level.BentIds);

        async Task SaveAsync(string name)
        {
            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder)
            {
                return;
            }

            var camera = new FlyCamera();
            camera.SetClipRange(10f, 1_000_000f);
            var size = MathF.Max(bounds.Size.X, bounds.Size.Y);
            var centre = ScumStudio.Rendering.Cameras.Frustum.TransformBounds(straight.Bounds, node.LocalTransform).Center;
            camera.Orbit(centre, -90f, -89f, size * 1.4f); // from straight above: the curve as a plan
            harness.Renderer.Render(harness.Target, level.Scene, camera);
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(harness.Target.ReadColorRgba(), 512, 384, Path.Combine(folder, name + ".png"));
        }
    }
}
