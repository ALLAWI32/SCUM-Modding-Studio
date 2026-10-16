using System.Numerics;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>seviscache #6: views along the world's axes (Blender's numpad). The angles, the other side, and where the camera ends up.</summary>
public sealed class ViewPresetsTests
{
    [Theory]
    [InlineData(ViewPreset.Front, 1f, 0f, 0f)]
    [InlineData(ViewPreset.Back, -1f, 0f, 0f)]
    [InlineData(ViewPreset.Right, 0f, 1f, 0f)]
    [InlineData(ViewPreset.Left, 0f, -1f, 0f)]
    public void TheSideViewsLookLevelAlongTheirAxis(ViewPreset preset, float x, float y, float z)
    {
        var camera = new FlyCamera { Yaw = 37f, Pitch = -40f };
        (camera.Yaw, camera.Pitch) = ViewPresets.Angles(preset, camera.Yaw);

        var forward = UeToGl.ToUeDirection(camera.Forward); // the direction in UE axes
        Assert.Equal(x, forward.X, 1e-4f);
        Assert.Equal(y, forward.Y, 1e-4f);
        Assert.Equal(z, forward.Z, 1e-4f);
        Assert.Equal(0f, camera.Up.X, 1e-4f); // level: no roll, up is up
        Assert.Equal(1f, camera.Up.Y, 1e-4f);
    }

    [Theory]
    [InlineData(37f, 0f)]
    [InlineData(-50f, -90f)]
    [InlineData(130f, 90f)]
    [InlineData(-170f, 180f)]
    [InlineData(179f, 180f)]
    public void TopLooksStraightDownWithTheHeadingTurnedToAQuarter(float yaw, float quarter)
    {
        var (topYaw, topPitch) = ViewPresets.Angles(ViewPreset.Top, yaw);
        var (bottomYaw, bottomPitch) = ViewPresets.Angles(ViewPreset.Bottom, yaw);

        Assert.Equal(quarter, topYaw, 1e-4f);
        Assert.Equal(quarter, bottomYaw, 1e-4f);
        Assert.Equal(-FlyCamera.MaxPitch, topPitch);
        Assert.Equal(FlyCamera.MaxPitch, bottomPitch);
        var camera = new FlyCamera { Yaw = topYaw, Pitch = topPitch };
        Assert.True(camera.Forward.Y < -0.9999f, $"down: {camera.Forward}");
        Assert.True(MathF.Abs(camera.Right.X) > 0.999f || MathF.Abs(camera.Right.Z) > 0.999f, $"the screen's right lies on a world axis: {camera.Right}");
    }

    [Fact]
    public void TheOtherSideFlipsTheLineOfSight()
    {
        foreach (var preset in Enum.GetValues<ViewPreset>())
        {
            var other = ViewPresets.Opposite(preset);
            Assert.NotEqual(preset, other);
            Assert.Equal(preset, ViewPresets.Opposite(other));
            var a = new FlyCamera();
            (a.Yaw, a.Pitch) = ViewPresets.Angles(preset, 10f);
            var b = new FlyCamera();
            (b.Yaw, b.Pitch) = ViewPresets.Angles(other, 10f);
            Assert.True(Vector3.Dot(a.Forward, b.Forward) < -0.999f, $"{preset} / {other}: {a.Forward} {b.Forward}");
        }

        // Numpad 9 from any view: the same line, looked along the other way.
        var camera = new FlyCamera { Yaw = 160f, Pitch = -30f };
        var before = camera.Forward;
        (camera.Yaw, camera.Pitch) = ViewPresets.Opposite(camera.Yaw, camera.Pitch);
        Assert.Equal(-before.X, camera.Forward.X, 1e-4f);
        Assert.Equal(-before.Y, camera.Forward.Y, 1e-4f);
        Assert.Equal(-before.Z, camera.Forward.Z, 1e-4f);
        Assert.InRange(camera.Yaw, -180f, 180f);
    }

    [Fact]
    public void OrbitingToAPresetKeepsTheTargetInTheMiddleAtTheSameDistance()
    {
        var target = UeToGl.Point(new FVector(120_000f, -45_000f, 3_000f));
        foreach (var preset in Enum.GetValues<ViewPreset>())
        {
            var camera = new FlyCamera();
            var (yaw, pitch) = ViewPresets.Angles(preset, 0f);
            camera.Orbit(target, yaw, pitch, 2500f);
            Assert.Equal(2500f, Vector3.Distance(camera.Position, target), 0.5f);
            var (origin, direction) = camera.ScreenRay(399.5f, 299.5f, 800, 600); // the middle of the screen
            var along = Vector3.Dot(target - origin, direction);
            Assert.True(Vector3.Distance(origin + (direction * along), target) < 5f, $"{preset}: the target is in the middle");
        }
    }
}
