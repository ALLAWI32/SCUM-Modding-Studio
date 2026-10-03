using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace ScumStudio.Rendering.Context;

/// <summary>
/// A headless OpenGL 4.3 core context backed by a hidden GLFW window (Silk.NET.Windowing). Everything is rendered into
/// framebuffer objects (<see cref="Targets.RenderTarget"/>), so the window's own framebuffer is never used.
/// Works on Windows with any GL 4.3 driver and on Linux under X11 or <c>xvfb-run</c> (Mesa llvmpipe).
/// </summary>
/// <remarks>
/// GLFW is not thread-safe: creation and disposal are serialised process-wide. Use the context on the thread that
/// created it (or call <see cref="MakeCurrent"/> on the new thread after releasing it on the old one).
/// </remarks>
public sealed class OffscreenGlContext : IGlContext, IDisposable
{
    /// <summary>OpenGL version requested from the driver.</summary>
    public static readonly APIVersion RequiredVersion = new(4, 3);

    private static readonly object GlfwLock = new();
    private static bool _platformRegistered;

    private readonly IWindow _window;
    private (int W, int H) _size;
    private bool _disposed;

    private OffscreenGlContext(IWindow window, GL gl, int width, int height)
    {
        _window = window;
        Gl = gl;
        _size = (width, height);
        Info = GlInfo.Query(gl);
    }

    /// <inheritdoc />
    public GL Gl { get; }

    /// <inheritdoc />
    public (int W, int H) Size => _size;

    /// <summary>Driver strings and capabilities of the created context.</summary>
    public GlInfo Info { get; }

    /// <summary>
    /// Creates a hidden-window GL 4.3 core context.
    /// </summary>
    /// <param name="width">Initial logical size reported by <see cref="Size"/> (render targets may differ).</param>
    /// <param name="height">Initial logical height.</param>
    /// <exception cref="GlContextUnavailableException">No display, no GLFW, or the driver cannot provide GL 4.3 core.</exception>
    public static OffscreenGlContext Create(int width = 1280, int height = 720)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Size must be positive.");
        }

        lock (GlfwLock)
        {
            IWindow? window = null;
            try
            {
                if (!_platformRegistered)
                {
                    GlfwNativeLoader.EnsureLoaded();
                    GlfwWindowing.RegisterPlatform();
                    _platformRegistered = true;
                }

                var options = WindowOptions.Default with
                {
                    Size = new Vector2D<int>(Math.Min(width, 256), Math.Min(height, 256)),
                    Title = "ScumStudio offscreen",
                    IsVisible = false,
                    VSync = false,
                    ShouldSwapAutomatically = false,
                    IsEventDriven = true,
                    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, RequiredVersion),
                };
                window = Window.Create(options);
                window.Initialize();
                if (window.GLContext is not { } glContext)
                {
                    throw new GlContextUnavailableException("The window has no OpenGL context.");
                }

                glContext.MakeCurrent();
                var gl = GL.GetApi(glContext);
                var context = new OffscreenGlContext(window, gl, width, height);
                if (!context.Info.IsAtLeast(RequiredVersion.MajorVersion, RequiredVersion.MinorVersion))
                {
                    var version = context.Info.Version;
                    context.DisposeCore();
                    window = null;
                    throw new GlContextUnavailableException($"OpenGL {RequiredVersion.MajorVersion}.{RequiredVersion.MinorVersion} core is required; the driver created '{version}'.");
                }

                return context;
            }
            catch (GlContextUnavailableException)
            {
                SafeDispose(window);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SafeDispose(window);
                throw new GlContextUnavailableException($"Could not create an OpenGL {RequiredVersion.MajorVersion}.{RequiredVersion.MinorVersion} core context: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Like <see cref="Create"/> but returns false (with the reason) instead of throwing.</summary>
    public static bool TryCreate(int width, int height, out OffscreenGlContext? context, out string? failureReason)
    {
        try
        {
            context = Create(width, height);
            failureReason = null;
            return true;
        }
        catch (GlContextUnavailableException ex)
        {
            context = null;
            failureReason = ex.Message;
            return false;
        }
    }

    /// <summary>Changes the logical size reported by <see cref="Size"/>.</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Size must be positive.");
        }

        _size = (width, height);
    }

    /// <inheritdoc />
    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.GLContext!.MakeCurrent();
    }

    /// <summary>Releases the context from the calling thread (so another thread can make it current).</summary>
    public void ClearCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.GLContext!.Clear();
    }

    /// <summary>Destroys the GL context and its hidden window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (GlfwLock)
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        _disposed = true;
        Gl.Dispose();
        SafeDispose(_window);
    }

    private static void SafeDispose(IWindow? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.Reset();
            window.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: a failed teardown of a hidden window must not mask the original error.
        }
    }
}

/// <summary>Thrown when an OpenGL 4.3 core context cannot be created (no display, no driver support).</summary>
public sealed class GlContextUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GlContextUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
