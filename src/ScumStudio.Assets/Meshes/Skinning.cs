using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Assets.Meshes;

/// <summary>A bone of a skeletal mesh's reference skeleton: name, parent (-1 for the root) and bind pose relative to the parent.</summary>
public sealed record SkinBone(string Name, int ParentIndex, FTransform Local);

/// <summary>
/// The bone influences of one LOD of a skeletal mesh, in the LOD's vertex order (the order <see cref="MeshExtractor"/>
/// extracts): vertex <c>i</c> is weighted by <see cref="BoneIndices"/>/<see cref="Weights"/> from <see cref="Offsets"/>[i]
/// to [i + 1]; bone indices refer to <see cref="Bones"/>.
/// </summary>
public sealed record SkinWeights(IReadOnlyList<SkinBone> Bones, int[] Offsets, int[] BoneIndices, float[] Weights)
{
    /// <summary>Number of vertices the weights cover.</summary>
    public int VertexCount => Offsets.Length - 1;
}

/// <summary>
/// CPU skinning: poses a bind-pose mesh by its bone weights. A pose is the mesh-space transform of each bone by name
/// (<see cref="Pose"/> builds one from a skeleton and an animation frame's bone-local transforms); a mesh follows the bones
/// it has in the pose, any other bone keeps its bind pose under its parent, so a head or hair mesh rigged on a subset
/// of the body's skeleton follows the body's pose bone by bone.
/// </summary>
public static class Skinning
{
    /// <summary>Mesh-space (bind pose) transform of every bone: its local composed with its parents'.</summary>
    public static FTransform[] BindWorlds(IReadOnlyList<SkinBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        var worlds = new FTransform[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var parent = bones[i].ParentIndex;
            worlds[i] = parent >= 0 && parent < i ? bones[i].Local * worlds[parent] : bones[i].Local;
        }

        return worlds;
    }

    /// <summary>
    /// The mesh-space transform of every bone in a pose: a bone named in <paramref name="pose"/> takes that transform,
    /// any other follows its parent with its bind-pose local.
    /// </summary>
    public static FTransform[] PosedWorlds(IReadOnlyList<SkinBone> bones, IReadOnlyDictionary<string, FTransform> pose)
    {
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(pose);
        var worlds = new FTransform[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var parent = bones[i].ParentIndex;
            worlds[i] = pose.TryGetValue(bones[i].Name, out var posed) ? posed
                : parent >= 0 && parent < i ? bones[i].Local * worlds[parent] : bones[i].Local;
        }

        return worlds;
    }

    /// <summary>
    /// A pose (mesh-space transform by bone name) of <paramref name="bones"/> from an animation frame's bone-local
    /// transforms (<paramref name="locals"/>, by bone name; <see cref="AnimationPose.Locals"/>). The animation gives
    /// every bone its rotation; translations stay the mesh's own (its bone lengths) except for the root and its children
    /// (the pelvis: its height in the pose), so an animation made for the shared skeleton fits a body of other proportions.
    /// </summary>
    public static Dictionary<string, FTransform> Pose(IReadOnlyList<SkinBone> bones, IReadOnlyDictionary<string, FTransform> locals)
    {
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(locals);
        var worlds = new FTransform[bones.Count];
        var pose = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var local = bone.Local;
            if (locals.TryGetValue(bone.Name, out var animated))
            {
                local = new FTransform(animated.Rotation, bone.ParentIndex <= 0 ? animated.Translation : local.Translation, local.Scale3D);
            }

            worlds[i] = bone.ParentIndex >= 0 && bone.ParentIndex < i ? local * worlds[bone.ParentIndex] : local;
            pose[bone.Name] = worlds[i];
        }

        return pose;
    }

    /// <summary>
    /// <paramref name="mesh"/> (one LOD, the vertex order of <paramref name="weights"/>) moved from the bind pose into
    /// <paramref name="pose"/>: positions and normals blended over each vertex's bones. The mesh itself when the pose
    /// names none of its bones or the weights do not match it.
    /// </summary>
    public static MeshData Skin(MeshData mesh, SkinWeights weights, IReadOnlyDictionary<string, FTransform> pose)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(pose);
        if (weights.VertexCount != mesh.VertexCount || !weights.Bones.Any(b => pose.ContainsKey(b.Name)))
        {
            return mesh;
        }

        var bind = BindWorlds(weights.Bones);
        var posed = PosedWorlds(weights.Bones, pose);
        var inverseBind = bind.Select(b => b.Inverse()).ToArray();
        var positions = new float[mesh.Positions.Length];
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;
        var normals = hasNormals ? new float[mesh.Normals.Length] : [];
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var p = new FVector(mesh.Positions[v * 3], mesh.Positions[(v * 3) + 1], mesh.Positions[(v * 3) + 2]);
            var n = hasNormals ? new FVector(mesh.Normals[v * 3], mesh.Normals[(v * 3) + 1], mesh.Normals[(v * 3) + 2]) : FVector.Zero;
            var sumP = FVector.Zero;
            var sumN = FVector.Zero;
            var total = 0f;
            for (var k = weights.Offsets[v]; k < weights.Offsets[v + 1]; k++)
            {
                var b = weights.BoneIndices[k];
                var w = weights.Weights[k];
                if (b < 0 || b >= bind.Length || w <= 0f)
                {
                    continue;
                }

                sumP += posed[b].TransformPosition(inverseBind[b].TransformPosition(p)) * w;
                sumN += posed[b].Rotation.RotateVector(bind[b].Rotation.UnrotateVector(n)) * w;
                total += w;
            }

            if (total <= 0f)
            {
                sumP = p;
                sumN = n;
                total = 1f;
            }

            sumP /= total;
            positions[v * 3] = sumP.X;
            positions[(v * 3) + 1] = sumP.Y;
            positions[(v * 3) + 2] = sumP.Z;
            if (hasNormals)
            {
                var len = sumN.Size();
                sumN = len > 1e-6f ? sumN / len : n;
                normals[v * 3] = sumN.X;
                normals[(v * 3) + 1] = sumN.Y;
                normals[(v * 3) + 2] = sumN.Z;
            }
        }

        return mesh with { Positions = positions, Normals = normals, Bounds = BoundingBox.FromPositions(positions) };
    }
}
