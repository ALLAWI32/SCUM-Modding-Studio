using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Vehicles studio's mouse in <see cref="MeshPreview"/> (owner: "every part of the car like Blender", decals "where I drop
/// them"): the cursor's ray in UE space for a decal, the pick id of a part, and the part gizmo's drag, with the Map's guard
/// against a ray running along an arrow.
/// </summary>
public sealed class MeshPreviewToolTests
{
    private const int Width = 800;
    private const int Height = 600;

    [Fact]
    public void TheCursorRayInUeSpaceMeetsTheUnprojectedPointAtItsDepth()
    {
        var camera = Camera();
        foreach (var (x, y) in new[] { (400f, 300f), (120f, 80f), (700f, 520f) })
        {
            // A point of the GL scene under the pixel (depth 0.97 of the depth buffer), in UE centimetres.
            var hit = UeToGl.ToUePoint(camera.Unproject(x, y, 0.97f, Width, Height, reverseZ: false));
            var (origin, direction) = MeshPreview.UeRay(camera, x, y, Width, Height);
            Assert.Equal(1f, direction.Size(), 3);
            var along = FVector.Dot(hit - origin, direction);
            Assert.True(along > 0f);
            Assert.InRange(FVector.Distance(origin + (direction * along), hit), 0f, 0.1f);
        }
    }

    [Fact]
    public void APickIdIsThePartIndexPlusOne()
    {
        Assert.Equal(0, MeshPreview.PartIndexOfPick(1));
        Assert.Equal(4, MeshPreview.PartIndexOfPick(5));
        Assert.Equal(-1, MeshPreview.PartIndexOfPick(0)); // the floor, the scale figure, the sky
    }

    [Fact]
    public void DraggingTheXArrowMovesThePartAsFarAsTheCursorWent()
    {
        var camera = Camera();
        var start = new FTransform(new FVector(20, -40, 60));
        var from = Pixel(camera, UeToGl.Point(start.Translation));
        var to = Pixel(camera, UeToGl.Point(start.Translation + new FVector(100, 0, 0)));
        Assert.InRange(Vector2.Distance(from, to), 50f, 400f); // a real drag on screen, not a few pixels

        var moved = MeshPreview.GizmoDrag(camera, Width, Height, start, GizmoAxis.X, from, to);
        Assert.NotNull(moved);
        Assert.Equal(120f, moved!.Value.Translation.X, 0.5f);
        Assert.Equal(-40f, moved.Value.Translation.Y, 0.01f);
        Assert.Equal(60f, moved.Value.Translation.Z, 0.01f);

        // Half the way on screen is not half the distance in perspective, but it is along X only and less than 100 cm.
        var half = MeshPreview.GizmoDrag(camera, Width, Height, start, GizmoAxis.X, from, Vector2.Lerp(from, to, 0.5f))!.Value;
        Assert.InRange(half.Translation.X - 20f, 1f, 99f);
        Assert.Equal(start.Rotation, half.Rotation);
    }

    [Fact]
    public void ARingAndACubeTurnAndScaleThePartAboutItsOrigin()
    {
        var camera = Camera();
        var start = new FTransform(new FVector(0, 0, 50));
        var centre = Pixel(camera, UeToGl.Point(start.Translation));
        var turned = MeshPreview.GizmoDrag(camera, Width, Height, start, GizmoAxis.ScaleUniform, centre, centre + new Vector2(75, 0))!.Value;
        Assert.Equal(1.5f, turned.Scale3D.X, 0.01f);
        Assert.Equal(start.Translation, turned.Translation);

        var ring = GizmoAxis.RotateZ;
        Assert.True(MeshPreview.Answers(PreviewGizmoMode.Rotate, ring));
        Assert.False(MeshPreview.Answers(PreviewGizmoMode.Move, ring));
        Assert.False(MeshPreview.Answers(PreviewGizmoMode.Rotate, GizmoAxis.RotateView));
        Assert.True(MeshPreview.Answers(PreviewGizmoMode.Move, GizmoAxis.X));
        Assert.True(MeshPreview.Answers(PreviewGizmoMode.Scale, GizmoAxis.ScaleUniform));
    }

    [Fact]
    public void ARayRunningAlongTheArrowDoesNotFlingThePart()
    {
        // The camera looks along the arrow of a part standing 1 m to the side; dragging towards where the arrow vanishes
        // gives a ray along it that meets it 500 m away (the Map's "objects ended up on the far side of the map").
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, 0f, 0f, 600f);
        camera.FitClipRange(600f, 300f);
        var along = UeToGl.ToUeDirection(camera.Forward);
        var (axis, side) = MathF.Abs(along.X) > MathF.Abs(along.Y) ? (GizmoAxis.X, new FVector(0, 100, 0)) : (GizmoAxis.Y, new FVector(100, 0, 0));
        var start = new FTransform(side);
        var from = Pixel(camera, UeToGl.Point(side));
        var far = Pixel(camera, UeToGl.Point(side + (along * 50_000f)));
        Assert.Null(MeshPreview.GizmoDrag(camera, Width, Height, start, axis, from, far));

        // A drag that ends 10 m along it still moves the part there.
        var near = Pixel(camera, UeToGl.Point(side + (along * 1_000f)));
        var moved = MeshPreview.GizmoDrag(camera, Width, Height, start, axis, from, near)!.Value;
        Assert.Equal(1_000f, FVector.Distance(moved.Translation, side), 5f);
        Assert.True(MeshPreview.PartReach < GizmoMath.MaxReach);
    }

    /// <summary>The pixel whose ray (<see cref="FlyCamera.ScreenRay"/> goes through pixel centres) passes through GL point <paramref name="gl"/>.</summary>
    private static Vector2 Pixel(FlyCamera camera, Vector3 gl) => MeshPreview.ScreenPoint(camera, gl, Width, Height)!.Value - new Vector2(0.5f);

    private static FlyCamera Camera()
    {
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, PreviewScene.DefaultViewYaw, -20f, 600f);
        camera.FitClipRange(600f, 300f);
        return camera;
    }
}
