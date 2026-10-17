using System.Numerics;
using Avalonia;
using ScumStudio.App.Services;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;

namespace ScumStudio.App.Controls;

/// <summary>
/// The Map's motion like the game's (owner: "camera motion is not smooth like the game", trees "shimmer" while it moves):
/// steady camera steps and mouse turns per frame (<see cref="SmoothCamera"/>), even frame pacing while it flies
/// (<see cref="FramePacing"/>), and foliage steadied over frames (<see cref="SteadyFoliage"/>, <see cref="TemporalStabilizer"/>).
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>
    /// Smooth camera (on by default): each frame moves the camera by the average frame time (a hitch is spread over the next
    /// frames) and turns it by the mouse moves smoothed over about two frames; off: the measured frame time and the raw moves.
    /// </summary>
    public static readonly StyledProperty<bool> SmoothCameraProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(SmoothCamera), true);

    /// <summary>Steady foliage (on by default): each frame is blended with the last one where the camera saw it, while it moves (<see cref="TemporalStabilizer"/>).</summary>
    public static readonly StyledProperty<bool> SteadyFoliageProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(SteadyFoliage), true);

    private readonly FrameClock _frameClock = new();
    private readonly LookSmoother _look = new();
    private TemporalStabilizer? _stabilizer;
    private bool _pacingHeld;

    /// <inheritdoc cref="SmoothCameraProperty" />
    public bool SmoothCamera
    {
        get => GetValue(SmoothCameraProperty);
        set => SetValue(SmoothCameraProperty, value);
    }

    /// <inheritdoc cref="SteadyFoliageProperty" />
    public bool SteadyFoliage
    {
        get => GetValue(SteadyFoliageProperty);
        set => SetValue(SteadyFoliageProperty, value);
    }

    /// <summary>The camera's time step for the frame that started at <paramref name="frameStart"/>; turns it by the mouse moves first.</summary>
    private float StepCamera(double frameStart)
    {
        (_frameClock.Smooth, _look.Smooth) = (SmoothCamera, SmoothCamera);
        var dt = _frameClock.Step(frameStart);
        if (_look.Take(dt) is { } turn && turn != Vector2.Zero)
        {
            _camera.Rotate(turn.X, turn.Y);
        }

        return dt;
    }

    /// <summary>
    /// Steadies the frame just drawn (after both scenes, before it is copied out); true while it wants more frames (the
    /// camera stopped and the history is fading out).
    /// </summary>
    private bool SteadyFrame()
    {
        // Not over wireframe lines (they would soften), nor on a driver that refused the pass.
        if (!SteadyFoliage || _target is null || _renderer is not { Settings.Wireframe: false } || EnsureStabilizer() is not { } stabilizer)
        {
            _stabilizer?.Reset();
            return false;
        }

        return stabilizer.Apply(_target, _camera) && stabilizer.Settling;
    }

    /// <summary>
    /// The stabilizer, compiled once (with the view's GL set-up, not on the first move: no hitch when flying starts); null
    /// when its pass is turned off for this driver. A driver that refuses its shaders turns it off and the view draws on.
    /// </summary>
    private TemporalStabilizer? EnsureStabilizer()
    {
        if (_renderer is null || _renderer.Fallbacks.IsOff(GraphicsFeature.TemporalStabilization))
        {
            return null;
        }

        try
        {
            return _stabilizer ??= new TemporalStabilizer(_renderer);
        }
        catch (InvalidOperationException ex)
        {
            _renderer.Fallbacks.TurnOff(GraphicsFeature.TemporalStabilization, ex.Message);
            return null;
        }
    }

    /// <summary>Holds the fine system clock while frames run back to back (moving, looking, settling) and gives it back at rest.</summary>
    private void PaceFrames(bool continuous)
    {
        if (continuous == _pacingHeld)
        {
            return;
        }

        _pacingHeld = continuous;
        if (continuous)
        {
            FramePacing.Hold();
        }
        else
        {
            FramePacing.Release();
        }
    }

    /// <summary>The GL side of the motion goes with the context (the clock is given back too).</summary>
    private void ReleaseMotion()
    {
        _stabilizer?.Dispose();
        _stabilizer = null;
        PaceFrames(false);
        _frameClock.Reset();
        _look.Clear();
    }
}
