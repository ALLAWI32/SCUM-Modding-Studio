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

/// <summary>What the left mouse button does in <see cref="MeshPreview"/> (the Vehicles studio picks it per tab).</summary>
public enum PreviewTool
{
    /// <summary>Left drag turns the view (the plain preview).</summary>
    Orbit,

    /// <summary>Left click and drag point at the model's surface (<see cref="MeshPreview.SurfacePointer"/>: a decal).</summary>
    Surface,

    /// <summary>Left click picks a part, the gizmo's handles move it (<see cref="MeshPreview.PartGizmo"/>).</summary>
    Gizmo,
}

/// <summary>Which handles of the part gizmo answer the mouse.</summary>
public enum PreviewGizmoMode
{
    /// <summary>The arrows and the plane squares.</summary>
    Move,

    /// <summary>The rings.</summary>
    Rotate,

    /// <summary>The cubes.</summary>
    Scale,
}

/// <summary>A click or drag on the model in <see cref="PreviewTool.Surface"/> mode: the part under the cursor and the cursor's ray, in UE space (cm).</summary>
public sealed class PreviewSurfaceEventArgs(int partIndex, FVector origin, FVector direction, bool isDrag, bool isRelease, bool isCancel = false) : EventArgs
{
    /// <summary>Index into the model's parts of the part under the cursor, or -1 (nothing drawn there).</summary>
    public int PartIndex { get; } = partIndex;

    /// <summary>Where the ray starts (the camera's near plane).</summary>
    public FVector Origin { get; } = origin;

    /// <summary>The ray's direction (unit).</summary>
    public FVector Direction { get; } = direction;

    /// <summary>The button is held and the cursor moved (false for the press itself).</summary>
    public bool IsDrag { get; } = isDrag;

    /// <summary>The button went up: the drag ends here.</summary>
    public bool IsRelease { get; } = isRelease;

    /// <summary>Escape ended the drag: undo what it did.</summary>
    public bool IsCancel { get; } = isCancel;
}

/// <summary>The wheel or a key over the model in <see cref="PreviewTool.Surface"/> mode (a decal sized, turned, moved); <see cref="Handled"/> keeps the camera's own use of it.</summary>
public sealed class PreviewSurfaceInputEventArgs(Key key, float notches, KeyModifiers modifiers) : EventArgs
{
    /// <summary>The key pressed, or <see cref="Key.None"/> for the wheel.</summary>
    public Key Key { get; } = key;

    /// <summary>Wheel notches (up positive), 0 for a key.</summary>
    public float Notches { get; } = notches;

    /// <summary>Ctrl, Shift … held.</summary>
    public KeyModifiers Modifiers { get; } = modifiers;

    /// <summary>Set by a handler that used it.</summary>
    public bool Handled { get; set; }
}

/// <summary>A part of <see cref="MeshPreview.Model"/> drawn away from its place: moved by <paramref name="Delta"/> (drawn as its place times the delta), or hidden.</summary>
public readonly record struct PreviewPartOverride(FTransform Delta, bool Hidden);

/// <summary>A gizmo drag of one part in <see cref="PreviewTool.Gizmo"/> mode: where the part stands now (UE space).</summary>
public sealed class PreviewGizmoEventArgs(int partIndex, FTransform transform, bool committed, FTransform? start = null) : EventArgs
{
    /// <summary>Where the part stood when the drag began (the delta of the drag is from here).</summary>
    public FTransform Start { get; } = start ?? transform;


    /// <summary>Index into the model's parts.</summary>
    public int PartIndex { get; } = partIndex;

    /// <summary>The part's transform in the vehicle (the start one again when Escape cancelled the drag).</summary>
    public FTransform Transform { get; } = transform;

    /// <summary>The mouse went up: this is where the part stays (false while dragging and for a cancel).</summary>
    public bool Committed { get; } = committed;
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

    /// <summary>
    /// The game's prisoner (<see cref="ScaleFigure"/>) stands beside the model while <see cref="ShowScale"/> is on; off hides
    /// him and keeps the grid (the "Person" toggle of the Vehicles and Weapons 3D tabs).
    /// </summary>
    public static readonly StyledProperty<bool> ShowFigureProperty =
        AvaloniaProperty.Register<MeshPreview, bool>(nameof(ShowFigure), true);

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

    /// <summary>What the left mouse button does: turn the view, point at the surface, or move the selected part.</summary>
    public static readonly StyledProperty<PreviewTool> ToolProperty =
        AvaloniaProperty.Register<MeshPreview, PreviewTool>(nameof(Tool));

    /// <summary>The gizmo's handles that answer the mouse in <see cref="PreviewTool.Gizmo"/> mode.</summary>
    public static readonly StyledProperty<PreviewGizmoMode> GizmoModeProperty =
        AvaloniaProperty.Register<MeshPreview, PreviewGizmoMode>(nameof(GizmoMode));

    /// <summary>Index into the model's parts of the selected part, or -1; set it to select a part from a list.</summary>
    public static readonly StyledProperty<int> SelectedPartIndexProperty =
        AvaloniaProperty.Register<MeshPreview, int>(nameof(SelectedPartIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Parts drawn away from their place in <see cref="Model"/> (the Vehicles studio's moved or hidden parts), by part index; kept across repaints.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, PreviewPartOverride>?> PartOverridesProperty =
        AvaloniaProperty.Register<MeshPreview, IReadOnlyDictionary<int, PreviewPartOverride>?>(nameof(PartOverrides));

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
    private bool _overridesDirty;
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
    private readonly List<(int X, int Y, bool Drag, bool Release)> _pendingSurface = [];
    private bool _surfaceDown;
    private int? _selectRequest;
    private GizmoAxis _gizmoHover;
    private GizmoAxis _gizmoHandle;
    private FTransform _gizmoStart;
    private Point _gizmoFrom;
    private int _gizmoPart = -1;

    private static Cursor? _handCursor;
    private static Cursor? _surfaceCursor;

    /// <summary>Farthest a gizmo drag moves a part (cm): a ray nearly along an arrow meets it kilometres away, the part stays instead.</summary>
    internal const float PartReach = 2000f;

    /// <summary>Pixels around a gizmo handle that still grab it (as on the Map).</summary>
    private const double GizmoPickRadius = 10;

    /// <summary>Raised in <see cref="PreviewTool.Surface"/> mode on a press, each drag step (at most one a frame) and the release (UI thread).</summary>
    public event EventHandler<PreviewSurfaceEventArgs>? SurfacePointer;

    /// <summary>Raised in <see cref="PreviewTool.Surface"/> mode for the wheel and keys before the view uses them (UI thread).</summary>
    public event EventHandler<PreviewSurfaceInputEventArgs>? SurfaceInput;

    /// <summary>Raised in <see cref="PreviewTool.Gizmo"/> mode while a part is dragged and once when it is dropped or the drag is cancelled (UI thread).</summary>
    public event EventHandler<PreviewGizmoEventArgs>? PartGizmo;

    static MeshPreview()
    {
        FocusableProperty.OverrideDefaultValue<MeshPreview>(true);
        DragDrop.AllowDropProperty.OverrideDefaultValue<MeshPreview>(true);
        ToolProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.OnToolChanged());
        GizmoModeProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        SelectedPartIndexProperty.Changed.AddClassHandler<MeshPreview>((c, e) =>
        {
            if (e.NewValue is int index && index != c._scene?.PartIndexOf(c._selectedPart))
            {
                c._selectRequest = index;
                c.RequestNextFrameRendering();
            }
        });
        ModelProperty.Changed.AddClassHandler<MeshPreview>((c, _) =>
        {
            c._modelDirty = true;
            c.RequestNextFrameRendering();
        });
        PartOverridesProperty.Changed.AddClassHandler<MeshPreview>((c, _) =>
        {
            c._overridesDirty = true;
            c.RequestNextFrameRendering();
        });
        ShowGridProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        ShowScaleProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        ShowFigureProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        LightProperty.Changed.AddClassHandler<MeshPreview>((c, _) => c.RequestNextFrameRendering());
        HighlightAttachmentProperty.Changed.AddClassHandler<MeshPreview>((c, _) =>
        {
            c._highlightDirty = true;
            c.RequestNextFrameRendering();
        });
    }

    /// <summary>Creates the view; it takes decal designs dragged from the Decals library.</summary>
    public MeshPreview()
    {
        AddHandler(DragDrop.DragOverEvent, (_, e) => OnDecalDragOver(e));
        AddHandler(DragDrop.DropEvent, (_, e) => OnDecalDrop(e, e.GetPosition(this)));
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

    /// <inheritdoc cref="ShowFigureProperty" />
    public bool ShowFigure
    {
        get => GetValue(ShowFigureProperty);
        set => SetValue(ShowFigureProperty, value);
    }

    /// <summary>The scene's figure switch: the scale on and the person not hidden.</summary>
    private bool FigureWanted => ShowScale && ShowFigure;

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

    /// <inheritdoc cref="ToolProperty" />
    public PreviewTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <inheritdoc cref="GizmoModeProperty" />
    public PreviewGizmoMode GizmoMode
    {
        get => GetValue(GizmoModeProperty);
        set => SetValue(GizmoModeProperty, value);
    }

    /// <inheritdoc cref="SelectedPartIndexProperty" />
    public int SelectedPartIndex
    {
        get => GetValue(SelectedPartIndexProperty);
        set => SetValue(SelectedPartIndexProperty, value);
    }

    /// <inheritdoc cref="PartOverridesProperty" />
    public IReadOnlyDictionary<int, PreviewPartOverride>? PartOverrides
    {
        get => GetValue(PartOverridesProperty);
        set => SetValue(PartOverridesProperty, value);
    }

    /// <summary>Every part of <paramref name="model"/> where <paramref name="overrides"/> puts it (its own place when it has none), shown or hidden.</summary>
    internal static void ApplyPartOverrides(PreviewScene scene, PreviewModel model, IReadOnlyDictionary<int, PreviewPartOverride>? overrides)
    {
        for (var i = 0; i < model.Parts.Count; i++)
        {
            PreviewPartOverride? o = overrides is not null && overrides.TryGetValue(i, out var found) ? found : null;
            scene.SetPartTransform(i, o is { } v ? model.Parts[i].Transform * v.Delta : model.Parts[i].Transform);
            scene.SetPartHidden(i, o?.Hidden == true);
        }
    }

    /// <summary>Moves part <paramref name="index"/> in the view (a part's offset from the Parts tab) without a new upload; false when there is no such part.</summary>
    public bool SetPartTransform(int index, FTransform transform)
    {
        var done = _scene?.SetPartTransform(index, transform) ?? false;
        RequestNextFrameRendering();
        return done;
    }

    /// <summary>Hides part <paramref name="index"/> in the view or shows it again; false when there is no such part.</summary>
    public bool SetPartHidden(int index, bool hidden)
    {
        var done = _scene?.SetPartHidden(index, hidden) ?? false;
        RequestNextFrameRendering();
        return done;
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
            _renderer = new SceneRenderer(_context, GraphicsStatus.Fallbacks);
            _target = _renderer.CreateTarget(Math.Max(1, PixelSize().W), Math.Max(1, PixelSize().H));
            _modelDirty = Model is not null;
            ScaleFigure.CharacterChanged -= OnFigureChanged;
            ScaleFigure.CharacterChanged += OnFigureChanged; // the game's prisoner arrives a moment after the catalog opens
            IsGlReady = true;
            GraphicsStatus.Started("3D preview", _renderer.Info, _renderer.CreationErrors.Select(e => e.ToString()).ToList());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Info = Loc.F("Preview.NoOpenGl", ex.Message);
            GraphicsStatus.StartFailed("3D preview", ex);
            IsGlReady = false;
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        try
        {
            RenderFrame(gl, fb);
            _failedFrames = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // D03: a driver error must not take the app down; the preview says so and the next frame tries again.
            _failedFrames++;
            GraphicsStatus.FrameFailed("3D preview", ex, _failedFrames);
            try
            {
                _renderer?.ResetState();
                _context?.Gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, (uint)fb);
                _context?.Gl.Finish(); // Avalonia hands the frame on after a flush only: complete, as RenderTarget.BlitTo leaves it
            }
            catch (Exception reset) when (reset is not OutOfMemoryException)
            {
                GraphicsStatus.FrameFailed("3D preview (restoring the GL state)", reset, _failedFrames);
            }

            var message = Loc.F("Viewport.GraphicsRecovered", ex.Message);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Info = message);
        }
    }

    private int _failedFrames; // frames in a row that threw (logged in full the first few times)

    /// <summary>The scale figure was loaded (or forgotten) on a worker: the next frame stands it beside the model.</summary>
    private void OnFigureChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RequestNextFrameRendering);

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ScaleFigure.CharacterChanged -= OnFigureChanged;
        ClearScene();
        _target?.Dispose();
        _target = null;
        _renderer?.Dispose();
        _renderer = null;
        _context = null;
        IsGlReady = false;
    }

    private void RenderFrame(GlInterface gl, int fb)
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
            _overridesDirty |= modelChanged; // a repaint brings the parts back to their places in the model
            // A repaint of the same parts keeps the scene and the camera; the same model with other parts (an armour kit)
            // keeps the camera; anything else is a new model, framed.
            if (modelChanged && (Model is null || _scene is null || !_scene.Restyle(Model)))
            {
                var sameModel = Model is not null && _scene?.Model.Name == Model.Name;
                Upload(Model);
                _frameRequested = !sameModel;
                SelectPart(null);
            }
            else if (modelChanged && _scene is { } restyled)
            {
                _bounds = restyled.Bounds; // a moved part (the camera stays)
            }

            _scene?.Highlight(HighlightAttachment); // a part row's "show on the car" (the selection colour)
            if (_selectedPart is { } kept)
            {
                kept.Selected = true; // the highlight above cleared it
            }
        }

        if (_overridesDirty && _scene is { } placed && Model is { } shown && _gizmoHandle == GizmoAxis.None)
        {
            _overridesDirty = false;
            ApplyPartOverrides(placed, shown, PartOverrides);
            _bounds = placed.Bounds;
        }

        if (_selectRequest is { } request)
        {
            _selectRequest = null;
            SelectPart(_scene?.PartNode(request));
        }

        if (_scene is { } extras && (extras.ShowScale != FigureWanted || extras.FigureStale || extras.Ground != (Light == PreviewLight.Sun)))
        {
            extras.ShowScale = FigureWanted;
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

        if (_pendingSurface.Count > 0 && _scene is { } pointed)
        {
            // One drag pick a frame however fast the mouse moves (a step waits for the next frame, the latest wins).
            foreach (var surface in _pendingSurface.ToList())
            {
                var node = _renderer.Pick(_target, pointed.Scene, _camera, surface.X * scale, surface.Y * scale)?.Node;
                var index = node is null ? -1 : PartIndexOfPick(node.SelectableId);
                var (origin, direction) = UeRay(_camera, surface.X, surface.Y, w, h);
                SurfacePointer?.Invoke(this, new PreviewSurfaceEventArgs(index, origin, direction, surface.Drag, surface.Release));
            }

            _pendingSurface.Clear();
        }

        _renderer.Overlay.Clear();
        _renderer.OverlayTriangles.Clear();
        if (Tool == PreviewTool.Gizmo && SelectedGizmo() is { } gizmo)
        {
            GizmoOverlay.Draw(gizmo, _gizmoHover, _gizmoHandle, _gizmoHandle != GizmoAxis.None, GizmoMode == PreviewGizmoMode.Scale, _renderer.Overlay, _renderer.OverlayTriangles);
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
        var left = point.Properties.IsLeftButtonPressed;
        if (left && e.ClickCount == 2 && Tool == PreviewTool.Orbit)
        {
            FrameAll();
            return;
        }

        if (left && Tool == PreviewTool.Surface)
        {
            _surfaceDown = true;
            QueueSurface(point.Position, drag: false, release: false);
            e.Pointer.Capture(this);
            return;
        }

        if (left && Tool == PreviewTool.Gizmo && _scene?.PartIndexOf(_selectedPart) is int part and >= 0 && TryHitGizmo(point.Position, out var handle))
        {
            _gizmoHandle = handle;
            _gizmoPart = part;
            _gizmoStart = _scene.PartTransform(part) ?? FTransform.Identity;
            _gizmoFrom = point.Position;
            e.Pointer.Capture(this);
            RequestNextFrameRendering();
            return;
        }

        // The tools keep the left button: the right one turns the view there, the middle one pans.
        _orbiting = Tool == PreviewTool.Orbit ? left : point.Properties.IsRightButtonPressed || (left && Tool == PreviewTool.Gizmo);
        _panning = Tool == PreviewTool.Orbit ? point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed : point.Properties.IsMiddleButtonPressed;
        if (_orbiting || _panning)
        {
            e.Pointer.Capture(this);
        }
    }

    /// <summary>The drag-and-drop format of a decal design dragged from the Decals library (its <c>DecalDesignItem</c>).</summary>
    public const string DecalDragFormat = "ScumStudio.DecalDesign";

    /// <summary>
    /// Raised when a decal design is dropped on the view (owner: "press and hold on a sticker and drop it where I let go"),
    /// before the drop point goes to <see cref="SurfacePointer"/> as a click there (a press and its release): the page arms
    /// the design here, the click puts it on the car.
    /// </summary>
    public event EventHandler<DragEventArgs>? SurfaceDrop;

    /// <summary>The clicks a drop queued that the next frame picks (tests).</summary>
    internal int PendingSurfaceCount => _pendingSurface.Count;

    /// <summary>A decal design over the view: copy it, on the surface tool only.</summary>
    internal void OnDecalDragOver(DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DecalDragFormat) && Tool == PreviewTool.Surface ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A decal design dropped at <paramref name="position"/>: the page arms it, then a click there places it.</summary>
    internal void OnDecalDrop(DragEventArgs e, Point position)
    {
        if (!e.Data.Contains(DecalDragFormat) || Tool != PreviewTool.Surface)
        {
            return;
        }

        SurfaceDrop?.Invoke(this, e);
        QueueSurface(position, drag: false, release: false);
        QueueSurface(position, drag: false, release: true);
        Focus();
        e.Handled = true;
    }

    /// <summary>A surface pick for the next frame (a drag step replaces the one still waiting; the press and the release are kept).</summary>
    private void QueueSurface(Point position, bool drag, bool release)
    {
        if (drag && _pendingSurface.Count > 0 && _pendingSurface[^1].Drag)
        {
            _pendingSurface.RemoveAt(_pendingSurface.Count - 1);
        }

        var scaling = RenderScaling();
        _pendingSurface.Add(((int)(position.X * scaling), (int)(position.Y * scaling), drag, release));
        RequestNextFrameRendering();
    }

    private void OnToolChanged()
    {
        CancelDrags();
        Cursor = Tool == PreviewTool.Surface ? _surfaceCursor ??= new Cursor(StandardCursorType.Cross) : null; // made on first use: a cursor needs the platform
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;
        if (_gizmoHandle != GizmoAxis.None && _scene is { } scene)
        {
            var (w, h) = PixelSize();
            var scaling = (float)RenderScaling();
            var from = new Vector2((float)_gizmoFrom.X * scaling, (float)_gizmoFrom.Y * scaling);
            var to = new Vector2((float)position.X * scaling, (float)position.Y * scaling);
            if (GizmoDrag(_camera, Math.Max(1, w), Math.Max(1, h), _gizmoStart, _gizmoHandle, from, to) is { } moved)
            {
                scene.SetPartTransform(_gizmoPart, moved);
                PartGizmo?.Invoke(this, new PreviewGizmoEventArgs(_gizmoPart, moved, committed: false, _gizmoStart));
                RequestNextFrameRendering();
            }

            e.Handled = true;
            return;
        }

        if (_surfaceDown)
        {
            QueueSurface(position, drag: true, release: false);
            e.Handled = true;
            return;
        }

        if (!_orbiting && !_panning && Tool == PreviewTool.Gizmo)
        {
            var hover = TryHitGizmo(position, out var lit) ? lit : GizmoAxis.None;
            if (hover != _gizmoHover)
            {
                _gizmoHover = hover;
                Cursor = hover == GizmoAxis.None ? null : _handCursor ??= new Cursor(StandardCursorType.Hand);
                RequestNextFrameRendering();
            }
        }

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
        if (_gizmoHandle != GizmoAxis.None)
        {
            var part = _gizmoPart;
            _gizmoHandle = GizmoAxis.None;
            _gizmoPart = -1;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (_scene?.PartTransform(part) is { } dropped)
            {
                PartGizmo?.Invoke(this, new PreviewGizmoEventArgs(part, dropped, committed: true, _gizmoStart));
            }

            RequestNextFrameRendering();
            return;
        }

        if (_surfaceDown)
        {
            _surfaceDown = false;
            QueueSurface(e.GetPosition(this), drag: false, release: true);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

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
        if (Tool == PreviewTool.Surface && !_surfaceDown && SurfaceInput is { } surface)
        {
            var args = new PreviewSurfaceInputEventArgs(e.Key, 0f, e.KeyModifiers);
            surface(this, args);
            if (args.Handled)
            {
                e.Handled = true;
                RequestNextFrameRendering();
                return;
            }
        }

        if (e.Key == Key.Escape && (_gizmoHandle != GizmoAxis.None || _surfaceDown))
        {
            CancelDrags();
            e.Handled = true;
            return;
        }

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
        var index = _scene?.PartIndexOf(_selectedPart) ?? -1;
        Dispatcher.UIThread.Post(() =>
        {
            SelectedPart = name;
            SelectedPartIndex = index;
        });
    }

    /// <summary>Ends a gizmo or surface drag without keeping it (Escape, another tool, a lost capture): the part goes back.</summary>
    private void CancelDrags()
    {
        if (_gizmoHandle != GizmoAxis.None)
        {
            var part = _gizmoPart;
            _gizmoHandle = GizmoAxis.None;
            _gizmoPart = -1;
            _scene?.SetPartTransform(part, _gizmoStart);
            PartGizmo?.Invoke(this, new PreviewGizmoEventArgs(part, _gizmoStart, committed: false, _gizmoStart));
        }

        if (_surfaceDown)
        {
            _surfaceDown = false;
            _pendingSurface.Clear();
            SurfacePointer?.Invoke(this, new PreviewSurfaceEventArgs(-1, default, default, isDrag: false, isRelease: true, isCancel: true));
        }

        RequestNextFrameRendering();
    }

    /// <summary>The selected part's gizmo, or null (no part, no scene).</summary>
    private GizmoFrame? SelectedGizmo()
    {
        return _scene?.PartIndexOf(_selectedPart) is int index and >= 0 && _scene.PartTransform(index) is { } at ? FrameAt(_camera, at) : null;
    }

    /// <summary>The handle under <paramref name="position"/> for the current <see cref="GizmoMode"/>: nearest priority first, as on the Map.</summary>
    private bool TryHitGizmo(Point position, out GizmoAxis handle)
    {
        handle = GizmoAxis.None;
        var (w, h) = PixelSize();
        if (SelectedGizmo() is not { } frame || w < 1 || h < 1)
        {
            return false;
        }

        var scaling = RenderScaling();
        var best = (Priority: int.MaxValue, Distance: GizmoPickRadius);
        foreach (var shape in GizmoOverlay.PickShapes(frame, GizmoMode == PreviewGizmoMode.Scale).Where(s => Answers(GizmoMode, s.Handle)))
        {
            var screen = shape.Points.Select(p => ScreenPoint(_camera, p, w, h)).ToList();
            if (shape.Priority > best.Priority || screen.Any(p => p is null))
            {
                continue; // a point behind the camera: not pickable
            }

            var points = screen.Select(p => new Point(p!.Value.X / scaling, p.Value.Y / scaling)).ToList();
            var distance = points.Count == 1 ? Distance(position, points[0]) : Enumerable.Range(0, shape.Closed ? points.Count : points.Count - 1)
                .Min(i => DistanceToSegment(position, points[i], points[(i + 1) % points.Count]));
            if (distance < best.Distance)
            {
                best = (shape.Priority, distance);
                handle = shape.Handle;
            }
        }

        return handle != GizmoAxis.None;
    }

    /// <summary>True when <paramref name="handle"/> answers the mouse in <paramref name="mode"/> (the view ring never: a part turns about its own axes).</summary>
    internal static bool Answers(PreviewGizmoMode mode, GizmoAxis handle) => GizmoMath.KindOf(handle) switch
    {
        GizmoKind.Move or GizmoKind.Plane => mode == PreviewGizmoMode.Move,
        GizmoKind.Rotate => mode == PreviewGizmoMode.Rotate && handle != GizmoAxis.RotateView,
        GizmoKind.Scale => mode == PreviewGizmoMode.Scale,
        _ => false,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared <= 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(p, new Point(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>The gizmo of a part standing at <paramref name="at"/> (UE space), on the world's axes, sized for <paramref name="camera"/>.</summary>
    internal static GizmoFrame FrameAt(FlyCamera camera, FTransform at)
    {
        var origin = UeToGl.Point(at.Translation);
        return new GizmoFrame(origin, GizmoMath.GlDirection(GizmoAxis.X), GizmoMath.GlDirection(GizmoAxis.Y), GizmoMath.GlDirection(GizmoAxis.Z),
            camera.Position, camera.Forward, GizmoMath.HandleLength(Vector3.Distance(camera.Position, origin)));
    }

    /// <summary>Where GL point <paramref name="gl"/> lands in a <paramref name="w"/> × <paramref name="h"/> pixel view, or null behind the camera.</summary>
    internal static Vector2? ScreenPoint(FlyCamera camera, Vector3 gl, int w, int h)
    {
        var clip = Vector4.Transform(new Vector4(gl, 1f), camera.GetViewProjection((float)w / h));
        if (clip.W <= 1e-4f)
        {
            return null;
        }

        var p = new Vector2((((clip.X / clip.W) * 0.5f) + 0.5f) * w, (0.5f - ((clip.Y / clip.W) * 0.5f)) * h);
        return float.IsFinite(p.X) && float.IsFinite(p.Y) ? p : null;
    }

    /// <summary>The ray through pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="width"/> × <paramref name="height"/> view, in UE space (cm).</summary>
    internal static (FVector Origin, FVector Direction) UeRay(FlyCamera camera, float x, float y, int width, int height)
    {
        var (origin, direction) = camera.ScreenRay(x, y, width, height);
        return (UeToGl.ToUePoint(origin), UeToGl.ToUeDirection(direction));
    }

    /// <summary>A <see cref="PreviewScene"/> pick id (1-based in the order of the model's parts, 0 = not a part) as a part index, -1 for none.</summary>
    internal static int PartIndexOfPick(uint selectableId) => selectableId == 0 ? -1 : (int)selectableId - 1;

    /// <summary>
    /// Where a drag of gizmo <paramref name="handle"/> from pixel <paramref name="from"/> to <paramref name="to"/> puts a part
    /// that stood at <paramref name="start"/>: along the arrow, in the square's plane, around the ring or scaled, about the
    /// part's origin on the world's axes. Null while the cursor's ray runs (nearly) parallel to the arrow or the plane: the
    /// Map's guard (<see cref="GizmoMath.TryClosestParameter"/>), so a part never flies off.
    /// </summary>
    internal static FTransform? GizmoDrag(FlyCamera camera, int width, int height, FTransform start, GizmoAxis handle, Vector2 from, Vector2 to)
    {
        var f = FrameAt(camera, start);
        var (o0, d0) = camera.ScreenRay(from.X, from.Y, width, height);
        var (o1, d1) = camera.ScreenRay(to.X, to.Y, width, height);
        switch (GizmoMath.KindOf(handle))
        {
            case GizmoKind.Move:
                var axis = f.Axis(handle);
                return GizmoMath.TryClosestParameter(o0, d0, f.Origin, axis, out var t0, out _) && GizmoMath.TryClosestParameter(o1, d1, f.Origin, axis, out var t1, out _)
                    && MathF.Abs(t1 - t0) <= PartReach
                    ? GizmoMath.Translate(start, handle, t1 - t0, 0f)
                    : null;
            case GizmoKind.Plane:
                var normal = f.Axis(GizmoMath.AxisOf(handle));
                if (MathF.Abs(Vector3.Dot(d1, normal)) < 0.05f || !GizmoMath.TryHitPlane(o0, d0, f.Origin, normal, out var h0) || !GizmoMath.TryHitPlane(o1, d1, f.Origin, normal, out var h1)
                    || Vector3.Distance(h0, h1) > PartReach)
                {
                    return null; // the plane seen edge-on
                }

                var (a, b) = GizmoMath.PlaneAxes(handle);
                var moved = h1 - h0;
                return GizmoMath.TranslateInPlane(start, GizmoMath.UeDirection(a), GizmoMath.UeDirection(b), Vector3.Dot(moved, f.Axis(a)), Vector3.Dot(moved, f.Axis(b)), 0f);
            case GizmoKind.Rotate when handle != GizmoAxis.RotateView:
                var ring = GizmoMath.AxisOf(handle);
                var (u, v) = f.RingBasis(ring);
                float turn;
                if (MathF.Abs(Vector3.Dot(d1, f.Axis(ring))) < 0.2f)
                {
                    // A ring seen nearly edge-on turns by the cursor's angle around the pivot on screen, as on the Map.
                    if (ScreenPoint(camera, f.Origin, width, height) is not { } centre)
                    {
                        return null;
                    }

                    var sense = Vector3.Dot(f.Axis(ring), f.View) < 0f ? -1f : 1f;
                    turn = GizmoMath.AngleDelta(ScreenAngle(from, centre), ScreenAngle(to, centre)) * sense;
                }
                else if (GizmoMath.TryHitPlane(o0, d0, f.Origin, f.Axis(ring), out var r0) && GizmoMath.TryHitPlane(o1, d1, f.Origin, f.Axis(ring), out var r1))
                {
                    turn = GizmoMath.AngleDelta(GizmoMath.PlaneAngle(r0, f.Origin, u, v), GizmoMath.PlaneAngle(r1, f.Origin, u, v));
                }
                else
                {
                    return null;
                }

                return GizmoMath.RotateAbout(start, GizmoMath.UeDirection(ring), turn);
            case GizmoKind.Scale:
                float ratio;
                if (handle == GizmoAxis.ScaleUniform)
                {
                    ratio = 1f + (((to.X - from.X) - (to.Y - from.Y)) / 150f); // right or up grows (150 px doubles)
                }
                else if (GizmoMath.TryClosestParameter(o0, d0, f.Origin, f.Axis(GizmoMath.AxisOf(handle)), out var s0, out _)
                         && GizmoMath.TryClosestParameter(o1, d1, f.Origin, f.Axis(GizmoMath.AxisOf(handle)), out var s1, out _) && s0 > 1e-3f)
                {
                    ratio = s1 / s0;
                }
                else
                {
                    return null;
                }

                return GizmoMath.Scale(start, handle, ratio, 0f);
            default:
                return null;
        }
    }

    /// <summary>The angle (degrees, clockwise on screen) of <paramref name="point"/> around <paramref name="centre"/>.</summary>
    private static float ScreenAngle(Vector2 point, Vector2 centre) => MathF.Atan2(point.Y - centre.Y, point.X - centre.X) * 180f / MathF.PI;

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

        if (Tool == PreviewTool.Surface && SurfaceInput is { } surface)
        {
            var args = new PreviewSurfaceInputEventArgs(Key.None, notches, e.KeyModifiers);
            surface(this, args);
            if (args.Handled)
            {
                RequestNextFrameRendering();
                return; // a decal took it: the camera stays
            }
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
        CancelDrags();
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
        _scene.ShowScale = FigureWanted;
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
