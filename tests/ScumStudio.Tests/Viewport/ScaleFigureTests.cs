using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>The 1.8 m person the 3D previews stand beside a model (<see cref="ScaleFigure"/>).</summary>
public sealed class ScaleFigureTests
{
    [Fact]
    public void FigureIsOnePointEightMetresTallWithItsFeetAtZero()
    {
        var mesh = ScaleFigure.Mesh;
        Assert.Empty(mesh.Validate());
        Assert.InRange(mesh.Bounds.Size.Z, 179.5f, 180.5f);
        Assert.Equal(0f, mesh.Bounds.Min.Z, 3);
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vector3 At(uint v) => new(mesh.Positions[v * 3], mesh.Positions[(v * 3) + 1], mesh.Positions[(v * 3) + 2]);
            var (a, b, c) = (At(mesh.Indices[i]), At(mesh.Indices[i + 1]), At(mesh.Indices[i + 2]));
            Assert.True(Vector3.Cross(b - a, c - a).Length() > 1e-3f, $"triangle {i / 3} is degenerate");
        }
    }

    [Fact]
    public void FigureStandsOnTheGroundBesideTheModelFacingTheCamera()
    {
        // A car in GL space (Y up): 4.12 m long, 1.52 m high, 1.86 m wide.
        var car = new BoundingBox(new Vector3(-206f, -76f, -93f), new Vector3(206f, 76f, 93f));
        var at = ScaleFigure.Placement(car, PreviewScene.DefaultViewYaw);
        Assert.Equal(car.Min.Y, Vector3.Transform(Vector3.Zero, at).Y, 3);

        // UE → GL swaps Y and Z; the figure's box must stay clear of the car's.
        var ue = ScaleFigure.Mesh.Bounds;
        var local = new BoundingBox(new Vector3(ue.Min.X, ue.Min.Z, ue.Min.Y), new Vector3(ue.Max.X, ue.Max.Z, ue.Max.Y));
        var figure = LevelSceneUploader.TransformBounds(local, at);
        var overlaps = figure.Min.X < car.Max.X && figure.Max.X > car.Min.X && figure.Min.Z < car.Max.Z && figure.Max.Z > car.Min.Z;
        Assert.False(overlaps, $"figure {figure} inside car {car}");

        // Its face (UE +X) turns to the default camera, which looks along (cos yaw, 0, sin yaw).
        var yaw = PreviewScene.DefaultViewYaw * MathF.PI / 180f;
        var toCamera = -new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Assert.True(Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, at)), toCamera) > 0.999f);
    }
}
