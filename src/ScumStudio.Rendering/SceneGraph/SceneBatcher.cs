using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Resources;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>A contiguous run of a batch's instances that lie close together: the unit of per-frame culling and LOD choice.</summary>
/// <param name="First">Index of the first instance in <see cref="RenderBatch.Instances"/>.</param>
/// <param name="Count">Number of instances.</param>
/// <param name="Bounds">World bounds of these instances.</param>
/// <param name="Radius">Bounding sphere radius of the largest single instance (world units), for projected-size tests.</param>
/// <param name="MaxDrawDistance">Cull distance shared by these instances (<see cref="SceneNode.MaxDrawDistance"/>; 0 = none).</param>
public readonly record struct InstanceCluster(int First, int Count, BoundingBox Bounds, float Radius, float MaxDrawDistance);

/// <summary>An instance record of a <see cref="BatchResult"/> patched in place (see <see cref="SceneBatcher.PendingUpdates"/>).</summary>
/// <param name="Batch">Index in <see cref="BatchResult.Batches"/>.</param>
/// <param name="Index">Index in <see cref="RenderBatch.Instances"/>.</param>
public readonly record struct InstanceUpdate(int Batch, int Index);

/// <summary>All visible instances of one mesh.</summary>
public sealed class RenderBatch
{
    internal RenderBatch(MeshHandle mesh, InstanceData[] instances, SceneNode[] nodes, BoundingBox bounds, InstanceCluster[] clusters)
    {
        Mesh = mesh;
        Instances = instances;
        Nodes = nodes;
        Bounds = bounds;
        Clusters = clusters;
    }

    /// <summary>The mesh.</summary>
    public MeshHandle Mesh { get; }

    /// <summary>Instance records in draw order (spatially sorted so that <see cref="Clusters"/> are contiguous).</summary>
    public InstanceData[] Instances { get; }

    /// <summary>The node of each instance (same order as <see cref="Instances"/>).</summary>
    public SceneNode[] Nodes { get; }

    /// <summary>World bounds of all instances (only ever grows while records are patched in place).</summary>
    public BoundingBox Bounds { get; internal set; }

    /// <summary>Spatial clusters covering <see cref="Instances"/> in order, each at most <see cref="SceneBatcher.ClusterSize"/> instances.</summary>
    public InstanceCluster[] Clusters { get; }
}

/// <summary>Result of <see cref="SceneBatcher.Build"/>.</summary>
/// <param name="Batches">One batch per mesh with at least one visible instance, ordered by mesh id.</param>
/// <param name="InstanceCount">Visible mesh instances over all batches.</param>
public sealed record BatchResult(IReadOnlyList<RenderBatch> Batches, int InstanceCount)
{
    /// <summary>
    /// Maps a pick code produced with this result (see <see cref="SceneBatcher.Build"/>) back to its batch and node.
    /// Returns false for 0 or out-of-range codes.
    /// </summary>
    public bool TryResolvePickCode(uint code, out RenderBatch? batch, out SceneNode? node)
    {
        batch = null;
        node = null;
        if (code == 0)
        {
            return false;
        }

        var index = (long)code - 1;
        foreach (var b in Batches)
        {
            if (index < b.Nodes.Length)
            {
                batch = b;
                node = b.Nodes[index];
                return true;
            }

            index -= b.Nodes.Length;
        }

        return false;
    }
}

/// <summary>
/// Flattens a <see cref="Scene"/> into per-mesh instance batches: applies visibility, composes world transforms, groups
/// the instances of each mesh into spatial clusters (so a frame can cull them cheaply) and assigns pick codes. An
/// instance keeps the result until the scene's <see cref="Scene.Version"/> changes; a change that only moves, tints or
/// selects nodes already in the batches is patched into the existing records (<see cref="PendingUpdates"/>), anything
/// else rebuilds.
/// </summary>
/// <remarks>
/// Pick codes are 1-based running indices over the returned batches (batch order, then instance order), so the ID
/// buffer can hold every drawn instance in a single R32UI value; <see cref="BatchResult.TryResolvePickCode"/> maps a code
/// back to the mesh and the node (and thus its <see cref="SceneNode.SelectableId"/>). Nodes with
/// <see cref="SceneNode.SelectableId"/> 0 are written as 0 (not pickable) but still occlude. Codes stay valid as long as
/// the same <see cref="BatchResult"/> is drawn, whatever the camera culls.
/// </remarks>
public sealed class SceneBatcher
{
    /// <summary>Largest number of instances per cluster (smaller clusters cull finer but cost more frustum tests).</summary>
    public const int ClusterSize = 16;

    private readonly List<InstanceUpdate> _updates = [];
    private Scene? _scene;
    private long _version;
    private BatchResult? _result;

    /// <summary>Number of times <see cref="Get"/> rebuilt the batches.</summary>
    public int Builds { get; private set; }

    /// <summary>Number of times <see cref="Get"/> patched records in place instead of rebuilding.</summary>
    public int Updates { get; private set; }

    /// <summary>
    /// Records patched in place since the last build and not yet acknowledged with <see cref="ClearUpdates"/> (the
    /// renderer re-uploads exactly these). Empty right after a rebuild.
    /// </summary>
    public IReadOnlyList<InstanceUpdate> PendingUpdates => _updates;

    /// <summary>Forgets <see cref="PendingUpdates"/> (after uploading them).</summary>
    public void ClearUpdates() => _updates.Clear();

    /// <summary>Returns the batches of <paramref name="scene"/>, rebuilding them only when the scene changed since the last call.</summary>
    public BatchResult Get(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_result is not null && ReferenceEquals(scene, _scene))
        {
            if (scene.Version == _version)
            {
                return _result;
            }

            if (TryPatch(scene.Root))
            {
                _version = scene.Version;
                Updates++;
                return _result;
            }
        }

        if (_scene is not null && !ReferenceEquals(scene, _scene))
        {
            _scene.Root.ChangeLog = null;
        }

        _result = Build(scene);
        _scene = scene;
        _version = scene.Version;
        _updates.Clear();
        scene.Root.ChangeLog = [];
        scene.Root.StructuralChange = false;
        Builds++;
        return _result;
    }

    /// <summary>
    /// Applies the changes logged on <paramref name="root"/> to the cached result: a moved, tinted or (de)selected node
    /// that already has a record gets it rewritten (its cluster's bounds grow to keep it culled correctly). Returns
    /// false when a rebuild is needed instead: structural changes, a changed node without a record (a group node that
    /// moved its children), or more changes than a rebuild would cost.
    /// </summary>
    private bool TryPatch(SceneNode root)
    {
        var result = _result!;
        var log = root.ChangeLog;
        if (log is null || root.StructuralChange || log.Count > Math.Max(64, result.InstanceCount / 4))
        {
            return false;
        }

        foreach (var node in log)
        {
            if (!node.IsEffectivelyVisible)
            {
                continue; // not drawn; showing it again is a structural change
            }

            if (node.Mesh is not { } mesh || !ReferenceEquals(node.SlotOwner, result))
            {
                return false;
            }

            var batch = result.Batches[node.SlotBatch];
            var i = node.SlotIndex;
            var old = batch.Instances[i];
            var world = node.WorldTransform;
            batch.Instances[i] = new InstanceData(world, node.Tint, old.PickCode, node.Selected ? InstanceFlags.Selected : InstanceFlags.None);
            if (world != old.Model)
            {
                var bounds = Frustum.TransformBounds(mesh.Bounds, world);
                var c = ClusterOf(batch.Clusters, i);
                var cluster = batch.Clusters[c];
                batch.Clusters[c] = cluster with { Bounds = cluster.Bounds.Union(bounds), Radius = MathF.Max(cluster.Radius, bounds.Extent.Length()) };
                batch.Bounds = batch.Bounds.Union(bounds);
            }

            _updates.Add(new InstanceUpdate(node.SlotBatch, i));
        }

        log.Clear();
        return true;
    }

    /// <summary>Index of the cluster holding instance <paramref name="index"/> (clusters tile the instance array in order).</summary>
    private static int ClusterOf(InstanceCluster[] clusters, int index)
    {
        var lo = 0;
        var hi = clusters.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (clusters[mid].First <= index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>Builds the batches of <paramref name="scene"/> (no caching).</summary>
    public static BatchResult Build(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var perMesh = new Dictionary<int, (MeshHandle Mesh, List<(Matrix4x4 World, SceneNode Node)> Items)>();
        var stack = new Stack<(SceneNode Node, Matrix4x4 ParentWorld)>();
        stack.Push((scene.Root, Matrix4x4.Identity));
        while (stack.Count > 0)
        {
            var (node, parentWorld) = stack.Pop();
            if (!node.Visible)
            {
                continue;
            }

            var world = node.LocalTransform * parentWorld;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], world));
            }

            if (node.Mesh is not { } mesh)
            {
                continue;
            }

            if (!perMesh.TryGetValue(mesh.Id, out var entry))
            {
                entry = (mesh, []);
                perMesh[mesh.Id] = entry;
            }

            entry.Items.Add((world, node));
        }

        var ids = perMesh.Keys.ToArray();
        Array.Sort(ids);
        var batches = new List<RenderBatch>(ids.Length);
        var total = 0;
        uint code = 1;
        foreach (var id in ids)
        {
            var (mesh, items) = perMesh[id];
            var worldBounds = new BoundingBox[items.Count];
            var centres = new Vector3[items.Count];
            var order = new int[items.Count];
            var cull = new float[items.Count];
            var mixedCull = false;
            for (var i = 0; i < items.Count; i++)
            {
                worldBounds[i] = Frustum.TransformBounds(mesh.Bounds, items[i].World);
                centres[i] = worldBounds[i].Center;
                order[i] = i;
                cull[i] = items[i].Node.MaxDrawDistance;
                mixedCull |= cull[i] != cull[0];
            }

            var clusters = new List<InstanceCluster>();
            if (mesh.Bounds.IsEmpty)
            {
                clusters.Add(new InstanceCluster(0, items.Count, BoundingBox.Empty, 0f, 0f));
            }
            else
            {
                // Instances sharing a cull distance form their own runs, so a cluster is culled as a whole.
                if (mixedCull)
                {
                    Array.Sort((float[])cull.Clone(), order);
                }

                var keys = new float[items.Count];
                for (var lo = 0; lo < items.Count;)
                {
                    var distance = cull[order[lo]];
                    var hi = lo + 1;
                    while (hi < items.Count && cull[order[hi]] == distance)
                    {
                        hi++;
                    }

                    Split(worldBounds, centres, order, keys, lo, hi, distance, clusters);
                    lo = hi;
                }
            }

            var instances = new InstanceData[items.Count];
            var nodes = new SceneNode[items.Count];
            for (var i = 0; i < order.Length; i++)
            {
                var (world, node) = items[order[i]];
                var flags = node.Selected ? InstanceFlags.Selected : InstanceFlags.None;
                instances[i] = new InstanceData(world, node.Tint, node.SelectableId == 0 ? 0u : code, flags);
                nodes[i] = node;
                code++;
            }

            var bounds = BoundingBox.Empty;
            foreach (var cluster in clusters)
            {
                bounds = bounds.Union(cluster.Bounds);
            }

            total += items.Count;
            batches.Add(new RenderBatch(mesh, instances, nodes, bounds, clusters.ToArray()));
        }

        var result = new BatchResult(batches, total);
        for (var b = 0; b < batches.Count; b++)
        {
            var nodes = batches[b].Nodes;
            for (var i = 0; i < nodes.Length; i++)
            {
                nodes[i].SlotOwner = result;
                nodes[i].SlotBatch = b;
                nodes[i].SlotIndex = i;
            }
        }

        return result;
    }

    /// <summary>
    /// Splits <c>order[lo..hi)</c> at the median along the longest axis of the instance centres until a run has at most
    /// <see cref="ClusterSize"/> instances, appending one cluster per run (a k-d tree whose leaves are stored in order).
    /// <paramref name="keys"/> is scratch space of the same length as <paramref name="order"/>.
    /// </summary>
    private static void Split(BoundingBox[] worldBounds, Vector3[] centres, int[] order, float[] keys, int lo, int hi, float maxDrawDistance, List<InstanceCluster> clusters)
    {
        if (hi - lo <= ClusterSize)
        {
            var bounds = BoundingBox.Empty;
            var radius = 0f;
            for (var i = lo; i < hi; i++)
            {
                bounds = bounds.Union(worldBounds[order[i]]);
                radius = MathF.Max(radius, worldBounds[order[i]].Extent.Length());
            }

            clusters.Add(new InstanceCluster(lo, hi - lo, bounds, radius, maxDrawDistance));
            return;
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = lo; i < hi; i++)
        {
            var c = centres[order[i]];
            min = Vector3.Min(min, c);
            max = Vector3.Max(max, c);
        }

        var size = max - min;
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        for (var i = lo; i < hi; i++)
        {
            keys[i] = centres[order[i]][axis];
        }

        var mid = lo + ((hi - lo) / 2);
        SelectKth(keys, order, lo, hi - 1, mid);
        Split(worldBounds, centres, order, keys, lo, mid, maxDrawDistance, clusters);
        Split(worldBounds, centres, order, keys, mid, hi, maxDrawDistance, clusters);
    }

    /// <summary>
    /// Partitions <c>keys[left..right]</c> (and <paramref name="order"/> alongside) so that the <paramref name="k"/>-th
    /// smallest key sits at index <paramref name="k"/>, smaller keys before it and larger ones after (Wirth's selection,
    /// O(n) expected; no allocation).
    /// </summary>
    private static void SelectKth(float[] keys, int[] order, int left, int right, int k)
    {
        while (left < right)
        {
            var pivot = keys[k];
            var i = left;
            var j = right;
            do
            {
                while (keys[i] < pivot)
                {
                    i++;
                }

                while (pivot < keys[j])
                {
                    j--;
                }

                if (i <= j)
                {
                    (keys[i], keys[j]) = (keys[j], keys[i]);
                    (order[i], order[j]) = (order[j], order[i]);
                    i++;
                    j--;
                }
            }
            while (i <= j);

            if (j < k)
            {
                left = i;
            }

            if (k < i)
            {
                right = j;
            }
        }
    }
}
