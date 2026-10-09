using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using ScumStudio.App.Services;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>
/// The 3D level viewport: hosts <see cref="SceneRenderer"/> in Avalonia's OpenGL control, uploads a
/// <see cref="PreparedLevelScene"/> when <see cref="Scene"/> changes, flies the camera (right-drag look, WASD/QE move,
/// Shift fast, wheel dolly), picks actors on left click (<see cref="SelectedId"/>), highlights the selection and hides
/// the actors in <see cref="HiddenIds"/> (deleted in the project). All GL work happens in <see cref="OnOpenGlRender"/>.
/// </summary>
public sealed partial class LevelViewport : OpenGlControlBase
{
    /// <summary>
    /// Whole-island backdrop (terrain only, drawn behind <see cref="Scene"/>); the tiles <see cref="Scene"/> brings in
    /// full detail are hidden in it.
    /// </summary>
    public static readonly StyledProperty<PreparedLevelScene?> BackdropProperty =
        AvaloniaProperty.Register<LevelViewport, PreparedLevelScene?>(nameof(Backdrop));

    /// <summary>Keep the camera where it is when a new <see cref="Scene"/> arrives (world mode streams cells under it).</summary>
    public static readonly StyledProperty<bool> KeepCameraProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(KeepCamera));

    /// <summary>The prepared scene to show (null clears the viewport).</summary>
    public static readonly StyledProperty<PreparedLevelScene?> SceneProperty =
        AvaloniaProperty.Register<LevelViewport, PreparedLevelScene?>(nameof(Scene));

    /// <summary>Selectable id of the selected actor (0 = none). Two-way: picking sets it, setting it highlights.</summary>
    public static readonly StyledProperty<uint> SelectedIdProperty =
        AvaloniaProperty.Register<LevelViewport, uint>(nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Selectable ids that must not be drawn (deleted actors).</summary>
    public static readonly StyledProperty<IReadOnlySet<uint>?> HiddenIdsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlySet<uint>?>(nameof(HiddenIds));

    /// <summary>ISM/HISM instance placements that must not be drawn (deleted instances of actors that are kept).</summary>
    public static readonly StyledProperty<IReadOnlySet<InstanceKey>?> HiddenInstancesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlySet<InstanceKey>?>(nameof(HiddenInstances));

    /// <summary>Spawn pin kinds that must not be drawn (switched off in the legend): only the pins, not what they stand in.</summary>
    public static readonly StyledProperty<IReadOnlySet<SpawnKind>?> HiddenPinKindsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlySet<SpawnKind>?>(nameof(HiddenPinKinds));

    /// <summary>
    /// The single ISM/HISM/foliage instance selected (one tree, rock or plank) — its <see cref="InstanceKey.SelectableId"/>
    /// equals <see cref="SelectedId"/> — or null when a whole actor is selected. Two-way: picking an instance sets it.
    /// </summary>
    public static readonly StyledProperty<InstanceKey?> SelectedInstanceProperty =
        AvaloniaProperty.Register<LevelViewport, InstanceKey?>(nameof(SelectedInstance), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>World transforms (UE space) of single instances the project moved.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<InstanceKey, FTransform>?> InstanceTransformsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<InstanceKey, FTransform>?>(nameof(InstanceTransforms));

    /// <summary>
    /// The spawn point pins of actors whose point arrays the project changed, by selectable id (see
    /// <see cref="LevelScene.ReplacePins"/>); an actor not listed gets the pins the level stores back.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyDictionary<uint, IReadOnlyList<ScenePlacement>>?> PinOverridesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<uint, IReadOnlyList<ScenePlacement>>?>(nameof(PinOverrides));

    /// <summary>Root world transforms (UE space) of actors the project moved, by selectable id.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<uint, FTransform>?> ActorTransformsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<uint, FTransform>?>(nameof(ActorTransforms));

    /// <summary>Actors the project added as copies of loaded actors, drawn as clones of the source placements.</summary>
    public static readonly StyledProperty<IReadOnlyList<ActorClone>?> ClonesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<ActorClone>?>(nameof(Clones));

    /// <summary>Road, rail and bridge pieces drawn with a new curve (see <see cref="LevelScene.SetSegmentBend"/>).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<InstanceKey, Level.Model.SplineMeshParams>?> SegmentBendsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<InstanceKey, Level.Model.SplineMeshParams>?>(nameof(SegmentBends));

    /// <summary>Actors drawn with another mesh in place of their own (the Replace tool; see <see cref="LevelScene.SetActorMesh"/>).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<uint, string>?> ReplacedMeshesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<uint, string>?>(nameof(ReplacedMeshes));

    /// <summary>Road pieces and building parts drawn with another mesh (see <see cref="LevelScene.SetSegmentBend"/> and <see cref="LevelScene.SetPartMesh"/>).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<InstanceKey, string>?> PartMeshesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<InstanceKey, string>?>(nameof(PartMeshes));

    /// <summary>The selected piece's curve with its two handles (Shape): drawn over the view and dragged sideways.</summary>
    public static readonly StyledProperty<ShapeHandleInfo?> ShapeHandlesProperty =
        AvaloniaProperty.Register<LevelViewport, ShapeHandleInfo?>(nameof(ShapeHandles));

    /// <summary>The multi-selection where it stands: dragging one member previews them all moving together.</summary>
    public static readonly StyledProperty<IReadOnlyList<GroupWorld>?> GroupWorldsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<GroupWorld>?>(nameof(GroupWorlds));

    /// <summary>Actors drawn bent (wall, bridge, road piece), by id: the curve in component space (see <see cref="LevelScene.SetBend"/>).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<uint, IReadOnlyList<Level.Model.SplineMeshParams>>?> BendsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyDictionary<uint, IReadOnlyList<Level.Model.SplineMeshParams>>?>(nameof(Bends));

    /// <summary>Meshes loaded after the scene was prepared (new mesh actors); uploaded on the render thread.</summary>
    public static readonly StyledProperty<IReadOnlyList<ExtraMesh>?> ExtraMeshesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<ExtraMesh>?>(nameof(ExtraMeshes));

    /// <summary>Current root world transform (UE space) of the selected actor: where the translation gizmo is drawn; null hides it.</summary>
    public static readonly StyledProperty<FTransform?> SelectedRootWorldProperty =
        AvaloniaProperty.Register<LevelViewport, FTransform?>(nameof(SelectedRootWorld));

    /// <summary>Snap step in cm for gizmo drags (0 = free).</summary>
    public static readonly StyledProperty<float> TranslationSnapProperty =
        AvaloniaProperty.Register<LevelViewport, float>(nameof(TranslationSnap));

    /// <summary>Snap step in degrees for yaw-ring drags (0 = free).</summary>
    public static readonly StyledProperty<float> RotationSnapProperty =
        AvaloniaProperty.Register<LevelViewport, float>(nameof(RotationSnap));

    /// <summary>Actors highlighted together with the selection ("select all of this kind").</summary>
    public static readonly StyledProperty<IReadOnlyCollection<uint>?> KindIdsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyCollection<uint>?>(nameof(KindIds));

    /// <summary>Instances highlighted together with the selection ("select all of this kind").</summary>
    public static readonly StyledProperty<IReadOnlyCollection<InstanceKey>?> KindInstancesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyCollection<InstanceKey>?>(nameof(KindInstances));

    /// <summary>3D quality preset: object draw distance, LOD detail and the smallest object drawn (see <see cref="RenderQualityProfile"/>).</summary>
    public static readonly StyledProperty<RenderQuality> QualityProperty =
        AvaloniaProperty.Register<LevelViewport, RenderQuality>(nameof(Quality), RenderQuality.Balanced);

    /// <summary>Auto-snap: a dragged object jumps flush against (or in line with) a neighbour that comes within 30 cm.</summary>
    public static readonly StyledProperty<bool> AutoSnapProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(AutoSnap), defaultValue: true);

    /// <summary>
    /// The gizmo's arrows follow the object's own axes (its front, side and up) instead of the world's (Discord salvador:
    /// "the gizmo is rotated, it doesn't align with the object's orientation"). The turn ring stays level.
    /// </summary>
    public static readonly StyledProperty<GizmoOrientation> OrientationProperty =
        AvaloniaProperty.Register<LevelViewport, GizmoOrientation>(nameof(Orientation), defaultValue: GizmoOrientation.Local);

    /// <summary>Part mode: a click picks the one part of a Blueprint under the cursor (a hangar's wall or lamp), not the whole Blueprint. Alt+click does it once.</summary>
    /// <summary>Until when (UTC) a click keeps the current selection instead of picking what is under the cursor (a fresh duplicate).</summary>
    public static readonly StyledProperty<DateTime> SelectionHoldUntilProperty =
        AvaloniaProperty.Register<LevelViewport, DateTime>(nameof(SelectionHoldUntil));

    /// <inheritdoc cref="SelectionHoldUntilProperty" />
    public DateTime SelectionHoldUntil
    {
        get => GetValue(SelectionHoldUntilProperty);
        set => SetValue(SelectionHoldUntilProperty, value);
    }

    public static readonly StyledProperty<bool> PickPartsProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(PickParts));

    /// <summary>Brush mode: a circle follows the cursor and holding the left button raises <see cref="BrushPainted"/> where it is.</summary>
    public static readonly StyledProperty<bool> BrushModeProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(BrushMode));

    /// <summary>Radius of the brush circle in metres.</summary>
    public static readonly StyledProperty<double> BrushRadiusProperty =
        AvaloniaProperty.Register<LevelViewport, double>(nameof(BrushRadius), 10d);

    /// <summary>One-line statistics of the last frame (read-only).</summary>
    public static readonly StyledProperty<string> FrameInfoProperty =
        AvaloniaProperty.Register<LevelViewport, string>(nameof(FrameInfo), string.Empty);

    /// <summary>True when the OpenGL context could be created.</summary>
    public static readonly StyledProperty<bool> IsGlReadyProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(IsGlReady));

    /// <summary>True while drone mode is on: the mouse is captured and looks around without a button (Tab toggles, Esc leaves).</summary>
    public static readonly StyledProperty<bool> IsDroneModeProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(IsDroneMode));

    /// <summary>The graphics card OpenGL runs on (e.g. "NVIDIA GeForce RTX 5080/PCIe/SSE2"), read-only.</summary>
    public static readonly StyledProperty<string> GpuNameProperty =
        AvaloniaProperty.Register<LevelViewport, string>(nameof(GpuName), string.Empty);

    /// <summary>Flying speed as text ("12 m/s"), read-only.</summary>
    public static readonly StyledProperty<string> SpeedTextProperty =
        AvaloniaProperty.Register<LevelViewport, string>(nameof(SpeedText), string.Empty);

    private readonly FlyCamera _camera = new() { Position = new Vector3(0f, 500f, 1500f), Pitch = -20f, Yaw = -90f };
    private readonly Stopwatch _clock = new();
    private ProcAddressGlContext? _context;
    private SceneRenderer? _renderer;
    private RenderTarget? _target;
    private LevelScene? _level;
    private PreparedLevelScene? _pendingScene;
    private PreparedLevelScene? _stagedScene; // on its way to the GPU (see GpuMeshCache.Stage)
    private bool _sceneDirty;
    private bool _backdropDirty;

    // Milliseconds a frame may spend sending a new scene's meshes and textures: little while the camera moves, more at rest.
    private const double StageBudgetMovingMs = 4.0;
    private const double StageBudgetRestingMs = 30.0;
    private LevelScene? _backdrop;
    private object? _backdropHiddenFor;
    private static readonly object EmptyMarker = new();
    private bool _selectionDirty;
    private int _litNodes = -1;
    private bool _visibilityDirty;
    private bool _transformsDirty;
    private bool _clonesDirty;
    private bool _bendsDirty;
    private bool _pinsDirty;
    private readonly Dictionary<uint, ActorClone> _appliedClones = [];
    private readonly HashSet<uint> _appliedPins = [];
    private bool _dragging;
    private GizmoAxis _dragAxis;
    private GizmoAxis _hoverAxis;
    private FTransform _dragStartRoot;
    private float _dragStartParameter;
    private DragPreview? _dragPreview;
    private bool _previewApplied;
    private (int X, int Y)? _pendingPick;
    private (int X, int Y, bool Paint)? _pendingBrush;
    private Vector3? _brushPointGl;
    private Vector3? _brushFromGl;
    private float? _brushGroundY;
    private bool _brushing;
    private bool _brushEndPending;
    private Point _lastPointer;
    private Point _pressPointer;
    private bool _looking;
    private bool _panning;
    private double _lastFrameSeconds;
    private bool _forward, _backward, _left, _right, _up, _down, _fast;
    private Vector3 _velocity;
    private bool _drone;
    private bool _cursorLocked;
    private PixelPoint _cursorRestore;
    private bool _leftDown;
    private bool _grabPending;
    private uint _grabId;
    private InstanceKey? _grabInstance;
    private Vector3 _grabPointGl;
    private bool _freeDragging;
    private Vector3 _freeMove;
    private float _freeLift;
    private float _freeYaw;
    private float _shownLift;
    private float _shownYaw;
    private bool _snapHeld;
    private bool _pickWhole;
    private bool _pickPart;
    private bool _pickToggle;
    private PreparedLevelScene? _framedBackdrop;
    // Meshes stay on the GPU between scene swaps: the levels around a moving camera share most of them.
    private readonly GpuMeshCache _gpuMeshes = new();
    private BoundingBox _snapMoving;
    private List<BoundingBox>? _snapOthers;
    private SnapPiece? _snapPiece;
    private List<SnapPiece> _snapPieces = [];
    private SnapPiece? _snappedTo;
    private bool _altHeld;

    // Joints made by hand this session, per (moving mesh, target mesh): offered again for the same pair.
    private readonly Dictionary<(string Moving, string Target), List<FTransform>> _learnedJoints = new();
    private string _lastInfo = string.Empty;
    private double _lastInfoSeconds;
    private double _frameMs;
    private bool _wasContinuous;
    private TopLevel? _topLevel;
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);

    static LevelViewport()
    {
        FocusableProperty.OverrideDefaultValue<LevelViewport>(true);
        SceneProperty.Changed.AddClassHandler<LevelViewport>((c, e) => c.OnSceneChanged(e.NewValue as PreparedLevelScene));
        BackdropProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._backdropDirty));
        SelectedIdProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._selectionDirty));
        SelectedInstanceProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._selectionDirty));
        KindIdsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._selectionDirty));
        KindInstancesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._selectionDirty));
        InstanceTransformsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._transformsDirty));
        PinOverridesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._pinsDirty));
        HiddenIdsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._visibilityDirty));
        HiddenInstancesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._visibilityDirty));
        HiddenPinKindsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._visibilityDirty));
        ActorTransformsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._transformsDirty));
        ClonesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._clonesDirty));
        BendsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._bendsDirty));
        SegmentBendsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._bendsDirty));
        ReplacedMeshesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._bendsDirty));
        PartMeshesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._bendsDirty));
        ShapeHandlesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        ScaleHandlesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        LegHandleProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        SelectedRootWorldProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        CanScaleProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        ExtraMeshesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._clonesDirty));
    }

    /// <summary>Raised on the UI thread when a gizmo drag ends: the actor's root should move to <see cref="TransformDragEventArgs.RootWorld"/>.</summary>
    public event EventHandler<TransformDragEventArgs>? TransformDragged;

    /// <summary>Raised on the UI thread when an actor was picked (0 when the click hit nothing).</summary>
    public event EventHandler<uint>? ActorPicked;

    /// <inheritdoc cref="SceneProperty" />
    public PreparedLevelScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <inheritdoc cref="SelectedIdProperty" />
    public uint SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <inheritdoc cref="HiddenIdsProperty" />
    public IReadOnlySet<uint>? HiddenIds
    {
        get => GetValue(HiddenIdsProperty);
        set => SetValue(HiddenIdsProperty, value);
    }

    /// <inheritdoc cref="HiddenInstancesProperty" />
    public IReadOnlySet<InstanceKey>? HiddenInstances
    {
        get => GetValue(HiddenInstancesProperty);
        set => SetValue(HiddenInstancesProperty, value);
    }

    /// <inheritdoc cref="HiddenPinKindsProperty" />
    public IReadOnlySet<SpawnKind>? HiddenPinKinds
    {
        get => GetValue(HiddenPinKindsProperty);
        set => SetValue(HiddenPinKindsProperty, value);
    }

    /// <inheritdoc cref="BackdropProperty" />
    public PreparedLevelScene? Backdrop
    {
        get => GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <inheritdoc cref="KeepCameraProperty" />
    public bool KeepCamera
    {
        get => GetValue(KeepCameraProperty);
        set => SetValue(KeepCameraProperty, value);
    }

    /// <summary>Camera position in UE world space (cm).</summary>
    public FVector CameraUe => UeToGl.ToUePoint(_camera.Position);

    /// <summary>
    /// A view (UE cm, camera yaw/pitch) used instead of framing the island when it first shows, then cleared: the map
    /// reopens where the user left it.
    /// </summary>
    public (FVector Location, float Yaw, float Pitch)? InitialView { get; set; }

    private bool TakeInitialView()
    {
        if (InitialView is not { } view)
        {
            return false;
        }

        InitialView = null;
        _camera.Position = UeToGl.Point(view.Location);
        _camera.Yaw = view.Yaw;
        _camera.Pitch = view.Pitch;
        return true;
    }

    /// <inheritdoc cref="SelectedInstanceProperty" />
    public InstanceKey? SelectedInstance
    {
        get => GetValue(SelectedInstanceProperty);
        set => SetValue(SelectedInstanceProperty, value);
    }

    /// <inheritdoc cref="InstanceTransformsProperty" />
    public IReadOnlyDictionary<InstanceKey, FTransform>? InstanceTransforms
    {
        get => GetValue(InstanceTransformsProperty);
        set => SetValue(InstanceTransformsProperty, value);
    }

    /// <inheritdoc cref="PinOverridesProperty" />
    public IReadOnlyDictionary<uint, IReadOnlyList<ScenePlacement>>? PinOverrides
    {
        get => GetValue(PinOverridesProperty);
        set => SetValue(PinOverridesProperty, value);
    }

    /// <inheritdoc cref="ActorTransformsProperty" />
    public IReadOnlyDictionary<uint, FTransform>? ActorTransforms
    {
        get => GetValue(ActorTransformsProperty);
        set => SetValue(ActorTransformsProperty, value);
    }

    /// <inheritdoc cref="ClonesProperty" />
    public IReadOnlyList<ActorClone>? Clones
    {
        get => GetValue(ClonesProperty);
        set => SetValue(ClonesProperty, value);
    }

    /// <inheritdoc cref="SegmentBendsProperty" />
    public IReadOnlyDictionary<InstanceKey, Level.Model.SplineMeshParams>? SegmentBends
    {
        get => GetValue(SegmentBendsProperty);
        set => SetValue(SegmentBendsProperty, value);
    }

    /// <inheritdoc cref="ReplacedMeshesProperty" />
    public IReadOnlyDictionary<uint, string>? ReplacedMeshes
    {
        get => GetValue(ReplacedMeshesProperty);
        set => SetValue(ReplacedMeshesProperty, value);
    }

    /// <inheritdoc cref="PartMeshesProperty" />
    public IReadOnlyDictionary<InstanceKey, string>? PartMeshes
    {
        get => GetValue(PartMeshesProperty);
        set => SetValue(PartMeshesProperty, value);
    }

    /// <inheritdoc cref="ShapeHandlesProperty" />
    public ShapeHandleInfo? ShapeHandles
    {
        get => GetValue(ShapeHandlesProperty);
        set => SetValue(ShapeHandlesProperty, value);
    }

    /// <summary>Raised while a shape handle is dragged: which handle (0 or 1) and its sideways push in cm.</summary>
    public event EventHandler<(int Index, float Sway)>? ShapeHandleDragged;

    /// <summary>Raised when a shape handle is let go.</summary>
    public event EventHandler? ShapeHandleReleased;

    /// <inheritdoc cref="GroupWorldsProperty" />
    public IReadOnlyList<GroupWorld>? GroupWorlds
    {
        get => GetValue(GroupWorldsProperty);
        set => SetValue(GroupWorldsProperty, value);
    }

    /// <inheritdoc cref="BendsProperty" />
    public IReadOnlyDictionary<uint, IReadOnlyList<Level.Model.SplineMeshParams>>? Bends
    {
        get => GetValue(BendsProperty);
        set => SetValue(BendsProperty, value);
    }

    /// <inheritdoc cref="ExtraMeshesProperty" />
    public IReadOnlyList<ExtraMesh>? ExtraMeshes
    {
        get => GetValue(ExtraMeshesProperty);
        set => SetValue(ExtraMeshesProperty, value);
    }

    /// <summary>
    /// The point <paramref name="distance"/> cm in front of the camera, in UE world space: where a new actor is placed.
    /// </summary>
    public FVector AimPointUe(float distance) => UeToGl.ToUePoint(_camera.Position + _camera.Forward * distance);

    /// <inheritdoc cref="SelectedRootWorldProperty" />
    public FTransform? SelectedRootWorld
    {
        get => GetValue(SelectedRootWorldProperty);
        set => SetValue(SelectedRootWorldProperty, value);
    }

    /// <inheritdoc cref="TranslationSnapProperty" />
    public float TranslationSnap
    {
        get => GetValue(TranslationSnapProperty);
        set => SetValue(TranslationSnapProperty, value);
    }

    /// <inheritdoc cref="RotationSnapProperty" />
    public float RotationSnap
    {
        get => GetValue(RotationSnapProperty);
        set => SetValue(RotationSnapProperty, value);
    }

    /// <summary>Clicks select nothing farther than this from the camera (cm; 100 m).</summary>
    public float MaxPickDistance { get; set; } = 10_000f;

    /// <summary>Farthest pick for the biggest objects (bridges, big buildings), cm: reach grows with an object's size up to this.</summary>
    public float MaxPickDistanceLarge { get; set; } = 100_000f;

    /// <summary>
    /// Raised on the UI thread for a Ctrl+click: the actor (and instance) under the cursor to add to or take out of the
    /// multi-selection; the single selection does not change.
    /// </summary>
    public event EventHandler<(uint Id, InstanceKey? Instance)>? PickToggled;

    /// <inheritdoc cref="KindIdsProperty" />
    public IReadOnlyCollection<uint>? KindIds
    {
        get => GetValue(KindIdsProperty);
        set => SetValue(KindIdsProperty, value);
    }

    /// <inheritdoc cref="KindInstancesProperty" />
    public IReadOnlyCollection<InstanceKey>? KindInstances
    {
        get => GetValue(KindInstancesProperty);
        set => SetValue(KindInstancesProperty, value);
    }

    /// <inheritdoc cref="QualityProperty" />
    public RenderQuality Quality
    {
        get => GetValue(QualityProperty);
        set => SetValue(QualityProperty, value);
    }

    /// <inheritdoc cref="AutoSnapProperty" />
    public bool AutoSnap
    {
        get => GetValue(AutoSnapProperty);
        set => SetValue(AutoSnapProperty, value);
    }

    /// <inheritdoc cref="OrientationProperty" />
    public GizmoOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>True for <see cref="GizmoOrientation.Local"/>; setting it picks Local or Global.</summary>
    public bool LocalAxes
    {
        get => Orientation == GizmoOrientation.Local;
        set => Orientation = value ? GizmoOrientation.Local : GizmoOrientation.Global;
    }

    /// <summary>The UE-space direction of a gizmo arrow in the current <see cref="Orientation"/>.</summary>
    private FVector AxisUe(GizmoAxis axis, FTransform root)
    {
        if (axis is not (GizmoAxis.X or GizmoAxis.Y or GizmoAxis.Z))
        {
            return GizmoMath.UeDirection(axis);
        }

        return Orientation switch
        {
            GizmoOrientation.Local => root.Rotation.RotateVector(GizmoMath.UeDirection(axis)),
            GizmoOrientation.View => ViewAxisUe(axis),
            _ => GizmoMath.UeDirection(axis),
        };
    }

    /// <summary>The view's axis: X the screen's right, Y its up, Z towards the camera (Blender's View orientation).</summary>
    private FVector ViewAxisUe(GizmoAxis axis)
    {
        var forward = Vector3.Normalize(_camera.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right); // looking straight down or up
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return UeToGl.ToUeDirection(axis switch
        {
            GizmoAxis.X => right,
            GizmoAxis.Y => up,
            _ => -forward,
        });
    }

    private Vector3 AxisGl(GizmoAxis axis, FTransform root) => UeToGl.Direction(AxisUe(axis, root));

    /// <inheritdoc cref="PickPartsProperty" />
    public bool PickParts
    {
        get => GetValue(PickPartsProperty);
        set => SetValue(PickPartsProperty, value);
    }

    /// <inheritdoc cref="BrushModeProperty" />
    public bool BrushMode
    {
        get => GetValue(BrushModeProperty);
        set => SetValue(BrushModeProperty, value);
    }

    /// <inheritdoc cref="BrushRadiusProperty" />
    public double BrushRadius
    {
        get => GetValue(BrushRadiusProperty);
        set => SetValue(BrushRadiusProperty, value);
    }

    /// <summary>
    /// Raised on the UI thread while the brush paints: the circle's centre now and where it was at the stroke's previous
    /// dab (UE world), so a fast sweep covers the whole way between them. The first dab of a stroke has From = To.
    /// </summary>
    public event EventHandler<(FVector From, FVector To)>? BrushPainted;

    /// <summary>Raised on the UI thread when the painting button is let go, after the stroke's last <see cref="BrushPainted"/>.</summary>
    public event EventHandler? BrushStrokeEnded;

    /// <summary>How close (cm) a neighbour's face must come for auto-snap to take it.</summary>
    public float SnapReach { get; set; } = 30f;

    /// <summary>The handle being dragged (or hovered), for tests and overlays.</summary>
    public GizmoAxis ActiveAxis => _dragging ? _dragAxis : _hoverAxis;

    /// <inheritdoc cref="FrameInfoProperty" />
    public string FrameInfo
    {
        get => GetValue(FrameInfoProperty);
        private set => SetValue(FrameInfoProperty, value);
    }

    /// <inheritdoc cref="IsGlReadyProperty" />
    public bool IsGlReady
    {
        get => GetValue(IsGlReadyProperty);
        private set => SetValue(IsGlReadyProperty, value);
    }

    /// <inheritdoc cref="IsDroneModeProperty" />
    public bool IsDroneMode
    {
        get => GetValue(IsDroneModeProperty);
        private set => SetValue(IsDroneModeProperty, value);
    }

    /// <inheritdoc cref="GpuNameProperty" />
    public string GpuName
    {
        get => GetValue(GpuNameProperty);
        private set => SetValue(GpuNameProperty, value);
    }

    /// <inheritdoc cref="SpeedTextProperty" />
    public string SpeedText
    {
        get => GetValue(SpeedTextProperty);
        private set => SetValue(SpeedTextProperty, value);
    }

    /// <summary>The camera (position/yaw/pitch in the renderer's GL space).</summary>
    public FlyCamera Camera => _camera;

    /// <summary>Turns drone mode on or off (Tab in the viewport does the same).</summary>
    public void ToggleDrone() => SetDrone(!_drone);

    /// <summary>True once a scene has been uploaded and drawn at least once.</summary>
    public bool HasRenderedScene { get; private set; }

    private PreparedLevelScene? _uploadedScene;
    private string? _pendingScreenshot;
    private TaskCompletionSource<string>? _screenshotResult;

    /// <summary>
    /// Saves the next rendered frame (the viewport's own colour buffer, so it works under Xvfb and off-screen) as a PNG.
    /// The task completes with the path once written.
    /// </summary>
    public Task<string> SaveScreenshotAsync(string path)
    {
        _screenshotResult?.TrySetCanceled();
        _screenshotResult = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingScreenshot = path;
        RequestNextFrameRendering();
        return _screenshotResult.Task;
    }

    /// <summary>True once <paramref name="scene"/> is uploaded and drawn (not merely assigned to <see cref="Scene"/>).</summary>
    public bool IsShowing(PreparedLevelScene? scene) => scene is not null && HasRenderedScene && ReferenceEquals(_uploadedScene, scene);

    /// <summary>
    /// Places the camera (UE space, centimetres): at <paramref name="location"/> when given, looking at
    /// <paramref name="lookAt"/> or along <paramref name="yaw"/>/<paramref name="pitch"/> (degrees; yaw 0 = +X, 90 = +Y;
    /// negative pitch looks down). With only a look-at point the camera turns towards it from where it is.
    /// </summary>
    public void SetView(FVector? location, FVector? lookAt, float? yaw, float? pitch)
    {
        if (location is { } at)
        {
            _camera.Position = UeToGl.Point(at);
        }

        if (lookAt is { } target)
        {
            var direction = UeToGl.Point(target) - _camera.Position;
            if (direction.LengthSquared() > 1e-6f)
            {
                direction = Vector3.Normalize(direction);
                _camera.Yaw = MathF.Atan2(direction.Z, direction.X) * UeMath.RadiansToDegrees;
                _camera.Pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) * UeMath.RadiansToDegrees;
            }
        }
        else
        {
            if (yaw is { } y)
            {
                _camera.Yaw = y;
            }

            if (pitch is { } p)
            {
                _camera.Pitch = p;
            }
        }

        var radius = SceneRadius();
        var center = _level is { } level && !level.Bounds.IsEmpty ? level.Bounds.Center : _camera.Position;
        _camera.FitClipRange(MathF.Max(Vector3.Distance(_camera.Position, center), 1f), radius);
        RequestNextFrameRendering();
    }

    /// <summary>Re-frames the camera on the whole scene.</summary>
    public void FrameAll()
    {
        if (_level is { } level && !ViewBounds(level).IsEmpty)
        {
            FrameBounds(ViewBounds(level));
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Frames the camera on the selected actor (its placements' bounds, or a 4 m box around its root when it draws no
    /// mesh, e.g. a mesh missing from the game files). Returns false when nothing is selected or loaded.
    /// </summary>
    public bool FrameSelection()
    {
        if (_level is not { } level || SelectedId == 0)
        {
            return false;
        }

        var bounds = SelectionBounds(level);
        if (bounds.IsEmpty && SelectedRootWorld is { } root)
        {
            var centre = UeToGl.Point(root.Translation);
            bounds = new BoundingBox(centre - new Vector3(200f), centre + new Vector3(200f));
        }

        if (bounds.IsEmpty)
        {
            return false;
        }

        // Keep the clip range the view had: framing one object must not cut the rest of the world away (it went dark).
        var (near, far) = (_camera.NearPlane, _camera.FarPlane);
        FrameBounds(bounds, pitch: -25f);
        _camera.SetClipRange(MathF.Min(near, _camera.NearPlane), MathF.Max(far, _camera.FarPlane));
        RequestNextFrameRendering();
        return true;
    }

    /// <summary>
    /// The selection's box (GL space): one tree, rock or road piece is just that piece, not the whole foliage or landscape
    /// actor holding it.
    /// </summary>
    private BoundingBox SelectionBounds(LevelScene level)
    {
        var instance = SelectedInstance is { } key && key.SelectableId == SelectedId ? key : (InstanceKey?)null;
        var bounds = BoundingBox.Empty;
        foreach (var node in level.Scene.Nodes)
        {
            if (node.SelectableId == SelectedId && node.Mesh is { } mesh
                && (instance is null || node.Tag is ScenePlacement { InstanceKey: { } placed } && placed == instance))
            {
                bounds = bounds.Union(LevelSceneUploader.TransformBounds(mesh.Bounds, node.WorldTransform));
            }
        }

        return bounds;
    }

    // The gizmo stands on what the user sees selected, not at its root (owner: "the circle shows at the end of the bridge;
    // it should be in the middle of the object"; a trader's globe stood 13 m away among the car shop's vehicles): its
    // middle in the root's own space, so it moves and turns with it. Placed again when the selection, its place or the
    // scene changes (meshes arrive after a selection), never during a drag; a long piece's slides with the camera.
    private object? _pivotFor;
    private FVector _pivotLocal;
    private (Vector3 Eye, Vector3 Look)? _pivotView;

    /// <summary>For tests without a GL scene: the drawn parts of the selection (mesh box, model matrix, mesh path) instead of the scene's.</summary>
    internal IReadOnlyList<(BoundingBox Box, Matrix4x4 World, string? Mesh)>? PivotPartsForTests { get; set; }

    /// <summary>Where the gizmo stands (GL), for tests; null without a selection.</summary>
    internal Vector3? GizmoOrigin => SelectedRootWorld is { } root ? Frame(root).Origin : null;

    /// <summary>True when the selection is a member of a multi-selection: the gizmo stands in the middle of them all (no scale cubes: one would grow, the rest not).</summary>
    private bool InGroup() => GroupWorlds is { Count: > 1 } group && group.Any(m => m.Id == SelectedId && m.Instance == SelectedInstance);

    /// <summary>Where the gizmo of the selection is (UE world) when its root is at <paramref name="root"/>.</summary>
    private FVector PivotOf(FTransform root)
    {
        if (!_dragging && !_freeDragging)
        {
            var key = (SelectedId, SelectedInstance, SelectedRootWorld, Bends, GroupWorlds, _level, _level?.Scene.Version, PivotPartsForTests);
            var moved = _pivotView is { } view
                && (Vector3.Distance(view.Eye, _camera.Position) > MathF.Max(500f, 0.2f * Vector3.Distance(view.Eye, UeToGl.Point(root.TransformPosition(_pivotLocal))))
                    || Vector3.Dot(view.Look, _camera.Forward) < 0.97f);
            if (moved || !key.Equals(_pivotFor))
            {
                _pivotFor = key;
                PlacePivot();
            }
        }

        return root.TransformPosition(_pivotLocal);
    }

    /// <summary>Puts the gizmo on the middle of the drawn selection (<see cref="GizmoMath.PivotParts"/>), a long piece's where the camera looks (<see cref="GizmoMath.PivotLocal"/>).</summary>
    private void PlacePivot()
    {
        _pivotLocal = FVector.Zero;
        _pivotView = null;
        if (SelectedRootWorld is not { } start || MathF.Abs(start.Scale3D.X * start.Scale3D.Y * start.Scale3D.Z) < 1e-9f)
        {
            return;
        }

        var instance = SelectedInstance is { } selected && selected.SelectableId == SelectedId ? selected : (InstanceKey?)null;
        var members = InGroup() ? GroupWorlds!.Select(m => (m.Id, m.Instance)).ToList() : [(SelectedId, instance)];
        var parts = PivotPartsForTests ?? (_level is { } level ? GizmoMath.PivotParts(level.Scene.Nodes, members) : []);

        // One piece can be long (a road, bridge, fence or wall); a building of many meshes or a multi-selection stands in its middle.
        var (pivot, slides) = GizmoMath.PivotLocal(GizmoMath.LocalBox(parts, start), start, parts.Count == 1 ? parts[0].Mesh : null, _camera.Position, _camera.Forward);
        _pivotLocal = pivot;
        _pivotView = slides ? (_camera.Position, _camera.Forward) : null;
    }

    /// <summary><paramref name="moved"/> (turned about its root) turned about the middle instead, which keeps its move.</summary>
    private FTransform AboutPivot(FTransform start, FTransform moved)
    {
        var pivot = start.TransformPosition(_pivotLocal) + (moved.Translation - start.Translation);
        return moved with { Translation = pivot - moved.Rotation.RotateVector(moved.Scale3D * _pivotLocal) };
    }

    /// <summary>The camera's place and direction, to come back to (see <see cref="RestoreView"/>).</summary>
    public (Vector3 Position, float Yaw, float Pitch) SaveView() => (_camera.Position, _camera.Yaw, _camera.Pitch);

    /// <summary>Puts the camera back where <see cref="SaveView"/> found it.</summary>
    public void RestoreView((Vector3 Position, float Yaw, float Pitch) view)
    {
        (_camera.Position, _camera.Yaw, _camera.Pitch) = view;
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _context = new ProcAddressGlContext(gl.GetProcAddress, PixelSize);
            _renderer = new SceneRenderer(_context);
            _renderer.Settings = _renderer.Settings with { ShowGrid = true };
            GpuName = _renderer.Info.Renderer;
            _target = _renderer.CreateTarget(Math.Max(1, PixelSize().W), Math.Max(1, PixelSize().H));
            _sceneDirty = _pendingScene is not null;
            _backdropDirty = Backdrop is not null;
            RequestNextFrameRendering(); // requests made before the context existed were dropped
            _clock.Restart();
            _lastFrameSeconds = 0;
            IsGlReady = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FrameInfo = "OpenGL 4.3 is not available: " + ex.Message;
            IsGlReady = false;
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _backdrop?.Dispose();
        _backdrop = null;
        _backdropDirty = Backdrop is not null;
        _level?.Dispose();
        _level = null;
        _gpuMeshes.Clear(); // the context and every mesh in it go away with the renderer
        _stagedScene = null;
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

        _target.Resize(w, h);
        var quality = RenderQualityProfile.For(Quality);
        var shadows = Quality != RenderQuality.Performance; // sun shadows cost a second geometry pass near the camera
        if (_renderer.Settings.ObjectDrawDistance != quality.ObjectDistanceCm || _renderer.Settings.LodBias != quality.LodBias
            || _renderer.Settings.CullPixelSize != quality.CullPixels || _renderer.Settings.Shadows != shadows)
        {
            _renderer.Settings = _renderer.Settings with
            {
                ObjectDrawDistance = quality.ObjectDistanceCm, LodBias = quality.LodBias, CullPixelSize = quality.CullPixels, Shadows = shadows,
            };
        }

        if (_sceneDirty)
        {
            _sceneDirty = false;
            _stagedScene = _level is not null && ReferenceEquals(_pendingScene, _uploadedScene) ? null : _pendingScene;
            if (_pendingScene is null)
            {
                _level?.Dispose();
                _level = null;
                _uploadedScene = null;
            }
        }

        if (_stagedScene is { } prepared)
        {
            // A new scene's meshes and textures go to the GPU a few milliseconds a frame (more while the camera rests) while
            // the current one stays on screen; then it is shown at once, keeping the nodes of the levels both scenes have
            // (owner: "it stutters while I fly": every streaming step re-uploaded and rebuilt the whole scene).
            // The new levels' nodes are built on a worker meanwhile (LevelScene.Prebuild), so the switch only hangs them in.
            if (_gpuMeshes.Stage(_renderer, prepared, _wasContinuous ? StageBudgetMovingMs : StageBudgetRestingMs) && (_level?.Prebuild(prepared) ?? true))
            {
                _stagedScene = null;
                // The same scene uploaded again (the page came back, the view docked back) keeps the camera where it was.
                var reupload = ReferenceEquals(prepared, _uploadedScene);
                if (_level is null)
                {
                    _level = LevelSceneUploader.Upload(_renderer, prepared, cache: _gpuMeshes);
                    _appliedClones.Clear();
                    _appliedPins.Clear();
                }
                else
                {
                    _level.Update(prepared); // clones and replaced pins of the levels that stay are still drawn
                }

                _gpuMeshes.Trim(_renderer);
                _uploadedScene = prepared;
                if (!KeepCamera && !reupload && !ViewBounds(_level).IsEmpty)
                {
                    FrameBounds(ViewBounds(_level), resetSpeed: true);
                }

                _selectionDirty = true;
                _visibilityDirty = true;
                _transformsDirty = true;
                _clonesDirty = true;
                _pinsDirty = true;
            }
            else
            {
                RequestNextFrameRendering();
            }
        }

        if (_backdropDirty)
        {
            _backdropDirty = false;
            _backdrop?.Dispose();
            _backdrop = Backdrop is { } island ? LevelSceneUploader.Upload(_renderer, island) : null;
            _backdropHiddenFor = null;
            var sameIsland = ReferenceEquals(Backdrop, _framedBackdrop);
            _framedBackdrop = Backdrop;
            if (_backdrop is { } b && !sameIsland && !b.TerrainBounds.IsEmpty && !TakeInitialView() && _level is null)
            {
                FrameBounds(b.TerrainBounds, yaw: -90f, pitch: -55f, resetSpeed: true);
            }

            if (_backdrop is not null)
            {
                _camera.SetClipRange(10f, 3_000_000f); // reverse-Z keeps precision from 10 cm to the far side of the island
            }
        }

        if (_backdrop is { } backdrop && !ReferenceEquals(_backdropHiddenFor, _uploadedScene ?? EmptyMarker))
        {
            // The tiles the detail scene brings at full resolution are hidden in the coarse backdrop.
            _backdropHiddenFor = _uploadedScene ?? EmptyMarker;
            backdrop.HideTerrainOf(_uploadedScene);
        }

        if (_level is { } level)
        {
            var preview = _dragPreview;
            if (preview is null && _previewApplied)
            {
                // The drag ended: fall back to the authoritative transforms (the view model's, applied below).
                _previewApplied = false;
                _transformsDirty = true;
                _clonesDirty = true;
                _appliedClones.Clear();
            }

            if (_pinsDirty)
            {
                // Spawn point pins follow the project's point lists: rebuilt for the actors with a changed list, put back
                // as the level stores them for the others; then selection, visibility and moves are applied to the new nodes.
                _pinsDirty = false;
                var wanted = PinOverrides ?? new Dictionary<uint, IReadOnlyList<ScenePlacement>>();
                foreach (var id in _appliedPins.Where(id => !wanted.ContainsKey(id)).ToList())
                {
                    level.ReplacePins(id, level.Prepared.Placements.Where(p => p.SelectableId == id && p.SpawnPoint is not null).ToList());
                    _appliedPins.Remove(id);
                }

                foreach (var (id, pins) in wanted)
                {
                    level.ReplacePins(id, pins);
                    _appliedPins.Add(id);
                }

                _selectionDirty = true;
                _visibilityDirty = true;
                _transformsDirty = true;
            }

            if (_clonesDirty)
            {
                _clonesDirty = false;
                _visibilityDirty = true;
                foreach (var extra in ExtraMeshes ?? [])
                {
                    if (!level.HasMesh(extra.Asset.MeshPath))
                    {
                        level.AddMesh(extra);
                        foreach (var waiting in _appliedClones.Where(c => string.Equals(c.Value.MeshPath, extra.Asset.MeshPath, StringComparison.OrdinalIgnoreCase)
                                     || c.Value.Placements?.Any(p => string.Equals(p.MeshPath, extra.Asset.MeshPath, StringComparison.OrdinalIgnoreCase)) == true)
                                     .Select(c => c.Key).ToList())
                        {
                            level.RemoveClone(waiting); // re-add now that the mesh exists (a copy from an unloaded level drew without it)
                            _appliedClones.Remove(waiting);
                        }
                    }
                }

                var wanted = Clones ?? [];
                var wantedIds = new HashSet<uint>(wanted.Select(c => c.Id));
                foreach (var stale in _appliedClones.Keys.Where(id => !wantedIds.Contains(id)).ToList())
                {
                    level.RemoveClone(stale);
                    _appliedClones.Remove(stale);
                }

                foreach (var clone in wanted)
                {
                    if (!_appliedClones.TryGetValue(clone.Id, out var applied) || applied != clone)
                    {
                        level.AddClone(clone.Id, clone.SourceId, clone.RootWorld, clone.Name, clone.MeshPath, clone.Placements);
                        _appliedClones[clone.Id] = clone;
                    }
                }

                _bendsDirty = true; // re-added clones come back straight
            }

            if (_bendsDirty)
            {
                _bendsDirty = false;

                // Replaced meshes first: a bend is built over the mesh the node shows.
                var replaced = ReplacedMeshes;
                foreach (var id in level.SwappedActors.Where(id => replaced is null || !replaced.ContainsKey(id)).ToList())
                {
                    level.SetActorMesh(id, null);
                }

                foreach (var (id, mesh) in replaced ?? new Dictionary<uint, string>())
                {
                    level.SetActorMesh(id, mesh);
                }

                var parts = PartMeshes;
                foreach (var key in level.SwappedParts.Where(k => parts is null || !parts.ContainsKey(k)).ToList())
                {
                    level.SetPartMesh(key, null);
                }

                foreach (var (key, mesh) in parts ?? new Dictionary<InstanceKey, string>())
                {
                    if (key.InstanceIndex == InstanceKey.Part)
                    {
                        level.SetPartMesh(key, mesh);
                    }
                }

                var bends = Bends;
                foreach (var id in level.BentIds.Where(id => bends is null || !bends.ContainsKey(id)).ToList())
                {
                    level.SetBend(id, null);
                }

                foreach (var (id, bent) in bends ?? new Dictionary<uint, IReadOnlyList<Level.Model.SplineMeshParams>>())
                {
                    level.SetBend(id, bent);
                }

                var pieces = SegmentBends;
                foreach (var key in level.BentSegments.Where(k => pieces is null || !pieces.ContainsKey(k)).ToList())
                {
                    level.SetSegmentBend(key, null);
                }

                foreach (var (key, spline) in pieces ?? new Dictionary<InstanceKey, Level.Model.SplineMeshParams>())
                {
                    level.SetSegmentBend(key, spline, parts?.GetValueOrDefault(key));
                }

            }

            if (_transformsDirty)
            {
                _transformsDirty = false;
                var moved = ActorTransforms;
                foreach (var id in level.MovedActors.Where(id => moved is null || !moved.ContainsKey(id)).ToList())
                {
                    level.ResetActorTransform(id);
                }

                if (moved is not null)
                {
                    foreach (var (id, rootWorld) in moved)
                    {
                        level.SetActorTransform(id, rootWorld);
                    }
                }

                level.SetInstanceTransforms(InstanceTransforms);
            }

            // After every step that builds nodes (owner: "sometimes a selected object shows no orange, only its numbers":
            // a copy re-added, a piece re-bent or an instance given its own node after the selection was lit came back unlit).
            if (_selectionDirty || level.NodesBuilt != _litNodes)
            {
                _selectionDirty = false;
                _litNodes = level.NodesBuilt;
                if (SelectedInstance is { } instance && instance.SelectableId == SelectedId)
                {
                    level.SelectInstance(instance);
                }
                else
                {
                    level.SetSelection(SelectedId == 0 ? [] : [SelectedId]);
                }

                level.HighlightAlso(KindIds ?? [], KindInstances ?? []);
            }

            if (_visibilityDirty)
            {
                _visibilityDirty = false;
                var hidden = HiddenIds;
                var hiddenInstances = HiddenInstances;
                var hiddenPins = HiddenPinKinds;
                foreach (var node in level.Scene.Nodes)
                {
                    var visible = node.SelectableId == 0 || hidden is null || !hidden.Contains(node.SelectableId);
                    if (visible && hiddenInstances is { Count: > 0 } && node.Tag is ScenePlacement { InstanceKey: { } key })
                    {
                        visible = !hiddenInstances.Contains(key);
                    }

                    if (visible && hiddenPins is { Count: > 0 } && node.Tag is ScenePlacement pin && SpawnMarkers.KindOfMesh(pin.MeshPath) is { } kind)
                    {
                        visible = !hiddenPins.Contains(kind);
                    }

                    node.Visible = visible;
                }
            }

            if (preview is not null)
            {
                _previewApplied = true;
                if (preview.Instance is { } instance)
                {
                    level.SetInstanceTransform(instance, preview.Root);
                }
                else if (level.CloneIds.Contains(preview.Id))
                {
                    level.SetCloneTransform(preview.Id, preview.Root);
                }
                else
                {
                    level.SetActorTransform(preview.Id, preview.Root);
                }

                PreviewGroup(level, preview);
            }

            BuildGizmoOverlay(_renderer, preview?.Root ?? SelectedRootWorld);
            DrawShapeHandles(_renderer);
            if (BrushMode && _brushPointGl is { } brushAt)
            {
                var ring = GizmoMath.RingPoints(brushAt, (float)BrushRadius * 100f, 64);
                for (var i = 1; i < ring.Length; i++)
                {
                    _renderer.Overlay.Add(new OverlayLine(ring[i - 1], ring[i], new Vector4(1f, 0.8f, 0.2f, 1f)));
                }
            }

            if (_snappedTo is not null && preview is not null && _snapPiece is { } snapped)
            {
                // Locked onto a neighbour: the piece's box glows green (Alt while dragging lets go).
                DrawBox(_renderer, PieceSnap.WorldBox(snapped with { World = preview.Root with { Scale3D = snapped.World.Scale3D } }), new Vector4(0.3f, 1f, 0.45f, 1f));
            }
        }

        // Camera motion: time-based so the speed does not depend on the frame rate.
        var now = _clock.Elapsed.TotalSeconds;
        var interval = now - _lastFrameSeconds;
        var dt = (float)Math.Clamp(interval, 0.0, 0.1);
        _lastFrameSeconds = now;
        ReleaseLostKeys();
        // Smooth start and stop: the velocity eases towards what the keys ask for (about 0.1 s), like the in-game drone.
        var f = (_forward ? 1f : 0f) - (_backward ? 1f : 0f);
        var r = (_right ? 1f : 0f) - (_left ? 1f : 0f);
        var u = (_up ? 1f : 0f) - (_down ? 1f : 0f);
        var speed = _camera.MoveSpeed * (_fast ? _camera.FastMultiplier : 1f);
        var target = ((_camera.Forward * f) + (_camera.Right * r) + (Vector3.UnitY * u)) * speed;
        _velocity = Vector3.Lerp(_velocity, target, 1f - MathF.Exp(-dt / 0.06f));
        var moving = _velocity.LengthSquared() > 1f;
        if (moving)
        {
            _camera.Position += _velocity * dt;
        }
        else
        {
            _velocity = Vector3.Zero;
        }

        if (_freeDragging && (_shownYaw != _freeYaw || _shownLift != _freeLift))
        {
            // Wheel turns and lifts glide to their target (about 0.08 s) instead of jumping a step per notch.
            var k = 1f - MathF.Exp(-dt / 0.08f);
            _shownYaw = MathF.Abs(_freeYaw - _shownYaw) < 0.01f ? _freeYaw : _shownYaw + ((_freeYaw - _shownYaw) * k);
            _shownLift = MathF.Abs(_freeLift - _shownLift) < 0.01f ? _freeLift : _shownLift + ((_freeLift - _shownLift) * k);
            UpdateFreePreview();
        }

        if (_pendingPick is { } pick && _level is { } pickable)
        {
            _pendingPick = null;
            // A few pixels around the cursor count, nearest first: thin bars, wires and fences take the click.
            var result = _renderer.Pick(_target, pickable.Scene, _camera, pick.X, pick.Y, radius: (int)Math.Round(4 * RenderScaling()));
            if (result is not null && Vector3.Distance(result.WorldPosition, _camera.Position) > PickReach(result.Node))
            {
                result = null; // too far away: a click on the horizon must not select something kilometres off
            }

            var id = result?.Node.SelectableId ?? 0u;
            // Shift+click takes the whole actor (all of a road, all of a foliage actor) instead of the one piece under the cursor;
            // a part of a Blueprint is picked on its own only in part mode (or with Alt), a spawn part (its pin or the item it
            // spawns: a house's drill press, a car shop's vehicle box) always, a door's leaf never (ScenePlacement.PickKey).
            var instance = !_pickWhole && result?.Node.Tag is ScenePlacement placed ? placed.PickKey(_pickPart) : null;
            if (!_pickToggle && SelectedId != 0 && DateTime.UtcNow < SelectionHoldUntil)
            {
                // A fresh duplicate stands on its source: the click (and a drag from it) keeps taking the copy.
                id = SelectedId;
                instance = SelectedInstance;
            }
            if (_pickToggle)
            {
                _pickToggle = false;
                if (id != 0)
                {
                    Dispatcher.UIThread.Post(() => PickToggled?.Invoke(this, (id, instance)));
                }
            }
            else
            {
            if (_grabPending)
            {
                // A left press waits for this pick: the actor (or single instance) under the cursor and where it was grabbed.
                _grabPending = false;
                _grabPointGl = result?.WorldPosition ?? default;
                _grabInstance = instance;
                _grabId = id;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Instance first: the view model then shows that one tree or rock, not the foliage actor that holds thousands.
                SelectedInstance = instance;
                SelectedId = id;
                ActorPicked?.Invoke(this, id);
            });
            }
        }

        if (_pendingBrush is { } brush)
        {
            ResolveBrush(_level is { } brushed ? _renderer.Pick(_target, brushed.Scene, _camera, brush.X, brush.Y)?.WorldPosition : null);
            RequestNextFrameRendering(); // the circle is drawn where the cursor now is
        }

        RenderStats stats;
        _renderer.Time = (float)(now % 3600.0); // the stand-ins' glint; wrapped so a long session keeps float precision
        if (_level is { } scene)
        {
            if (_backdrop is { } island)
            {
                _renderer.Render(_target, island.Scene, _camera);
            }

            stats = _renderer.Render(_target, scene.Scene, _camera, clear: _backdrop is null);
        }
        else
        {
            stats = _renderer.Render(_target, _backdrop?.Scene ?? EmptyScene, _camera);
        }

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
        HasRenderedScene |= _level is not null;
        // Frame rate while the camera moves (frames are continuous then); the UI thread renders the GL frame, so this is
        // what the owner sees and the number to look at when movement feels like steps instead of a glide.
        if (_wasContinuous && interval > 0)
        {
            var frameMs = interval * 1000.0;
            _frameMs = _frameMs <= 0 ? frameMs : (_frameMs * 0.9) + (frameMs * 0.1);
        }

        _wasContinuous = moving || _looking || _panning;

        var info = _level is { } l
            ? $"{l.PlacedCount:N0} placements · {stats.Instances:N0} drawn · {stats.Culled:N0} culled · {stats.Triangles:N0} tris" + (_frameMs > 0 ? $" · {1000.0 / _frameMs:0} fps" : string.Empty)
            : "no level loaded";
        if (info != _lastInfo && now - _lastInfoSeconds > 0.25)
        {
            // A few updates a second: posting a new text every frame re-lays out the HUD and costs frame time.
            _lastInfo = info;
            _lastInfoSeconds = now;
            Dispatcher.UIThread.Post(() => FrameInfo = info, DispatcherPriority.Background);
        }

        if (moving || _looking || _panning)
        {
            RequestNextFrameRendering();
        }
        else if (_level is { HasShimmer: true } && !_shimmerQueued)
        {
            // Stand-ins keep glinting at rest: a frame every 40 ms (25 fps) is smooth for a 1.5 s pulse and cheap.
            // ponytail: runs while any stand-in is in the scene, hidden layers included; gate on the spawn layers if it costs.
            _shimmerQueued = true;
            DispatcherTimer.RunOnce(() =>
            {
                _shimmerQueued = false;
                RequestNextFrameRendering();
            }, TimeSpan.FromMilliseconds(40));
        }
    }

    private bool _shimmerQueued;

    private static readonly Rendering.SceneGraph.Scene EmptyScene = new();

    /// <summary>
    /// Gives the control a hit area. The GL frame is a child surface visual that Avalonia's hit test ignores, so without
    /// this every click, drag and wheel went to the panel behind the viewport.
    /// </summary>
    public override void Render(Avalonia.Media.DrawingContext context) =>
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is WindowBase window)
        {
            window.Deactivated += OnWindowDeactivated;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BrushModeProperty || change.Property == BrushRadiusProperty || change.Property == OrientationProperty)
        {
            RequestNextFrameRendering(); // the circle shows, hides or changes size
            return;
        }

        if (change.Property != IsVisibleProperty)
        {
            return;
        }

        if (IsVisible)
        {
            RequestNextFrameRendering(); // back from another page: pick up where it stopped
        }
        else
        {
            // Another page is shown: give the mouse back and stop flying.
            _forward = _backward = _left = _right = _up = _down = _fast = false;
            _looking = false;
            SetDrone(false);
            UnlockCursor();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetDrone(false);
        UnlockCursor();
        if (_topLevel is WindowBase window)
        {
            window.Deactivated -= OnWindowDeactivated;
        }

        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        e.Handled = true;
        if (_drone)
        {
            // Drone mode: the cursor is hidden, a left click selects what the crosshair points at.
            if (point.Properties.IsLeftButtonPressed)
            {
                QueuePick(new Point(Bounds.Width / 2, Bounds.Height / 2), grab: false);
            }

            return;
        }

        _lastPointer = _pressPointer = point.Position;
        if (BrushMode && point.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Brush: holding the button paints; nothing is grabbed or moved (Ctrl+click still takes one object out).
            _brushing = true;
            _brushFromGl = null; // a new stroke
            QueueBrush(point.Position, paint: true);
            e.Pointer.Capture(this);
            return;
        }

        if (point.Properties.IsLeftButtonPressed && TryBeginHandleDrag(point.Position, shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Pointer.Capture(this);
            RequestNextFrameRendering();
            return;
        }

        if (point.Properties.IsLeftButtonPressed && TryHitGizmo(point.Position, out var handle) && SelectedRootWorld is { } root)
        {
            BeginGizmoDrag(handle, point.Position, root);
            e.Pointer.Capture(this);
            RequestNextFrameRendering();
            return;
        }

        if (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+click adds the object to the multi-selection (or takes it out); nothing is dragged.
            QueuePick(point.Position, grab: false, whole: e.KeyModifiers.HasFlag(KeyModifiers.Shift), toggle: true, part: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            // Select on press; dragging afterwards moves the actor (see UpdateFreeDrag).
            _leftDown = true;
            QueuePick(point.Position, grab: true, whole: e.KeyModifiers.HasFlag(KeyModifiers.Shift), part: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            e.Pointer.Capture(this);
            return;
        }

        _looking = point.Properties.IsRightButtonPressed;
        _panning = point.Properties.IsMiddleButtonPressed;
        if (_looking)
        {
            LockCursor();
        }

        if (_looking || _panning)
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
        if (_cursorLocked)
        {
            if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5)
            {
                return; // the move caused by putting the cursor back in the middle (or sub-pixel noise)
            }

            RecenterCursor();
        }

        if (_drone || _looking)
        {
            _camera.Rotate((float)delta.X, (float)delta.Y);
            RequestNextFrameRendering();
            e.Handled = true;
            return;
        }

        _snapHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        _altHeld = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (BrushMode && !_panning)
        {
            QueueBrush(position, paint: _brushing);
            if (_brushing)
            {
                e.Handled = true;
                return;
            }
        }

        if (IsHandleDragging)
        {
            DragHandle(position);
            RequestNextFrameRendering();
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            UpdateGizmoDrag(position);
            e.Handled = true;
            return;
        }

        if (_leftDown)
        {
            UpdateFreeDrag(position);
            e.Handled = true;
            return;
        }

        if (!_panning && SelectedRootWorld is not null)
        {
            UpdateHover(position);
        }

        if (_panning)
        {
            var scale = MathF.Max(_camera.MoveSpeed, 100f) * 0.002f;
            _camera.Move(0f, (float)-delta.X * scale, (float)delta.Y * scale);
            RequestNextFrameRendering();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_brushing && e.InitialPressMouseButton == MouseButton.Left)
        {
            _brushing = false;
            e.Pointer.Capture(null);
            if (_pendingBrush is { Paint: true })
            {
                _brushEndPending = true; // raised right after that last dab (ResolveBrush)
            }
            else
            {
                Dispatcher.UIThread.Post(() => BrushStrokeEnded?.Invoke(this, EventArgs.Empty));
            }

            e.Handled = true;
            return;
        }

        if (IsHandleDragging)
        {
            e.Pointer.Capture(null);
            EndHandleDrag();
            e.Handled = true;
            return;
        }

        if (_dragging)
        {
            e.Pointer.Capture(null);
            EndGizmoDrag();
            e.Handled = true;
            return;
        }

        if (_leftDown && e.InitialPressMouseButton == MouseButton.Left)
        {
            _leftDown = false;
            e.Pointer.Capture(null);
            if (_freeDragging)
            {
                (_shownYaw, _shownLift) = (_freeYaw, _freeLift); // land exactly where the wheel aimed
                UpdateFreePreview();
                _freeDragging = false;
                CommitDragPreview();
            }

            e.Handled = true;
            return;
        }

        if (_looking && e.InitialPressMouseButton == MouseButton.Right)
        {
            _looking = false;
            if (!_drone)
            {
                UnlockCursor();
            }
        }
        else if (_panning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _panning = false;
        }

        e.Pointer.Capture(null);
        RequestNextFrameRendering();
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

        if (_leftDown && (_freeDragging || TryBeginFreeDrag()))
        {
            // While an actor is held: the wheel turns it 2 degrees a notch (Ctrl: 0.25), Shift + wheel raises or lowers it
            // 2 cm a notch (Ctrl: 0.5 cm). Both glide there (see the easing in OnOpenGlRender).
            var fine = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _freeLift += notches * (fine ? 0.5f : 2f);
            }
            else
            {
                _freeYaw += notches * (fine ? 0.25f : 2f);
            }

            UpdateFreePreview();
            return;
        }

        if (_drone || _looking)
        {
            SetSpeed(_camera.MoveSpeed * MathF.Pow(1.25f, notches));
            return;
        }

        // Zoom: fly towards the point under the cursor, half a second of flight per notch.
        var (_, direction) = ScreenRay(e.GetPosition(this));
        _camera.Position += direction * (notches * MathF.Max(_camera.MoveSpeed * 0.5f, 50f));
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleDrone();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _drone)
        {
            SetDrone(false);
            e.Handled = true;
        }
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0 && SetKey(e.Key, true))
        {
            // Ctrl+A / Ctrl+C / Alt+… are shortcuts for the page, never a camera move.
            _fast = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            RequestNextFrameRendering();
            e.Handled = true;
        }
        else if (e.Key is Key.PageUp or Key.PageDown && SelectedId != 0 && SelectedRootWorld is { } root && !_dragging && !_freeDragging)
        {
            // Fine height: 1 cm a press (Shift: 10 cm, Ctrl: 1 mm), to line up towers, wires, bridge ends.
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10f : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1f : 1f;
            var t = root.Translation;
            var raised = new FTransform(root.Rotation, new FVector(t.X, t.Y, t.Z + (e.Key == Key.PageUp ? step : -step)), root.Scale3D);
            TransformDragged?.Invoke(this, new TransformDragEventArgs(SelectedId, raised));
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            if (SelectedId != 0)
            {
                FrameSelection();
            }
            else
            {
                FrameAll();
            }

            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (SetKey(e.Key, false))
        {
            _fast = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _forward = _backward = _left = _right = _up = _down = _fast = false;
        _looking = false;
        SetDrone(false);
        UnlockCursor();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Alt+Tab and friends must always give the mouse back, and a key released in another window must not keep flying.
        _forward = _backward = _left = _right = _up = _down = _fast = false;
        _looking = false;
        SetDrone(false);
        UnlockCursor();
    }

    private void SetDrone(bool on)
    {
        if (on == _drone)
        {
            return;
        }

        _drone = on;
        IsDroneMode = on;
        if (on)
        {
            Focus();
            LockCursor();
        }
        else if (!_looking)
        {
            UnlockCursor();
        }

        RequestNextFrameRendering();
    }

    private void SetSpeed(float centimetresPerSecond)
    {
        _camera.MoveSpeed = Math.Clamp(centimetresPerSecond, 100f, 200_000f);
        var metres = _camera.MoveSpeed / 100f;
        var text = metres.ToString(metres < 10f ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
        if (Dispatcher.UIThread.CheckAccess())
        {
            SpeedText = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() => SpeedText = text);
        }
    }

    /// <summary>Hides the cursor and keeps it inside the viewport; mouse moves become relative look deltas.</summary>
    private void LockCursor()
    {
        if (_cursorLocked || !CursorLock.IsSupported || _topLevel?.TryGetPlatformHandle() is null)
        {
            return; // no native window (e.g. headless tests): never touch the real mouse
        }

        _cursorRestore = this.PointToScreen(_lastPointer);
        var topLeft = this.PointToScreen(default);
        var bottomRight = this.PointToScreen(new Point(Bounds.Width, Bounds.Height));
        CursorLock.Clip(new PixelRect(topLeft, bottomRight));
        Cursor = HiddenCursor;
        _cursorLocked = true;
        RecenterCursor();
    }

    private void RecenterCursor()
    {
        // Remember where the cursor really lands (whole screen pixels, mapped back exactly as Avalonia maps the move it
        // causes). Using the unrounded centre instead left a fraction of a pixel at 125%/150% display scaling, which read
        // as a new movement, warped again, and turned the camera forever ("it keeps going up by itself").
        var target = this.PointToScreen(new Point(Bounds.Width / 2, Bounds.Height / 2));
        CursorLock.MoveTo(target);
        _lastPointer = this.PointToClient(target);
    }

    /// <summary>Gives the mouse back where it was when the look started.</summary>
    private void UnlockCursor()
    {
        if (!_cursorLocked)
        {
            return;
        }

        _cursorLocked = false;
        CursorLock.Release();
        CursorLock.MoveTo(_cursorRestore);
        _lastPointer = this.PointToClient(_cursorRestore);
        Cursor = null;
    }

    /// <summary>
    /// How far away a click may pick <paramref name="node"/>: <see cref="MaxPickDistance"/> for small things, more for big
    /// ones (20 × their radius: a bridge or a hangar from 500 m and more), at most <see cref="MaxPickDistanceLarge"/>.
    /// </summary>
    private float PickReach(SceneNode node)
    {
        if (node.Mesh is not { } mesh || mesh.Bounds.IsEmpty)
        {
            return MaxPickDistance;
        }

        var radius = Frustum.TransformBounds(mesh.Bounds, node.WorldTransform).Extent.Length();
        return Math.Clamp(radius * 20f, MaxPickDistance, MathF.Max(MaxPickDistance, MaxPickDistanceLarge));
    }

    private void QueueBrush(Point position, bool paint)
    {
        if (!paint && _pendingBrush is { Paint: true })
        {
            return; // a dab not yet taken by a frame is not replaced by a plain hover
        }

        var scaling = RenderScaling();
        _pendingBrush = ((int)(position.X * scaling), (int)(position.Y * scaling), paint);
        if (_renderer is null)
        {
            ResolveBrush(null); // no OpenGL (headless): nothing to pick, the ground under the cursor still counts
        }

        RequestNextFrameRendering();
    }

    /// <summary>
    /// Places the brush circle for the queued cursor position and, while painting, raises <see cref="BrushPainted"/>
    /// from the stroke's previous centre to this one. <paramref name="hitGl"/> is what the pick found under the cursor.
    /// </summary>
    private void ResolveBrush(Vector3? hitGl)
    {
        if (_pendingBrush is not { } brush)
        {
            return;
        }

        _pendingBrush = null;
        var (w, h) = PixelSize();
        var (origin, direction) = _camera.ScreenRay(brush.X, brush.Y, Math.Max(1, w), Math.Max(1, h));
        var centre = BrushCentre(origin, direction, hitGl);
        _brushPointGl = centre;
        if (brush.Paint)
        {
            var stroke = (UeToGl.ToUePoint(_brushFromGl ?? centre), UeToGl.ToUePoint(centre));
            _brushFromGl = centre;
            Dispatcher.UIThread.Post(() => BrushPainted?.Invoke(this, stroke));
            if (_brushEndPending)
            {
                _brushEndPending = false;
                Dispatcher.UIThread.Post(() => BrushStrokeEnded?.Invoke(this, EventArgs.Empty));
            }
        }
    }

    /// <summary>
    /// The brush circle's centre (GL) for a mouse ray (owner: "everything inside the circle", not only what the cursor is
    /// on): what the pick hit, else the terrain under the ray, else the level plane of the last ground the brush touched,
    /// at most <see cref="MaxPickDistanceLarge"/> away across the ground (towards the sky: that far straight ahead), so the
    /// circle follows the cursor over empty ground and the sky too.
    /// </summary>
    private Vector3 BrushCentre(Vector3 origin, Vector3 direction, Vector3? hitGl)
    {
        if (hitGl is null && ((Scene?.HeightField?.RaycastGl(origin, direction, _camera.FarPlane) ?? Backdrop?.HeightField?.RaycastGl(origin, direction, _camera.FarPlane)) is { } ground))
        {
            hitGl = UeToGl.Point(new FVector(ground.Position.X, ground.Position.Y, ground.Position.Z));
        }

        if (hitGl is { } hit)
        {
            _brushGroundY = hit.Y;
            return hit;
        }

        var y = _brushGroundY ?? (_level is { } level && !ViewBounds(level).IsEmpty ? ViewBounds(level).Min.Y : 0f);
        var flat = new Vector2(direction.X, direction.Z);
        var length = flat.Length();
        if (length < 1e-6f)
        {
            return new Vector3(origin.X, y, origin.Z); // straight down (or up)
        }

        var t = MathF.Abs(direction.Y) > 1e-6f ? (y - origin.Y) / direction.Y : -1f;
        var across = MathF.Min(t > 0f ? t * length : float.MaxValue, MathF.Max(MaxPickDistance, MaxPickDistanceLarge));
        flat /= length;
        return new Vector3(origin.X + (flat.X * across), y, origin.Z + (flat.Y * across));
    }

    private void QueuePick(Point position, bool grab, bool whole = false, bool toggle = false, bool part = false)
    {
        var scaling = RenderScaling();
        _grabPending = grab;
        _pickWhole = whole;
        _pickPart = part || PickParts;
        _pickToggle = toggle;
        _grabId = 0;
        _pendingPick = ((int)(position.X * scaling), (int)(position.Y * scaling));
        RequestNextFrameRendering();
    }

    /// <summary>Left-drag on an actor: it follows the cursor over the horizontal plane through the point it was grabbed at.</summary>
    private void UpdateFreeDrag(Point position)
    {
        if (!_freeDragging)
        {
            var moved = position - _pressPointer;
            if (Math.Sqrt((moved.X * moved.X) + (moved.Y * moved.Y)) < 4 || !TryBeginFreeDrag())
            {
                return;
            }
        }

        var (origin, direction) = ScreenRay(position);
        if (GizmoMath.HitHorizontalPlane(origin, direction, _grabPointGl.Y, _camera.FarPlane) is { } hit)
        {
            _freeMove = hit - _grabPointGl;
            UpdateFreePreview();
        }
    }

    /// <summary>Starts a free drag once the press's pick has selected an actor and its root transform is known.</summary>
    private bool TryBeginFreeDrag()
    {
        if (_grabId == 0 || SelectedId != _grabId || SelectedInstance != _grabInstance || SelectedRootWorld is not { } root)
        {
            return false;
        }

        PivotOf(root); // the press may have just selected it: place its pivot before the drag freezes it
        _freeDragging = true;
        _dragStartRoot = root;
        _freeMove = Vector3.Zero;
        _freeLift = _shownLift = 0f;
        _freeYaw = _shownYaw = 0f;
        BeginAutoSnap(_grabId, _grabInstance);
        return true;
    }

    private void UpdateFreePreview()
    {
        _dragPreview = new DragPreview(_grabId, AutoSnapped(AboutPivot(_dragStartRoot, GizmoMath.FreeMove(_dragStartRoot, _freeMove, _shownLift, _shownYaw, _snapHeld ? TranslationSnap : 0f)), null), _grabInstance);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// The dragged piece and the single-mesh pieces within 100 m of it (plus its own size), for <see cref="PieceSnap"/>.
    /// Road and river pieces bent along splines and bent actors are left out: their ends are not straight.
    /// </summary>
    private void BeginPieceSnap(uint id, InstanceKey? instance)
    {
        _snapPiece = null;
        _snapPieces = [];
        if (_level is null)
        {
            return;
        }

        var own = _level.Scene.Nodes.Where(n => n.Mesh is not null && n.SelectableId == id
            && (instance is null || n.Tag is ScenePlacement { InstanceKey: { } k } && k == instance)).Take(2).ToList();
        if (own.Count != 1 || PieceOf(own[0]) is not { } moving)
        {
            return;
        }

        _snapPiece = moving;
        var range = 10_000f + moving.Bounds.Size.Length() * MathF.Max(1f, MathF.Max(moving.World.Scale3D.X, MathF.Max(moving.World.Scale3D.Y, moving.World.Scale3D.Z)));
        foreach (var node in _level.Scene.Nodes)
        {
            if (node.SelectableId == id && (instance is null || (node.Tag is ScenePlacement { InstanceKey: { } k2 } && k2 == instance)))
            {
                continue;
            }

            if (PieceOf(node) is { } piece && FVector.Distance(piece.World.Translation, moving.World.Translation) <= range)
            {
                _snapPieces.Add(piece);
            }
        }
    }

    /// <summary>The snap piece a node draws (its mesh, where it stands now, its bounds), or null.</summary>
    private SnapPiece? PieceOf(SceneNode node)
    {
        if (node.Mesh is null || !node.IsEffectivelyVisible || node.SelectableId == 0 || Bends?.ContainsKey(node.SelectableId) == true)
        {
            return null;
        }

        string? mesh;
        FTransform world;
        if (node.Tag is ScenePlacement placement)
        {
            if (placement.InstanceKey is { InstanceIndex: InstanceKey.Segment })
            {
                return null;
            }

            mesh = placement.MeshPath;
            world = placement.InstanceKey is { } key && InstanceTransforms?.TryGetValue(key, out var movedInstance) == true ? movedInstance
                : ActorTransforms?.TryGetValue(placement.SelectableId, out var root) == true ? placement.World.GetRelativeTransform(placement.Actor.WorldTransform) * root
                : placement.World;
        }
        else if (Clones?.FirstOrDefault(c => c.Id == node.SelectableId && c.MeshPath is not null) is { } clone)
        {
            mesh = clone.MeshPath!;
            world = clone.RootWorld;
        }
        else
        {
            return null;
        }

        var bounds = _level?.Prepared.Meshes.TryGetValue(mesh, out var asset) == true ? asset.Mesh.Bounds
            : ExtraMeshes?.FirstOrDefault(m => string.Equals(m.Asset.MeshPath, mesh, StringComparison.OrdinalIgnoreCase))?.Asset.Mesh.Bounds;
        return bounds is { IsEmpty: false } b ? new SnapPiece(mesh, world, b) : null;
    }

    /// <summary>A piece dropped by hand touching another (not snapped): remember how this pair was joined.</summary>
    private void LearnJoint(FTransform dropped)
    {
        if (_snappedTo is not null || _snapPiece is not { } piece || _altHeld)
        {
            return;
        }

        var placed = piece with { World = dropped with { Scale3D = piece.World.Scale3D } };
        foreach (var target in _snapPieces)
        {
            if (PieceSnap.JointOf(placed, target) is not { } joint)
            {
                continue;
            }

            var known = _learnedJoints.TryGetValue((piece.Mesh, target.Mesh), out var list) ? list : _learnedJoints[(piece.Mesh, target.Mesh)] = [];
            if (!known.Any(j => FVector.Distance(j.Translation, joint.Translation) < 5f && j.Rotation.AngularDistance(joint.Rotation) < 0.02f))
            {
                known.Add(joint);
                if (known.Count > 8)
                {
                    known.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>With <see cref="AutoSnap"/> on, takes the dragged object's box and every other placement's box (UE space) at the start of a drag.</summary>
    private void BeginAutoSnap(uint id, InstanceKey? instance)
    {
        _snapOthers = null;
        if (!AutoSnap || _level is null)
        {
            return;
        }

        _snapMoving = BoundingBox.Empty;
        _snapOthers = [];
        BeginPieceSnap(id, instance);
        foreach (var node in _level.Scene.Nodes)
        {
            if (node.Tag is not ScenePlacement placement || node.Mesh is not { } mesh || !node.IsEffectivelyVisible)
            {
                continue; // terrain, sea and helpers are not snap targets
            }

            var gl = LevelSceneUploader.TransformBounds(mesh.Bounds, node.WorldTransform);
            var box = new BoundingBox(new Vector3(gl.Min.X, gl.Min.Z, gl.Min.Y), new Vector3(gl.Max.X, gl.Max.Z, gl.Max.Y));
            var moving = instance is { } key ? placement.InstanceKey == key : placement.SelectableId == id;
            if (moving)
            {
                _snapMoving = _snapMoving.Union(box);
            }
            else
            {
                _snapOthers.Add(box);
            }
        }
    }

    /// <summary>
    /// The end-to-end join of the piece a gizmo drags, when there is one in reach that needs no turn and lies along the
    /// dragged arrow (<paramref name="along"/>) or in the dragged plane (normal <paramref name="planeNormal"/>): gizmo moves
    /// join pieces like a free drag does (owner: "Snap is on but the bridge piece does not stick to the same piece"), and
    /// move freely everywhere else (no stepping onto other objects' boxes). Alt lets go. Null when nothing joins.
    /// </summary>
    private FTransform? PieceSnapAlong(FTransform moved, FVector? along, FVector? planeNormal)
    {
        _snappedTo = null;
        if (!AutoSnap || _altHeld || _snapPiece is not { } piece || _snapPieces.Count == 0)
        {
            return null;
        }

        var moving = piece with { World = moved with { Scale3D = piece.World.Scale3D } };
        if (PieceSnap.Best(moving, _snapPieces, PieceSnap.Reach(moving), t => _learnedJoints.GetValueOrDefault((piece.Mesh, t.Mesh)) ?? []) is not { } snap
            || !snap.World.Rotation.Equals(moved.Rotation, 2e-3f))
        {
            return null;
        }

        var jump = snap.World.Translation - moved.Translation;
        if (along is { } a && (jump - (a * FVector.Dot(jump, a))).Size() > 5f)
        {
            return null; // the joint is off to the side of the arrow
        }

        if (planeNormal is { } n && MathF.Abs(FVector.Dot(jump, n)) > 5f)
        {
            return null; // the joint is out of the dragged plane
        }

        _snappedTo = snap.Target;
        return snap.World with { Scale3D = moved.Scale3D };
    }

    /// <summary><paramref name="moved"/> shifted onto the nearest neighbour face in reach (only along <paramref name="axis"/> when given).</summary>
    private FTransform AutoSnapped(FTransform moved, FVector? axis)
    {
        if (!AutoSnap || _snapOthers is null || _snapMoving.IsEmpty)
        {
            return moved;
        }

        // A piece dragged freely near another joins it end to end (or the way this pair was joined before); Alt places freely.
        _snappedTo = null;
        if (axis is null && !_altHeld && _snapPiece is { } piece && _snapPieces.Count > 0)
        {
            var moving = piece with { World = moved with { Scale3D = piece.World.Scale3D } };
            if (PieceSnap.Best(moving, _snapPieces, PieceSnap.Reach(moving), t => _learnedJoints.GetValueOrDefault((piece.Mesh, t.Mesh)) ?? []) is { } snap)
            {
                _snappedTo = snap.Target;
                return snap.World with { Scale3D = moved.Scale3D };
            }
        }

        var d = moved.Translation - _dragStartRoot.Translation;
        var shift = new Vector3(d.X, d.Y, d.Z);
        var offset = GizmoMath.SnapOffset(new BoundingBox(_snapMoving.Min + shift, _snapMoving.Max + shift), _snapOthers, SnapReach);
        if (axis is { } a)
        {
            offset = new Vector3(a.X, a.Y, a.Z) * Vector3.Dot(offset, new Vector3(a.X, a.Y, a.Z));
        }

        var t = moved.Translation;
        return moved with { Translation = new FVector(t.X + offset.X, t.Y + offset.Y, t.Z + offset.Z) };
    }

    /// <summary>Ends a drag: the previewed transform goes to the journal through <see cref="TransformDragged"/> (<paramref name="scaled"/>: its scale too).</summary>
    private void CommitDragPreview(bool scaled = false)
    {
        var preview = _dragPreview;
        _dragPreview = null;
        if (preview is not null)
        {
            LearnJoint(preview.Root);
        }

        _snappedTo = null;
        if (preview is not null && !preview.Root.Equals(_dragStartRoot, 1e-3f))
        {
            TransformDragged?.Invoke(this, new TransformDragEventArgs(preview.Id, preview.Root, scaled));
        }

        RequestNextFrameRendering();
    }

    /// <summary>
    /// Lets go of keys and buttons the system says are up but whose release never reached the view: focus moved while a
    /// key was held (a toast, a menu, the entity list), so the key-up went elsewhere and the camera kept flying; a right
    /// button released over another window left mouse-look on and the cursor jumped (owner: "I press W once and it keeps
    /// going; the mouse jumps up, right, left"). Windows only; never in headless tests.
    /// </summary>
    private void ReleaseLostKeys()
    {
        if (!CursorLock.IsSupported || _topLevel?.TryGetPlatformHandle() is null)
        {
            return;
        }

        // Another app in front (a game, a chat): its keys are not ours, even when the switch never reached the window
        // (owner: "I pressed W, came back and it was flying by itself, as if someone controlled my PC").
        if (CursorLock.AppIsInFront() == false)
        {
            _forward = _backward = _left = _right = _up = _down = _fast = _panning = false;
            if (_looking)
            {
                _looking = false;
                if (!_drone)
                {
                    UnlockCursor();
                }
            }

            return;
        }

        static bool Up(params int[] keys) => keys.All(k => CursorLock.IsDown(k) == false);
        if (_forward && Up(0x57)) { _forward = false; } // W
        if (_backward && Up(0x53)) { _backward = false; } // S
        if (_left && Up(0x41)) { _left = false; } // A
        if (_right && Up(0x44)) { _right = false; } // D
        if (_up && Up(0x45, 0x20)) { _up = false; } // E, Space
        if (_down && Up(0x51, 0x43)) { _down = false; } // Q, C
        if (_fast && Up(0x10)) { _fast = false; } // Shift
        if (_looking && Up(0x02, 0x01)) // right (or, with swapped buttons, left) button
        {
            _looking = false;
            if (!_drone)
            {
                UnlockCursor();
            }
        }

        if (_panning && Up(0x04)) // middle button
        {
            _panning = false;
        }
    }

    private bool SetKey(Key key, bool down)
    {
        switch (key)
        {
            case Key.W: _forward = down; return true;
            case Key.S: _backward = down; return true;
            case Key.A: _left = down; return true;
            case Key.D: _right = down; return true;
            case Key.E: case Key.Space: _up = down; return true;
            case Key.Q: case Key.C: _down = down; return true;
            default: return false;
        }
    }

    private void OnSceneChanged(PreparedLevelScene? scene)
    {
        _pendingScene = scene;
        _sceneDirty = true;
        RequestNextFrameRendering();
    }

    private void MarkDirty(ref bool flag)
    {
        flag = true;
        RequestNextFrameRendering();
    }

    /// <summary>Mouse ray in GL world space for a control-space position.</summary>
    private (Vector3 Origin, Vector3 Direction) ScreenRay(Point position)
    {
        var (w, h) = PixelSize();
        var scaling = RenderScaling();
        return _camera.ScreenRay((float)(position.X * scaling), (float)(position.Y * scaling), Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Where a GL point is drawn, in control coordinates; null when it is behind the camera.</summary>
    private Point? ToScreen(Vector3 gl)
    {
        var (w, h) = PixelSize();
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        var clip = Vector4.Transform(new Vector4(gl, 1f), _camera.GetViewProjection((float)w / h));
        if (clip.W <= 1e-4f)
        {
            return null;
        }

        var scaling = RenderScaling();
        return new Point(((clip.X / clip.W) * 0.5 + 0.5) * w / scaling, (0.5 - ((clip.Y / clip.W) * 0.5)) * h / scaling);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared < 1e-9 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        var (cx, cy) = (a.X + (t * dx) - p.X, a.Y + (t * dy) - p.Y);
        return Math.Sqrt((cx * cx) + (cy * cy));
    }

    /// <summary>Angle in degrees of <paramref name="p"/> around <paramref name="centre"/> on screen.</summary>
    private static float ScreenAngle(Point p, Point centre) => (float)(Math.Atan2(p.Y - centre.Y, p.X - centre.X) * 180 / Math.PI);

    private sealed record DragPreview(uint Id, FTransform Root, InstanceKey? Instance = null);

    /// <summary>The twelve edges of a UE-space box as overlay lines.</summary>
    private static void DrawBox(SceneRenderer renderer, BoundingBox ue, Vector4 color)
    {
        Vector3 Corner(int i) => UeToGl.Point(new FVector((i & 1) == 0 ? ue.Min.X : ue.Max.X, (i & 2) == 0 ? ue.Min.Y : ue.Max.Y, (i & 4) == 0 ? ue.Min.Z : ue.Max.Z));
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
        {
            renderer.Overlay.Add(new OverlayLine(Corner(a), Corner(b), color));
        }
    }

    /// <summary>While a member of the multi-selection is dragged, every other member follows it rigidly.</summary>
    private void PreviewGroup(LevelScene level, DragPreview preview)
    {
        if (GroupWorlds is not { Count: > 1 } members
            || members.FirstOrDefault(m => m.Id == preview.Id && m.Instance == preview.Instance) is not { } dragged)
        {
            return;
        }

        var motion = (dragged.World with { Scale3D = FVector.One }).Inverse() * (preview.Root with { Scale3D = FVector.One });
        foreach (var member in members)
        {
            if (ReferenceEquals(member, dragged))
            {
                continue;
            }

            var world = member.World * motion;
            if (member.Instance is { } key)
            {
                level.SetInstanceTransform(key, world);
            }
            else if (level.CloneIds.Contains(member.Id))
            {
                level.SetCloneTransform(member.Id, world);
            }
            else
            {
                level.SetActorTransform(member.Id, world);
            }
        }
    }

    private void FrameBounds(BoundingBox bounds, float yaw = -135f, float pitch = -35f, bool resetSpeed = false)
    {
        var (w, h) = PixelSize();
        var aspect = h > 0 ? (float)w / h : 16f / 9f;
        var distance = _camera.Frame(bounds, aspect, yaw, pitch);
        if (resetSpeed)
        {
            // A fresh scene starts at a drone-like speed for its size; the wheel (while flying) changes it after that.
            SetSpeed(distance * 0.15f);
        }
    }

    private float SceneRadius() => _level is { } l && !ViewBounds(l).IsEmpty ? MathF.Max(ViewBounds(l).Extent.Length(), 100f) : 1000f;

    /// <summary>
    /// What "frame all" fits: the terrain when the scene has some (a stray placement near the world origin would otherwise
    /// make the camera frame kilometres of sea), else every placement.
    /// </summary>
    private static BoundingBox ViewBounds(LevelScene level) => level.TerrainBounds.IsEmpty ? level.Bounds : level.TerrainBounds;

    private double RenderScaling() => VisualRoot?.RenderScaling ?? 1.0;

    private (int W, int H) PixelSize()
    {
        var scaling = RenderScaling();
        return ((int)Math.Round(Bounds.Width * scaling), (int)Math.Round(Bounds.Height * scaling));
    }
}

/// <summary>Arguments of <see cref="LevelViewport.TransformDragged"/>.</summary>
/// <param name="Id">Selectable id of the dragged actor.</param>
/// <param name="RootWorld">New root world transform (UE space).</param>
/// <param name="Scaled">True when a scale cube was dragged: the transform's scale is meant, not only its place and turn.</param>
public sealed record TransformDragEventArgs(uint Id, FTransform RootWorld, bool Scaled = false);
