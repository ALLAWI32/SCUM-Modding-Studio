namespace ScumStudio.Assets.Export;

/// <summary>Options shared by the OBJ and glTF mesh exporters.</summary>
public sealed record MeshExportOptions
{
    /// <summary>
    /// Uniform scale applied to positions. Default 0.01 converts UE centimetres to metres (glTF's unit).
    /// </summary>
    public float Scale { get; init; } = 0.01f;

    /// <summary>
    /// Convert from UE (X forward, Y right, Z up, left-handed) to a right-handed Y-up frame by writing (x, z, y).
    /// Default true (glTF requires Y up; most OBJ importers assume it). When false, UE axes are written unchanged.
    /// </summary>
    public bool ConvertToYUp { get; init; } = true;

    /// <summary>Write normals when the mesh has them. Default true.</summary>
    public bool IncludeNormals { get; init; } = true;

    /// <summary>Write UV0 when the mesh has it. Default true.</summary>
    public bool IncludeUvs { get; init; } = true;
}
