using System.Diagnostics;
using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

[Collection(TimingCollection.Name)]
public sealed class LodCullingTests
{
    private static readonly MeshHandle Cube = new(1, "Cube", new BoundingBox(new Vector3(-50f), new Vector3(50f)));
    private static readonly float TanHalfFov60 = MathF.Tan(30f * MathF.PI / 180f);

    /// <summary>A 4 m wide, 10 m tall tree with three LODs.</summary>
    private static readonly MeshHandle Tree = new(2, "Tree", new BoundingBox(new Vector3(-200f, 0f, -200f), new Vector3(200f, 1000f, 200f)));
    private static readonly float[] TreeLods = [1f, 0.3f, 0.1f];

    private readonly ITestOutputHelper _output;

    public LodCullingTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// Audit F268: two copies of a tree 5 km apart once formed one cluster whose box held the camera, so both were drawn
    /// at LOD 0 and the far one was never culled by its 100 m cull distance.
    /// </summary>
    [Fact]
    public void SparseInstancesDoNotShareACluster()
    {
        var scene = new Scene();
        var a = scene.Add(Tree, Matrix4x4.Identity, 1);
        var b = scene.Add(Tree, Matrix4x4.CreateTranslation(500_000f, 0f, 0f), 2);
        a.MaxDrawDistance = b.MaxDrawDistance = 10_000f;
        var batch = Assert.Single(SceneBatcher.Build(scene).Batches);

        // The camera 10 m from tree A: A fills the view (LOD 0), B is 5 km away, far beyond its cull distance.
        var eye = new Vector3(1000f, 500f, 0f);
        foreach (var cluster in batch.Clusters)
        {
            var lod = LodMath.Classify(cluster, null, eye, TanHalfFov60, 0f, 1f, TreeLods);
            for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            {
                var expected = ReferenceEquals(batch.Nodes[k], a) ? 0 : -1;
                Assert.True(lod == expected, $"tree {(expected == 0 ? "A" : "B")} drawn at LOD {lod}, expected {expected} ({batch.Clusters.Length} cluster(s))");
            }
        }
    }

    /// <summary>
    /// Audit F426: the LOD radius was the half-diagonal of the instance's world box, about 1.3× larger for a cube turned
    /// 45°; UE scales the mesh's bounding sphere by the instance's largest axis scale, so turning an instance never moves
    /// its LOD switch.
    /// </summary>
    [Fact]
    public void RotatedInstancesSwitchLodAtTheSameDistance()
    {
        static float SwitchDistance(Matrix4x4 transform)
        {
            var scene = new Scene();
            scene.Add(Cube, transform, 1);
            var cluster = Assert.Single(Assert.Single(SceneBatcher.Build(scene).Batches).Clusters);
            var (near, far) = (100f, 100_000f);
            for (var i = 0; i < 40; i++)
            {
                var d = (near + far) / 2f;
                if (LodMath.Classify(cluster, null, new Vector3(d, 0f, 0f), TanHalfFov60, 0f, 1f, [1f, 0.05f]) == 0)
                {
                    near = d;
                }
                else
                {
                    far = d;
                }
            }

            return near;
        }

        var straight = SwitchDistance(Matrix4x4.Identity);
        var turned = SwitchDistance(Matrix4x4.CreateRotationY(MathF.PI / 4f));
        Assert.True(MathF.Abs((turned / straight) - 1f) < 0.02f, $"LOD 1 from {straight:0} cm straight, {turned:0} cm turned 45°");
    }

    /// <summary>
    /// BENCHMARK (synthetic, no GL), against the count-only clusters the batcher made before: a forest of 5,000 trees on
    /// 2 × 2 km seen from its edge draws fewer triangles now that each tree takes the LOD of where it stands, and building
    /// 100,000 instances (a dense 300 × 300 grid of bushes and 10,000 rocks scattered over 10 km) costs about the same.
    /// The rocks' clusters each hold one rock now, so classifying the clusters of a frame takes longer (printed).
    /// </summary>
    [Fact]
    public void ForestLodTriangleBenchmark()
    {
        var random = new Random(7);
        var forest = new Scene();
        for (var i = 0; i < 5000; i++)
        {
            var turn = Matrix4x4.CreateScale(0.8f + (random.NextSingle() * 0.4f)) * Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau);
            forest.Add(Tree, turn * Matrix4x4.CreateTranslation(random.NextSingle() * 200_000f, 0f, random.NextSingle() * 200_000f), (uint)(i + 1));
        }

        // The camera 10 m outside the forest's west edge, 2 m up, looking east across it (60° FOV, 16:9, 1080 px: 4 px culled).
        var camera = new FlyCamera { Position = new Vector3(-1000f, 200f, 100_000f), Yaw = 0f, Pitch = 0f, FieldOfView = 60f };
        camera.SetClipRange(10f, 1_000_000f);
        var before = Frame(SceneBatcher.BuildCountOnly(forest));
        var after = Frame(SceneBatcher.Build(forest));
        _output.WriteLine($"forest, 5,000 trees: {before.Triangles:N0} triangles in {before.Clusters:N0} clusters before, {after.Triangles:N0} in {after.Clusters:N0} after ({100.0 * after.Triangles / before.Triangles:0.0} %)");
        Assert.True(after.Triangles <= before.Triangles, $"{after.Triangles:N0} triangles after, {before.Triangles:N0} before");

        var mixed = new Scene();
        var bush = new MeshHandle(3, "Bush", new BoundingBox(new Vector3(-50f), new Vector3(50f)));
        var rock = new MeshHandle(4, "Rock", new BoundingBox(new Vector3(-100f), new Vector3(100f)));
        for (var i = 0; i < 90_000; i++)
        {
            mixed.Add(bush, Matrix4x4.CreateTranslation((i % 300) * 200f, 0f, (i / 300) * 200f), (uint)(i + 1));
        }

        for (var i = 0; i < 10_000; i++)
        {
            mixed.Add(rock, Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(random.NextSingle() * 1_000_000f, 0f, random.NextSingle() * 1_000_000f), (uint)(100_000 + i));
        }

        // Interleaved, best of five after a warm-up each: the time of the build itself, not of the collector or the JIT.
        var (countOnly, split) = (double.MaxValue, double.MaxValue);
        BatchResult? oldResult = null, newResult = null;
        for (var round = 0; round < 6; round++)
        {
            GC.Collect();
            var start = Stopwatch.GetTimestamp();
            oldResult = SceneBatcher.BuildCountOnly(mixed);
            countOnly = round == 0 ? countOnly : Math.Min(countOnly, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            GC.Collect();
            start = Stopwatch.GetTimestamp();
            newResult = SceneBatcher.Build(mixed);
            split = round == 0 ? split : Math.Min(split, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        camera.Position = new Vector3(-1000f, 200f, -1000f); // the grid's corner, looking across it
        camera.Yaw = 45f;
        var (oldFrame, newFrame) = (Frame(oldResult!), Frame(newResult!));
        int Rocks(BatchResult result) => result.Batches.Single(b => b.Mesh.Id == rock.Id).Clusters.Length;
        _output.WriteLine($"100,000 instances: built in {countOnly:0.0} ms before, {split:0.0} ms after; {oldFrame.Clusters:N0} -> {newFrame.Clusters:N0} clusters (rocks {Rocks(oldResult!):N0} -> {Rocks(newResult!):N0})");
        _output.WriteLine($"100,000 instances, one frame: clusters classified in {oldFrame.Ms:0.000} ms before, {newFrame.Ms:0.000} ms after; {oldFrame.Drawn:N0} -> {newFrame.Drawn:N0} drawn, {oldFrame.Triangles:N0} -> {newFrame.Triangles:N0} triangles");
        Assert.Equal(oldFrame.Clusters - Rocks(oldResult!), newFrame.Clusters - Rocks(newResult!)); // dense bushes keep their clusters
        Assert.True(newFrame.Triangles <= oldFrame.Triangles, $"{newFrame.Triangles:N0} triangles after, {oldFrame.Triangles:N0} before");
        if (!Ci.On) Assert.True(split <= countOnly * 1.25, $"built in {split:0.0} ms, {countOnly:0.0} ms before");

        // Every cluster classified as SceneRenderer.PrepareFrame does (best of 20 passes), at 1,000 / 300 / 50 triangles per LOD.
        (long Triangles, int Clusters, int Drawn, double Ms) Frame(BatchResult result)
        {
            int[] triangles = [1000, 300, 50];
            var frustum = camera.GetFrustum(16f / 9f);
            var (best, sum, clusters, drawn) = (double.MaxValue, 0L, 0, 0);
            for (var round = 0; round < 20; round++)
            {
                var start = Stopwatch.GetTimestamp();
                (sum, clusters, drawn) = (0L, 0, 0);
                foreach (var batch in result.Batches)
                {
                    clusters += batch.Clusters.Length;
                    foreach (var cluster in batch.Clusters)
                    {
                        var lod = LodMath.Classify(cluster, frustum, camera.Position, TanHalfFov60, 4f / 1080f, 1f, TreeLods);
                        if (lod >= 0)
                        {
                            sum += (long)cluster.Count * triangles[lod];
                            drawn++;
                        }
                    }
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            return (sum, clusters, drawn, best);
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
