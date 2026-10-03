using System.Numerics;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>A retained scene: a root node plus the id lookup used by picking.</summary>
public sealed class Scene
{
    /// <summary>Root node (identity transform by default).</summary>
    public SceneNode Root { get; } = new("root");

    /// <summary>
    /// Optional look overrides for this scene (sky, sun, fog, grid handling), applied over the renderer settings when
    /// <see cref="RenderSettings.UseSceneEnvironment"/> is on. Null keeps the renderer settings unchanged.
    /// </summary>
    public SceneEnvironment? Environment { get; set; }

    /// <summary>Adds a mesh node below the root and returns it.</summary>
    public SceneNode Add(MeshHandle mesh, Matrix4x4 transform, uint selectableId = 0, string? name = null) =>
        Root.Add(new SceneNode(name ?? mesh.Name, mesh, transform, selectableId));

    /// <summary>All nodes below (and including) the root.</summary>
    public IEnumerable<SceneNode> Nodes => Root.DescendantsAndSelf();

    /// <summary>Changes whenever any node of the scene changed in a way that affects drawing (see <see cref="SceneNode.Version"/>).</summary>
    public long Version => Root.Version;

    /// <summary>Finds the first node with <paramref name="selectableId"/> (null when absent or 0).</summary>
    public SceneNode? FindBySelectableId(uint selectableId) =>
        selectableId == 0 ? null : Nodes.FirstOrDefault(n => n.SelectableId == selectableId);

    /// <summary>Clears the selection flag of every node, then selects the nodes whose id is in <paramref name="ids"/>.</summary>
    public void SetSelection(IEnumerable<uint> ids)
    {
        var set = ids as ISet<uint> ?? new HashSet<uint>(ids);
        foreach (var node in Nodes)
        {
            node.Selected = node.SelectableId != 0 && set.Contains(node.SelectableId);
        }
    }
}
