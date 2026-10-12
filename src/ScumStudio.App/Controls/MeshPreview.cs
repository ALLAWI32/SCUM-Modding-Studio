using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>How <see cref="MeshPreview"/> lights the model.</summary>
public enum PreviewLight
{
    /// <summary>Soft studio light: a neutral sky and a warm key light that follows the camera, so every side reads.</summary>
    Studio,

    /// <summary>The island's sun at 13:30 with its sky, and shadows on a floor: the model as it stands in the game.</summary>
    Sun,
}

/// <summary>A toggle's <c>IsChecked</c> to <see cref="PreviewLight.Sun"/> and back.</summary>
public sealed class PreviewLightConverter : IValueConverter
{
    /// <summary>Checked = <see cref="PreviewLight.Sun"/>.</summary>
    public static PreviewLightConverter IsSun { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is PreviewLight.Sun;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? PreviewLight.Sun : PreviewLight.Studio;
}

/// <summary>
/// An orbit preview of one <see cref="PreviewModel"/> (meshes with their textures) in the app's OpenGL renderer, in studio
/// light or in the island's sun (<see cref="Light"/>), with a 1.8 m person and a metre grid beside it (<see cref="ShowScale"/>):
/// left-drag orbits, right/middle-drag pans, the wheel zooms, a double click or <c>F</c> reframes. The frame is drawn at
/// twice the control's size and filtered down (<see cref="RenderScale"/>). The model is prepared on a worker
/// (<see cref="MeshPreviewLoader"/>); the GPU upload happens in <see cref="OnOpenGlRender"/> when <see cref="Model"/> changes,
/// its full-size textures over the next frames (<see cref="PreviewScene.UploadStaged"/>).
/// </summary>
public sealed class MeshPreview : OpenGlControlBase
{
    /// <summary>The model to show (null clears the preview).</summary>
    public static readonly StyledProperty<PreviewModel?> ModelProperty =
        AvaloniaProperty.Register<MeshPreview, PreviewModel?>(nameof(Model));

    /// <summary>Draw the ground grid under the model (while <see cref="ShowScale"/> is on).</summary>
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<MeshPreview, bool>(nameof(ShowGrid), true);

    /// <summary>A 1.8 m person beside the model and a grid in metres (centimetres for small items) under it.</summary>
    public static readonly StyledProperty<bool> ShowScaleProperty =
        AvaloniaProperty.Register<MeshPreview, bool>(nameof(ShowScale), true);

    /// <summary>Studio light or the island's sun.</summary>
    public static readonly StyledProperty<PreviewLight> LightProperty =
        AvaloniaProperty.Register<MeshPreview, PreviewLight>(nameof(Light));

    /// <summary>The model's length × width × height ("4.12 × 1.86 × 1.52 m"), read-only.</summary>
    public static readonly StyledProperty<string> SizeTextProperty =
        AvaloniaProperty.Register<MeshPreview, string>(nameof(SizeText), string.Empty);

    /// <summary>True when the OpenGL context could be created.</summary>
    public static readonly StyledProperty<bool> IsGlReadyProperty =
        AvaloniaProperty.Register<MeshPreview, bool>(nameof(IsGlReady));

    /// <summary>The part picked with a click ("Wheel_FL / M_Tyre"), or empty (read-only).</summary>
    public static readonly StyledProperty<string> SelectedPartProperty =
        AvaloniaProperty.Register<MeshPreview, string>(nameof(SelectedPart), string.Empty);

    /// <summary>Vehicle attachment package whose parts are drawn highlighted (<see cref="PreviewPart.Attachment"/>), or empty.</summary>
    public static readonly StyledProperty<string> HighlightAttachmentProperty =
        AvaloniaProperty.Register<MeshPreview, string>(nameof(HighlightAttachment), string.Empty);

    /// <summary>"3 parts · 12,345 tris" for the uploaded model (read-only).</summary>
    public static readonly StyledProperty<string> InfoProperty =
        AvaloniaProperty.Register<MeshPreview, string>(nameof(Info), string.Empty);

    private const float OrbitDegreesPerPixel = 0.4f;
    private const float DefaultYaw = PreviewScene.DefaultViewYaw;
    private const float DefaultPitch = -20f;

    /// <summary>13:30, the island's usual look (<see cref="RenderSettings.TimeOfDay"/>).</summary>
    private const float IslandHour = 13.5f;

    /// <summary>Milliseconds a frame may spend sending full-size textures (<see cref="PreviewScene.Stage"/>).</summary>
    private const double StageBudgetMs = 8.0;

    private static readonly Scene EmptyScene = new();

    // Soft studio light: brighter hemisphere ambient than the level viewport, a warm key light that follows the camera.
    private static readonly Vector3 StudioSky = new(0.5f, 0.52f, 0.56f);
    private static readonly Vector3 StudioGround = new(0.2f, 0.19f, 0.17f);
    private static readonly Vector3 StudioLight = new(0.72f, 0.7f, 0.64f);

    private readonly FlyCamera _camera = new();
    private ProcAddressGlContext? _context;
    private SceneRenderer? _renderer;
    private RenderTarget? _target;
    private PreviewScene? _scene;
    private bool _modelDirty;
    private bool _highlightDirty;
    private bool _frameRequested;
    private BoundingBox _bounds = BoundingBox.Empty;
    private Vector3 _orbitCentre;
    private float _yaw = DefaultYaw;
    private float _pitch = DefaultPitch;
    private float _distance = 300f;
    private bool _orbiting;
    private bool _panning;
    private Point _lastPointer;
    private Point _pressPointer;
    private (int X, int Y)? _pendingPick;
    private SceneNode? _selectedPart;
    private string? _pendingScreenshot;
    private TaskCompletionSource<string>? _screenshotResult;

    static MeshPreview()
    {
        FocusableProperty.OverrideDefaultValue<MeshPreview>(true);
        ModelProperty.Changed.AddClassHandler<MeshPreview>((c, _) =>
        {
            c._modelDirty = true;
            c.RequestNextFrameRendering();
        });
        ShowGridProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        ShowScaleProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        LightProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        HighlightAttachmentProperty.Changed.AddClassHandler<MeshPreview>((c, _) =>
        {
            c._highlightDirty = true;
            c.RequestNextFrameRendering();
        });
    }

    /// <inheritdoc cref="ModelProperty" />
    public PreviewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <inheritdoc cref="ShowGridProperty" />
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    /// <inheritdoc cref="ShowScaleProperty" />
    public bool ShowScale
    {
        get => GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <inheritdoc cref="LightProperty" />
    public PreviewLight Light
    {
        get => GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }

    /// <inheritdoc cref="SizeTextProperty" />
    public string SizeText
    {
        get => GetValue(SizeTextProperty);
        private set => SetValue(SizeTextProperty, value);
    }

    /// <inheritdoc cref="IsGlReadyProperty" />
    public bool IsGlReady
    {
        get => GetValue(IsGlReadyProperty);
        private set => SetValue(IsGlReadyProperty, value);
    }

    /// <inheritdoc cref="SelectedPartProperty" />
    public string SelectedPart
    {
        get => GetValue(SelectedPartProperty);
        private set => SetValue(SelectedPartProperty, value);
    }

    /// <inheritdoc cref="HighlightAttachmentProperty" />
    public string HighlightAttachment
    {
        get => GetValue(HighlightAttachmentProperty);
        set => SetValue(HighlightAttachmentProperty, value);
    }

    /// <inheritdoc cref="InfoProperty" />
    public string Info
    {
        get => GetValue(InfoProperty);
        private set => SetValue(InfoProperty, value);
    }

    /// <summary>True once <paramref name="model"/> is uploaded and drawn (not merely assigned to <see cref="Model"/>).</summary>
    public bool IsShowing(PreviewModel? model) => model is not null && ReferenceEquals(_scene?.Model, model) && HasRenderedModel;

    /// <summary>True once a model has been uploaded and drawn at least once.</summary>
    public bool HasRenderedModel { get; private set; }

    /// <summary>Puts the whole model in view from the default angle.</summary>
    public void FrameAll()
    {
        _frameRequested = true;
        RequestNextFrameRendering();
    }

    /// <summary>Saves the next rendered frame (the preview's own colour buffer) as a PNG; completes with the path once written.</summary>
    public Task<string> SaveScreenshotAsync(string path)
    {
        _screenshotResult?.TrySetCanceled();
        _screenshotResult = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingScreenshot = path;
        RequestNextFrameRendering();
        return _screenshotResult.Task;
    }

    /// <inheritdoc />
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _context = new ProcAddressGlContext(gl.GetProcAddress, PixelSize);
            _renderer = new SceneRenderer(_context);
            _target = _renderer.CreateTarget(Math.Max(1, PixelSize().W), Math.Max(1, PixelSize().H));
            _modelDirty = Model is not null;
            IsGlReady = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Info = Loc.F("Preview.NoOpenGl", ex.Message);
            IsGlReady = false;
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ClearScene();
        _target?.Dispose();
        _target = null;
        _renderer?.Dispose();
        _renderer = null;
        _context = null;
        IsGlReady = false;
    }

    /// <inheritdoc />
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is null || _target is null)
        {
            return;
        }

        var (w, h) = PixelSize();
        if (w < 1 || h < 1)
        {
            return;
        }

        var scale = RenderScale(w, h);
        _target.Resize(w * scale, h * scale);
        if (_modelDirty || _highlightDirty)
        {
            var modelChanged = _modelDirty;
            _modelDirty = _highlightDirty = false;
            // A repaint of the same parts keeps the scene and the camera; the same model with other parts (an armour kit)
            // keeps the camera; anything else is a new model, framed.
            if (modelChanged && (Model is null || _scene is null || !_scene.Restyle(Model)))
            {
                var sameModel = Model is not null && _scene?.Model.Name == Model.Name;
                Upload(Model);
                _frameRequested = !sameModel;
                SelectPart(null);
            }

            _scene?.Highlight(HighlightAttachment); // a part row's "show on the car" (the selection colour)
        }

        if (_scene is { } extras && (extras.ShowScale != ShowScale || extras.Ground != (Light == PreviewLight.Sun)))
        {
            extras.ShowScale = ShowScale;
            extras.Ground = Light == PreviewLight.Sun;
            _bounds = extras.Bounds; // the camera stays; a double click frames the person too
        }

        if (_frameRequested)
        {
            _frameRequested = false;
            Frame(w, h);
        }

        // Full-size textures land a few a frame over their stand-ins (all at once for a screenshot).
        if (_scene is { PendingTextures: > 0 } staging && !staging.Stage(_pendingScreenshot is null ? StageBudgetMs : double.PositiveInfinity))
        {
            RequestNextFrameRendering();
        }

        var radius = Radius();
        _camera.Orbit(_orbitCentre, _yaw, _pitch, _distance);
        _camera.FitClipRange(_distance, radius);
        var model = _scene?.ModelBounds.Size ?? Vector3.Zero;
        var keyLight = Vector3.Normalize(_camera.Forward - (_camera.Up * 0.8f) + (_camera.Right * 0.35f));
        // The sun's shadow square is centred ahead of the camera: it grows with the zoom so a far model keeps its shadow.
        _renderer.Settings = SettingsFor(Light, _renderer.Settings, MathF.Max(radius, _distance * 0.5f), keyLight) with
        {
            ShowGrid = ShowGrid && ShowScale && !_bounds.IsEmpty,
            GridHeight = _bounds.IsEmpty ? 0f : _bounds.Min.Y,
            GridCellSize = GridCellFor(MathF.Max(model.X, MathF.Max(model.Y, model.Z))),
            GridFadeDistance = MathF.Max(_distance * 3f, radius * 6f),
        };
        if (_pendingPick is { } pick && _scene is { } picked)
        {
            _pendingPick = null;
            SelectPart(_renderer.Pick(_target, picked.Scene, _camera, pick.X * scale, pick.Y * scale)?.Node);
        }

        _renderer.Render(_target, _scene?.Scene ?? EmptyScene, _camera);

        if (_pendingScreenshot is { } shotPath)
        {
            _pendingScreenshot = null;
            var tcs = _screenshotResult;
            try
            {
                var rgba = _target.ReadColorRgba();
                var (sw, sh) = (_target.Width, _target.Height);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // The control's own size, as on screen (the supersampled frame halved by the same 2×2 box).
                        (rgba, sw, sh) = ThumbnailService.Shrink(rgba, sw, sh, Math.Max(w, h));
                        await Rendering.Imaging.ImageExport.SavePngAsync(rgba, sw, sh, shotPath).ConfigureAwait(false);
                        tcs?.TrySetResult(shotPath);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        tcs?.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                tcs?.TrySetException(ex);
            }
        }

        _target.BlitTo((uint)fb, w, h);
        HasRenderedModel = _scene is not null;
    }

    /// <summary>
    /// Gives the control a hit area: the GL frame is a child surface visual that Avalonia's hit test ignores (see
    /// <see cref="LevelViewport.Render"/>).
    /// </summary>
    public override void Render(Avalonia.Media.DrawingContext context) =>
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = _pressPointer = point.Position;
        e.Handled = true;
        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            FrameAll();
            return;
        }

        _orbiting = point.Properties.IsLeftButtonPressed;
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        if (_orbiting || _panning)
        {
            e.Pointer.Capture(this);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;
        if (_orbiting)
        {
            // "Grab" the model: it turns the way the mouse goes (dragging right turns its front to the right; the camera
            // orbits the other way), dragging down tips its top towards you.
            _yaw += (float)delta.X * OrbitDegreesPerPixel;
            _pitch = Math.Clamp(_pitch - ((float)delta.Y * OrbitDegreesPerPixel), -FlyCamera.MaxPitch, FlyCamera.MaxPitch);
        }
        else if (_panning)
        {
            // One pixel moves the model one pixel at the orbit centre's depth.
            var (_, h) = PixelSize();
            var scale = 2f * _distance * MathF.Tan(_camera.FieldOfView * UeMath.DegreesToRadians * 0.5f) / Math.Max(1, h) * (float)RenderScaling();
            _orbitCentre += ((_camera.Right * (float)-delta.X) + (_camera.Up * (float)delta.Y)) * scale;
        }
        else
        {
            return;
        }

        e.Handled = true;
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_orbiting || _panning)
        {
            var moved = e.GetPosition(this) - _pressPointer;
            if (_orbiting && Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4)
            {
                // A click (not a drag) picks the part under the cursor: a wheel, a door, a seat.
                var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                _pendingPick = ((int)(_pressPointer.X * scaling), (int)(_pressPointer.Y * scaling));
                RequestNextFrameRendering();
            }

            _orbiting = _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F)
        {
            FrameAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Every hidden part comes back.
            foreach (var node in _scene?.Scene.Nodes ?? [])
            {
                node.Visible = true;
            }
        }
        else if (e.Key == Key.H && _selectedPart is { } part)
        {
            // Hide the picked part to look under it (a door off, a wheel off).
            part.Visible = false;
            SelectPart(null);
        }
        else if (e.Key == Key.Escape && _selectedPart is not null)
        {
            SelectPart(null);
        }
        else
        {
            return;
        }

        e.Handled = true;
        RequestNextFrameRendering();
    }

    /// <summary>Highlights <paramref name="node"/> (one part of the model) or nothing.</summary>
    private void SelectPart(SceneNode? node)
    {
        if (_selectedPart is { } old)
        {
            old.Selected = false;
        }

        _selectedPart = node is { SelectableId: > 0 } ? node : null;
        if (_selectedPart is { } part)
        {
            part.Selected = true;
        }

        var name = _selectedPart?.Name ?? string.Empty;
        Dispatcher.UIThread.Post(() => SelectedPart = name);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var notches = (float)(e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X);
        if (notches == 0f)
        {
            return;
        }

        var radius = Radius();
        _distance = Math.Clamp(_distance * MathF.Pow(0.85f, notches), radius * 0.02f, radius * 40f);
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            RequestNextFrameRendering(); // shown again (its page came back)
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _orbiting = _panning = false;
    }

    private void Upload(PreviewModel? model)
    {
        ClearScene();
        HasRenderedModel = false;
        if (model is null || _renderer is null)
        {
            PostInfo(string.Empty, string.Empty);
            return;
        }

        _scene = PreviewScene.UploadStaged(_renderer, model);
        _scene.ShowScale = ShowScale;
        _scene.Ground = Light == PreviewLight.Sun;
        _bounds = _scene.Bounds;
        var textured = model.Parts.Count(p => p.TexturePath is not null);
        var parts = model.Parts.Count.ToString("N0", CultureInfo.CurrentCulture);
        var triangles = model.Triangles.ToString("N0", CultureInfo.CurrentCulture);
        PostInfo(textured > 0 ? Loc.F("Preview.Info", parts, triangles, textured.ToString("N0", CultureInfo.CurrentCulture)) : Loc.F("Preview.InfoUntextured", parts, triangles),
            PreviewSize(_scene.ModelBounds));
    }

    /// <summary>
    /// Supersampling of the preview's frame: 2 (drawn at twice the control's size, then a 2×2 box down to it: smooth edges,
    /// calmer leaves and fences) while that frame stays within 4K (3840×2160 pixels, 4096 per edge), else 1.
    /// </summary>
    internal static int RenderScale(int width, int height) =>
        (long)width * height * 4 <= 3840L * 2160 && width * 2 <= 4096 && height * 2 <= 4096 ? 2 : 1;

    /// <summary>
    /// The grid's cell (cm) under a model whose largest edge is <paramref name="extentCm"/>: a metre from 50 cm up (major
    /// lines every 10 m), so a car or a tree reads in metres; 10 cm below that, 1 cm under 5 cm.
    /// </summary>
    internal static float GridCellFor(float extentCm) => extentCm >= 50f ? 100f : extentCm >= 5f ? 10f : 1f;

    /// <summary>
    /// Length × width × height of GL-space <paramref name="bounds"/> in UE's axes (X, Y, Z): metres with two decimals
    /// from 1 m up, centimetres below; empty bounds give an empty text.
    /// </summary>
    internal static string PreviewSize(BoundingBox bounds)
    {
        if (bounds.IsEmpty)
        {
            return string.Empty;
        }

        var (x, y, z) = (bounds.Size.X, bounds.Size.Z, bounds.Size.Y); // GL Y is up (UE Z)
        string Text(float value, string format) => value.ToString(format, CultureInfo.CurrentCulture);
        return MathF.Max(x, MathF.Max(y, z)) >= 100f
            ? Loc.F("Preview.SizeMetres", Text(x / 100f, "0.00"), Text(y / 100f, "0.00"), Text(z / 100f, "0.00"))
            : Loc.F("Preview.SizeCentimetres", Text(x, "0.#"), Text(y, "0.#"), Text(z, "0.#"));
    }

    /// <summary>
    /// <paramref name="current"/> lit for <paramref name="light"/>: the studio's soft sky and a key light along
    /// <paramref name="keyLight"/> (it follows the camera), or the island's sun at 13:30 with its sky and shadows reaching
    /// six times <paramref name="radius"/> (at least 30 m) around the view.
    /// </summary>
    internal static RenderSettings SettingsFor(PreviewLight light, RenderSettings current, float radius, Vector3 keyLight) => light == PreviewLight.Sun
        ? current with
        {
            ClearColor = RenderSettings.Outdoor.ClearColor,
            SkyColor = RenderSettings.Outdoor.SkyColor,
            GroundColor = RenderSettings.Outdoor.GroundColor,
            LightColor = RenderSettings.Outdoor.LightColor,
            LightDirection = RenderSettings.IslandSunDirection,
            Exposure = RenderSettings.Outdoor.Exposure,
            Sky = true,
            TimeOfDay = IslandHour,
            Shadows = true,
            ShadowDistance = MathF.Max(radius * 6f, 3000f),
        }
        : current with
        {
            ClearColor = new RenderSettings().ClearColor,
            SkyColor = StudioSky,
            GroundColor = StudioGround,
            LightColor = StudioLight,
            LightDirection = keyLight,
            Exposure = 0f,
            Sky = false,
            TimeOfDay = null,
            Shadows = false,
        };

    private void ClearScene()
    {
        _scene?.Dispose();
        _scene = null;
        _bounds = BoundingBox.Empty;
    }

    private void Frame(int w, int h)
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        if (_bounds.IsEmpty)
        {
            _orbitCentre = Vector3.Zero;
            _distance = 300f;
            return;
        }

        _orbitCentre = _bounds.Center;
        var aspect = h > 0 ? (float)w / h : 16f / 9f;
        _distance = _camera.Frame(_bounds, aspect, _yaw, _pitch, margin: 1.05f);
        // The bounding sphere leaves long, flat models (a rifle, a car) small: pull in until a box corner would leave the view.
        var tanY = MathF.Tan(_camera.FieldOfView * UeMath.DegreesToRadians * 0.5f);
        var tanX = tanY * aspect;
        var forward = _camera.Forward;
        var right = _camera.Right;
        var up = _camera.Up;
        var needed = 0f;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? _bounds.Min.X : _bounds.Max.X,
                (i & 2) == 0 ? _bounds.Min.Y : _bounds.Max.Y,
                (i & 4) == 0 ? _bounds.Min.Z : _bounds.Max.Z) - _orbitCentre;
            var depth = Vector3.Dot(corner, forward);
            needed = MathF.Max(needed, depth + (MathF.Abs(Vector3.Dot(corner, right)) / tanX));
            needed = MathF.Max(needed, depth + (MathF.Abs(Vector3.Dot(corner, up)) / tanY));
        }

        _distance = Math.Clamp(needed * 1.12f, Radius() * 0.5f, _distance);
    }

    private float Radius() => _bounds.IsEmpty ? 100f : MathF.Max(_bounds.Extent.Length(), 1f);

    private void PostInfo(string text, string size)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Info = text;
            SizeText = size;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                Info = text;
                SizeText = size;
            });
        }
    }

    private double RenderScaling() => VisualRoot?.RenderScaling ?? 1.0;

    private (int W, int H) PixelSize()
    {
        var scaling = RenderScaling();
        return ((int)Math.Round(Bounds.Width * scaling), (int)Math.Round(Bounds.Height * scaling));
    }
}
