using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;

namespace ScumStudio.Tests.Rendering;

public sealed class CameraMathTests
{
    public static TheoryData<float, float> Angles => new()
    {
        { 0f, 0f },
        { 90f, 0f },
        { -135f, -20f },
        { 30f, 45f },
        { 170f, -80f },
    };

    [Fact]
    public void YawZeroLooksAlongUeForwardWithUeRightOnTheRight()
    {
        var camera = new FlyCamera();
        RenderAssert.Near(Vector3.UnitX, camera.Forward);
        RenderAssert.Near(UeToGl.Direction(FVector.Right), camera.Right);
        RenderAssert.Near(Vector3.UnitY, camera.Up);

        camera.Yaw = 90f;
        RenderAssert.Near(UeToGl.Direction(FVector.Right), camera.Forward);
        camera.Yaw = 0f;
        camera.Pitch = 90f;
        Assert.Equal(FlyCamera.MaxPitch, camera.Pitch);
        Assert.True(camera.Forward.Y > 0.99f);
    }

    [Theory]
    [MemberData(nameof(Angles))]
    public void ViewMatrixMatchesSystemNumericsLookAtAndUeCamera(float yaw, float pitch)
    {
        var camera = new FlyCamera { Position = new Vector3(120f, 340f, -560f), Yaw = yaw, Pitch = pitch };
        var forward = camera.Forward;
        var expected = Matrix4x4.CreateLookAt(camera.Position, camera.Position + forward, Vector3.UnitY);
        // Position + Forward loses ~1e-4 of the direction at |Position| ≈ 500, so the oracle itself is off by ~0.02 in the translation row.
        RenderAssert.Near(expected, camera.ViewMatrix, 0.05f);

        // Same camera expressed in UE terms (location in cm, rotator) through the Core conversion.
        var ueLocation = UeToGl.ToUePoint(camera.Position);
        var ueView = UeToGl.CreateViewMatrix(ueLocation, new FRotator(pitch, yaw, 0f));
        RenderAssert.Near(ueView, camera.ViewMatrix, 1e-3f);

        // The view maps the eye to the origin and the view direction to -Z.
        RenderAssert.Near(Vector3.Zero, Vector3.Transform(camera.Position, camera.ViewMatrix), 1e-2f);
        RenderAssert.Near(-Vector3.UnitZ, Vector3.Normalize(Vector3.TransformNormal(forward, camera.ViewMatrix)));
    }

    [Theory]
    [InlineData(-88f)]
    [InlineData(-89f)]
    [InlineData(-89.5f)]
    [InlineData(89.5f)]
    public void ViewMatrix_FarFromTheOrigin_LookingStraightDown_StaysValid(float pitch)
    {
        // Island coordinates (GL x/z around -600 000 cm): Position + Forward used to lose the horizontal part of the view
        // direction, so CreateLookAt built a NaN matrix and top-down views rendered empty.
        var camera = new FlyCamera { Position = new Vector3(-622_000f, 450_000f, -556_000f), Yaw = 0f, Pitch = pitch };
        var view = camera.ViewMatrix;
        Assert.False(float.IsNaN(view.M11) || float.IsNaN(view.M22) || float.IsNaN(view.M33), view.ToString());
        RenderAssert.Near(-Vector3.UnitZ, Vector3.Normalize(Vector3.TransformNormal(camera.Forward, view)), 1e-4f);
        var below = camera.Position + (camera.Forward * 1000f);
        RenderAssert.Near(new Vector3(0f, 0f, -1000f), Vector3.Transform(below, view), 0.5f);
    }

    [Fact]
    public void ProjectionMatchesGlConventionAndSystemNumericsXy()
    {
        var camera = new FlyCamera { FieldOfView = 70f };
        camera.SetClipRange(5f, 5000f);
        const float aspect = 16f / 9f;
        var gl = camera.GetProjection(aspect);
        var fov = 70f * MathF.PI / 180f;
        RenderAssert.Near(UeToGl.CreatePerspectiveGl(fov, aspect, 5f, 5000f), gl);

        // X/Y scaling equals System.Numerics' (D3D depth) projection; only the depth terms differ.
        var d3d = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 5f, 5000f);
        RenderAssert.Near(d3d.M11, gl.M11);
        RenderAssert.Near(d3d.M22, gl.M22);
        Assert.Equal(-1f, gl.M34);

        // GL depth: near -> -1, far -> +1.
        RenderAssert.Near(-1f, NdcZ(gl, -5f));
        RenderAssert.Near(1f, NdcZ(gl, -5000f), 1e-3f);

        // Reverse-Z: near -> 1, far -> 0, monotonically decreasing.
        var rz = camera.GetReverseZProjection(aspect);
        RenderAssert.Near(1f, NdcZ(rz, -5f));
        RenderAssert.Near(0f, NdcZ(rz, -5000f), 1e-6f);
        Assert.True(NdcZ(rz, -50f) > NdcZ(rz, -500f));
        RenderAssert.Near(d3d.M11, rz.M11);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnprojectInvertsProjection(bool reverseZ)
    {
        var camera = new FlyCamera { Position = new Vector3(10f, 200f, 30f), Yaw = 25f, Pitch = -15f };
        camera.SetClipRange(1f, 10_000f);
        const int w = 800;
        const int h = 600;
        var world = camera.Position + (camera.Forward * 700f) + (camera.Right * 90f) + (camera.Up * -40f);
        var proj = reverseZ ? camera.GetReverseZProjection((float)w / h) : camera.GetProjection((float)w / h);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * proj);
        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        var px = ((ndc.X + 1f) * 0.5f * w) - 0.5f;
        var py = ((1f - ndc.Y) * 0.5f * h) - 0.5f;
        var depth = reverseZ ? ndc.Z : (ndc.Z * 0.5f) + 0.5f;
        RenderAssert.Near(world, camera.Unproject(px, py, depth, w, h, reverseZ), 0.5f);
    }

    [Fact]
    public void ScreenRayThroughCentreFollowsForward()
    {
        var camera = new FlyCamera { Position = new Vector3(1f, 2f, 3f), Yaw = 60f, Pitch = 10f };
        var (origin, direction) = camera.ScreenRay(399.5f, 299.5f, 800, 600);
        RenderAssert.Near(camera.Forward, direction, 1e-3f);
        RenderAssert.Near(camera.Position + (camera.Forward * camera.NearPlane), origin, 1e-2f);
    }

    [Fact]
    public void WasdAndMouseDeltaMoveTheCamera()
    {
        var camera = new FlyCamera { MoveSpeed = 100f, MouseSensitivity = 0.5f };
        camera.Update(new FlyInput(Forward: true, Backward: false, Left: false, Right: false), 1f);
        RenderAssert.Near(new Vector3(100f, 0f, 0f), camera.Position);
        camera.Update(new FlyInput(false, false, Left: false, Right: true, Fast: true), 0.5f);
        RenderAssert.Near(new Vector3(100f, 0f, 250f), camera.Position);
        camera.Update(new FlyInput(false, false, false, false, Up: true), 1f);
        RenderAssert.Near(new Vector3(100f, 100f, 250f), camera.Position);

        camera.Rotate(180f, 20f); // 90 degrees right, 10 degrees down
        RenderAssert.Near(90f, camera.Yaw);
        RenderAssert.Near(-10f, camera.Pitch);
        camera.Rotate(540f, 0f); // wraps into (-180, 180]
        RenderAssert.Near(0f, camera.Yaw, 1e-3f);
    }

    [Fact]
    public void FrameKeepsBoundsInsideTheFrustum()
    {
        var camera = new FlyCamera();
        var bounds = new BoundingBox(new Vector3(-210f, 0f, -90f), new Vector3(210f, 130f, 90f));
        camera.Frame(bounds, 16f / 9f);
        var frustum = camera.GetFrustum(16f / 9f);
        foreach (var corner in Corners(bounds))
        {
            Assert.True(frustum.Contains(corner), $"Corner {corner} is outside the framed view.");
        }

        Assert.True(camera.NearPlane < Vector3.Distance(camera.Position, bounds.Center) - bounds.Extent.Length());
    }

    [Fact]
    public void InvalidParametersThrow()
    {
        var camera = new FlyCamera();
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.FieldOfView = 0f);
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.SetClipRange(10f, 5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.NearPlane = camera.FarPlane * 2f);
        Assert.Throws<ArgumentOutOfRangeException>(() => FlyCamera.CreateReverseZPerspective(1f, 0f, 1f, 2f));
    }

    internal static IEnumerable<Vector3> Corners(BoundingBox b)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        }
    }

    private static float NdcZ(in Matrix4x4 projection, float viewZ)
    {
        var clip = Vector4.Transform(new Vector4(0f, 0f, viewZ, 1f), projection);
        return clip.Z / clip.W;
    }
}
