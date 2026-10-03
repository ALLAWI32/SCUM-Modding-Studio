using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Tests.Rendering;

public sealed class SceneCullingTests
{
    private static readonly MeshHandle Cube = new(1, "Cube", new BoundingBox(new Vector3(-50f), new Vector3(50f)));
    private static readonly MeshHandle Other = new(2, "Other", new BoundingBox(new Vector3(-10f), new Vector3(10f)));

    private static FlyCamera CameraAtOriginLookingAlongX()
    {
        var camera = new FlyCamera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        camera.SetClipRange(1f, 100_000f);
        return camera;
    }

    [Fact]
    public void FrustumRejectsBoxesBehindTheCamera()
    {
        var frustum = CameraAtOriginLookingAlongX().GetFrustum(1.5f);
        Assert.True(frustum.Intersects(Box(new Vector3(1000f, 0f, 0f), 50f)));
        Assert.False(frustum.Intersects(Box(new Vector3(-1000f, 0f, 0f), 50f)));
        Assert.False(frustum.Intersects(Box(new Vector3(200_000f, 0f, 0f), 50f)), "beyond the far plane");
        Assert.False(frustum.Intersects(Box(new Vector3(1000f, 0f, 5000f), 50f)), "far outside the right plane");
        Assert.True(frustum.Intersects(Box(new Vector3(0f, 0f, 0f), 50f)), "the box around the eye straddles the near plane");
        Assert.False(frustum.Intersects(BoundingBox.Empty));
        Assert.True(frustum.Contains(new Vector3(10f, 0f, 0f)));
        Assert.False(frustum.Contains(new Vector3(-10f, 0f, 0f)));
    }

    [Fact]
    public void TransformBoundsEnclosesTransformedCorners()
    {
        var local = new BoundingBox(new Vector3(-1f, -2f, -3f), new Vector3(4f, 5f, 6f));
        var m = Matrix4x4.CreateScale(2f, 0.5f, -1f) * Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f) * Matrix4x4.CreateTranslation(100f, -20f, 5f);
        var world = Frustum.TransformBounds(local, m);
        var exact = BoundingBox.Empty;
        foreach (var c in CameraMathTests.Corners(local))
        {
            var p = Vector3.Transform(c, m);
            exact = exact.Include(p);
            Assert.True(world.Contains(p) || Vector3.Distance(Vector3.Clamp(p, world.Min, world.Max), p) < 1e-3f);
        }

        // Arvo's method is exact for the box of the transformed corners.
        RenderAssert.Near(exact.Min, world.Min, 1e-3f);
        RenderAssert.Near(exact.Max, world.Max, 1e-3f);
    }

    [Fact]
    public void BatcherGroupsByMeshAndClustersInstancesForCulling()
    {
        var scene = new Scene();
        for (var i = 0; i < 10; i++)
        {
            // Five cubes in front (+X), five "others" behind (-X).
            var x = i < 5 ? 500f + (i * 200f) : -500f - (i * 200f);
            scene.Add(i < 5 ? Cube : Other, Matrix4x4.CreateTranslation(x, 0f, 0f), (uint)(i + 1));
        }

        // A long row of cubes along +Z that must be split into several spatially sorted clusters.
        for (var i = 0; i < 4 * SceneBatcher.ClusterSize; i++)
        {
            scene.Add(Cube, Matrix4x4.CreateTranslation(1000f, 0f, i * 150f), (uint)(100 + i));
        }

        var result = SceneBatcher.Build(scene);
        Assert.Equal(10 + (4 * SceneBatcher.ClusterSize), result.InstanceCount);
        Assert.Equal([1, 2], result.Batches.Select(b => b.Mesh.Id));
        var (cubes, others) = (result.Batches[0], result.Batches[1]);
        Assert.Equal([1u, 2u, 3u, 4u, 5u], cubes.Nodes.Select(n => n.SelectableId).Where(id => id < 100).Order());
        Assert.Equal([6u, 7u, 8u, 9u, 10u], others.Nodes.Select(n => n.SelectableId).Order());
        Assert.True(cubes.Bounds.Min.X > 0f && others.Bounds.Max.X < 0f);

        // Clusters tile the instance array in order, each within the limit and with the bounds of its instances.
        Assert.True(cubes.Clusters.Length >= 4, $"{cubes.Clusters.Length} clusters");
        Assert.Equal(0, cubes.Clusters[0].First);
        Assert.Equal(cubes.Instances.Length, cubes.Clusters.Sum(c => c.Count));
        for (var i = 0; i < cubes.Clusters.Length; i++)
        {
            var cluster = cubes.Clusters[i];
            Assert.InRange(cluster.Count, 1, SceneBatcher.ClusterSize);
            Assert.Equal(i == 0 ? 0 : cubes.Clusters[i - 1].First + cubes.Clusters[i - 1].Count, cluster.First);
            for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            {
                Assert.True(cluster.Bounds.Contains(cubes.Instances[k].Model.Translation));
            }
        }

        // The camera at the origin looking along +X sees the cubes (and only part of the long row), never the others.
        var camera = CameraAtOriginLookingAlongX();
        var frustum = camera.GetFrustum(1f);
        Assert.True(frustum.Intersects(cubes.Bounds));
        Assert.False(frustum.Intersects(others.Bounds));
        Assert.Contains(cubes.Clusters, c => frustum.Intersects(c.Bounds));
        Assert.Contains(cubes.Clusters, c => !frustum.Intersects(c.Bounds));
        camera.Yaw = 180f;
        Assert.True(camera.GetFrustum(1f).Intersects(others.Bounds));
    }

    [Fact]
    public void BatchCacheIsReusedUntilASceneNodeChanges()
    {
        var scene = new Scene();
        var a = scene.Add(Cube, Matrix4x4.CreateTranslation(1000f, 0f, 0f), 1);
        var b = scene.Add(Other, Matrix4x4.CreateTranslation(1200f, 0f, 0f), 2);
        var batcher = new SceneBatcher();

        var first = batcher.Get(scene);
        Assert.Same(first, batcher.Get(scene));
        Assert.Equal(1, batcher.Builds);
        Assert.Equal(2, first.Batches.Count);

        // Assigning the same values again changes nothing.
        a.LocalTransform = a.LocalTransform;
        b.Visible = true;
        scene.SetSelection([]);
        a.Name = "renamed";
        Assert.Same(first, batcher.Get(scene));
        Assert.Equal(1, batcher.Builds);

        // Moving a node patches the record in place (same result, new world matrix; see LodCullingTests for the details).
        a.LocalTransform = Matrix4x4.CreateTranslation(1000f, 500f, 0f);
        var moved = batcher.Get(scene);
        Assert.Same(first, moved);
        Assert.Equal(1, batcher.Builds);
        Assert.Equal(1, batcher.Updates);
        Assert.Equal(new Vector3(1000f, 500f, 0f), Assert.Single(moved.Batches[0].Instances).Model.Translation);
        Assert.Same(moved, batcher.Get(scene));

        // Hiding, re-parenting and adding nodes rebuild; selecting and tinting patch.
        b.Visible = false;
        Assert.Single(batcher.Get(scene).Batches);
        Assert.Equal(2, batcher.Builds);
        scene.SetSelection([1u]);
        Assert.Equal(InstanceFlags.Selected, Assert.Single(batcher.Get(scene).Batches[0].Instances).Flags);
        a.Tint = new Vector4(1f, 0f, 0f, 1f);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), Assert.Single(batcher.Get(scene).Batches[0].Instances).Tint);
        Assert.Equal(2, batcher.Builds);
        var group = scene.Root.Add(new SceneNode("group", localTransform: Matrix4x4.CreateTranslation(0f, 0f, 300f)));
        group.Add(a);
        Assert.Equal(new Vector3(1000f, 500f, 300f), Assert.Single(batcher.Get(scene).Batches[0].Instances).Model.Translation);
        scene.Add(Cube, Matrix4x4.CreateTranslation(2000f, 0f, 0f), 3);
        Assert.Equal(2, batcher.Get(scene).Batches[0].Instances.Length);
        Assert.Equal(4, batcher.Builds);

        // Another scene gets its own build (the cache holds one scene).
        batcher.Get(new Scene());
        Assert.Equal(5, batcher.Builds);
    }

    [Fact]
    public void PickCodesResolveToMeshAndNode()
    {
        var scene = new Scene();
        var a = scene.Add(Other, Matrix4x4.CreateTranslation(1000f, 0f, 0f), 42);
        var b = scene.Add(Cube, Matrix4x4.CreateTranslation(1200f, 0f, 0f), 7);
        var hidden = scene.Add(Cube, Matrix4x4.CreateTranslation(1400f, 0f, 0f), 0, "not pickable");
        var result = SceneBatcher.Build(scene);

        var codes = result.Batches.SelectMany(x => x.Instances).Select(i => i.PickCode).ToArray();
        Assert.Equal(3, codes.Length);
        Assert.Contains(0u, codes);
        foreach (var batch in result.Batches)
        {
            for (var i = 0; i < batch.Instances.Length; i++)
            {
                var code = batch.Instances[i].PickCode;
                if (code == 0)
                {
                    Assert.Same(hidden, batch.Nodes[i]);
                    continue;
                }

                Assert.True(result.TryResolvePickCode(code, out var resolvedBatch, out var node));
                Assert.Same(batch, resolvedBatch);
                Assert.Same(batch.Nodes[i], node);
            }
        }

        Assert.Contains(result.Batches.SelectMany(x => x.Nodes), n => ReferenceEquals(n, a));
        Assert.Contains(result.Batches.SelectMany(x => x.Nodes), n => ReferenceEquals(n, b));
        Assert.False(result.TryResolvePickCode(0, out _, out _));
        Assert.False(result.TryResolvePickCode(99, out _, out _));
    }

    [Fact]
    public void HierarchyComposesTransformsVisibilityAndSelection()
    {
        var scene = new Scene();
        var group = scene.Root.Add(new SceneNode("group", localTransform: Matrix4x4.CreateTranslation(1000f, 0f, 0f)));
        var child = group.Add(new SceneNode("child", Cube, Matrix4x4.CreateTranslation(0f, 50f, 0f), 5));
        Assert.Equal(new Vector3(1000f, 50f, 0f), child.WorldTransform.Translation);

        scene.SetSelection([5u]);
        var result = SceneBatcher.Build(scene);
        var instance = Assert.Single(result.Batches.Single().Instances);
        Assert.Equal(InstanceFlags.Selected, instance.Flags);
        Assert.Equal(new Vector3(1000f, 50f, 0f), instance.Model.Translation);
        Assert.Same(child, scene.FindBySelectableId(5));

        group.Visible = false;
        Assert.False(child.IsEffectivelyVisible);
        Assert.Empty(SceneBatcher.Build(scene).Batches);

        group.Visible = true;
        Assert.Throws<InvalidOperationException>(() => child.Add(group));
        Assert.True(group.Remove(child));
        Assert.Null(child.Parent);
        Assert.Empty(SceneBatcher.Build(scene).Batches);
    }

    [Fact]
    public void CubeGridSceneAssignsRowMajorIds()
    {
        var grid = CubeGridScene.Build(new MeshHandle(1, "Cube", Box(Vector3.Zero, 50f)), columns: 8, rows: 4, spacing: 200f);
        Assert.Equal(32, grid.Count);
        Assert.Equal(32, grid.Scene.Nodes.Count(n => n.Mesh is not null));
        Assert.Equal(1u, grid.IdAt(0, 0));
        Assert.Equal(12u, grid.IdAt(3, 1));
        var node = grid.Scene.FindBySelectableId(12)!;
        RenderAssert.Near(grid.PositionAt(3, 1) with { Y = 0f }, node.WorldTransform.Translation with { Y = 0f });
        Assert.True(node.WorldTransform.Translation.Y > 0f, "cubes stand on the ground plane");
    }

    [Fact]
    public void InstanceDataLayoutIs96Bytes()
    {
        Assert.Equal(InstanceData.SizeInBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<InstanceData>());
        Assert.Equal(InstanceData.SizeInBytes, System.Runtime.InteropServices.Marshal.SizeOf<InstanceData>());
    }

    private static BoundingBox Box(Vector3 center, float half) => new(center - new Vector3(half), center + new Vector3(half));
}
