using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>
/// Reference from a <see cref="SceneNode"/> to a mesh registered with a <see cref="SceneRenderer"/>
/// (<see cref="SceneRenderer.AddMesh(MeshData, Resources.MeshSpace, float, Resources.GpuTexture?)"/>). Carries the
/// local bounds so that scenes can be culled and batched without a GL context.
/// </summary>
/// <param name="Id">Renderer mesh id (1-based, unique per renderer).</param>
/// <param name="Name">Mesh name.</param>
/// <param name="Bounds">Local bounds in the renderer's GL world units.</param>
public sealed record MeshHandle(int Id, string Name, BoundingBox Bounds);
