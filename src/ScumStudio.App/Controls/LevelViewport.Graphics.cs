using Avalonia.OpenGL;
using Avalonia.Threading;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Rendering.Context;

namespace ScumStudio.App.Controls;

/// <summary>
/// The viewport's guard against the graphics driver (D03, AMD cards crashed when areas loaded in 3.2.0): a frame that throws
/// is logged with the GPU, the GL state is put back, the riskier features are turned off and the next frame tries again;
/// after <see cref="MaxFailedFrames"/> failures in a row the view stops and says so instead of the app going down.
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>Failed frames in a row after which the view stops drawing (it tries again every few seconds).</summary>
    internal const int MaxFailedFrames = 5;

    private int _failedFrames;
    private long _lastFailureTicks;

    /// <inheritdoc />
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_failedFrames >= MaxFailedFrames && Environment.TickCount64 - _lastFailureTicks < 3000)
        {
            return; // stopped: the message is up; a retry a few seconds later (a new level may draw)
        }

        try
        {
            RenderFrame(gl, fb);
            if (_failedFrames > 0)
            {
                _failedFrames = 0;
                var info = _lastInfo; // the warning gives way to the frame numbers again
                Dispatcher.UIThread.Post(() => FrameInfo = info, DispatcherPriority.Background);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            FrameFailed(ex, fb);
        }
    }

    /// <summary>Reports the GPU the view started on and sends the driver's own messages to the log.</summary>
    private void ReportGlStart()
    {
        if (_renderer is not { } renderer)
        {
            return;
        }

        GlDebugOutput.Enable(renderer.Context.Gl, GraphicsStatus.DriverMessage);
        GraphicsStatus.Started("map view", renderer.Info, renderer.CreationErrors.Select(e => e.ToString()).ToList());
    }

    private void FrameFailed(Exception error, int fb)
    {
        _failedFrames++;
        _lastFailureTicks = Environment.TickCount64;
        GraphicsStatus.FrameFailed("map view", error, _failedFrames);
        try
        {
            _renderer?.ResetState();
            _context?.Gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, (uint)fb);
            _context?.Gl.Finish(); // Avalonia hands the frame on after a flush only: complete, as RenderTarget.BlitTo leaves it
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GraphicsStatus.FrameFailed("map view (restoring the GL state)", ex, _failedFrames);
        }

        foreach (var feature in GraphicsStatus.FeaturesToDrop(error, _failedFrames))
        {
            GraphicsStatus.Fallbacks.TurnOff(feature, $"a frame failed: {error.GetType().Name}: {error.Message}"); // logged once each
        }

        var stopped = _failedFrames >= MaxFailedFrames;
        var message = Loc.F(stopped ? "Viewport.GraphicsStopped" : "Viewport.GraphicsRecovered", error.Message);
        Dispatcher.UIThread.Post(() => FrameInfo = message, DispatcherPriority.Background);
        if (!stopped)
        {
            RequestNextFrameRendering();
        }
    }
}
