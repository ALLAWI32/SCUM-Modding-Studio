using ScumStudio.Core.Geometry;

namespace ScumStudio.Assets.Export;

/// <summary>Applies <see cref="MeshExportOptions"/> axis and unit conversion to mesh arrays.</summary>
internal static class MeshTransform
{
    /// <summary>
    /// Transformed positions and normals. The (x, y, z) -> (x, z, y) swap is a reflection: it maps UE's clockwise front faces to
    /// counter-clockwise ones in the right-handed output frame, so triangle index order is kept as is. Without the swap (UE axes
    /// kept) the order is reversed per triangle, so right-handed consumers still see counter-clockwise front faces.
    /// </summary>
    public static (float[] Positions, float[] Normals, bool FlipWinding) Apply(MeshData mesh, MeshExportOptions options)
    {
        var n = mesh.VertexCount;
        var positions = new float[n * 3];
        var normals = options.IncludeNormals && mesh.Normals.Length == n * 3 ? new float[n * 3] : [];
        for (var i = 0; i < n; i++)
        {
            var x = mesh.Positions[i * 3];
            var y = mesh.Positions[i * 3 + 1];
            var z = mesh.Positions[i * 3 + 2];
            Write(positions, i, x * options.Scale, y * options.Scale, z * options.Scale, options.ConvertToYUp);
            if (normals.Length > 0)
            {
                Write(normals, i, mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2], options.ConvertToYUp);
            }
        }

        return (positions, normals, !options.ConvertToYUp);
    }

    private static void Write(float[] target, int i, float x, float y, float z, bool yUp)
    {
        target[i * 3] = x;
        target[i * 3 + 1] = yUp ? z : y;
        target[i * 3 + 2] = yUp ? y : z;
    }

    /// <summary>Returns the index of a triangle corner, swapping corners 1 and 2 when <paramref name="flip"/> is set.</summary>
    public static uint Corner(uint[] indices, int triangleStart, int corner, bool flip) =>
        indices[triangleStart + (flip && corner != 0 ? 3 - corner : corner)];

    /// <summary>A file-name-safe material name from an object path (<c>/Game/A/MI_X.MI_X</c> gives <c>MI_X</c>).</summary>
    public static string MaterialLabel(string materialPath, int sectionIndex)
    {
        if (string.IsNullOrWhiteSpace(materialPath))
        {
            return "Section_" + sectionIndex;
        }

        var name = materialPath[(materialPath.LastIndexOfAny(['/', '.', ':']) + 1)..];
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return chars.Length == 0 ? "Section_" + sectionIndex : new string(chars);
    }
}
