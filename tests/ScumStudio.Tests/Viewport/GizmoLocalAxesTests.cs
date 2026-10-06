using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>Discord salvador: "the gizmo doesn't align with the object's orientation". A drag along the object's own axis.</summary>
public sealed class GizmoLocalAxesTests
{
    [Fact]
    public void ADragAlongTheObjectsOwnAxisFollowsItsTurn()
    {
        var start = new FTransform(new FRotator(0f, 90f, 0f), new FVector(100f, 200f, 300f), FVector.One); // turned 90°: its front is world +Y
        var own = start.Rotation.RotateVector(GizmoMath.UeDirection(GizmoAxis.X));
        Assert.Equal(0f, own.X, 0.001f);
        Assert.Equal(1f, own.Y, 0.001f);

        var moved = GizmoMath.Translate(start, own, 50f, 0f);
        Assert.Equal(new FVector(100f, 250f, 300f).Y, moved.Translation.Y, 0.01f);
        Assert.Equal(100f, moved.Translation.X, 0.01f);
        Assert.Equal(start.Rotation, moved.Rotation);

        // The world axis, as before, and the grid snap on the step.
        Assert.Equal(150f, GizmoMath.Translate(start, GizmoAxis.X, 50f, 0f).Translation.X, 0.01f);
        Assert.Equal(60f, GizmoMath.Translate(start, own, 57f, 10f).Translation.Y - 200f, 0.01f);
    }
}
