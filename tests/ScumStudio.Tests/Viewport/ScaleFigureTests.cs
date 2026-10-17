using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The person the 3D previews stand beside a model (<see cref="ScaleFigure"/>): the game's prisoner, turned to face +X,
/// feet on the ground, never a figure of boxes (owner, 2026-10-17: "the box man looks bad").
/// </summary>
public sealed class ScaleFigureTests
{
    [Fact]
    public void TheGamesCharacterIsStoodUpFacingForwardWithItsFeetAtZero()
    {
        // A skeletal mesh as the game has it: facing +Y (its nose at +Y), feet a little below the origin, 1.78 m tall.
        var stood = ScaleFigure.Stand(Character(178f));
        var part = Assert.Single(stood.Parts);
        Assert.Equal(FTransform.Identity, part.Transform);
        Assert.Equal("T_Body", part.TexturePath);
        var box = ScaleFigure.Bounds(stood);
        Assert.Equal(0f, box.Min.Z, 2);
        Assert.Equal(178f, box.Size.Z, 1); // within 5 % of 1.8 m: kept as the game made it
        Assert.Equal(0f, box.Center.X, 1);
        Assert.Equal(0f, box.Center.Y, 1);

        // The nose (the vertex furthest along the mesh's +Y) now points along +X.
        var nose = Enumerable.Range(0, part.Mesh.VertexCount).MaxBy(i => part.Mesh.Positions[(i * 3) + 2] > 150f ? part.Mesh.Positions[i * 3] : float.MinValue);
        Assert.True(part.Mesh.Positions[nose * 3] > 10f, "the face turns to +X");
        Assert.InRange(MathF.Abs(part.Mesh.Positions[(nose * 3) + 1]), 0f, 2f);

        // A figure far off the game's height (a wrong mesh, a centimetre/metre mix-up) is drawn at 1.8 m.
        Assert.Equal(ScaleFigure.Height, ScaleFigure.Bounds(ScaleFigure.Stand(Character(2.4f))).Size.Z, 1);
    }

    [Fact]
    public void FigureStandsOnTheGroundBesideTheModelFacingTheCamera()
    {
        // A car in GL space (Y up): 4.12 m long, 1.52 m high, 1.86 m wide.
        var car = new BoundingBox(new Vector3(-206f, -76f, -93f), new Vector3(206f, 76f, 93f));
        var at = ScaleFigure.Placement(car, PreviewScene.DefaultViewYaw);
        Assert.Equal(car.Min.Y, Vector3.Transform(Vector3.Zero, at).Y, 3);

        // UE → GL swaps Y and Z; the figure's box (its shoulders) must stay clear of the car's.
        var ue = new BoundingBox(new Vector3(-15f, -31f, 0f), new Vector3(15f, 31f, 180f));
        var local = new BoundingBox(new Vector3(ue.Min.X, ue.Min.Z, ue.Min.Y), new Vector3(ue.Max.X, ue.Max.Z, ue.Max.Y));
        var figure = LevelSceneUploader.TransformBounds(local, at);
        var overlaps = figure.Min.X < car.Max.X && figure.Max.X > car.Min.X && figure.Min.Z < car.Max.Z && figure.Max.Z > car.Min.Z;
        Assert.False(overlaps, $"figure {figure} inside car {car}");

        // Its face (UE +X) turns to the default camera, which looks along (cos yaw, 0, sin yaw).
        var yaw = PreviewScene.DefaultViewYaw * MathF.PI / 180f;
        var toCamera = -new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Assert.True(Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, at)), toCamera) > 0.999f);
    }

    /// <summary>A stand-in character mesh <paramref name="height"/> cm tall, facing +Y (a head with a nose), feet at Z -2.</summary>
    internal static PreviewModel Character(float height)
    {
        var body = PrimitiveMeshes.Cube(1f);
        var positions = new List<float>();
        var indices = new List<uint>();
        void Box(Vector3 centre, Vector3 size)
        {
            var first = (uint)(positions.Count / 3);
            for (var i = 0; i < body.Positions.Length; i += 3)
            {
                positions.Add((body.Positions[i] * size.X) + centre.X);
                positions.Add((body.Positions[i + 1] * size.Y) + centre.Y);
                positions.Add((body.Positions[i + 2] * size.Z) + centre.Z);
            }

            indices.AddRange(body.Indices.Select(index => index + first));
        }

        var h = height / 178f;
        Box(new Vector3(0f, 0f, (89f * h) - 2f), new Vector3(40f, 24f, 178f) * h);
        Box(new Vector3(0f, 16f * h, (165f * h) - 2f), new Vector3(3f, 6f, 3f) * h); // the nose, at +Y
        var mesh = MeshData.Create("SK_Test", [.. positions], [.. indices]);
        return new PreviewModel("SK_Test", [new PreviewPart("SK_Test", mesh, "T_Body", FTransform.Identity)],
            new Dictionary<string, TextureImage> { ["T_Body"] = new("T_Body", 1, 1, [200, 90, 40, 255], "PF_B8G8R8A8", 0, 1, 1, true, false) });
    }
}
