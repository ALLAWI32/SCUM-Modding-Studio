namespace ScumStudio.Rendering.Resources;

/// <summary>Coordinate system of the vertices handed to <see cref="GpuMesh"/>.</summary>
public enum MeshSpace
{
    /// <summary>
    /// Unreal Engine space (cm, X forward, Y right, Z up, left-handed) as produced by <c>MeshExtractor</c>: positions and
    /// normals are converted to the renderer's GL world with <c>UeToGl</c> (Y/Z swap) at upload; index order is kept.
    /// </summary>
    Unreal,

    /// <summary>Already in the renderer's right-handed, Y-up world (e.g. Wavefront OBJ files, procedural meshes).</summary>
    Gl,
}
