using System.Numerics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Gl;
using ScumStudio.Rendering.Targets;
using Silk.NET.OpenGL;

namespace ScumStudio.Rendering;

/// <summary>
/// Steady foliage while the camera moves (owner: trees and bushes "shimmer" in motion). Leaves and twigs thinner than a
/// pixel land on a pixel in one frame and miss it in the next, so even anti-aliased (4 samples, alpha to coverage) a
/// forest sparkles as the view slides over it; the game hides this with temporal anti-aliasing. This is its core without
/// the jitter: each frame, once every scene is drawn, every pixel is found where it was a frame ago (its depth back
/// through this frame's camera and forward through the last one's), the last output there is read (Catmull-Rom, sharp)
/// and kept to the colours around the pixel now (variance clipping, so what moved or was uncovered does not trail), and
/// blended with the new colour, the history weighing <see cref="Feedback"/>. The result goes back into the target, so
/// what is copied to the screen, read back or saved is the steady picture; overlays (gizmos, outlines) are drawn after it,
/// sharp. When the camera stops, the history's share falls to nothing within <see cref="SettleFrames"/> frames, so a view
/// at rest shows exactly what is drawn. A camera jump (more than <see cref="JumpCm"/> or <see cref="JumpDegrees"/> in a
/// frame) or a new size starts over.
/// </summary>
public sealed class TemporalStabilizer : IDisposable
{
    /// <summary>
    /// The history's share of a pixel while the camera moves: 0.7 halves the flicker of a slow pan over a forest and leaves the
    /// frame closer to a 64-sample picture than a plain frame; more steadies further but blurs (see TemporalStabilizerTests).
    /// </summary>
    public const float DefaultFeedback = 0.7f;

    /// <summary>Frames after the camera stops until the picture is exactly the newest frame.</summary>
    public const int SettleFrames = 4;

    /// <summary>A move longer than this in one frame (cm) is a jump: the history is dropped.</summary>
    public const float JumpCm = 10_000f;

    /// <summary>A turn larger than this in one frame (degrees) is a jump.</summary>
    public const float JumpDegrees = 30f;

    private readonly SceneRenderer _renderer;
    private readonly GL _gl;
    private readonly ShaderProgram _resolve;
    private readonly ShaderProgram _copy;
    private readonly uint _vao;
    private readonly uint[] _history = new uint[2];
    private readonly uint[] _historyFbo = new uint[2];
    private uint _depth;
    private uint _depthFbo;
    private (int W, int H) _size;
    private int _current; // the history written last
    private bool _hasHistory;
    private int _still;
    private View _last;
    private bool _disposed;

    /// <summary>Compiles the passes on <paramref name="renderer"/>'s context (made current by the caller).</summary>
    public TemporalStabilizer(SceneRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _gl = renderer.Context.Gl;
        _resolve = new ShaderProgram(_gl, "temporal", FullScreenVertex, ResolveFragment);
        try
        {
            _copy = new ShaderProgram(_gl, "temporal copy", FullScreenVertex, CopyFragment);
        }
        catch
        {
            _resolve.Dispose();
            throw;
        }

        _vao = _gl.GenVertexArray();
    }

    /// <summary>The history's share of a pixel while the camera moves (0 = off, 0.95 at most).</summary>
    public float Feedback { get; set; } = DefaultFeedback;

    /// <summary>True while the picture still differs from the newest frame (the camera stopped fewer than <see cref="SettleFrames"/> frames ago): draw again.</summary>
    public bool Settling => _hasHistory && _still < SettleFrames;

    /// <summary>The history's share used by the last <see cref="Apply"/> (0 when it started over or had settled).</summary>
    public float LastFeedback { get; private set; }

    /// <summary>
    /// Steadies the frame drawn into <paramref name="target"/> with <paramref name="camera"/> (call once per frame, after
    /// every scene of it and before it is copied, read or saved). False when it did nothing (turned off for this driver,
    /// see <see cref="GraphicsFeature.TemporalStabilization"/>, or a GL error, which turns it off for good).
    /// </summary>
    public bool Apply(RenderTarget target, FlyCamera camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(camera);
        if (_renderer.Fallbacks.IsOff(GraphicsFeature.TemporalStabilization))
        {
            _hasHistory = false;
            return false;
        }

        var reverseZ = _renderer.UsesReverseZ;
        var view = new View(camera.Position, camera.Forward, camera.Up,
            reverseZ ? camera.GetReverseZProjection(target.AspectRatio) : camera.GetProjection(target.AspectRatio));
        if (_size != (target.Width, target.Height))
        {
            Allocate(target.Width, target.Height);
        }

        var jumped = _hasHistory
            && (Vector3.Distance(view.Position, _last.Position) > JumpCm
                || Vector3.Dot(view.Forward, _last.Forward) < MathF.Cos(JumpDegrees * MathF.PI / 180f));
        var moved = !_hasHistory || !view.Equals(_last);
        _still = moved ? 0 : _still + 1;
        var feedback = !_hasHistory || jumped ? 0f : Math.Clamp(Feedback, 0f, 0.95f) * Math.Max(0f, 1f - (_still / (float)SettleFrames));

        GlErrors.Drain(_gl); // the frame checked its own
        var color = target.ResolveSceneOnly();

        // This frame's depth: a multisampled target's samples are resolved by the blit (one of them; the formats match).
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.ColorFramebuffer);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depthFbo);
        _gl.BlitFramebuffer(0, 0, target.Width, target.Height, 0, 0, target.Width, target.Height, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);

        var next = 1 - _current;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _historyFbo[next]);
        _gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.SampleAlphaToCoverage);
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.BindVertexArray(_vao);
        Bind(0, color);
        Bind(1, _depth);
        Bind(2, _history[_current]);
        _resolve.Use();
        _resolve.Set("uCurrent", 0);
        _resolve.Set("uDepth", 1);
        _resolve.Set("uHistory", 2);
        _resolve.Set("uReproject", Reprojection(view, _hasHistory ? _last : view));
        _resolve.Set("uDepthZeroToOne", reverseZ ? 1 : 0);
        _resolve.Set("uReverseZ", reverseZ ? 1 : 0);
        _resolve.Set("uFeedback", feedback);
        _resolve.Set("uSize", new Vector2(target.Width, target.Height));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Back into the target (every sample of a multisampled one): the overlay left pending is drawn over it, then resolved.
        target.BindColor();
        Bind(2, 0);
        Bind(0, _history[next]);
        _copy.Use();
        _copy.Set("uSource", 0);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        Bind(0, 0);
        Bind(1, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.UseProgram(0);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (GlErrors.Drain(_gl) is { Count: > 0 } errors)
        {
            _renderer.Fallbacks.TurnOff(GraphicsFeature.TemporalStabilization,
                $"steadying a {target.Samples}-sample {target.Width}x{target.Height} frame failed: {string.Join(", ", errors)}");
            _hasHistory = false;
            return false;
        }

        (_current, _last, _hasHistory, LastFeedback) = (next, view, true, feedback);
        return true;
    }

    /// <summary>Drops the history: the next frame starts over (a new scene, a jump of the camera).</summary>
    public void Reset() => (_hasHistory, _still) = (false, 0);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        _resolve.Dispose();
        _copy.Dispose();
        _gl.DeleteVertexArray(_vao);
    }

    /// <summary>
    /// The matrix from this frame's normalized device coordinates to the last frame's clip space, built around this
    /// frame's eye (a view matrix at island coordinates, near a million centimetres, loses float precision in its inverse).
    /// Row-vector, uploaded as is: GLSL's <c>m * v</c> applies it.
    /// </summary>
    internal static Matrix4x4 Reprojection(in View now, in View before)
    {
        var current = Matrix4x4.CreateLookTo(Vector3.Zero, now.Forward, now.Up) * now.Projection;
        var previous = Matrix4x4.CreateLookTo(before.Position - now.Position, before.Forward, before.Up) * before.Projection;
        return Matrix4x4.Invert(current, out var inverse) ? inverse * previous : Matrix4x4.Identity;
    }

    private void Bind(int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.BindSampler((uint)unit, 0);
    }

    private unsafe void Allocate(int width, int height)
    {
        Release();
        _size = (width, height);
        _hasHistory = false;
        for (var i = 0; i < 2; i++)
        {
            _history[i] = Texture(SizedInternalFormat.Rgba16f, linear: true);
            _historyFbo[i] = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _historyFbo[i]);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _history[i], 0);
            _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        }

        _depth = Texture(SizedInternalFormat.DepthComponent32f, linear: false);
        _depthFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _depthFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depth, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        uint Texture(SizedInternalFormat format, bool linear)
        {
            var texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexStorage2D(TextureTarget.Texture2D, 1, format, (uint)width, (uint)height);
            var filter = (int)(linear ? GLEnum.Linear : GLEnum.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }
    }

    private void Release()
    {
        for (var i = 0; i < 2; i++)
        {
            if (_historyFbo[i] != 0)
            {
                _gl.DeleteFramebuffer(_historyFbo[i]);
                _gl.DeleteTexture(_history[i]);
                (_historyFbo[i], _history[i]) = (0, 0);
            }
        }

        if (_depthFbo != 0)
        {
            _gl.DeleteFramebuffer(_depthFbo);
            _gl.DeleteTexture(_depth);
            (_depthFbo, _depth) = (0, 0);
        }

        _size = default;
    }

    /// <summary>A frame's camera: eye, axes and projection (reverse-Z or not, as drawn).</summary>
    internal readonly record struct View(Vector3 Position, Vector3 Forward, Vector3 Up, Matrix4x4 Projection);

    private const string FullScreenVertex = """
        #version 430 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string CopyFragment = """
        #version 430 core
        uniform sampler2D uSource;
        layout(location = 0) out vec4 oColor;
        void main()
        {
            oColor = vec4(texelFetch(uSource, ivec2(gl_FragCoord.xy), 0).rgb, 1.0);
        }
        """;

    private const string ResolveFragment = """
        #version 430 core
        uniform sampler2D uCurrent;
        uniform sampler2D uDepth;
        uniform sampler2D uHistory;
        uniform mat4 uReproject;
        uniform int uDepthZeroToOne;
        uniform int uReverseZ;
        uniform float uFeedback;
        uniform vec2 uSize;
        layout(location = 0) out vec4 oColor;

        vec3 fetch(ivec2 p)
        {
            return texelFetch(uCurrent, clamp(p, ivec2(0), ivec2(uSize) - 1), 0).rgb;
        }

        // The last output at uv through a Catmull-Rom filter in five bilinear taps (Jimenez, "Filmic SMAA", 2016): bilinear
        // reads would blur the steady picture a little more every frame the camera moves.
        vec3 history(vec2 uv)
        {
            vec2 position = uv * uSize;
            vec2 centre = floor(position - 0.5) + 0.5;
            vec2 f = position - centre;
            vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            vec2 w3 = f * f * (-0.5 + 0.5 * f);
            vec2 w12 = w1 + w2;
            vec2 t0 = (centre - 1.0) / uSize;
            vec2 t3 = (centre + 2.0) / uSize;
            vec2 t12 = (centre + w2 / w12) / uSize;
            vec3 sum = texture(uHistory, vec2(t12.x, t0.y)).rgb * (w12.x * w0.y)
                     + texture(uHistory, vec2(t0.x, t12.y)).rgb * (w0.x * w12.y)
                     + texture(uHistory, t12).rgb * (w12.x * w12.y)
                     + texture(uHistory, vec2(t3.x, t12.y)).rgb * (w3.x * w12.y)
                     + texture(uHistory, vec2(t12.x, t3.y)).rgb * (w12.x * w3.y);
            float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
            return clamp(sum / weight, 0.0, 1.0);
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec3 current = fetch(p);
            if (uFeedback <= 0.0)
            {
                oColor = vec4(current, 1.0);
                return;
            }

            // The colours around the pixel now (mean and spread) and the nearest depth among them, so the edge of a near
            // object moves with the object rather than with what is behind it.
            vec3 m1 = vec3(0.0);
            vec3 m2 = vec3(0.0);
            vec3 lo = vec3(1.0);
            vec3 hi = vec3(0.0);
            float nearest = uReverseZ != 0 ? 0.0 : 1.0;
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    ivec2 q = clamp(p + ivec2(x, y), ivec2(0), ivec2(uSize) - 1);
                    vec3 c = texelFetch(uCurrent, q, 0).rgb;
                    m1 += c;
                    m2 += c * c;
                    lo = min(lo, c);
                    hi = max(hi, c);
                    float d = texelFetch(uDepth, q, 0).r;
                    nearest = uReverseZ != 0 ? max(nearest, d) : min(nearest, d);
                }
            }

            vec2 uv = (vec2(p) + 0.5) / uSize;
            vec4 before = uReproject * vec4(uv * 2.0 - 1.0, uDepthZeroToOne != 0 ? nearest : nearest * 2.0 - 1.0, 1.0);
            vec2 at = before.xy / before.w * 0.5 + 0.5;
            if (before.w <= 0.0 || any(lessThan(at, vec2(0.0))) || any(greaterThan(at, vec2(1.0))))
            {
                oColor = vec4(current, 1.0); // came into view this frame
                return;
            }

            vec3 mean = m1 / 9.0;
            vec3 spread = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0))) * 1.25;
            vec3 kept = clamp(history(at), max(lo, mean - spread), min(hi, mean + spread));
            float moved = length((at - uv) * uSize);
            float feedback = uFeedback * (1.0 - 0.25 * smoothstep(2.0, 24.0, moved)); // fast moves trade a little steadiness for sharpness
            oColor = vec4(mix(current, kept, feedback), 1.0);
        }
        """;
}
