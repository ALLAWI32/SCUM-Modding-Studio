using System.Numerics;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Rendering;

/// <summary>Counters of one rendered frame.</summary>
/// <param name="Batches">Instanced draw calls issued for meshes.</param>
/// <param name="Instances">Mesh instances drawn.</param>
/// <param name="Culled">Mesh instances rejected by frustum culling.</param>
/// <param name="Triangles">Triangles submitted (instances x triangles per mesh).</param>
public sealed record RenderStats(int Batches, int Instances, int Culled, long Triangles);

/// <summary>Result of a successful pick.</summary>
/// <param name="MeshId">Renderer mesh id of the hit instance.</param>
/// <param name="InstanceId">The hit node's <see cref="SceneNode.SelectableId"/>.</param>
/// <param name="Node">The hit node.</param>
/// <param name="Depth">Window-space depth of the hit pixel ([0, 1]).</param>
/// <param name="WorldPosition">Reconstructed world position of the hit surface point.</param>
public sealed record PickResult(int MeshId, uint InstanceId, SceneNode Node, float Depth, Vector3 WorldPosition);
