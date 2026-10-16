using System.Numerics;
using Avalonia;
using Avalonia.Input;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>
/// The view's own keys and switches (seviscache #5-#7): Esc cancels a drag (<see cref="CancelDrag"/>), the numpad turns
/// the camera to a view along an axis like Blender (7 top, 1 front, 3 right, Ctrl the other side, 9 the opposite side,
/// . frames the selection) around the selection or the ground under the screen's centre, F3 or Shift+Z draws the scene
/// as wireframe, and <see cref="ShowBackdrop"/> hides the island's terrain (Isolate).
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>Draws the scene's triangles as lines (the sky, the grid and the gizmo stay filled). Not kept between sessions.</summary>
    public static readonly StyledProperty<bool> WireframeProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(Wireframe), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>False hides the island's terrain behind the loaded levels (Isolate): it stays uploaded, so showing it again is instant.</summary>
    public static readonly StyledProperty<bool> ShowBackdropProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(ShowBackdrop), true);

    // After the properties above (static fields initialise in the order they are written).
    private static readonly IDisposable[] ViewPropertyHandlers =
    [
        WireframeProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering()),
        ShowBackdropProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering()),
    ];

    /// <summary>The farthest the view turns around a point under the screen's centre (10 km): a hit near the horizon must not throw the camera across the island.</summary>
    private const float MaxPresetDistance = 1_000_000f;

    /// <inheritdoc cref="WireframeProperty" />
    public bool Wireframe
    {
        get => GetValue(WireframeProperty);
        set => SetValue(WireframeProperty, value);
    }

    /// <inheritdoc cref="ShowBackdropProperty" />
    public bool ShowBackdrop
    {
        get => GetValue(ShowBackdropProperty);
        set => SetValue(ShowBackdropProperty, value);
    }

    /// <summary>The island's terrain as drawn and hit: null while it is hidden (<see cref="ShowBackdrop"/>).</summary>
    private LevelScene? ShownBackdrop => ShowBackdrop ? BackdropScene : null;

    /// <summary>The island's terrain the ground ray may hit: null while it is hidden.</summary>
    private PreparedLevelScene? HitBackdrop => ShowBackdrop ? Backdrop : null;

    /// <summary>Hands <see cref="Wireframe"/> to the renderer (each frame: a new GL context starts from the defaults).</summary>
    private void SyncViewSettings(SceneRenderer renderer)
    {
        if (renderer.Settings.Wireframe != Wireframe)
        {
            renderer.Settings = renderer.Settings with { Wireframe = Wireframe };
        }
    }

    /// <summary>
    /// The view's keys, whatever has the focus on the page (the page forwards them before its own shortcuts): Esc cancels
    /// a drag; the numpad's views by the physical key, so with Num Lock off Numpad 3 (PageDown), 1 (End) and . (Delete)
    /// change the view instead of nudging, dropping to the ground or deleting; F3 or Shift+Z switch wireframe. True when taken.
    /// </summary>
    public bool HandleViewKey(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (e.Key == Key.Escape && CancelDrag())
        {
            return true;
        }

        if ((e.Key == Key.F3 && modifiers == KeyModifiers.None) || (e.Key == Key.Z && modifiers == KeyModifiers.Shift))
        {
            Wireframe = !Wireframe;
            return true;
        }

        if ((modifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return false;
        }

        var other = modifiers.HasFlag(KeyModifiers.Control);
        ViewPreset? preset = e.PhysicalKey switch
        {
            PhysicalKey.NumPad7 => ViewPreset.Top,
            PhysicalKey.NumPad1 => ViewPreset.Front,
            PhysicalKey.NumPad3 => ViewPreset.Right,
            _ => null,
        };
        if (preset is { } view)
        {
            SetViewPreset(other ? ViewPresets.Opposite(view) : view);
            return true;
        }

        if (e.PhysicalKey == PhysicalKey.NumPad9 && !other)
        {
            ViewFromOtherSide();
            return true;
        }

        if (e.PhysicalKey == PhysicalKey.NumPadDecimal && !other)
        {
            if (!FrameSelection())
            {
                FrameAll();
            }

            return true;
        }

        return false;
    }

    /// <summary>Turns the camera to <paramref name="preset"/>, framing the selection, else around the ground under the screen's centre.</summary>
    public void SetViewPreset(ViewPreset preset)
    {
        var (yaw, pitch) = ViewPresets.Angles(preset, _camera.Yaw);
        LookFrom(yaw, pitch);
    }

    /// <summary>The same line of sight from the other side of what the view turns around (Numpad 9).</summary>
    public void ViewFromOtherSide()
    {
        var (yaw, pitch) = ViewPresets.Opposite(_camera.Yaw, _camera.Pitch);
        LookFrom(yaw, pitch);
    }

    private void LookFrom(float yaw, float pitch)
    {
        if (SelectedId != 0 && PresetSelectionBounds() is { IsEmpty: false } box)
        {
            // As Frame selection: the view keeps its clip range, so framing one object does not cut the world away.
            var (near, far) = (_camera.NearPlane, _camera.FarPlane);
            FrameBounds(box, yaw, pitch);
            _camera.SetClipRange(MathF.Min(near, _camera.NearPlane), MathF.Max(far, _camera.FarPlane));
        }
        else
        {
            var (target, distance) = ViewCentre();
            _camera.Orbit(target, yaw, pitch, distance);
        }

        RequestNextFrameRendering();
    }

    /// <summary>The selection's box (GL), or a 4 m box around its root when it draws nothing (or no scene is uploaded).</summary>
    private BoundingBox PresetSelectionBounds()
    {
        var bounds = _level is { } level ? SelectionBounds(level) : BoundingBox.Empty;
        if (bounds.IsEmpty && SelectedRootWorld is { } root)
        {
            var centre = UeToGl.Point(root.Translation);
            bounds = new BoundingBox(centre - new Vector3(200f), centre + new Vector3(200f));
        }

        return bounds;
    }

    /// <summary>What the view turns around with nothing selected: the ground under the screen's centre (else the ground plane, else 50 m ahead) and how far it is.</summary>
    private (Vector3 Target, float Distance) ViewCentre()
    {
        var (origin, direction, far) = (_camera.Position, _camera.Forward, _camera.FarPlane);
        var ground = (Scene?.HeightField?.RaycastGl(origin, direction, far) ?? HitBackdrop?.HeightField?.RaycastGl(origin, direction, far)) is { } terrain
            ? UeToGl.Point(new FVector(terrain.Position.X, terrain.Position.Y, terrain.Position.Z))
            : GizmoMath.HitHorizontalPlane(origin, direction, 0f, far);
        var target = ground ?? origin + (direction * 5000f);
        var distance = Vector3.Distance(origin, target);
        if (distance > MaxPresetDistance)
        {
            target = origin + (direction * MaxPresetDistance);
            distance = MaxPresetDistance;
        }

        return (target, MathF.Max(distance, 100f));
    }
}
