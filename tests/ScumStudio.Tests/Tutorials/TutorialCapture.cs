using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Controls;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.App;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Tutorials;

/// <summary>
/// Frames and <c>steps.json</c> of the tutorial recorders. The headless platform gives the GL viewport no context, so while
/// <see cref="Map"/> is set each frame's 3D view is rendered offscreen from the page's prepared scene with the page's edit
/// state applied (selection, hidden actors, instances and pin kinds, moved actors and parts, added objects, pin lists) and
/// pasted into the window render where the viewport shows through; flyouts, cards and toasts drawn over it stay on top.
/// Nothing of the owner's file system is readable in a frame: toasts and the status line that print such a path are replaced.
/// </summary>
internal sealed class TutorialCapture(Window window, AppServices services, string output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] OwnerRoots = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)];

    private readonly List<object> _steps = [];
    private (Vector3 Position, float Yaw, float Pitch, float Near, float Far)? _view;
    private (BoundingBox? Box, uint Id, float MarginCm, float Pitch, float Shift) _frame;

    /// <summary>The map page while its 3D view should be in the pictures.</summary>
    public MapPageViewModel? Map { get; set; }

    /// <summary>Frames recorded so far.</summary>
    public int Count => _steps.Count;

    /// <summary>
    /// The next shots look at this actor with <paramref name="marginCm"/> around it (the camera stays there, so a move shows as
    /// a move). <paramref name="shift"/> slides the camera right by that fraction of the view, so the actor sits left of centre
    /// (out from under a flyout).
    /// </summary>
    public void FrameOn(uint selectableId, float marginCm, float pitch, float shift = 0f)
    {
        _frame = (null, selectableId, marginCm, pitch, shift);
        _view = null;
    }

    /// <summary>The next shots look at the <paramref name="radiusCm"/> box around a UE point (a pin, a spot where something will be placed); <paramref name="shift"/> as in <see cref="FrameOn"/>.</summary>
    public void FrameAt(FVector ue, float radiusCm, float pitch, float shift = 0f)
    {
        var centre = UeToGl.Point(ue);
        _frame = (new BoundingBox(centre - new Vector3(radiusCm), centre + new Vector3(radiusCm)), 0, 0f, pitch, shift);
        _view = null;
    }

    /// <summary>Saves the next numbered frame and its step row (the click is the on-screen centre of <paramref name="target"/>); returns the file name.</summary>
    public string Shot(string caption, string narration, Control? target)
    {
        var file = Save(_steps.Count + 1);
        _steps.Add(new { file, caption, narration, click = Centre(target) });
        return file;
    }

    /// <summary>Saves frame <paramref name="index"/> as <c>NN.png</c> in the output folder; returns the file name.</summary>
    public string Save(int index)
    {
        Scrub();
        HeadlessUi.Pump();
        var file = index.ToString("00", CultureInfo.InvariantCulture) + ".png";
        var viewport = HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d");
        var frame = Map is { PreparedScene: not null } map && viewport is { IsVisible: true } ? Render(map, viewport) : null;
        if (frame is null)
        {
            HeadlessUi.SaveScreenshot(window, Path.GetFileNameWithoutExtension(file));
        }
        else
        {
            File.WriteAllBytes(Path.Combine(output, file), Compose(viewport!, frame));
        }

        return file;
    }

    /// <summary>Writes <c>steps.json</c>.</summary>
    public void WriteSteps() => File.WriteAllText(Path.Combine(output, "steps.json"), JsonSerializer.Serialize(_steps, JsonOptions));

    /// <summary>True when <paramref name="text"/> shows a path of the owner's profile or temp folder.</summary>
    public static bool ShowsOwnerPath(string? text) =>
        text is not null && OwnerRoots.Any(root => root.Length > 0 && text.Contains(root, StringComparison.OrdinalIgnoreCase));

    // Toasts and the status line (the last log line) that print an owner path are taken out of the picture.
    private void Scrub()
    {
        foreach (var toast in services.Notifications.Toasts.Where(t => ShowsOwnerPath(t.Title) || ShowsOwnerPath(t.Message)).ToList())
        {
            services.Notifications.Dismiss(toast);
        }

        if (ShowsOwnerPath(services.Log.Last?.Message))
        {
            services.Log.Add(new LogEntry(DateTime.Now, LogLevel.Information, "tutorial", "Ready"));
        }
    }

    // The window-relative centre of the part of the control that is on screen (clipped by every ancestor), or null.
    private int[]? Centre(Control? focus)
    {
        if (focus is not { IsVisible: true, Bounds: { Width: > 0, Height: > 0 } }
            || focus.TranslatePoint(new Point(0, 0), window) is not { } topLeft
            || focus.TranslatePoint(new Point(focus.Bounds.Width, focus.Bounds.Height), window) is not { } bottomRight)
        {
            return null;
        }

        var shown = new Rect(topLeft, bottomRight).Intersect(new Rect(window.Bounds.Size));
        foreach (var ancestor in focus.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.TranslatePoint(new Point(0, 0), window) is { } a && ancestor.TranslatePoint(new Point(ancestor.Bounds.Width, ancestor.Bounds.Height), window) is { } b)
            {
                shown = shown.Intersect(new Rect(a, b));
            }
        }

        return shown.Width > 0 && shown.Height > 0 ? [(int)Math.Round(shown.Center.X), (int)Math.Round(shown.Center.Y)] : null;
    }

    // The viewport's own renderer over the page's prepared scene with the page's edit state applied, as LevelViewport does.
    private byte[]? Render(MapPageViewModel map, LevelViewport viewport)
    {
        var w = Math.Max(1, (int)Math.Round(viewport.Bounds.Width));
        var h = Math.Max(1, (int)Math.Round(viewport.Bounds.Height));
        if (!OffscreenGlContext.TryCreate(w, h, out var context, out _) || context is null)
        {
            return null;
        }

        using (context)
        {
            using var renderer = new SceneRenderer(context);
            using var level = LevelSceneUploader.Upload(renderer, map.PreparedScene!);
            foreach (var extra in map.ExtraMeshes)
            {
                if (!level.HasMesh(extra.Asset.MeshPath))
                {
                    level.AddMesh(extra);
                }
            }

            foreach (var (id, pins) in map.PinOverrides)
            {
                level.ReplacePins(id, pins);
            }

            foreach (var clone in map.Clones)
            {
                level.AddClone(clone.Id, clone.SourceId, clone.RootWorld, clone.Name, clone.MeshPath, clone.Placements);
            }

            foreach (var (id, root) in map.ActorTransforms)
            {
                level.SetActorTransform(id, root);
            }

            level.SetInstanceTransforms(map.InstanceTransforms);
            var hidden = map.HiddenActorIds;
            var hiddenInstances = map.HiddenInstanceKeys;
            var hiddenPins = map.HiddenPinKinds;
            foreach (var node in level.Scene.Nodes)
            {
                var visible = node.SelectableId == 0 || !hidden.Contains(node.SelectableId);
                if (visible && hiddenInstances.Count > 0 && node.Tag is ScenePlacement { InstanceKey: { } key })
                {
                    visible = !hiddenInstances.Contains(key);
                }

                if (visible && hiddenPins.Count > 0 && node.Tag is ScenePlacement pin && SpawnMarkers.KindOfMesh(pin.MeshPath) is { } kind)
                {
                    visible = !hiddenPins.Contains(kind);
                }

                node.Visible = visible;
            }

            if (map.SelectedInstanceKey is { } instance && instance.SelectableId == map.SelectedActorId)
            {
                level.SelectInstance(instance);
            }
            else
            {
                level.SetSelection(map.SelectedActorId == 0 ? [] : [map.SelectedActorId]);
            }

            level.HighlightAlso(map.KindSelectionIds, map.KindSelectionInstances);

            var camera = new FlyCamera();
            var aspect = (float)w / h;
            var all = level.TerrainBounds.IsEmpty ? level.Bounds : level.TerrainBounds;
            if (_view is { } view)
            {
                (camera.Position, camera.Yaw, camera.Pitch) = (view.Position, view.Yaw, view.Pitch);
                camera.SetClipRange(view.Near, view.Far);
            }
            else if (FocusBox(level) is { IsEmpty: false } focus)
            {
                var distance = camera.Frame(focus, aspect, -135f, _frame.Pitch);
                var halfWidth = distance * MathF.Tan(camera.FieldOfView * UeMath.DegreesToRadians * 0.5f) * aspect; // half the view at the focus
                camera.Position += camera.Right * (_frame.Shift * 2f * halfWidth);
                camera.FitClipRange(distance, MathF.Max(all.Extent.Length(), 1f) + Vector3.Distance(focus.Center, all.Center));
                _view = (camera.Position, camera.Yaw, camera.Pitch, camera.NearPlane, camera.FarPlane);
            }
            else
            {
                camera.Frame(all, aspect, -135f, -35f);
            }

            using var targetBuffer = renderer.CreateTarget(w, h);
            renderer.Render(targetBuffer, level.Scene, camera);
            var rgba = targetBuffer.ReadColorRgba();
            var temp = Path.Combine(output, "viewport.tmp.png");
            ImageExport.SavePngAsync(rgba, w, h, temp).GetAwaiter().GetResult();
            var png = File.ReadAllBytes(temp);
            File.Delete(temp);
            return png;
        }
    }

    // The box the camera frames: the one FrameAt gave, or the actor FrameOn named with its margin; empty when neither.
    private BoundingBox FocusBox(LevelScene level)
    {
        if (_frame.Box is { } box)
        {
            return box;
        }

        if (_frame.Id == 0 || Bounds(level, _frame.Id) is not { IsEmpty: false } actor)
        {
            return BoundingBox.Empty;
        }

        var margin = new Vector3(_frame.MarginCm);
        return new BoundingBox(actor.Min - margin, actor.Max + margin);
    }

    private static BoundingBox Bounds(LevelScene level, uint selectableId)
    {
        var bounds = BoundingBox.Empty;
        foreach (var node in level.Scene.Nodes)
        {
            if (node.SelectableId == selectableId && node.Mesh is { } mesh)
            {
                bounds = bounds.Union(LevelSceneUploader.TransformBounds(mesh.Bounds, node.WorldTransform));
            }
        }

        return bounds;
    }

    // The GL control draws nothing headless, so its host panel's background shows through wherever nothing opaque sits
    // above it. Rendered over black and over white, the difference is that coverage: the offscreen frame goes under
    // everything drawn over the viewport (flyouts, cards, a scrim) by its alpha, unlike AppStudioUi.ComposeWindow, which
    // only knows the viewport's own sibling overlays.
    private byte[] Compose(LevelViewport viewport, byte[] glPng)
    {
        var scaling = window.RenderScaling;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Round(window.ClientSize.Width * scaling)), Math.Max(1, (int)Math.Round(window.ClientSize.Height * scaling)));
        var dpi = new Avalonia.Vector(96 * scaling, 96 * scaling);
        var host = viewport.Parent as Panel;
        var keep = host?.Background;
        byte[] black, white;
        int stride;
        try
        {
            host?.SetValue(Panel.BackgroundProperty, Brushes.Black);
            black = RenderWindow(pixels, dpi, out stride);
            host?.SetValue(Panel.BackgroundProperty, Brushes.White);
            white = RenderWindow(pixels, dpi, out _);
        }
        finally
        {
            host?.SetValue(Panel.BackgroundProperty, keep);
        }

        if (viewport.TranslatePoint(default, window) is not { } origin)
        {
            return Encode(black, pixels, dpi, stride);
        }

        using var frame = new Bitmap(new MemoryStream(glPng));
        var gl = ReadPixels(frame, out var glStride);
        var x0 = (int)Math.Round(origin.X * scaling);
        var y0 = (int)Math.Round(origin.Y * scaling);
        var width = Math.Min(frame.PixelSize.Width, pixels.Width - x0);
        var height = Math.Min(frame.PixelSize.Height, pixels.Height - y0);
        for (var y = Math.Max(0, -y0); y < height; y++)
        {
            for (var x = Math.Max(0, -x0); x < width; x++)
            {
                var i = ((y0 + y) * stride) + ((x0 + x) * 4);
                var g = (y * glStride) + (x * 4);
                // Premultiplied BGRA over black is the overlay's own contribution; over white minus over black is what shows through.
                var through = ((white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2])) / (3f * 255f);
                if (through >= 1f)
                {
                    gl.AsSpan(g, 4).CopyTo(black.AsSpan(i, 4));
                }
                else if (through > 0f)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        black[i + c] = (byte)Math.Clamp(black[i + c] + (gl[g + c] * through), 0f, 255f);
                    }

                    black[i + 3] = 255;
                }
            }
        }

        return Encode(black, pixels, dpi, stride);
    }

    private byte[] RenderWindow(PixelSize pixels, Avalonia.Vector dpi, out int stride)
    {
        using var target = new RenderTargetBitmap(pixels, dpi);
        target.Render(window);
        return ReadPixels(target, out stride);
    }

    /// <summary>The pixels of <paramref name="bitmap"/> as premultiplied BGRA rows.</summary>
    private static byte[] ReadPixels(Bitmap bitmap, out int stride)
    {
        using var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        bitmap.CopyPixels(buffer, AlphaFormat.Premul);
        stride = buffer.RowBytes;
        var bytes = new byte[stride * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] Encode(byte[] bgra, PixelSize pixels, Avalonia.Vector dpi, int stride)
    {
        using var composed = new WriteableBitmap(pixels, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = composed.Lock())
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                Marshal.Copy(bgra, y * stride, buffer.Address + (y * buffer.RowBytes), Math.Min(stride, buffer.RowBytes));
            }
        }

        using var stream = new MemoryStream();
        composed.Save(stream);
        return stream.ToArray();
    }
}
