using ScumStudio.Core.Geometry;

namespace ScumStudio.Assets.Meshes;

/// <summary>Kind of mesh asset.</summary>
public enum MeshAssetKind
{
    /// <summary><c>UStaticMesh</c>.</summary>
    Static,

    /// <summary><c>USkeletalMesh</c>.</summary>
    Skeletal,
}

/// <summary>A material slot of a mesh asset.</summary>
/// <param name="Index">Slot index (the value sections refer to).</param>
/// <param name="SlotName">Material slot name (e.g. <c>Chassis</c>); empty when unnamed.</param>
/// <param name="MaterialPath">Object path of the assigned material or material instance, or empty when none.</param>
public sealed record MeshMaterialSlot(int Index, string SlotName, string MaterialPath);

/// <summary>Counts of one cooked LOD, read without converting vertices.</summary>
/// <param name="Index">LOD index in the asset (0 = highest detail).</param>
/// <param name="VertexCount">Number of render vertices.</param>
/// <param name="IndexCount">Number of triangle-list indices.</param>
/// <param name="SectionCount">Number of material sections.</param>
/// <param name="Uses32BitIndices">True when the cooked index buffer stores 32-bit indices.</param>
/// <param name="TexCoordCount">Number of UV channels.</param>
/// <param name="IsStripped">True when the LOD has no render data (stripped or streamed out) and cannot be extracted.</param>
public sealed record MeshLodInfo(int Index, int VertexCount, int IndexCount, int SectionCount, bool Uses32BitIndices, int TexCoordCount, bool IsStripped)
{
    /// <summary>Number of triangles.</summary>
    public int TriangleCount => IndexCount / 3;
}

/// <summary>A mesh's extracted LOD chain, finest first (see <see cref="MeshExtractor.ExtractLods"/>).</summary>
/// <param name="Lods">The LODs (at least one).</param>
/// <param name="ScreenSizes">
/// Per LOD, the screen size (bounding sphere diameter as a fraction of the view height) below which the next coarser
/// LOD takes over, as cooked in <c>FStaticMeshRenderData.ScreenSize</c>.
/// </param>
public sealed record MeshLodChain(IReadOnlyList<MeshData> Lods, IReadOnlyList<float> ScreenSizes);

/// <summary>Summary of a mesh asset: bounds, material slots and per-LOD counts.</summary>
/// <param name="Name">Asset name.</param>
/// <param name="Kind">Static or skeletal.</param>
/// <param name="Bounds">
/// Asset bounds stored in the package (<c>RenderData.Bounds</c> / <c>ImportedBounds</c>: origin +/- box extent), in UE centimetres.
/// </param>
/// <param name="Materials">Material slots.</param>
/// <param name="Lods">Per-LOD counts.</param>
/// <param name="BoneCount">Number of reference-skeleton bones (0 for static meshes).</param>
public sealed record MeshAssetInfo(string Name, MeshAssetKind Kind, BoundingBox Bounds, IReadOnlyList<MeshMaterialSlot> Materials, IReadOnlyList<MeshLodInfo> Lods, int BoneCount);
