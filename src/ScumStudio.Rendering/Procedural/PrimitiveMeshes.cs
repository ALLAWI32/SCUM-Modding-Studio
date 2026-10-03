using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.Procedural;

/// <summary>Small procedural meshes in the renderer's GL world (Y up, counter-clockwise front faces).</summary>
public static class PrimitiveMeshes
{
    /// <summary>An axis-aligned cube centred on the origin with per-face normals and UVs (24 vertices, 12 triangles).</summary>
    /// <param name="size">Edge length.</param>
    public static MeshData Cube(float size = 100f)
    {
        var h = size * 0.5f;
        // Each face: normal, then 4 corners counter-clockwise when seen from outside.
        (float[] N, float[][] C)[] faces =
        [
            ([1, 0, 0], [[h, -h, h], [h, -h, -h], [h, h, -h], [h, h, h]]),
            ([-1, 0, 0], [[-h, -h, -h], [-h, -h, h], [-h, h, h], [-h, h, -h]]),
            ([0, 1, 0], [[-h, h, h], [h, h, h], [h, h, -h], [-h, h, -h]]),
            ([0, -1, 0], [[-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]]),
            ([0, 0, 1], [[-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h]]),
            ([0, 0, -1], [[h, -h, -h], [-h, -h, -h], [-h, h, -h], [h, h, -h]]),
        ];
        float[][] uv = [[0, 1], [1, 1], [1, 0], [0, 0]];
        var positions = new List<float>(72);
        var normals = new List<float>(72);
        var uvs = new List<float>(48);
        var indices = new List<uint>(36);
        foreach (var (n, corners) in faces)
        {
            var baseIndex = (uint)(positions.Count / 3);
            for (var i = 0; i < 4; i++)
            {
                positions.AddRange(corners[i]);
                normals.AddRange(n);
                uvs.AddRange(uv[i]);
            }

            indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
        }

        return MeshData.Create("Cube", [.. positions], [.. indices], [.. normals], [.. uvs]);
    }

    /// <summary>A horizontal square (Y = 0) facing +Y, centred on the origin.</summary>
    public static MeshData Plane(float size = 100f)
    {
        var h = size * 0.5f;
        float[] positions = [-h, 0, h, h, 0, h, h, 0, -h, -h, 0, -h];
        float[] normals = [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0];
        float[] uvs = [0, 1, 1, 1, 1, 0, 0, 0];
        return MeshData.Create("Plane", positions, [0, 1, 2, 0, 2, 3], normals, uvs);
    }

    /// <summary>A checkerboard RGBA8 texture (top-down rows).</summary>
    public static byte[] Checkerboard(int size, int cells, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        if (size <= 0 || cells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var pixels = new byte[size * size * 4];
        var cell = Math.Max(1, size / cells);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var c = ((x / cell) + (y / cell)) % 2 == 0 ? a : b;
                var o = ((y * size) + x) * 4;
                pixels[o] = c.R;
                pixels[o + 1] = c.G;
                pixels[o + 2] = c.B;
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }
}
