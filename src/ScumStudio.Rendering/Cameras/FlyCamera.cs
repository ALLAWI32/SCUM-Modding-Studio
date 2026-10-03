using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Rendering.Cameras;

/// <summary>
/// A free-flying perspective camera in the renderer's GL world (right-handed, Y up; see <c>UeToGl</c>).
/// </summary>
/// <remarks>
/// Yaw and pitch use Unreal's conventions after the axis swap, so a camera placed with a UE rotator looks the same way:
/// yaw 0 looks along GL +X (UE +X), positive yaw turns towards GL +Z (UE +Y, right), positive pitch looks up.
/// The camera holds no input state: feed it mouse deltas (<see cref="Rotate"/>) and key states
/// (<see cref="Update"/>) from whatever UI toolkit hosts it.
/// </remarks>
public sealed class FlyCamera
{
    /// <summary>Largest absolute pitch in degrees (keeps the look-at basis well defined).</summary>
    public const float MaxPitch = 89.5f;

    private float _pitch;
    private float _fieldOfView = 60f;
    private float _near = 10f;
    private float _far = 2_000_000f;

    /// <summary>Eye position in GL world units (centimetres by default).</summary>
    public Vector3 Position { get; set; }

    /// <summary>Heading in degrees (UE yaw).</summary>
    public float Yaw { get; set; }

    /// <summary>Elevation in degrees (UE pitch), clamped to ±<see cref="MaxPitch"/>.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    /// <summary>Vertical field of view in degrees (1..179).</summary>
    public float FieldOfView
    {
        get => _fieldOfView;
        set => _fieldOfView = value is > 0f and < 180f ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Near clip distance (&gt; 0).</summary>
    public float NearPlane
    {
        get => _near;
        set => _near = value > 0f && value < _far ? value : throw new ArgumentOutOfRangeException(nameof(value), "Require 0 < near < far.");
    }

    /// <summary>Far clip distance (&gt; near).</summary>
    public float FarPlane
    {
        get => _far;
        set => _far = value > _near ? value : throw new ArgumentOutOfRangeException(nameof(value), "Require far > near.");
    }

    /// <summary>Sets both clip distances at once (avoids ordering problems with the individual setters).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unless 0 &lt; <paramref name="near"/> &lt; <paramref name="far"/>.</exception>
    public void SetClipRange(float near, float far)
    {
        if (!(near > 0f) || !(far > near) || float.IsInfinity(far))
        {
            throw new ArgumentOutOfRangeException(nameof(near), "Require 0 < near < far.");
        }

        _near = near;
        _far = far;
    }

    /// <summary>Movement speed in world units per second for <see cref="Update"/>.</summary>
    public float MoveSpeed { get; set; } = 1000f;

    /// <summary>Speed multiplier while <see cref="FlyInput.Fast"/> is held.</summary>
    public float FastMultiplier { get; set; } = 5f;

    /// <summary>Degrees per mouse pixel for <see cref="Rotate"/>.</summary>
    public float MouseSensitivity { get; set; } = 0.15f;

    /// <summary>Unit view direction.</summary>
    public Vector3 Forward
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw * UeMath.DegreesToRadians);
            var (sp, cp) = MathF.SinCos(Pitch * UeMath.DegreesToRadians);
            return new Vector3(cp * cy, sp, cp * sy);
        }
    }

    /// <summary>Unit right vector (horizontal).</summary>
    public Vector3 Right
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw * UeMath.DegreesToRadians);
            return new Vector3(-sy, 0f, cy);
        }
    }

    /// <summary>Unit up vector of the view basis.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>Right-handed view matrix (row-vector convention).</summary>
    /// <remarks>
    /// Built from the view direction and the camera's own up vector (not from <c>Position + Forward</c> and world up): far
    /// from the origin (SCUM's island spans ±10 km) the small horizontal part of a near-vertical view direction would
    /// round away in <c>Position + Forward</c>, giving a degenerate matrix and an empty frame when looking straight down.
    /// </remarks>
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookTo(Position, Forward, Up);

    /// <summary>OpenGL projection with clip depth in [-1, 1] (<c>UeToGl.CreatePerspectiveGl</c>).</summary>
    public Matrix4x4 GetProjection(float aspectRatio) =>
        UeToGl.CreatePerspectiveGl(FieldOfView * UeMath.DegreesToRadians, aspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// Reverse-Z projection for clip depth in [0, 1] (use with <c>glClipControl(LOWER_LEFT, ZERO_TO_ONE)</c>,
    /// a float depth buffer cleared to 0 and <c>GL_GREATER</c>): near maps to 1, far to 0, which keeps precision across
    /// kilometre-sized levels.
    /// </summary>
    public Matrix4x4 GetReverseZProjection(float aspectRatio) =>
        CreateReverseZPerspective(FieldOfView * UeMath.DegreesToRadians, aspectRatio, NearPlane, FarPlane);

    /// <summary>View * projection ([-1, 1] depth); also the matrix frustum culling uses.</summary>
    public Matrix4x4 GetViewProjection(float aspectRatio) => ViewMatrix * GetProjection(aspectRatio);

    /// <summary>The culling frustum for <paramref name="aspectRatio"/>.</summary>
    public Frustum GetFrustum(float aspectRatio) => Frustum.FromViewProjection(GetViewProjection(aspectRatio));

    /// <summary>Applies a mouse movement (pixels): right turns right, down looks down.</summary>
    public void Rotate(float deltaX, float deltaY)
    {
        Yaw = NormalizeDegrees(Yaw + (deltaX * MouseSensitivity));
        Pitch -= deltaY * MouseSensitivity;
    }

    /// <summary>Moves in camera-local axes (forward along the view direction, right horizontal, up world +Y).</summary>
    public void Move(float forward, float right, float up)
    {
        Position += (Forward * forward) + (Right * right) + (Vector3.UnitY * up);
    }

    /// <summary>Advances WASD-style movement by <paramref name="deltaSeconds"/>.</summary>
    public void Update(in FlyInput input, float deltaSeconds)
    {
        var speed = MoveSpeed * (input.Fast ? FastMultiplier : 1f) * deltaSeconds;
        var f = (input.Forward ? 1f : 0f) - (input.Backward ? 1f : 0f);
        var r = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);
        var u = (input.Up ? 1f : 0f) - (input.Down ? 1f : 0f);
        if (f != 0f || r != 0f || u != 0f)
        {
            Move(f * speed, r * speed, u * speed);
        }
    }

    /// <summary>Places the camera on a sphere around <paramref name="target"/> looking at it.</summary>
    /// <param name="target">Point to look at.</param>
    /// <param name="yaw">Camera heading in degrees (the camera looks along this heading).</param>
    /// <param name="pitch">Camera pitch in degrees (negative looks down onto the target).</param>
    /// <param name="distance">Distance from the target.</param>
    public void Orbit(Vector3 target, float yaw, float pitch, float distance)
    {
        Yaw = yaw;
        Pitch = pitch;
        Position = target - (Forward * distance);
    }

    /// <summary>
    /// Orbits so that <paramref name="bounds"/> fits the view (bounding sphere inside the smaller field of view),
    /// and adapts near/far to the object size. Returns the distance used.
    /// </summary>
    public float Frame(BoundingBox bounds, float aspectRatio, float yaw = -135f, float pitch = -25f, float margin = 1.0f)
    {
        if (bounds.IsEmpty)
        {
            bounds = new BoundingBox(-Vector3.One, Vector3.One);
        }

        var radius = MathF.Max(bounds.Extent.Length(), 1e-3f);
        var fovY = FieldOfView * UeMath.DegreesToRadians;
        var fovX = 2f * MathF.Atan(MathF.Tan(fovY * 0.5f) * aspectRatio);
        var half = MathF.Min(fovX, fovY) * 0.5f;
        var distance = radius * margin / MathF.Sin(half);
        FitClipRange(distance, radius);
        Orbit(bounds.Center, yaw, pitch, distance);
        return distance;
    }

    /// <summary>
    /// Sets near/far for looking at a sphere of <paramref name="radius"/> from <paramref name="distance"/> away
    /// (tight range keeps depth precision high even without reverse-Z).
    /// </summary>
    public void FitClipRange(float distance, float radius)
    {
        var near = MathF.Max(distance - (radius * 2f), MathF.Max(distance * 0.001f, 1e-3f));
        var far = MathF.Max(distance + (radius * 4f), near * 2f);
        SetClipRange(near, far);
    }

    /// <summary>
    /// World-space ray through a pixel (<paramref name="x"/>, <paramref name="y"/> from the top-left of a
    /// <paramref name="width"/> x <paramref name="height"/> viewport).
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) ScreenRay(float x, float y, int width, int height)
    {
        var aspect = (float)width / height;
        Matrix4x4.Invert(GetViewProjection(aspect), out var inv);
        var ndcX = ((x + 0.5f) / width * 2f) - 1f;
        var ndcY = 1f - ((y + 0.5f) / height * 2f);
        var near = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), inv);
        var far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inv);
        var n = new Vector3(near.X, near.Y, near.Z) / near.W;
        var f = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (n, Vector3.Normalize(f - n));
    }

    /// <summary>
    /// Reconstructs the world position of a pixel from its window-space depth ([0, 1] as stored in the depth buffer).
    /// </summary>
    /// <param name="x">Pixel column from the left.</param>
    /// <param name="y">Pixel row from the top.</param>
    /// <param name="depth">Depth buffer value.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <param name="reverseZ">True when the depth was written with <see cref="GetReverseZProjection"/> and [0, 1] clip depth.</param>
    public Vector3 Unproject(float x, float y, float depth, int width, int height, bool reverseZ)
    {
        var aspect = (float)width / height;
        var viewProj = ViewMatrix * (reverseZ ? GetReverseZProjection(aspect) : GetProjection(aspect));
        Matrix4x4.Invert(viewProj, out var inv);
        var ndcX = ((x + 0.5f) / width * 2f) - 1f;
        var ndcY = 1f - ((y + 0.5f) / height * 2f);
        var ndcZ = reverseZ ? depth : (depth * 2f) - 1f;
        var p = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), inv);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    /// <summary>Right-handed reverse-Z perspective, clip depth [0, 1] with near = 1 and far = 0 (row-vector convention).</summary>
    public static Matrix4x4 CreateReverseZPerspective(float fieldOfViewYRadians, float aspectRatio, float nearPlane, float farPlane)
    {
        if (fieldOfViewYRadians <= 0f || fieldOfViewYRadians >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewYRadians));
        }

        if (aspectRatio <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        }

        if (nearPlane <= 0f || farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Require 0 < near < far.");
        }

        var f = 1f / MathF.Tan(fieldOfViewYRadians * 0.5f);
        var range = farPlane - nearPlane;
        return new Matrix4x4(
            f / aspectRatio, 0f, 0f, 0f,
            0f, f, 0f, 0f,
            0f, 0f, nearPlane / range, -1f,
            0f, 0f, farPlane * nearPlane / range, 0f);
    }

    private static float NormalizeDegrees(float degrees)
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

/// <summary>Key state for <see cref="FlyCamera.Update"/> (W/S = forward/backward, A/D = left/right, E/Q = up/down, Shift = fast).</summary>
/// <param name="Forward">Move forward.</param>
/// <param name="Backward">Move backward.</param>
/// <param name="Left">Strafe left.</param>
/// <param name="Right">Strafe right.</param>
/// <param name="Up">Rise along world up.</param>
/// <param name="Down">Sink along world up.</param>
/// <param name="Fast">Apply <see cref="FlyCamera.FastMultiplier"/>.</param>
public readonly record struct FlyInput(bool Forward, bool Backward, bool Left, bool Right, bool Up = false, bool Down = false, bool Fast = false);
