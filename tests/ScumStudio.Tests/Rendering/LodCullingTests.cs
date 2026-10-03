using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Tests.Rendering;

public sealed class LodCullingTests
{
    private static readonly MeshHandle Cube = new(1, "Cube", new BoundingBox(new Vector3(-50f), new Vector3(50f)));
    private static readonly float TanHalfFov60 = MathF.Tan(30f * MathF.PI / 180f);

    [Fact]
    public void LodIsChosenByProjectedScreenSize()
    {
        // A 10 m sphere (radius 500 cm) seen with a 60° FOV: at 866 cm it fills the view height (size 1), at 8.66 m it is 0.1.
        Assert.Equal(1f, LodMath.ScreenSize(500f, 500f / TanHalfFov60, TanHalfFov60), 3);
        Assert.Equal(0.1f, LodMath.ScreenSize(500f, 5000f / TanHalfFov60, TanHalfFov60), 3);
        Assert.Equal(500f / TanHalfFov60, LodMath.ScreenSize(500f, 0f, TanHalfFov60), 2); // distance clamped to 1

        float[] thresholds = [1f, 0.5f, 0.25f, 0.1f];
        Assert.Equal(0, LodMath.ChooseLod(thresholds, 2f));     // bigger than the view: finest
        Assert.Equal(0, LodMath.ChooseLod(thresholds, 0.6f));
        Assert.Equal(0, LodMath.ChooseLod(thresholds, 0.5f));   // at the threshold itself the finer LOD still applies (UE compares strictly)
        Assert.Equal(1, LodMath.ChooseLod(thresholds, 0.49f));
        Assert.Equal(1, LodMath.ChooseLod(thresholds, 0.3f));
        Assert.Equal(2, LodMath.ChooseLod(thresholds, 0.2f));
        Assert.Equal(3, LodMath.ChooseLod(thresholds, 0.05f));
        Assert.Equal(3, LodMath.ChooseLod(thresholds, 0f));
        Assert.Equal(3, LodMath.ChooseLod(thresholds, 0.05f, minLod: 2));
        Assert.True(LodMath.ChooseLod(thresholds, 0.6f, minLod: 2) == 2, "minLod is a floor");
        Assert.True(LodMath.ChooseLod([1f], 0.001f) == 0, "a single LOD is always drawn");
    }

    [Fact]
    public void ClustersHonourCullDistanceAndPixelSize()
    {
        var box = new BoundingBox(new Vector3(950f, -50f, -50f), new Vector3(1050f, 50f, 50f));
        var sphereRadius = new Vector3(50f).Length();
        var near = new InstanceCluster(0, 4, box, sphereRadius, MaxDrawDistance: 2000f);
        var eye = Vector3.Zero;
        float[] lods = [1f, 0.2f];

        // In range: 950 cm away, 1 m cube projects to about 0.16 of the view height -> coarse LOD 1.
        Assert.Equal(1, LodMath.Classify(near, null, eye, TanHalfFov60, minScreenSize: 0f, distanceScale: 1f, lods));
        Assert.True(LodMath.Classify(near, null, eye, TanHalfFov60, 0f, 1f, [1f]) == 0, "one LOD: always 0");
        // Beyond the component's InstanceEndCullDistance, culled; the view-distance multiplier brings it back.
        var far = near with { Bounds = new BoundingBox(new Vector3(2950f, -50f, -50f), new Vector3(3050f, 50f, 50f)) };
        Assert.Equal(-1, LodMath.Classify(far, null, eye, TanHalfFov60, 0f, 1f, lods));
        Assert.Equal(1, LodMath.Classify(far, null, eye, TanHalfFov60, 0f, 2f, lods));
        Assert.True(LodMath.Classify(far with { MaxDrawDistance = 0f }, null, eye, TanHalfFov60, 0f, 1f, lods) == 1, "0 = never culled by distance");
        // Smaller than the pixel threshold (4 px of 720 = 0.0056): the cube at 29.5 m projects to about 0.05 -> drawn; at 2 km it is 0.0007 -> culled.
        Assert.Equal(1, LodMath.Classify(far with { MaxDrawDistance = 0f }, null, eye, TanHalfFov60, 4f / 720f, 1f, lods));
        var tiny = far with { MaxDrawDistance = 0f, Bounds = new BoundingBox(new Vector3(199_950f, -50f, -50f), new Vector3(200_050f, 50f, 50f)) };
        Assert.Equal(-1, LodMath.Classify(tiny, null, eye, TanHalfFov60, 4f / 720f, 1f, lods));
        Assert.True(LodMath.Classify(tiny, null, eye, TanHalfFov60, 0f, 1f, lods) == 1, "threshold 0 draws everything in view");
        // Behind the camera: the frustum wins before anything else.
        var camera = new FlyCamera { Position = eye, Yaw = 180f };
        camera.SetClipRange(1f, 100_000f);
        Assert.Equal(-1, LodMath.Classify(near, camera.GetFrustum(1f), eye, TanHalfFov60, 0f, 1f, lods));
    }

    [Fact]
    public void BatcherKeepsInstancesWithDifferentCullDistancesInSeparateClusters()
    {
        var scene = new Scene();
        for (var i = 0; i < 8; i++)
        {
            // Interleaved: even nodes are foliage culled at 3 km, odd nodes are never culled, all at the same spot.
            var node = scene.Add(Cube, Matrix4x4.CreateTranslation(1000f + (i * 10f), 0f, 0f), (uint)(i + 1));
            node.MaxDrawDistance = i % 2 == 0 ? 300_000f : 0f;
        }

        var batch = Assert.Single(SceneBatcher.Build(scene).Batches);
        Assert.Equal(2, batch.Clusters.Length);
        Assert.Equal([0f, 300_000f], batch.Clusters.Select(c => c.MaxDrawDistance).Order());
        foreach (var cluster in batch.Clusters)
        {
            Assert.Equal(4, cluster.Count);
            Assert.Equal(new Vector3(50f).Length(), cluster.Radius, 3);
            for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            {
                Assert.Equal(cluster.MaxDrawDistance, batch.Nodes[k].MaxDrawDistance);
            }
        }
    }

    [Fact]
    public void SectionSplitKeepsIndexRangesAcrossPackedLods()
    {
        var lod0 = MeshData.Create("Box", PrimitiveMeshes.Cube(100f).Positions, PrimitiveMeshes.Cube(100f).Indices, sections:
        [
            new MeshSection("/Game/M/Wall.Wall", 0, 18),
            new MeshSection("/Game/M/Roof.Roof", 18, 18),
        ]);
        var lod1 = MeshData.Create("Box_LOD1", [0f, 0f, 0f, 100f, 0f, 0f, 0f, 100f, 0f, 0f, 0f, 100f], [0u, 1u, 2u, 0u, 2u, 3u], sections:
        [
            new MeshSection("/Game/M/Wall.Wall", 0, 6),
        ]);

        var prepared = PreparedMesh.FromLods([lod0, lod1], [1f, 0.3f], MeshSpace.Gl);
        Assert.Equal(24 + 4, prepared.VertexCount);
        Assert.Equal(36 + 6, prepared.Indices.Length);
        Assert.Equal([1f, 0.3f], prepared.Lods.Select(l => l.ScreenSize));

        var (wall, roof) = (prepared.Lods[0].Sections[0], prepared.Lods[0].Sections[1]);
        Assert.Equal(new PreparedSection(0, 18, 0, "/Game/M/Wall.Wall"), wall);
        Assert.Equal(new PreparedSection(18, 18, 0, "/Game/M/Roof.Roof"), roof);
        // LOD 1 follows LOD 0 in both arrays: indices stay relative to its own vertices through BaseVertex.
        var coarse = Assert.Single(prepared.Lods[1].Sections);
        Assert.Equal(new PreparedSection(36, 6, 24, "/Game/M/Wall.Wall"), coarse);
        Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u], prepared.Indices[36..]);
        Assert.Equal(6, prepared.Lods[1].IndexCount);
        Assert.Equal(new Vector3(100f, 100f, 100f), prepared.Bounds.Max);

        // A single-LOD mesh keeps its sections too, and a mesh without sections gets one covering everything.
        Assert.Equal(2, PreparedMesh.From(lod0, MeshSpace.Gl).Lods.Single().Sections.Length);
        var bare = new MeshData("Bare", lod1.Positions, [], [], lod1.Indices, [], lod1.Bounds);
        Assert.Equal(new PreparedSection(0, 6, 0, string.Empty), PreparedMesh.From(bare, MeshSpace.Gl).Lods.Single().Sections.Single());
        Assert.Throws<ArgumentException>(() => PreparedMesh.FromLods([], [], MeshSpace.Gl));
    }

    [Fact]
    public void MovingTintingAndSelectingPatchRecordsInPlaceWhileStructureChangesRebuild()
    {
        var scene = new Scene();
        var a = scene.Add(Cube, Matrix4x4.CreateTranslation(1000f, 0f, 0f), 1);
        var b = scene.Add(Cube, Matrix4x4.CreateTranslation(1200f, 0f, 0f), 2);
        var batcher = new SceneBatcher();
        var result = batcher.Get(scene);
        var batch = Assert.Single(result.Batches);
        var clusterBefore = Assert.Single(batch.Clusters);

        // A drag preview: the same result object, the record rewritten, the cluster bounds grown, the update queued once.
        a.LocalTransform = Matrix4x4.CreateTranslation(1000f, 900f, 0f);
        Assert.Same(result, batcher.Get(scene));
        Assert.Equal(1, batcher.Builds);
        Assert.Equal(1, batcher.Updates);
        var slot = Array.IndexOf(batch.Nodes, a);
        Assert.Equal(new Vector3(1000f, 900f, 0f), batch.Instances[slot].Model.Translation);
        Assert.Equal([new InstanceUpdate(0, slot)], batcher.PendingUpdates);
        var cluster = Assert.Single(batch.Clusters);
        Assert.True(cluster.Bounds.Contains(new Vector3(1000f, 950f, 0f)) && cluster.Bounds.Contains(clusterBefore.Bounds.Min), "bounds grew to cover both places");
        Assert.True(batch.Bounds.Contains(new Vector3(1000f, 950f, 0f)));
        batcher.ClearUpdates();
        Assert.Empty(batcher.PendingUpdates);

        // Selection and tint are patched the same way; pick codes never change.
        var code = batch.Instances[slot].PickCode;
        scene.SetSelection([1u]);
        a.Tint = new Vector4(1f, 0f, 0f, 1f);
        Assert.Same(result, batcher.Get(scene));
        Assert.Equal(InstanceFlags.Selected, batch.Instances[slot].Flags);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), batch.Instances[slot].Tint);
        Assert.Equal(code, batch.Instances[slot].PickCode);
        Assert.Equal(2, batcher.Updates);
        Assert.Equal(1, batcher.Builds);

        // Hiding, showing, re-parenting under a moved group, cull distance and adding nodes rebuild.
        b.Visible = false;
        Assert.NotSame(result, result = batcher.Get(scene));
        Assert.Equal(2, batcher.Builds);
        Assert.Empty(batcher.PendingUpdates);
        b.LocalTransform = Matrix4x4.CreateTranslation(1300f, 0f, 0f); // hidden node: nothing to patch, no rebuild
        Assert.Same(result, batcher.Get(scene));
        Assert.Equal(2, batcher.Builds);
        b.Visible = true;
        Assert.NotSame(result, result = batcher.Get(scene));
        Assert.Equal(3, batcher.Builds);
        var group = scene.Root.Add(new SceneNode("group"));
        group.Add(a);
        result = batcher.Get(scene);
        Assert.Equal(4, batcher.Builds);
        group.LocalTransform = Matrix4x4.CreateTranslation(0f, 0f, 500f); // a group moved: its children have no record of their own change
        Assert.NotSame(result, result = batcher.Get(scene));
        Assert.Equal(5, batcher.Builds);
        Assert.Equal(new Vector3(1000f, 900f, 500f), result.Batches[0].Instances[Array.IndexOf(result.Batches[0].Nodes, a)].Model.Translation);
        a.MaxDrawDistance = 5000f;
        batcher.Get(scene);
        Assert.Equal(6, batcher.Builds);
        scene.Add(Cube, Matrix4x4.CreateTranslation(2000f, 0f, 0f), 3);
        batcher.Get(scene);
        Assert.Equal(7, batcher.Builds);
    }
}
