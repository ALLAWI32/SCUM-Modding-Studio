using Avalonia;
using ScumStudio.Rendering.Cameras;

namespace ScumStudio.App.Controls;

/// <summary>
/// The camera's clip range (D09, F15; seviscache: "if I zoom in, it gets cut off"; "the camera clips the mesh, making it
/// impossible to see exactly where you want to place the item"): worked out for every frame from what is drawn and the
/// near clip distance of Settings › Near clip (Ctrl+Shift+N on the Map), never kept from an earlier framing.
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>Near clip distance in centimetres (Settings › Near clip; <see cref="FlyCamera.ClipRangeFor"/>).</summary>
    public static readonly StyledProperty<float> NearClipProperty =
        AvaloniaProperty.Register<LevelViewport, float>(nameof(NearClip), FlyCamera.DefaultMapNear);

    /// <inheritdoc cref="NearClipProperty" />
    public float NearClip
    {
        get => GetValue(NearClipProperty);
        set => SetValue(NearClipProperty, value);
    }

    /// <summary>Sets near and far for this frame from the camera and everything drawn (the scene and the island behind it).</summary>
    private void UpdateClipRange()
    {
        var drawn = (_level?.Bounds ?? Core.Geometry.BoundingBox.Empty).Union(BackdropScene?.Bounds ?? Core.Geometry.BoundingBox.Empty);
        var (near, far) = FlyCamera.ClipRangeFor(_camera.Position, drawn, NearClip, _renderer is { UsesReverseZ: true });
        _camera.SetClipRange(near, far);
    }
}
