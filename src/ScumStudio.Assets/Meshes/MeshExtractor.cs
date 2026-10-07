using System.Numerics;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Assets.Meshes;

/// <summary>
/// Converts cooked <see cref="UStaticMesh"/> and <see cref="USkeletalMesh"/> exports to renderer-neutral <see cref="MeshData"/>.
/// </summary>
/// <remarks>
/// <para><b>Axis convention.</b> Output stays in Unreal Engine space: centimetres, X forward, Y right, Z up (left-handed),
/// exactly as stored in the cooked vertex buffers; no scaling, axis swap or index reordering is applied. UE front faces are
/// wound clockwise when seen from the front in this left-handed frame, i.e. <c>dot(cross(v1 - v0, v2 - v0), normal) &lt; 0</c>
/// for most triangles of a stock mesh. Converting to a right-handed Y-up frame (glTF, OpenGL) by swapping Y and Z
/// (<c>(x, y, z) -> (x, z, y)</c>) is a reflection, which turns those triangles counter-clockwise, so the index order can be
/// kept (see <see cref="Export.GltfExporter"/>).</para>
/// <para>UVs are as stored (origin top-left, V down), the same convention as glTF. Normals are unpacked from the
/// cooked 8-bit tangent basis and re-normalised. Skeletal meshes are extracted in their bind pose.</para>
/// <para>LOD indices refer to the asset's LOD list; asking for a stripped LOD (no render data) gives the next LOD that has
/// render data.</para>
/// </remarks>
public static class MeshExtractor
{
    /// <summary>True when <paramref name="obj"/> is a mesh this extractor understands.</summary>
    public static bool IsMesh(UObject? obj) => obj is UStaticMesh or USkeletalMesh;

    /// <summary>Extracts LOD <paramref name="lod"/> of a static or skeletal mesh.</summary>
    /// <exception cref="NotSupportedException"><paramref name="mesh"/> is neither a static nor a skeletal mesh.</exception>
    /// <exception cref="InvalidDataException">The LOD does not exist or neither it nor a later LOD has render data.</exception>
    public static MeshData Extract(UObject mesh, int lod = 0) => mesh switch
    {
        UStaticMesh sm => ExtractStaticMesh(sm, lod),
        USkeletalMesh sk => ExtractSkeletalMesh(sk, lod),
        _ => throw new NotSupportedException($"{mesh.Name} is a {mesh.ExportType}, not a StaticMesh or SkeletalMesh."),
    };

    /// <summary>
    /// Extracts the LOD chain of a mesh from LOD <paramref name="firstLod"/> on (at most <paramref name="maxLods"/> LODs,
    /// finest first) with the cooked screen-size thresholds (<c>FStaticMeshRenderData.ScreenSize</c>). Stripped LODs
    /// and LODs the game never selects (threshold 0) are left out; skeletal meshes give one LOD.
    /// </summary>
    /// <exception cref="InvalidDataException">LOD <paramref name="firstLod"/> does not exist or neither it nor a later LOD has render data.</exception>
    public static MeshLodChain ExtractLods(UObject mesh, int firstLod = 0, int maxLods = 8) => mesh switch
    {
        UStaticMesh sm => ExtractStaticMeshLods(sm, firstLod, maxLods),
        USkeletalMesh sk => new MeshLodChain([ExtractSkeletalMesh(sk, firstLod)], [1f]),
        _ => throw new NotSupportedException($"{mesh.Name} is a {mesh.ExportType}, not a StaticMesh or SkeletalMesh."),
    };

    /// <summary>Extracts the LOD chain of a static mesh (see <see cref="ExtractLods"/>).</summary>
    public static MeshLodChain ExtractStaticMeshLods(UStaticMesh mesh, int firstLod = 0, int maxLods = 8)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = mesh.RenderData?.LODs ?? throw new InvalidDataException($"{mesh.Name} has no render data.");
        firstLod = WithRenderData(firstLod, lods.Length, i => lods[i].SkipLod);
        CheckLod(mesh.Name, firstLod, lods.Length, lods.Length > firstLod && firstLod >= 0 && lods[firstLod].SkipLod);
        if (!mesh.TryConvert(out CStaticMesh converted))
        {
            throw new InvalidDataException($"{mesh.Name} could not be converted.");
        }

        var slots = StaticSlots(mesh);
        var thresholds = mesh.RenderData!.ScreenSize ?? [];
        var meshes = new List<MeshData>();
        var sizes = new List<float>();
        var convertedIndex = lods.Take(firstLod).Count(l => !l.SkipLod);
        for (var i = firstLod; i < lods.Length && convertedIndex < converted.LODs.Count && meshes.Count < Math.Max(1, maxLods); i++)
        {
            if (lods[i].SkipLod)
            {
                continue;
            }

            var src = converted.LODs[convertedIndex++];
            var size = i < thresholds.Length ? thresholds[i] : 0f;
            if (meshes.Count > 0 && size <= 0f)
            {
                continue;
            }

            meshes.Add(Build(mesh.Name, src.Verts, src.NumVerts, src.Indices.Value, src.Sections.Value, slots));
            sizes.Add(size > 0f ? size : 1f);
        }

        return new MeshLodChain(meshes, sizes);
    }

    /// <summary>Describes a static or skeletal mesh (bounds, materials, per-LOD counts) without converting vertices.</summary>
    public static MeshAssetInfo Describe(UObject mesh) => mesh switch
    {
        UStaticMesh sm => DescribeStaticMesh(sm),
        USkeletalMesh sk => DescribeSkeletalMesh(sk),
        _ => throw new NotSupportedException($"{mesh.Name} is a {mesh.ExportType}, not a StaticMesh or SkeletalMesh."),
    };

    /// <summary>Extracts one LOD of a static mesh.</summary>
    public static MeshData ExtractStaticMesh(UStaticMesh mesh, int lod = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = mesh.RenderData?.LODs ?? throw new InvalidDataException($"{mesh.Name} has no render data.");
        lod = WithRenderData(lod, lods.Length, i => lods[i].SkipLod);
        CheckLod(mesh.Name, lod, lods.Length, lods.Length > lod && lod >= 0 && lods[lod].SkipLod);
        if (!mesh.TryConvert(out CStaticMesh converted))
        {
            throw new InvalidDataException($"{mesh.Name} could not be converted.");
        }

        // The converter drops LODs without render data; map the asset LOD index onto the converted list.
        var convertedIndex = lods.Take(lod).Count(l => !l.SkipLod);
        var src = converted.LODs[convertedIndex];
        var slots = StaticSlots(mesh);
        return Build(mesh.Name, src.Verts, src.NumVerts, src.Indices.Value, src.Sections.Value, slots);
    }

    /// <summary>Extracts one LOD of a skeletal mesh (bind pose).</summary>
    public static MeshData ExtractSkeletalMesh(USkeletalMesh mesh, int lod = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = mesh.LODModels ?? throw new InvalidDataException($"{mesh.Name} has no LOD models.");
        lod = WithRenderData(lod, lods.Length, i => lods[i].SkipLod);
        CheckLod(mesh.Name, lod, lods.Length, lods.Length > lod && lod >= 0 && lods[lod].SkipLod);
        if (!mesh.TryConvert(out CSkeletalMesh converted))
        {
            throw new InvalidDataException($"{mesh.Name} could not be converted.");
        }

        var convertedIndex = lods.Take(lod).Count(l => !l.SkipLod);
        var src = converted.LODs[convertedIndex];
        var slots = SkeletalSlots(mesh);
        return Build(mesh.Name, src.Verts, src.NumVerts, src.Indices.Value, src.Sections.Value, slots);
    }

    /// <summary>
    /// The bone influences of LOD <paramref name="lod"/> of a skeletal mesh, in the vertex order of
    /// <see cref="ExtractSkeletalMesh"/>, with the mesh's reference skeleton (bind pose), for <see cref="Skinning"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The LOD does not exist or neither it nor a later LOD has render data.</exception>
    public static SkinWeights ExtractSkinWeights(USkeletalMesh mesh, int lod = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = mesh.LODModels ?? throw new InvalidDataException($"{mesh.Name} has no LOD models.");
        lod = WithRenderData(lod, lods.Length, i => lods[i].SkipLod);
        CheckLod(mesh.Name, lod, lods.Length, lods.Length > lod && lod >= 0 && lods[lod].SkipLod);
        if (!mesh.TryConvert(out CSkeletalMesh converted))
        {
            throw new InvalidDataException($"{mesh.Name} could not be converted.");
        }

        var src = converted.LODs[lods.Take(lod).Count(l => !l.SkipLod)];
        var offsets = new int[src.NumVerts + 1];
        var bones = new List<int>(src.NumVerts * 4);
        var weights = new List<float>(src.NumVerts * 4);
        for (var i = 0; i < src.NumVerts; i++)
        {
            foreach (var influence in src.Verts[i].Influences)
            {
                if (influence.Weight > 0f)
                {
                    bones.Add(influence.Bone);
                    weights.Add(influence.Weight);
                }
            }

            offsets[i + 1] = bones.Count;
        }

        return new SkinWeights(ReferenceBones(mesh), offsets, bones.ToArray(), weights.ToArray());
    }

    /// <summary>The reference skeleton of a skeletal mesh (bind pose, bone-relative), in bone order.</summary>
    public static IReadOnlyList<SkinBone> ReferenceBones(USkeletalMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var skeleton = mesh.ReferenceSkeleton;
        var info = skeleton?.FinalRefBoneInfo ?? [];
        var poses = skeleton?.FinalRefBonePose ?? [];
        var bones = new List<SkinBone>(info.Length);
        for (var i = 0; i < info.Length && i < poses.Length; i++)
        {
            var p = poses[i];
            bones.Add(new SkinBone(info[i].Name.Text, info[i].ParentIndex, new Core.Mathematics.FTransform(
                new Core.Mathematics.FQuat(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W),
                new Core.Mathematics.FVector(p.Translation.X, p.Translation.Y, p.Translation.Z),
                new Core.Mathematics.FVector(p.Scale3D.X, p.Scale3D.Y, p.Scale3D.Z))));
        }

        return bones;
    }

    /// <summary>Describes a static mesh.</summary>
    public static MeshAssetInfo DescribeStaticMesh(UStaticMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = new List<MeshLodInfo>();
        var srcLods = mesh.RenderData?.LODs ?? [];
        for (var i = 0; i < srcLods.Length; i++)
        {
            var l = srcLods[i];
            var ib = l.IndexBuffer;
            lods.Add(new MeshLodInfo(
                i,
                l.PositionVertexBuffer?.Verts.Length ?? 0,
                ib?.Length ?? 0,
                l.Sections.Length,
                ib is { Indices32.Length: > 0 },
                l.VertexBuffer?.NumTexCoords ?? 0,
                l.SkipLod));
        }

        return new MeshAssetInfo(mesh.Name, MeshAssetKind.Static, ToBox(mesh.RenderData?.Bounds), StaticSlots(mesh), lods, 0);
    }

    /// <summary>Describes a skeletal mesh.</summary>
    public static MeshAssetInfo DescribeSkeletalMesh(USkeletalMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lods = new List<MeshLodInfo>();
        var srcLods = mesh.LODModels ?? [];
        for (var i = 0; i < srcLods.Length; i++)
        {
            var l = srcLods[i];
            var indices = l.Indices;
            var count = indices is null ? 0 : Math.Max(indices.Indices16.Length, indices.Indices32.Length);
            var vertexCount = l.VertexBufferGPUSkin?.GetVertexCount() ?? 0;
            if (vertexCount == 0)
            {
                vertexCount = l.Sections.Sum(s => s.SoftVertices?.Length ?? 0);
            }

            lods.Add(new MeshLodInfo(i, vertexCount, count, l.Sections.Length, indices is { Indices32.Length: > 0 }, l.NumTexCoords, l.SkipLod));
        }

        var bones = mesh.ReferenceSkeleton?.FinalRefBoneInfo?.Length ?? 0;
        return new MeshAssetInfo(mesh.Name, MeshAssetKind.Skeletal, ToBox(mesh.ImportedBounds), SkeletalSlots(mesh), lods, bones);
    }

    /// <summary>
    /// <paramref name="lod"/>, or the next LOD after it that has render data. SCUM cooks some meshes with their finest LODs
    /// stripped (MinLOD 1: the hangar chairs, wheelie bins, wall lights, lavender); the game draws the first LOD it has.
    /// Failing on LOD 0 left about 193,000 placements off the map (Discord: a green wheelie bin, chairs) and the Assets
    /// page could not export those meshes ("some static meshes can't be exported").
    /// </summary>
    private static int WithRenderData(int lod, int count, Func<int, bool> stripped)
    {
        while (lod >= 0 && lod < count - 1 && stripped(lod))
        {
            lod++;
        }

        return lod;
    }

    private static void CheckLod(string name, int lod, int count, bool stripped)
    {
        if (lod < 0 || lod >= count)
        {
            throw new InvalidDataException($"{name} has {count} LOD(s); LOD {lod} does not exist.");
        }

        if (stripped)
        {
            throw new InvalidDataException($"{name} LOD {lod} has no render data (stripped).");
        }
    }

    private static MeshData Build(string name, IReadOnlyList<CMeshVertex> verts, int vertexCount, FRawStaticIndexBuffer indexBuffer,
        IReadOnlyList<CMeshSection> sections, IReadOnlyList<MeshMaterialSlot> slots)
    {
        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var uvs = new float[vertexCount * 2];
        for (var i = 0; i < vertexCount; i++)
        {
            var v = verts[i];
            positions[i * 3] = v.Position.X;
            positions[i * 3 + 1] = v.Position.Y;
            positions[i * 3 + 2] = v.Position.Z;
            var n = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
            var len = n.Length();
            n = len > 1e-6f ? n / len : Vector3.UnitZ;
            normals[i * 3] = n.X;
            normals[i * 3 + 1] = n.Y;
            normals[i * 3 + 2] = n.Z;
            uvs[i * 2] = v.UV.U;
            uvs[i * 2 + 1] = v.UV.V;
        }

        var indices = ToUInt32(indexBuffer);
        var outSections = new List<MeshSection>(sections.Count);
        foreach (var s in sections)
        {
            var first = Math.Clamp(s.FirstIndex, 0, indices.Length);
            var count = Math.Clamp(s.NumFaces * 3, 0, indices.Length - first);
            if (count == 0)
            {
                continue;
            }

            outSections.Add(new MeshSection(SectionMaterialName(s, slots), first, count));
        }

        if (outSections.Count == 0 && indices.Length > 0)
        {
            outSections.Add(new MeshSection(string.Empty, 0, indices.Length));
        }

        return new MeshData(name, positions, normals, uvs, indices, outSections.ToArray(), BoundingBox.FromPositions(positions));
    }

    /// <summary>Widens a cooked index buffer (16- or 32-bit) to 32-bit indices.</summary>
    internal static uint[] ToUInt32(FRawStaticIndexBuffer buffer)
    {
        if (buffer.Indices32.Length > 0)
        {
            return (uint[])buffer.Indices32.Clone();
        }

        var result = new uint[buffer.Indices16.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = buffer.Indices16[i];
        }

        return result;
    }

    private static string SectionMaterialName(CMeshSection section, IReadOnlyList<MeshMaterialSlot> slots)
    {
        if (section.MaterialIndex >= 0 && section.MaterialIndex < slots.Count && slots[section.MaterialIndex].MaterialPath.Length > 0)
        {
            return slots[section.MaterialIndex].MaterialPath;
        }

        if (section.Material is { } m)
        {
            return PathOf(m);
        }

        return section.MaterialName ?? string.Empty;
    }

    private static IReadOnlyList<MeshMaterialSlot> StaticSlots(UStaticMesh mesh)
    {
        var list = new List<MeshMaterialSlot>();
        if (mesh.StaticMaterials is { Length: > 0 } statics)
        {
            for (var i = 0; i < statics.Length; i++)
            {
                var s = statics[i];
                var material = s.MaterialInterface ?? (i < mesh.Materials.Length ? mesh.Materials[i] : null);
                list.Add(new MeshMaterialSlot(i, NameText(s.MaterialSlotName.Text), material is null ? string.Empty : PathOf(material)));
            }
        }
        else
        {
            for (var i = 0; i < mesh.Materials.Length; i++)
            {
                var m = mesh.Materials[i];
                list.Add(new MeshMaterialSlot(i, string.Empty, m is null ? string.Empty : PathOf(m)));
            }
        }

        return list;
    }

    private static IReadOnlyList<MeshMaterialSlot> SkeletalSlots(USkeletalMesh mesh)
    {
        var list = new List<MeshMaterialSlot>();
        var mats = mesh.SkeletalMaterials ?? [];
        for (var i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            list.Add(new MeshMaterialSlot(i, NameText(m.MaterialSlotName.Text), m.Material is null ? string.Empty : PathOf(m.Material)));
        }

        return list;
    }

    private static string NameText(string? text) => string.IsNullOrEmpty(text) || text == "None" ? string.Empty : text;

    /// <summary>Object path (<c>/Game/A/B.B</c>) of a resolved import/export.</summary>
    internal static string PathOf(ResolvedObject obj)
    {
        try
        {
            return AssetPaths.NormalizeObjectPath(obj.GetPathName());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return obj.Name.Text;
        }
    }

    private static BoundingBox ToBox(FBoxSphereBounds? bounds)
    {
        if (bounds is null)
        {
            return BoundingBox.Empty;
        }

        var o = new Vector3(bounds.Origin.X, bounds.Origin.Y, bounds.Origin.Z);
        var e = new Vector3(bounds.BoxExtent.X, bounds.BoxExtent.Y, bounds.BoxExtent.Z);
        return new BoundingBox(o - e, o + e);
    }
}
