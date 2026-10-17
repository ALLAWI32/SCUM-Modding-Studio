using System.Numerics;

namespace ScumStudio.Rendering.Cameras;

/// <summary>
/// The time step each frame moves the fly camera by (owner: "flying is not smooth like the game"). A frame's measured
/// interval jitters by a few milliseconds around the display's (the moment the frame starts drifts with the work before
/// it), while the frames reach the screen at the display's steady rate: moving the camera by the raw interval gives equal
/// frames unequal steps, the judder the eye reads as stutter. Smoothed, the step is the running average interval plus a
/// share of the time not yet given out, so every second still moves the camera a second's worth and a hitch is spread
/// over the next few frames instead of one jump. After a rest (a gap over <see cref="RestSeconds"/>) the first step is the
/// last known frame interval (or 1/60 s), never the whole pause: the first frame of a move no longer jumps ahead.
/// Allocation-free; one instance per view.
/// </summary>
public sealed class FrameClock
{
    /// <summary>The longest step of one frame (a frame after a long hitch moves at most this much).</summary>
    public const double MaxStepSeconds = 0.1;

    /// <summary>A gap between two frames at least this long is a rest, not a slow frame.</summary>
    public const double RestSeconds = 0.25;

    private const double AverageWeight = 0.1; // the newest interval's weight in the running average (about 10 frames)
    private const double CatchUp = 0.2;       // share of the owed time given out per frame (about 5 frames)
    private const double MaxOwedSeconds = 0.25;
    private const double NominalSeconds = 1.0 / 60.0;

    private double _last = double.NaN;
    private double _average;
    private double _owed;

    /// <summary>On (the default): steady steps; off: each frame steps by its measured interval (at most <see cref="MaxStepSeconds"/>).</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>The interval measured before the last step, in seconds (0 for the first frame and after a rest).</summary>
    public double LastInterval { get; private set; }

    /// <summary>The running average frame interval in seconds (0 until two frames came in a row).</summary>
    public double AverageInterval => _average;

    /// <summary>The camera's time step for a frame starting at <paramref name="nowSeconds"/> (a monotonic clock).</summary>
    public float Step(double nowSeconds)
    {
        var previous = _last;
        _last = nowSeconds;
        if (double.IsNaN(previous))
        {
            LastInterval = 0;
            return 0f;
        }

        var interval = Math.Max(0.0, nowSeconds - previous);
        if (interval >= RestSeconds)
        {
            // Back from a rest: the camera starts with one ordinary frame's step (the old code moved it by up to 0.1 s at once).
            LastInterval = 0;
            _owed = 0;
            return (float)(Smooth && _average > 0 ? _average : Math.Min(interval, NominalSeconds));
        }

        LastInterval = interval;
        if (!Smooth)
        {
            return (float)Math.Min(interval, MaxStepSeconds);
        }

        _average = _average <= 0 ? interval : _average + ((interval - _average) * AverageWeight);
        _owed = Math.Clamp(_owed + interval - _average, -MaxOwedSeconds, MaxOwedSeconds);
        var given = _owed * CatchUp;
        _owed -= given;
        return (float)Math.Clamp(_average + given, 0.0, MaxStepSeconds);
    }

    /// <summary>Forgets the history (a new view, a jump of the camera).</summary>
    public void Reset()
    {
        _last = double.NaN;
        (_average, _owed, LastInterval) = (0, 0, 0);
    }
}

/// <summary>
/// Mouse look, applied once per frame (owner: "the camera is not smooth like the game"). The mouse reports moves at its
/// own rate (125 to 1000 a second), so a frame gets two moves and the next three: turned by each move as it came, the
/// view turned by uneven amounts per frame at an even hand speed. The moves are summed and taken by the frame; smoothed
/// (UE's mouse smoothing, on by default in the game's input settings) the turn follows the summed moves with a short time
/// constant (<see cref="TimeConstant"/>, about two frames), which evens the steps out at the price of that small lag; no
/// move is lost, the rest arrives over the next frames (<see cref="Settling"/>).
/// </summary>
public sealed class LookSmoother
{
    /// <summary>Seconds the smoothed turn takes to cover about two thirds of a move.</summary>
    public const float TimeConstant = 0.03f;

    private const float Done = 0.01f; // pixels: what is left below this is given out at once

    private Vector2 _pending;
    private Vector2 _carry;

    /// <summary>On (the default): smoothed; off: each frame turns by exactly what the mouse moved since the last one.</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>True while part of a move is still to be turned (the view keeps drawing frames until it is).</summary>
    public bool Settling => _carry.LengthSquared() > Done * Done || _pending != Vector2.Zero;

    /// <summary>Adds a mouse move of <paramref name="dx"/>, <paramref name="dy"/> pixels.</summary>
    public void Add(float dx, float dy) => _pending += new Vector2(dx, dy);

    /// <summary>The turn (in mouse pixels) of a frame <paramref name="dt"/> seconds after the last one.</summary>
    public Vector2 Take(float dt)
    {
        var input = _pending;
        _pending = Vector2.Zero;
        if (!Smooth)
        {
            var all = input + _carry;
            _carry = Vector2.Zero;
            return all;
        }

        _carry += input;
        var share = dt <= 0f ? 0f : 1f - MathF.Exp(-dt / TimeConstant);
        var turn = _carry * share;
        _carry -= turn;
        if (_carry.LengthSquared() <= Done * Done)
        {
            turn += _carry;
            _carry = Vector2.Zero;
        }

        return turn;
    }

    /// <summary>Drops what is still to be turned (the look ended, the view lost focus).</summary>
    public void Clear() => (_pending, _carry) = (Vector2.Zero, Vector2.Zero);
}
