using ScumStudio.Rendering.Cameras;

namespace ScumStudio.Viewport;

/// <summary>A view along a world axis (seviscache: "views per axis, like Blender and Unreal"; Blender's numpad keys).</summary>
public enum ViewPreset
{
    /// <summary>Looking straight down (Numpad 7).</summary>
    Top,

    /// <summary>Looking straight up (Ctrl+Numpad 7).</summary>
    Bottom,

    /// <summary>Looking along UE +X, level (Numpad 1).</summary>
    Front,

    /// <summary>Looking along UE -X, level (Ctrl+Numpad 1).</summary>
    Back,

    /// <summary>Looking along UE +Y, level (Numpad 3).</summary>
    Right,

    /// <summary>Looking along UE -Y, level (Ctrl+Numpad 3).</summary>
    Left,
}

/// <summary>
/// The camera angles of the <see cref="ViewPreset"/>s, in <see cref="FlyCamera"/>'s conventions (UE yaw and pitch,
/// degrees). Top and Bottom keep the heading the camera had, turned to the nearest quarter so the grid lines up with the
/// screen; they stop at <see cref="FlyCamera.MaxPitch"/> (straight down has no heading).
/// </summary>
public static class ViewPresets
{
    /// <summary>Yaw and pitch of <paramref name="preset"/> for a camera now heading <paramref name="currentYaw"/>.</summary>
    public static (float Yaw, float Pitch) Angles(ViewPreset preset, float currentYaw) => preset switch
    {
        ViewPreset.Top => (QuarterOf(currentYaw), -FlyCamera.MaxPitch),
        ViewPreset.Bottom => (QuarterOf(currentYaw), FlyCamera.MaxPitch),
        ViewPreset.Front => (0f, 0f),
        ViewPreset.Back => (180f, 0f),
        ViewPreset.Right => (90f, 0f),
        ViewPreset.Left => (-90f, 0f),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>The same line of sight from the other side (Blender's Numpad 9): heading turned half round, pitch mirrored.</summary>
    public static (float Yaw, float Pitch) Opposite(float yaw, float pitch) => (Normalize(yaw + 180f), -pitch);

    /// <summary>The view from the other side of <paramref name="preset"/> (Ctrl with the same key).</summary>
    public static ViewPreset Opposite(ViewPreset preset) => preset switch
    {
        ViewPreset.Top => ViewPreset.Bottom,
        ViewPreset.Bottom => ViewPreset.Top,
        ViewPreset.Front => ViewPreset.Back,
        ViewPreset.Back => ViewPreset.Front,
        ViewPreset.Right => ViewPreset.Left,
        ViewPreset.Left => ViewPreset.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary><paramref name="yaw"/> turned to the nearest multiple of 90 degrees, in (-180, 180].</summary>
    public static float QuarterOf(float yaw) => Normalize(MathF.Round(yaw / 90f, MidpointRounding.AwayFromZero) * 90f);

    private static float Normalize(float degrees)
    {
        degrees %= 360f;
        return degrees switch
        {
            > 180f => degrees - 360f,
            <= -180f => degrees + 360f,
            _ => degrees,
        };
    }
}
