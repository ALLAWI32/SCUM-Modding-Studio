using System.Numerics;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>
/// A node of the retained scene graph: an optional mesh reference, a local transform relative to the parent, an id
/// reported by picking, and visibility / selection state. Children inherit the parent's transform and visibility.
/// </summary>
/// <remarks>
/// Every change that affects drawing bumps <see cref="Version"/> on the node and all its ancestors, so a renderer can
/// cache its per-frame work (see <see cref="SceneBatcher"/>) and rebuild it only when the scene actually changed. The
/// top node of a tree also records which nodes changed (<see cref="ChangeLog"/>) and whether a change was structural
/// (mesh, visibility, id, children, cull distance) or only a transform / tint / selection, so a batcher can patch
/// instance records in place instead of rebuilding.
/// </remarks>
public sealed class SceneNode
{
    private readonly List<SceneNode> _children = [];
    private MeshHandle? _mesh;
    private Matrix4x4 _localTransform;
    private uint _selectableId;
    private bool _visible = true;
    private bool _selected;
    private Vector4 _tint = Vector4.One;
    private Vector2 _surface;
    private float _maxDrawDistance;
    private long _version;

    /// <summary>Creates a node.</summary>
    public SceneNode(string name = "", MeshHandle? mesh = null, Matrix4x4? localTransform = null, uint selectableId = 0)
    {
        Name = name;
        _mesh = mesh;
        _localTransform = localTransform ?? Matrix4x4.Identity;
        _selectableId = selectableId;
    }

    /// <summary>Display name (e.g. an actor or component name).</summary>
    public string Name { get; set; }

    /// <summary>Mesh drawn by this node (null for pure grouping nodes).</summary>
    public MeshHandle? Mesh
    {
        get => _mesh;
        set => Set(ref _mesh, value, structural: true);
    }

    /// <summary>Transform relative to the parent in the row-vector convention (<c>world = local * parentWorld</c>).</summary>
    public Matrix4x4 LocalTransform
    {
        get => _localTransform;
        set => Set(ref _localTransform, value, structural: false);
    }

    /// <summary>Id returned by picking (e.g. an actor index); 0 = drawn but not pickable.</summary>
    public uint SelectableId
    {
        get => _selectableId;
        set => Set(ref _selectableId, value, structural: true);
    }

    /// <summary>When false this node and its subtree are not drawn.</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value, structural: true);
    }

    /// <summary>Draws the node with the selection highlight.</summary>
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value, structural: false);
    }

    /// <summary>Linear RGBA colour multiplied with the mesh albedo.</summary>
    public Vector4 Tint
    {
        get => _tint;
        set => Set(ref _tint, value, structural: false);
    }

    /// <summary>Metal (x) and gloss (y), 0..1, for shiny paint; zero = plain shading. The texture's alpha masks it.</summary>
    public Vector2 Surface
    {
        get => _surface;
        set => Set(ref _surface, value, structural: false);
    }

    /// <summary>
    /// Distance from the camera (world units) beyond which the node is not drawn; 0 = always drawn. Level scenes set it
    /// from the <c>InstanceEndCullDistance</c> of HISM/foliage components, scaled by <see cref="RenderSettings.ViewDistanceScale"/>.
    /// </summary>
    public float MaxDrawDistance
    {
        get => _maxDrawDistance;
        set => Set(ref _maxDrawDistance, value, structural: true);
    }

    /// <summary>Arbitrary caller data (e.g. the level actor this node represents).</summary>
    public object? Tag { get; set; }

    /// <summary>Parent node (null for roots).</summary>
    public SceneNode? Parent { get; private set; }

    /// <summary>Child nodes.</summary>
    public IReadOnlyList<SceneNode> Children => _children;

    /// <summary>
    /// Changes whenever this node or a descendant changed in a way that affects drawing (mesh, transform, id,
    /// visibility, selection, tint, cull distance, children). Assigning a property its current value does not count.
    /// </summary>
    public long Version => _version;

    /// <summary>World transform (local transforms composed up to the root).</summary>
    public Matrix4x4 WorldTransform => Parent is null ? LocalTransform : LocalTransform * Parent.WorldTransform;

    /// <summary>True when this node and all ancestors are visible.</summary>
    public bool IsEffectivelyVisible => Visible && (Parent?.IsEffectivelyVisible ?? true);

    /// <summary>
    /// On the top node of a tree: the nodes changed since the list was set (duplicates possible), or null when nobody
    /// listens. <see cref="SceneBatcher"/> installs it after a build and drains it on the next change.
    /// </summary>
    internal List<SceneNode>? ChangeLog { get; set; }

    /// <summary>On the top node of a tree: true when a logged change was structural (needs a rebuild rather than a patch).</summary>
    internal bool StructuralChange { get; set; }

    /// <summary>The batch result this node's instance record lives in (set by <see cref="SceneBatcher.Build"/>), or null.</summary>
    internal BatchResult? SlotOwner { get; set; }

    /// <summary>Batch index of the node's instance record in <see cref="SlotOwner"/>.</summary>
    internal int SlotBatch { get; set; }

    /// <summary>Instance index of the node's record inside its batch.</summary>
    internal int SlotIndex { get; set; }

    /// <summary>Adds <paramref name="child"/> (detaching it from its previous parent) and returns it.</summary>
    public SceneNode Add(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        for (var p = this; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, child))
            {
                throw new InvalidOperationException("A node cannot be added below itself.");
            }
        }

        child.Parent?.Remove(child);
        child.Parent = this;
        _children.Add(child);
        Touch(structural: true);
        return child;
    }

    /// <summary>Removes <paramref name="child"/>; returns false when it is not a child of this node.</summary>
    public bool Remove(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!_children.Remove(child))
        {
            return false;
        }

        child.Parent = null;
        Touch(structural: true);
        return true;
    }

    /// <summary>Removes all children.</summary>
    public void Clear()
    {
        foreach (var child in _children)
        {
            child.Parent = null;
        }

        _children.Clear();
        Touch(structural: true);
    }

    /// <summary>This node and all descendants, depth first.</summary>
    public IEnumerable<SceneNode> DescendantsAndSelf()
    {
        var stack = new Stack<SceneNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node._children.Count - 1; i >= 0; i--)
            {
                stack.Push(node._children[i]);
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} (id {SelectableId}, mesh {Mesh?.Name ?? "-"})";

    private void Set<T>(ref T field, T value, bool structural)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Touch(structural);
    }

    private void Touch(bool structural)
    {
        var node = this;
        while (true)
        {
            node._version++;
            if (node.Parent is null)
            {
                break;
            }

            node = node.Parent;
        }

        if (node.ChangeLog is { } log)
        {
            // Past the cap a rebuild is cheaper than patching anyway (and a scene nobody renders stops growing the log).
            if (log.Count < MaxChangeLog)
            {
                log.Add(this);
            }
            else
            {
                node.StructuralChange = true;
            }

            node.StructuralChange |= structural;
        }
    }

    /// <summary>Largest number of entries kept in <see cref="ChangeLog"/>; more changes force a rebuild.</summary>
    internal const int MaxChangeLog = 65_536;
}
