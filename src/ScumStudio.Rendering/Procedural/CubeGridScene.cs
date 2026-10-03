using System.Numerics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Rendering.Procedural;

/// <summary>
/// The procedural test scene: a <c>columns x rows</c> grid of cubes with varying heights and colours, one node per cube,
/// selectable ids <c>1..columns*rows</c> in row-major order (id = row * columns + column + 1).
/// </summary>
public sealed class CubeGridScene
{
    private CubeGridScene(Scene scene, int columns, int rows, float spacing, float cubeSize)
    {
        Scene = scene;
        Columns = columns;
        Rows = rows;
        Spacing = spacing;
        CubeSize = cubeSize;
    }

    /// <summary>The scene.</summary>
    public Scene Scene { get; }

    /// <summary>Cubes along X.</summary>
    public int Columns { get; }

    /// <summary>Cubes along Z.</summary>
    public int Rows { get; }

    /// <summary>Centre distance between neighbouring cubes.</summary>
    public float Spacing { get; }

    /// <summary>Cube edge length before per-instance height scaling.</summary>
    public float CubeSize { get; }

    /// <summary>Number of cube instances.</summary>
    public int Count => Columns * Rows;

    /// <summary>Builds the scene using an already uploaded cube mesh (e.g. <see cref="PrimitiveMeshes.Cube"/>).</summary>
    public static CubeGridScene Build(MeshHandle cube, int columns = 64, int rows = 64, float spacing = 200f)
    {
        ArgumentNullException.ThrowIfNull(cube);
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        var cubeSize = cube.Bounds.Size.X;
        var scene = new Scene();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var id = (uint)((r * columns) + c + 1);
                var height = 0.5f + (1.5f * Hash01(c, r));
                var position = new Vector3(c * spacing, cubeSize * height * 0.5f, r * spacing);
                var transform = Matrix4x4.CreateScale(1f, height, 1f) * Matrix4x4.CreateTranslation(position);
                var node = scene.Add(cube, transform, id, $"Cube_{c}_{r}");
                node.Tint = Palette(c, r, columns, rows);
            }
        }

        return new CubeGridScene(scene, columns, rows, spacing, cubeSize);
    }

    /// <summary>World-space centre of the grid footprint (at ground level).</summary>
    public Vector3 Center => new((Columns - 1) * Spacing * 0.5f, 0f, (Rows - 1) * Spacing * 0.5f);

    /// <summary>Selectable id of the cube at (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public uint IdAt(int column, int row) => (uint)((row * Columns) + column + 1);

    /// <summary>Base centre of the cube at (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public Vector3 PositionAt(int column, int row) => new(column * Spacing, 0f, row * Spacing);

    /// <summary>
    /// Points <paramref name="camera"/> from above and to the side at the grid centre, so that the image centre shows
    /// the cube nearest to <see cref="Center"/>.
    /// </summary>
    public void FrameCamera(FlyCamera camera, float yaw = 45f, float pitch = -35f)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var (column, row) = CenterCell;
        var target = PositionAt(column, row) + new Vector3(0f, CubeSize * 0.5f, 0f);
        var extent = MathF.Max(Columns, Rows) * Spacing;
        camera.SetClipRange(10f, MathF.Max(extent * 4f, 10_000f));
        camera.Orbit(target, yaw, pitch, MathF.Max(extent * 0.35f, Spacing * 4f));
    }

    /// <summary>Grid cell closest to <see cref="Center"/>.</summary>
    public (int Column, int Row) CenterCell => ((Columns - 1) / 2, (Rows - 1) / 2);

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            var h = (uint)((x * 73856093) ^ (y * 19349663));
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }

    private static Vector4 Palette(int c, int r, int columns, int rows)
    {
        var u = columns > 1 ? (float)c / (columns - 1) : 0.5f;
        var v = rows > 1 ? (float)r / (rows - 1) : 0.5f;
        return new Vector4(0.25f + (0.7f * u), 0.35f + (0.3f * Hash01(r, c)), 0.25f + (0.7f * v), 1f);
    }
}
