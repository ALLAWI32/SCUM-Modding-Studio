using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.Controls;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.App.Services;

/// <summary>Takes the "Share your mod" picture from the Map.</summary>
public interface IViewportCapture
{
    /// <summary>
    /// The visible Map view drawn again off screen, <paramref name="height"/> pixels tall and as wide as the view's shape
    /// (at most <paramref name="maxWidth"/>), as top-down RGBA8; null when there is no OpenGL or no scene drawn yet.
    /// </summary>
    Task<(byte[] Rgba, int Width, int Height)?> CaptureAsync(int height, int maxWidth, CancellationToken ct);
}

/// <summary>
/// <see cref="IViewportCapture"/> over the main window: finds the <see cref="LevelViewport"/> that shows a scene (as the AI
/// bridge does) and asks it for one off-screen frame. The renderer takes the aspect from the target, so the same camera
/// gives the same view at any size (SceneRenderer.Render).
/// </summary>
public sealed class ViewportCapture(Func<TopLevel?> window) : IViewportCapture
{
    /// <summary>Widest picture taken.</summary>
    public const int MaxWidth = 2560;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<(byte[] Rgba, int Width, int Height)?> CaptureAsync(int height, int maxWidth, CancellationToken ct)
    {
        var viewport = window()?.GetVisualDescendants().OfType<LevelViewport>()
            .FirstOrDefault(v => v.IsEffectivelyVisible && v.IsGlReady && v.HasRenderedScene);
        if (viewport is null || viewport.Bounds.Width < 1 || viewport.Bounds.Height < 1)
        {
            return null;
        }

        var (w, h) = SizeFor(viewport.Bounds.Width / viewport.Bounds.Height, height, maxWidth);
        return await viewport.CaptureAsync(w, h).WaitAsync(Timeout, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The picture size for a view of shape <paramref name="aspect"/> (width / height): <paramref name="height"/> tall, or
    /// <paramref name="maxWidth"/> wide with the height that keeps the shape when that would be wider.
    /// </summary>
    public static (int Width, int Height) SizeFor(double aspect, int height, int maxWidth = MaxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        if (!double.IsFinite(aspect) || aspect <= 0)
        {
            aspect = 16.0 / 9.0;
        }

        var width = (int)Math.Round(height * aspect);
        return width <= maxWidth ? (Math.Max(1, width), height) : (maxWidth, Math.Max(1, (int)Math.Round(maxWidth / aspect)));
    }

    /// <summary>
    /// Draws what the viewport draws each frame (the island backdrop, then the detailed level over it) into a new
    /// <paramref name="width"/> x <paramref name="height"/> target, without the ground grid, and reads it back.
    /// GL thread only.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) RenderOffscreen(SceneRenderer renderer, FlyCamera camera, Scene? backdrop, Scene? level, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(camera);
        var settings = renderer.Settings;
        renderer.Settings = settings with { ShowGrid = false };
        try
        {
            using var target = renderer.CreateTarget(width, height);
            if (backdrop is not null || level is null)
            {
                renderer.Render(target, backdrop ?? new Scene(), camera);
            }

            if (level is not null)
            {
                renderer.Render(target, level, camera, clear: backdrop is null);
            }

            return (target.ReadColorRgba(), target.Width, target.Height);
        }
        finally
        {
            renderer.Settings = settings;
        }
    }
}
