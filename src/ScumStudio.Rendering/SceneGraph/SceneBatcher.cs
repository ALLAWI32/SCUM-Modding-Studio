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
    internal RenderBatch(MeshHandle mesh, InstanceData[] instances, SceneNode[] nodes, BoundingBox bounds, InstanceCluster[] clusters, uint firstCode)
    {
        Mesh = mesh;
        Instances = instances;
        Nodes = nodes;
        Bounds = bounds;
        Clusters = clusters;
        FirstCode = firstCode;
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

    /// <summary>Pick code of the first instance: instance <c>i</c> has code <c>FirstCode + i</c> (written as 0 when its node has no id).</summary>
    public uint FirstCode { get; }
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

        foreach (var b in Batches)
        {
            if (code >= b.FirstCode && code - b.FirstCode < (uint)b.Nodes.Length)
            {
                batch = b;
                node = b.Nodes[code - b.FirstCode];
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Flattens a <see cref="Scene"/> into per-mesh instance batches: applies visibility, composes world transforms, groups
/// the instances of each mesh into spatial clusters (so a frame can cull them cheaply) and assigns pick codes. An
/// instance keeps the result until the scene's <see cref="Scene.Version"/> changes; a change that only moves, tints or
/// selects nodes already in the batches is patched into the existing records (<see cref="PendingUpdates"/>); a
/// structural change (nodes added, removed, shown, hidden, given another mesh, id or cull distance) rebuilds only the
/// batches of the meshes it touched and keeps the others as they are (levels streaming in and out around the camera);
/// a change of the root itself rebuilds everything.
/// </summary>
/// <remarks>
/// Pick codes are unique over the returned batches: a batch's instances have consecutive codes from
/// <see cref="RenderBatch.FirstCode"/> (a full build numbers them 1, 2, … in batch order; a partial rebuild numbers the
/// rebuilt batches after the highest code in use, so kept batches keep theirs), so the ID buffer can hold every drawn
/// instance in a single R32UI value; <see cref="BatchResult.TryResolvePickCode"/> maps a code back to the mesh and the
/// node (and thus its <see cref="SceneNode.SelectableId"/>). Nodes with <see cref="SceneNode.SelectableId"/> 0 are
/// written as 0 (not pickable) but still occlude. Codes stay valid as long as the same <see cref="BatchResult"/> is
/// drawn, whatever the camera culls.
/// </remarks>
public sealed class SceneBatcher
{
    /// <summary>Largest number of instances per cluster (smaller clusters cull finer but cost more frustum tests).</summary>
    public const int ClusterSize = 16;

    private readonly List<InstanceUpdate> _updates = [];
    private Scene? _scene;
    private long _version;
    private BatchResult? _result;
    private uint _nextCode;

    /// <summary>Number of times <see cref="Get"/> rebuilt batches (all of them, or those of the meshes a change touched).</summary>
    public int Builds { get; private set; }

    /// <summary>Of <see cref="Builds"/>, the ones that rebuilt only the batches of the meshes a change touched.</summary>
    public int PartialBuilds { get; private set; }

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

            if (RebuildChanged(scene.Root) is { } partial)
            {
                Install(scene, partial);
                PartialBuilds++;
                return partial;
            }
        }

        if (_scene is not null && !ReferenceEquals(scene, _scene))
        {
            _scene.Root.ChangeLog = null;
        }

        var result = Build(scene, claim: true);
        Install(scene, result);
        return result;
    }

    private void Install(Scene scene, BatchResult result)
    {
        _result = result;
        _scene = scene;
        _version = scene.Version;
        _updates.Clear();
        _nextCode = 1;
        foreach (var batch in result.Batches)
        {
            _nextCode = Math.Max(_nextCode, batch.FirstCode + (uint)batch.Nodes.Length);
        }

        scene.Root.ChangeLog = [];
        scene.Root.StructuralChange = false;
        scene.Root.LogOverflow = false;
        Builds++;
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
            batch.Instances[i] = new InstanceData(world, node.Tint, old.PickCode, node.Selected ? InstanceFlags.Selected : InstanceFlags.None, node.Surface);
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

    /// <summary>
    /// The cached result with the batches of every mesh the logged changes touched rebuilt (the meshes drawn in a logged
    /// subtree before and after the change) and the other batches kept as they are (never more work than a full build:
    /// the same walk, fewer batches); null when a full build is due: the log is incomplete, or the root itself changed.
    /// </summary>
    private BatchResult? RebuildChanged(SceneNode root)
    {
        var result = _result!;
        var log = root.ChangeLog;
        if (log is null || root.LogOverflow || log.Contains(root))
        {
            return null;
        }

        var dirty = new HashSet<int>();
        var walked = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<SceneNode>();
        foreach (var logged in log)
        {
            pending.Push(logged);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!walked.Add(node))
                {
                    continue; // logged twice, or below another logged node
                }

                if (ReferenceEquals(node.SlotOwner, result))
                {
                    dirty.Add(result.Batches[node.SlotBatch].Mesh.Id);
                }

                if (node.Mesh is { } mesh)
                {
                    dirty.Add(mesh.Id);
                }

                foreach (var child in node.Children)
                {
                    pending.Push(child);
                }
            }
        }

        var batches = new List<RenderBatch>(result.Batches.Count + 16);
        foreach (var batch in result.Batches)
        {
            if (!dirty.Contains(batch.Mesh.Id))
            {
                batches.Add(batch);
            }
        }

        var perMesh = Collect(root, dirty);
        var added = perMesh.Values.Sum(e => (long)e.Items.Count);
        if (_nextCode + added >= uint.MaxValue)
        {
            return null; // the pick codes would run out: number everything anew
        }

        var code = _nextCode;
        foreach (var (mesh, items) in perMesh.Values.OrderBy(e => e.Mesh.Id))
        {
            batches.Add(BuildBatch(mesh, items, code));
            code += (uint)items.Count;
        }

        batches.Sort((a, b) => a.Mesh.Id.CompareTo(b.Mesh.Id));
        return Finish(batches, claim: true);
    }

    /// <summary>Builds the batches of <paramref name="scene"/> (no caching).</summary>
    public static BatchResult Build(Scene scene) => Build(scene, claim: false);

    /// <summary>Builds the batches of <paramref name="scene"/>; with <paramref name="claim"/> its nodes remember their records (see <see cref="SceneNode.SlotOwner"/>).</summary>
    private static BatchResult Build(Scene scene, bool claim)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var perMesh = Collect(scene.Root, null);
        var ids = perMesh.Keys.ToArray();
        Array.Sort(ids);
        var batches = new List<RenderBatch>(ids.Length);
        uint code = 1;
        foreach (var id in ids)
        {
            var (mesh, items) = perMesh[id];
            batches.Add(BuildBatch(mesh, items, code));
            code += (uint)items.Count;
        }

        return Finish(batches, claim);
    }

    /// <summary>The visible mesh nodes below <paramref name="root"/> with their world transforms, per mesh id (only <paramref name="meshes"/> when given).</summary>
    private static Dictionary<int, (MeshHandle Mesh, List<(Matrix4x4 World, SceneNode Node)> Items)> Collect(SceneNode root, HashSet<int>? meshes)
    {
        var perMesh = new Dictionary<int, (MeshHandle Mesh, List<(Matrix4x4 World, SceneNode Node)> Items)>();
        var stack = new Stack<(SceneNode Node, Matrix4x4 ParentWorld)>();
        stack.Push((root, Matrix4x4.Identity));
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

            if (node.Mesh is not { } mesh || (meshes is not null && !meshes.Contains(mesh.Id)))
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

        return perMesh;
    }

    /// <summary>The batch of one mesh: its instances sorted into clusters, numbered from <paramref name="firstCode"/>.</summary>
    private static RenderBatch BuildBatch(MeshHandle mesh, List<(Matrix4x4 World, SceneNode Node)> items, uint firstCode)
    {
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
            instances[i] = new InstanceData(world, node.Tint, node.SelectableId == 0 ? 0u : firstCode + (uint)i, flags, node.Surface);
            nodes[i] = node;
        }

        var bounds = BoundingBox.Empty;
        foreach (var cluster in clusters)
        {
            bounds = bounds.Union(cluster.Bounds);
        }

        return new RenderBatch(mesh, instances, nodes, bounds, clusters.ToArray(), firstCode);
    }

    /// <summary>The result over <paramref name="batches"/> (ordered by mesh id); with <paramref name="claim"/> each node is told where its record now lives.</summary>
    private static BatchResult Finish(List<RenderBatch> batches, bool claim)
    {
        var total = 0;
        foreach (var batch in batches)
        {
            total += batch.Instances.Length;
        }

        var result = new BatchResult(batches, total);
        for (var b = 0; claim && b < batches.Count; b++)
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
