using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Procedural;

namespace ScumStudio.Viewport;

/// <summary>
/// The 1.8 m person the 3D previews stand beside a model so its size reads at a glance: a low-poly figure of boxes in UE
/// space (centimetres, facing +X, feet at Z 0).
/// </summary>
public static class ScaleFigure
{
    /// <summary>Height of the figure (cm).</summary>
    public const float Height = 180f;

    /// <summary>
    /// Models whose largest edge is shorter than this (cm) get no figure: next to a 1.8 m person a bullet or a key would be
    /// a speck; the centimetre grid and the size readout give their scale.
    /// </summary>
    public const float MinModelSize = 50f;

    /// <summary>Half the figure's width, arm to arm (cm).</summary>
    private const float HalfWidth = 31f;

    /// <summary>The figure.</summary>
    public static MeshData Mesh { get; } = Build();

    /// <summary>
    /// Where the figure stands for a model with GL-space bounds <paramref name="model"/> seen from <paramref name="viewYaw"/>
    /// (the previews' default camera yaw): on the model's ground, just beside it on the view's right, turned to the camera.
    /// </summary>
    public static Matrix4x4 Placement(BoundingBox model, float viewYaw)
    {
        var (sy, cy) = MathF.SinCos(viewYaw * UeMath.DegreesToRadians);
        var right = new Vector3(-sy, 0f, cy); // FlyCamera.Right at that yaw
        var half = model.Extent;
        var reach = (MathF.Abs(right.X) * half.X) + (MathF.Abs(right.Z) * half.Z);
        var gap = MathF.Max(25f, MathF.Max(model.Size.X, MathF.Max(model.Size.Y, model.Size.Z)) * 0.05f);
        var at = model.Center + (right * (reach + gap + HalfWidth));
        at.Y = model.Min.Y;
        // Its +X (UE forward) towards the camera: the opposite of the camera's horizontal forward (cos yaw, 0, sin yaw).
        return Matrix4x4.CreateRotationY(MathF.Atan2(sy, -cy)) * Matrix4x4.CreateTranslation(at);
    }

    private static MeshData Build()
    {
        // Centre and size (x forward, y right, z up) of each box, in cm: feet, legs, hips, chest, arms, neck, head, nose.
        (Vector3 Centre, Vector3 Size)[] boxes =
        [
            (new(4f, -10f, 3.5f), new(26f, 10f, 7f)),
            (new(4f, 10f, 3.5f), new(26f, 10f, 7f)),
            (new(0f, -10f, 47f), new(13f, 13f, 80f)),
            (new(0f, 10f, 47f), new(13f, 13f, 80f)),
            (new(0f, 0f, 93f), new(20f, 34f, 14f)),
            (new(0f, 0f, 126f), new(22f, 40f, 52f)),
            (new(0f, -26f, 119f), new(10f, 10f, 62f)),
            (new(0f, 26f, 119f), new(10f, 10f, 62f)),
            (new(0f, 0f, 156f), new(10f, 10f, 8f)),
            (new(1f, 0f, 170f), new(20f, 17f, 20f)),
            (new(12f, 0f, 168f), new(4f, 3f, 4f)),
        ];
        var cube = PrimitiveMeshes.Cube(1f);
        var positions = new List<float>(boxes.Length * cube.Positions.Length);
        var normals = new List<float>(boxes.Length * cube.Normals.Length);
        var uvs = new List<float>(boxes.Length * cube.Uv0.Length);
        var indices = new List<uint>(boxes.Length * cube.Indices.Length);
        foreach (var (centre, size) in boxes)
        {
            var first = (uint)(positions.Count / 3);
            for (var i = 0; i < cube.Positions.Length; i += 3)
            {
                positions.Add((cube.Positions[i] * size.X) + centre.X);
                positions.Add((cube.Positions[i + 1] * size.Y) + centre.Y);
                positions.Add((cube.Positions[i + 2] * size.Z) + centre.Z);
            }

            normals.AddRange(cube.Normals); // axis-aligned faces keep their normals under an axis-aligned stretch
            uvs.AddRange(cube.Uv0);
            indices.AddRange(cube.Indices.Select(index => index + first));
        }

        return MeshData.Create("ScaleFigure", [.. positions], [.. indices], [.. normals], [.. uvs]);
    }
}
