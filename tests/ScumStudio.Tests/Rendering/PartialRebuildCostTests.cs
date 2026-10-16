using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Audit F434: a level streaming in or out rebuilt the batches of its meshes by walking the whole island scene again
/// (100k+ nodes per streaming step). The rebuild looks only at the logged subtrees and the batches they share meshes with,
/// and its records must be the ones a full build makes.
/// </summary>
public sealed class PartialRebuildCostTests(ITestOutputHelper output)
{
    private const int Levels = 50;
    private const int NodesPerLevel = 2000;
    private const int SharedPerLevel = 400; // 20,000 nodes of the shared mesh over the scene

    [Fact]
    public void AGroupStreamingInVisitsOnlyItsNodesAndTheBatchesItShares()
    {
        var (scene, shared, meshes) = Island();
        var batcher = new SceneBatcher();
        batcher.Get(scene);

        // A 200-node level comes in: 100 nodes of a mesh not drawn yet, 100 of the mesh 20,000 nodes already share.
        var fresh = Mesh(1000);
        var group = new SceneNode("level in");
        for (var i = 0; i < 200; i++)
        {
            group.Add(new SceneNode("n", i % 2 == 0 ? fresh : shared, Matrix4x4.CreateTranslation(-5000f - (i * 300f), 0f, 0f), (uint)(900_000 + i)));
        }

        scene.Root.Add(group);
        var result = batcher.Get(scene);
        output.WriteLine($"partial rebuild visited {batcher.LastVisitedNodes:N0} of {Levels * NodesPerLevel + 200:N0} nodes");
        Assert.Equal(1, batcher.PartialBuilds);
        Assert.InRange(batcher.LastVisitedNodes, 1, (Levels * SharedPerLevel) + 200 + 16);
        AssertSameAsFullBuild(scene, result);
        Assert.Equal(meshes + 1, result.Batches.Count);
    }

    [Fact]
    public void MixedChangesRebuildTheRecordsAFullBuildMakes()
    {
        var (scene, shared, _) = Island(levels: 6, perLevel: 300, sharedPerLevel: 60);
        var batcher = new SceneBatcher();
        batcher.Get(scene);
        var levels = scene.Root.Children.ToArray();

        // A level moved together with a structural change elsewhere: its nodes' world transforms change, kept nodes keep theirs.
        levels[0].LocalTransform = Matrix4x4.CreateTranslation(0f, 0f, 750f);
        levels[1].Visible = false;
        Step("move one level, hide another");

        levels[1].Visible = true;
        var moved = levels[2].Children[5];
        levels[3].Add(moved); // re-parented under a level with another transform
        levels[3].LocalTransform = Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateTranslation(100f, 0f, 0f);
        Step("show it again, re-parent a node, turn its new level");

        levels[4].Children[7].Mesh = shared;
        levels[4].Children[8].Tint = new Vector4(0.2f, 0.4f, 0.6f, 0.5f);
        levels[4].Children[9].SelectableId = 0;
        levels[4].Children[10].Selected = true;
        levels[4].Add(new SceneNode("late", shared, Matrix4x4.CreateTranslation(1f, 2f, 3f), 77));
        Step("change a mesh, a tint, an id and a selection, add a node");

        scene.Root.Remove(levels[5]);
        levels[0].Add(new SceneNode("group") { Visible = false }).Add(new SceneNode("hidden child", shared, Matrix4x4.Identity, 78));
        Step("remove a level, add a hidden group");

        void Step(string what)
        {
            var result = batcher.Get(scene);
            output.WriteLine($"{what}: visited {batcher.LastVisitedNodes:N0}, partial builds {batcher.PartialBuilds}");
            AssertSameAsFullBuild(scene, result);
        }
    }

    private static (Scene Scene, MeshHandle Shared, int Meshes) Island(int levels = Levels, int perLevel = NodesPerLevel, int sharedPerLevel = SharedPerLevel)
    {
        // Every level draws the shared mesh plus its own mix of 40 others, spread over a 2 km square.
        var shared = Mesh(1);
        var others = Enumerable.Range(2, 40).Select(Mesh).ToArray();
        var scene = new Scene();
        var random = new Random(434);
        uint id = 1;
        for (var l = 0; l < levels; l++)
        {
            var level = scene.Root.Add(new SceneNode($"level {l}", localTransform: Matrix4x4.CreateTranslation(l * 4000f, 0f, 0f)));
            for (var i = 0; i < perLevel; i++)
            {
                var mesh = i < sharedPerLevel ? shared : others[random.Next(others.Length)];
                var at = new Vector3(random.NextSingle() * 200_000f, random.NextSingle() * 200_000f, random.NextSingle() * 3000f);
                level.Add(new SceneNode("n", mesh, Matrix4x4.CreateRotationZ(random.NextSingle()) * Matrix4x4.CreateTranslation(at), i % 7 == 0 ? 0 : id++));
            }
        }

        return (scene, shared, others.Length + 1);
    }

    private static MeshHandle Mesh(int id) => new(id, $"mesh {id}", new BoundingBox(new Vector3(-50f), new Vector3(50f)));

    /// <summary>Same batches, the same record for every node as a full build, and pick codes unique, resolvable and claimed.</summary>
    internal static void AssertSameAsFullBuild(Scene scene, BatchResult result)
    {
        var full = SceneBatcher.Build(scene);
        Assert.Equal(full.Batches.Select(b => b.Mesh.Id), result.Batches.Select(b => b.Mesh.Id));
        Assert.Equal(full.InstanceCount, result.InstanceCount);
        foreach (var (expected, actual) in full.Batches.Zip(result.Batches))
        {
            var records = new Dictionary<SceneNode, InstanceData>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < expected.Nodes.Length; i++)
            {
                records.Add(expected.Nodes[i], expected.Instances[i]);
            }

            Assert.Equal(expected.Nodes.Length, actual.Nodes.Length);
            for (var i = 0; i < actual.Nodes.Length; i++)
            {
                Assert.True(records.TryGetValue(actual.Nodes[i], out var want), $"{actual.Nodes[i]} is not in the full build");
                var got = actual.Instances[i];
                Assert.Equal(want.Model, got.Model);
                Assert.Equal(want.Tint, got.Tint);
                Assert.Equal(want.Flags, got.Flags);
                Assert.Equal(want.Surface, got.Surface);
                Assert.Equal(want.PickCode == 0, got.PickCode == 0);
            }

            var first = 0;
            foreach (var cluster in actual.Clusters)
            {
                Assert.Equal(first, cluster.First);
                first += cluster.Count;
                for (var i = cluster.First; i < cluster.First + cluster.Count; i++)
                {
                    Assert.Equal(actual.Nodes[i].MaxDrawDistance, cluster.MaxDrawDistance);
                }
            }

            Assert.Equal(actual.Instances.Length, first);
        }

        var codes = new HashSet<uint>();
        for (var b = 0; b < result.Batches.Count; b++)
        {
            var batch = result.Batches[b];
            for (var i = 0; i < batch.Instances.Length; i++)
            {
                var code = batch.FirstCode + (uint)i;
                Assert.True(codes.Add(code));
                Assert.True(result.TryResolvePickCode(code, out var resolved, out var node));
                Assert.Same(batch, resolved);
                Assert.Same(batch.Nodes[i], node);
                Assert.Same(result, node!.SlotOwner);
                Assert.Equal((b, i), (node.SlotBatch, node.SlotIndex));
                Assert.Equal(node.SelectableId == 0 ? 0u : code, batch.Instances[i].PickCode);
            }
        }
    }
}
