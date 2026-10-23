using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using ScumStudio.App.Localization;
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

    /// <summary>Time of day in hours (0-24) for outdoor scenes: the sun, sky and light (<see cref="RenderSettings.AtHour"/>).</summary>
    public static readonly StyledProperty<double> TimeOfDayProperty =
        AvaloniaProperty.Register<LevelViewport, double>(nameof(TimeOfDay), 13.5);

    /// <summary>Light and shade the scene (<see cref="RenderSettings.Lighting"/>); off: flat colours, no shadow pass.</summary>
    public static readonly StyledProperty<bool> LightingProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(Lighting), true);

    /// <summary>Move the sea's waves (<see cref="RenderSettings.AnimateWater"/>); off: still water.</summary>
    public static readonly StyledProperty<bool> AnimateWaterProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(AnimateWater), true);

    /// <summary>
    /// Draw with 4 samples a pixel: smooth edges, and leaves and grass cut by coverage, steady while the camera moves
    /// (<see cref="SceneRenderer.CreateTarget"/>); off: one sample, the previous look.
    /// </summary>
    public static readonly StyledProperty<bool> AntiAliasedFoliageProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(AntiAliasedFoliage), true);

    /// <summary>How many sectors (<see cref="SectorSizeCm"/>) around the camera the backdrop draws the game's far models (1-4).</summary>
    public static readonly StyledProperty<int> RenderDistanceProperty = AvaloniaProperty.Register<LevelViewport, int>(nameof(RenderDistance), 2);

    private const float DefaultSectorSizeCm = 300_000f;

    /// <summary>A map sector's edge in cm, the unit of <see cref="RenderDistance"/>.</summary>
    public static readonly StyledProperty<float> SectorSizeCmProperty = AvaloniaProperty.Register<LevelViewport, float>(nameof(SectorSizeCm), DefaultSectorSizeCm);

    /// <summary>Keep frames under 16.7 ms by drawing less while they take longer (<see cref="FrameBudget"/>); off: as the quality asks.</summary>
    public static readonly StyledProperty<bool> SmoothProperty = AvaloniaProperty.Register<LevelViewport, bool>(nameof(Smooth), true);

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

    /// <summary>The spots the brush is about to plant (UE world, on the ground), marked in the view: Plants = false where one is left out.</summary>
    public static readonly StyledProperty<IReadOnlyList<(FVector At, bool Plants)>?> BrushSpotsProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<(FVector At, bool Plants)>?>(nameof(BrushSpots));

    /// <summary>The brush circle stands on the terrain under the cursor, not on the canopy or roof the cursor is over (planting).</summary>
    public static readonly StyledProperty<bool> BrushOnGroundProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(BrushOnGround));

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

    // Milliseconds a frame may spend sending the ground near the camera at full detail (one component is about 1 ms).
    private const double TerrainDetailBudgetMs = 3.0;

    // The island backdrop goes to the GPU a few milliseconds a frame too (it was sent in one frame: audit F323).
    private readonly BackdropStager _backdropStager = new();
    private LevelScene? BackdropScene => _backdropStager.Scene;

    // Smooth: the view draws less while frames take too long (see FrameBudget); the GPU half of a frame's cost.
    private readonly FrameBudget _budget = new();
    private GpuFrameTimer? _gpuTimer;

    // The settings of the two scenes, made again only when an input changes (a `with` every frame was garbage).
    private (RenderQuality Quality, float Hour, bool Lighting, bool Water) _detailKey;
    private RenderSettings? _detailSettings;
    private (RenderSettings? Detail, int Sectors, float SectorCm) _backdropKey;
    private RenderSettings? _backdropSettings;

    // The frame readout: CPU time of the draw calls (moving average), and the collector's pauses since the last readout.
    private double _cpuMs;
    private TimeSpan _hudPause;
    private int _hudGen2;
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
    private IPointer? _brushPointer;
    private Point _lastPointer;
    private Point _pressPointer;
    private bool _looking;
    private bool _panning;
    private bool _forward, _backward, _left, _right, _up, _down, _fast;
    private Vector3 _velocity;
    private bool _drone;
    private bool _cursorLocked;
    private PixelPoint _cursorRestore;
    private bool _leftDown;
    private bool _grabPending;
    private uint _grabId;
    private InstanceKey? _grabInstance;
    private PreparedLevelScene? _grabScene; // the numbering _grabId is in (the scene drawn when it was picked)
    private PreparedLevelScene? _dragScene; // the Scene a drag began in: its preview's id is in that numbering
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

    // The ground near the camera drawn from every landscape sample (the levels load it at every fourth).
    private readonly TerrainDetailRings _terrainDetail = new();
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
        TimeOfDayProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        NearClipProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering()); // D09/F15 near clip
        LightingProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        AnimateWaterProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        AntiAliasedFoliageProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        RenderDistanceProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        SectorSizeCmProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        SmoothProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        SteadyFoliageProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        ScaleHandlesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        LegHandleProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        SelectedRootWorldProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        CanScaleProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
        ExtraMeshesProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.MarkDirty(ref c._clonesDirty));
        BrushSpotsProperty.Changed.AddClassHandler<LevelViewport>((c, _) => c.RequestNextFrameRendering());
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

    /// <inheritdoc cref="LightingProperty" />
    public bool Lighting
    {
        get => GetValue(LightingProperty);
        set => SetValue(LightingProperty, value);
    }

    /// <inheritdoc cref="AnimateWaterProperty" />
    public bool AnimateWater
    {
        get => GetValue(AnimateWaterProperty);
        set => SetValue(AnimateWaterProperty, value);
    }

    /// <inheritdoc cref="AntiAliasedFoliageProperty" />
    public bool AntiAliasedFoliage
    {
        get => GetValue(AntiAliasedFoliageProperty);
        set => SetValue(AntiAliasedFoliageProperty, value);
    }

    /// <inheritdoc cref="RenderDistanceProperty" />
    public int RenderDistance
    {
        get => GetValue(RenderDistanceProperty);
        set => SetValue(RenderDistanceProperty, value);
    }

    /// <inheritdoc cref="SectorSizeCmProperty" />
    public float SectorSizeCm
    {
        get => GetValue(SectorSizeCmProperty);
        set => SetValue(SectorSizeCmProperty, value);
    }

    /// <inheritdoc cref="SmoothProperty" />
    public bool Smooth
    {
        get => GetValue(SmoothProperty);
        set => SetValue(SmoothProperty, value);
    }

    /// <summary>Samples a pixel of the target (<see cref="AntiAliasedFoliage"/>).</summary>
    private int TargetSamples => AntiAliasedFoliage ? 4 : 1;

    /// <inheritdoc cref="TimeOfDayProperty" />
    public double TimeOfDay
    {
        get => GetValue(TimeOfDayProperty);
        set => SetValue(TimeOfDayProperty, value);
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

    /// <summary>
    /// Raised on the UI thread when the painting button is let go (or the stroke is lost: another button, the capture,
    /// another app in front), once, after the stroke's last <see cref="BrushPainted"/>.
    /// </summary>
    public event EventHandler? BrushStrokeEnded;

    /// <summary>
    /// Raised on the UI thread with the circle's centre (UE world) while the brush is not painting: on each hover (at most
    /// once a frame) and right after <see cref="BrushStrokeEnded"/>, so the planned spots can be shown before the click.
    /// </summary>
    public event EventHandler<FVector>? BrushHovered;

    /// <inheritdoc cref="BrushSpotsProperty" />
    public IReadOnlyList<(FVector At, bool Plants)>? BrushSpots
    {
        get => GetValue(BrushSpotsProperty);
        set => SetValue(BrushSpotsProperty, value);
    }

    /// <inheritdoc cref="BrushOnGroundProperty" />
    public bool BrushOnGround
    {
        get => GetValue(BrushOnGroundProperty);
        set => SetValue(BrushOnGroundProperty, value);
    }

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

    private (int Width, int Height)? _pendingCapture;
    private TaskCompletionSource<(byte[] Rgba, int Width, int Height)>? _captureResult;

    /// <summary>
    /// Draws the next frame's view once more off screen at <paramref name="width"/> x <paramref name="height"/> (same camera,
    /// no grid) for "Share your mod"; completes with its top-down RGBA8 pixels.
    /// </summary>
    public Task<(byte[] Rgba, int Width, int Height)> CaptureAsync(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _captureResult?.TrySetCanceled();
        _captureResult = new TaskCompletionSource<(byte[] Rgba, int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCapture = (width, height);
        RequestNextFrameRendering();
        return _captureResult.Task;
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
            _renderer = new SceneRenderer(_context, GraphicsStatus.Fallbacks);
            _renderer.Settings = _renderer.Settings with { ShowGrid = true };
            (_detailSettings, _backdropSettings) = (null, null);
            _gpuTimer = new GpuFrameTimer(_context.Gl);
            (_hudPause, _hudGen2) = (GC.GetTotalPauseDuration(), GC.CollectionCount(2));
            GpuName = _renderer.Info.Renderer;
            ReportGlStart();
            _target = _renderer.CreateTarget(Math.Max(1, PixelSize().W), Math.Max(1, PixelSize().H), TargetSamples);
            _ = EnsureStabilizer(); // its shaders compile now, not on the first move (a refusal only turns Steady foliage off)
            _sceneDirty = _pendingScene is not null;
            _backdropDirty = Backdrop is not null;
            RequestNextFrameRendering(); // requests made before the context existed were dropped
            _clock.Restart();
            IsGlReady = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FrameInfo = "OpenGL 4.3 is not available: " + ex.Message;
            GraphicsStatus.StartFailed("map view", ex);
            IsGlReady = false;
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _backdropStager.Clear();
        _backdropDirty = Backdrop is not null;
        _level?.Dispose();
        _level = null;
        _gpuMeshes.Clear(); // the context and every mesh in it go away with the renderer
        _terrainDetail.Clear();
        _stagedScene = null;
        _gpuTimer?.Dispose();
        _gpuTimer = null;
        ReleaseMotion();
        _target?.Dispose();
        _target = null;
        _renderer?.Dispose();
        _renderer = null;
        _context = null;
        IsGlReady = false;
    }

    /// <summary>One frame (see <see cref="OnOpenGlRender"/>, which catches what it throws).</summary>
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

        // The frame's time, taken before any of its work: the staging below takes longer in some frames than in others, and
        // a clock read after it moved the camera by uneven steps between frames the screen shows evenly (LevelViewport.Motion.cs).
        var frameStart = _clock.Elapsed.TotalSeconds;
        _target.Resize(w, h, Math.Min(TargetSamples, _renderer.Info.MaxSamples));
        _budget.Enabled = Smooth;
        var detailSettings = _budget.Apply(DetailSettings(_renderer));
        _renderer.Settings = detailSettings;

        // What the camera does in this frame (keys held, looking, panning) sets how long staging may take, not what it did in
        // the last one: the first frame of a move spent the 30 ms of a resting frame (audit F459).
        var stageBudget = _forward || _backward || _left || _right || _up || _down || _looking || _panning || _velocity != Vector3.Zero
            ? StageBudgetMovingMs
            : StageBudgetRestingMs;
        var stageStart = Stopwatch.GetTimestamp();

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
            if (_gpuMeshes.Stage(_renderer, prepared, stageBudget) && (_level?.Prebuild(prepared) ?? true))
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
                GraphicsStatus.Streamed("the map view");
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

        // The island backdrop with what this frame's staging budget has left (the detail levels go first: the camera is there);
        // the one on screen stays drawn until the new one is all on the GPU.
        var shownBackdrop = BackdropScene;
        var backdropLeftMs = stageBudget - Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
        if (_backdropDirty && (backdropLeftMs <= 0 || !_backdropStager.Step(_renderer, Backdrop, backdropLeftMs)))
        {
            RequestNextFrameRendering();
        }
        else if (_backdropDirty)
        {
            _backdropDirty = false;
        }

        if (!ReferenceEquals(shownBackdrop, BackdropScene))
        {
            _backdropHiddenFor = null;
            var sameIsland = ReferenceEquals(Backdrop, _framedBackdrop);
            _framedBackdrop = Backdrop;
            if (BackdropScene is { } b && !sameIsland && !b.TerrainBounds.IsEmpty && !TakeInitialView() && _level is null)
            {
                FrameBounds(b.TerrainBounds, yaw: -90f, pitch: -55f, resetSpeed: true);
            }

            if (BackdropScene is not null)
            {
                _camera.SetClipRange(10f, 3_000_000f); // reverse-Z keeps precision from 10 cm to the far side of the island
            }
        }

        if (BackdropScene is { } backdrop && !ReferenceEquals(_backdropHiddenFor, _uploadedScene ?? EmptyMarker))
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
                        _pinsDirty |= PinOverrides?.ContainsKey(clone.Id) == true; // a re-added copy took its source's pins again
                    }
                }

                _bendsDirty = true; // re-added clones come back straight
            }

            if (_pinsDirty)
            {
                // Spawn point pins follow the project's point lists: rebuilt for the actors with a changed list, put back
                // as the level stores them for the others (a copy takes its source's again); then selection, visibility and
                // moves are applied to the new nodes. After the copies: a copy's own pins replace the ones it took (igor8802).
                _pinsDirty = false;
                var wanted = PinOverrides ?? new Dictionary<uint, IReadOnlyList<ScenePlacement>>();
                foreach (var id in _appliedPins.Where(id => !wanted.ContainsKey(id)).ToList())
                {
                    if (_appliedClones.TryGetValue(id, out var clone))
                    {
                        level.RemoveClone(id);
                        level.AddClone(clone.Id, clone.SourceId, clone.RootWorld, clone.Name, clone.MeshPath, clone.Placements);
                        _bendsDirty = true;
                    }
                    else
                    {
                        level.ReplacePins(id, level.Prepared.Placements.Where(p => p.SelectableId == id && p.SpawnPoint is not null).ToList());
                    }

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

            if (_bendsDirty)
            {
                _bendsDirty = false;

                // Replaced meshes first: a bend is built over the mesh the node shows.
                var parts = PartMeshes;
                level.SetMeshes(ReplacedMeshes, parts);
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
                var shimmer = false;
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
                    shimmer |= level.HasShimmer && Shimmers(node);
                }

                (_shimmerShown, _shimmerNodes) = (shimmer, level.NodesBuilt);
            }
            else if (level.NodesBuilt != _shimmerNodes)
            {
                // Nodes built since (a copy re-added, a piece re-bent) may be stand-ins.
                (_shimmerShown, _shimmerNodes) = (level.HasShimmer && level.Scene.Nodes.Any(Shimmers), level.NodesBuilt);
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
            SyncHoles();
            DrawGroundOutlines(_renderer.Overlay);
            if (BrushMode && !HoleMode && _brushPointGl is { } brushAt)
            {
                // Over the ground, not a flat disc cut by every slope; about a point every half metre on big circles.
                var radius = (float)BrushRadius * 100f;
                var (detail, island) = (Scene?.HeightField, Backdrop?.HeightField);
                var ring = DrapedRing(brushAt, radius, (x, y) => detail?.SampleHeight(x, y) ?? island?.SampleHeight(x, y), Math.Clamp((int)(radius / 50f), 64, 256));
                for (var i = 1; i < ring.Length; i++)
                {
                    _renderer.Overlay.Add(new OverlayLine(ring[i - 1], ring[i], new Vector4(1f, 0.8f, 0.2f, 1f)));
                }
            }

            if (BrushMode && BrushSpots is { Count: > 0 } spots)
            {
                _renderer.Overlay.AddRange(SpotMarks(spots));
            }

            if (_snappedTo is not null && preview is not null && _snapPiece is { } snapped)
            {
                // Locked onto a neighbour: the piece's box glows green (Alt while dragging lets go).
                DrawBox(_renderer, PieceSnap.WorldBox(snapped with { World = preview.Root with { Scale3D = snapped.World.Scale3D } }), new Vector4(0.3f, 1f, 0.45f, 1f));
            }
        }

        // Camera motion: time-based so the speed does not depend on the frame rate; steady steps with Smooth camera.
        var now = _clock.Elapsed.TotalSeconds;
        var dt = StepCamera(frameStart);
        var interval = _frameClock.LastInterval;
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

        UpdateClipRange(); // D09/F15: near and far from what is drawn, every frame (LevelViewport.Clip.cs)

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
            var drawn = _uploadedScene;
            // Shift+click takes the whole actor (all of a road, all of a foliage actor) instead of the one piece under the cursor;
            // a part of a Blueprint is picked on its own only in part mode (or with Alt), a spawn part (its pin or the item it
            // spawns: a house's drill press, a car shop's vehicle box) always, a door's leaf never (ScenePlacement.PickKey).
            var instance = !_pickWhole && result?.Node.Tag is ScenePlacement placed ? placed.PickKey(_pickPart) : null;
            if (!_pickToggle && SelectedId != 0 && DateTime.UtcNow < SelectionHoldUntil)
            {
                // A fresh duplicate stands on its source: the click (and a drag from it) keeps taking the copy.
                id = SelectedId;
                instance = SelectedInstance;
                drawn = Scene; // the page's numbering
            }
            if (_pickToggle)
            {
                _pickToggle = false;
                if (id != 0)
                {
                    Dispatcher.UIThread.Post(() => DeliverPick(drawn, id, instance, toggle: true));
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
                _grabScene = drawn;
            }

            Dispatcher.UIThread.Post(() => DeliverPick(drawn, id, instance, toggle: false));
            }
        }

        if (_pendingBrush is { } brush)
        {
            SceneRenderer renderer = _renderer;
            RenderTarget pickTarget = _target;
            var brushed = _level;
            ResolveBrush(() => brushed is null ? null : renderer.Pick(pickTarget, brushed.Scene, _camera, brush.X, brush.Y)?.WorldPosition);
            RequestNextFrameRendering(); // the circle is drawn where the cursor now is
        }

        // The ground near the camera from every landscape sample, as the game draws it there (TerrainDetailRings): rebuilt on
        // a worker as the camera moves, sent a few milliseconds a frame.
        _terrainDetail.Settings = TerrainDetailSettings.For(Quality);
        if (_terrainDetail.Update(_renderer, _level, _camera.Position, TerrainDetailBudgetMs))
        {
            RequestNextFrameRendering();
        }

        RenderStats stats;
        RenderStats? farStats = null;
        _renderer.Time = (float)(now % 3600.0); // the stand-ins' glint; wrapped so a long session keeps float precision
        SyncViewSettings(_renderer);          // r33-mapux: push the Wireframe toggle onto the renderer
        detailSettings = _renderer.Settings;  // keep the synced wireframe for the backdrop and detail passes
        var renderStart = Stopwatch.GetTimestamp();
        _gpuTimer?.Begin();
        if (ShownBackdrop is { } islandScene)
        {
            // The island with the game's far view out to the chosen sectors; the detail levels over it (hidden by Show backdrop).
            _renderer.Settings = BackdropSettings(detailSettings);
            farStats = _renderer.Render(_target, islandScene.Scene, _camera);
            _renderer.Settings = detailSettings;
        }

        if (_level is { } scene)
        {
            stats = _renderer.Render(_target, scene.Scene, _camera, clear: farStats is null);
        }
        else
        {
            stats = farStats ?? _renderer.Render(_target, EmptyScene, _camera);
            farStats = null;
        }

        var settling = SteadyFrame(); // foliage steadied over frames while the camera moves (LevelViewport.Motion.cs)
        _gpuTimer?.End();
        var renderMs = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
        _cpuMs = _cpuMs <= 0 ? renderMs : (_cpuMs * 0.9) + (renderMs * 0.1);
        _budget.Add(renderMs, _gpuTimer?.LastMs ?? double.NaN, now);

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

        if (_pendingCapture is { } capture)
        {
            _pendingCapture = null;
            var done = _captureResult;
            try
            {
                done?.TrySetResult(ViewportCapture.RenderOffscreen(_renderer, _camera, ShownBackdrop?.Scene, _level?.Scene, capture.Width, capture.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                done?.TrySetException(ex);
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

        // A turn still being eased in, or a steadied picture still fading to the newest frame, keeps the frames coming too.
        var continuous = moving || _looking || _panning || _look.Settling || settling;
        _wasContinuous = moving || _looking || _panning;
        PaceFrames(continuous);

        if (now - _lastInfoSeconds > 0.25)
        {
            // A few updates a second, and the text is only made then: posting a new one every frame re-lays out the HUD.
            var info = FrameText(stats, farStats, now - _lastInfoSeconds);
            _lastInfoSeconds = now;
            if (info != _lastInfo)
            {
                _lastInfo = info;
                Dispatcher.UIThread.Post(() => FrameInfo = info, DispatcherPriority.Background);
            }
        }

        if (continuous)
        {
            RequestNextFrameRendering();
        }
        else if (_shimmerShown && _level is not null && !_shimmerQueued)
        {
            // Stand-ins keep glinting at rest: a frame every 40 ms (25 fps) is smooth for a 1.5 s pulse and cheap. Only while
            // one is shown: hidden spawn layers left the view drawing 25 times a second at rest (audit F271/F456).
            _shimmerQueued = true;
            DispatcherTimer.RunOnce(() =>
            {
                _shimmerQueued = false;
                RequestNextFrameRendering();
            }, TimeSpan.FromMilliseconds(40));
        }
    }

    private bool _shimmerQueued;
    private bool _shimmerShown;
    private int _shimmerNodes = -1;

    private static readonly Rendering.SceneGraph.Scene EmptyScene = new();

    /// <summary>True when <paramref name="node"/> is shown and draws a glinting stand-in (<c>GpuMesh.Shimmer</c>).</summary>
    private bool Shimmers(SceneNode node) =>
        node.Visible && node.Mesh is { } mesh && _renderer is not null && _renderer.Meshes.TryGetValue(mesh.Id, out var gpu) && gpu.Shimmer;

    /// <summary>
    /// The detail scene's settings for the quality, hour, light and water: the same object until one of them changes
    /// (<see cref="FrameBudget.Apply"/> lowers them while Smooth needs it).
    /// </summary>
    private RenderSettings DetailSettings(SceneRenderer renderer)
    {
        var key = (Quality, (float)TimeOfDay, Lighting, AnimateWater);
        if (_detailSettings is null || key != _detailKey)
        {
            var quality = RenderQualityProfile.For(key.Quality);
            _detailKey = key;
            _detailSettings = (_detailSettings ?? renderer.Settings) with
            {
                ObjectDrawDistance = quality.ObjectDistanceCm, LodBias = quality.LodBias, CullPixelSize = quality.CullPixels,
                Shadows = key.Quality != RenderQuality.Performance, // sun shadows cost a second geometry pass near the camera
                ShadowDistance = quality.ShadowDistanceCm, TimeOfDay = key.Item2, Lighting = key.Lighting, AnimateWater = key.AnimateWater,
            };
        }

        return _detailSettings;
    }

    /// <summary>
    /// The island backdrop's settings: <paramref name="detail"/> with the game's far models drawn out to
    /// <see cref="RenderDistance"/> sectors (one less at Smooth's last step); the same object while nothing changes.
    /// </summary>
    private RenderSettings BackdropSettings(RenderSettings detail)
    {
        var (sectors, sectorCm) = (_budget.RenderDistance(RenderDistance), SectorSizeCm > 0f ? SectorSizeCm : DefaultSectorSizeCm);
        if (_backdropSettings is null || !ReferenceEquals(_backdropKey.Detail, detail) || _backdropKey.Sectors != sectors || _backdropKey.SectorCm != sectorCm)
        {
            _backdropKey = (detail, sectors, sectorCm);
            _backdropSettings = FrameBudget.Backdrop(detail, sectors, sectorCm);
        }

        return _backdropSettings;
    }

    /// <summary>
    /// The frame readout: placements, what was drawn (backdrop and levels), draw calls, CPU and GPU time of the draw calls,
    /// frame rate, the collector's pauses per second since the last readout and its full collections since the view opened,
    /// and how far Smooth lowered the view (owner: a line to screenshot when it stutters).
    /// </summary>
    private string FrameText(RenderStats stats, RenderStats? farStats, double seconds)
    {
        var pause = GC.GetTotalPauseDuration();
        var pausePerSecond = seconds > 0 ? (pause - _hudPause).TotalMilliseconds / seconds : 0.0;
        _hudPause = pause;
        if (_level is not { } level)
        {
            return Loc.T("Map.NoLevelLoaded");
        }

        var culture = CultureInfo.CurrentCulture;
        var gpu = _gpuTimer?.LastMs is { } gpuMs && double.IsFinite(gpuMs) ? gpuMs.ToString("0.0", culture) : "-";
        var fps = _frameMs > 0 ? (1000.0 / _frameMs).ToString("0", culture) : "-";
        var text = Loc.F("Map.Hud.Stats", level.PlacedCount, stats.Instances + (farStats?.Instances ?? 0), stats.Triangles + (farStats?.Triangles ?? 0),
            stats.Batches + (farStats?.Batches ?? 0), _cpuMs, gpu, fps, pausePerSecond, GC.CollectionCount(2) - _hudGen2);
        return _budget.Level > 0 ? text + " · " + Loc.F("Map.Hud.Smoothed", _budget.Level) : text;
    }

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
        if (change.Property == GroundOutlinesProperty || change.Property == TerrainHolesProperty || change.Property == HoleModeProperty)
        {
            RequestNextFrameRendering(); // the hole's outline moved, or a hole was cut or filled
            return;
        }

        if (change.Property == BrushModeProperty || change.Property == BrushRadiusProperty || change.Property == OrientationProperty)
        {
            if (!BrushMode)
            {
                EndBrushStroke();
            }

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
            EndBrushStroke(now: true);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetDrone(false);
        UnlockCursor();
        EndBrushStroke(now: true);
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
            EndBrushStroke(now: true); // the last stroke's end, if it still waits for its frame, comes first
            _brushing = true;
            _brushPointer = e.Pointer;
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
            _dragScene = Scene; // SelectedId is in the page's numbering
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
        if (_brushing && e.GetCurrentPoint(this).Properties is { IsLeftButtonPressed: false } held
            && (held.IsRightButtonPressed || held.IsMiddleButtonPressed || held.IsXButton1Pressed || held.IsXButton2Pressed))
        {
            EndBrushStroke(); // the left button came up while another was held: no release is raised for it
        }

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
            _look.Add((float)delta.X, (float)delta.Y); // turned once per frame (LookSmoother), not per mouse report
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
        if (_brushing && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Whatever button the release names: after a chord it can be the other one (audit F115).
            EndBrushStroke();
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
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndBrushStroke(); // another control or the system took the mouse: its release will not come here
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

        if (TryHoleWheel(notches))
        {
            return; // the hole tool: the wheel sizes the hole
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
        if (HandleViewKey(e))
        {
            e.Handled = true; // Esc cancelling a drag, the numpad's views, wireframe (LevelViewport.Views.cs)
        }
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
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
        EndBrushStroke(); // nor a button released there keep planting
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
            ResolveBrush(NoPick); // no OpenGL (headless): nothing to pick, the ground under the cursor still counts
        }

        RequestNextFrameRendering();
    }

    private static readonly Func<Vector3?> NoPick = static () => null;

    /// <summary>
    /// Ends the brush stroke: <see cref="BrushStrokeEnded"/> once, after the last dab (audit F115: a second button, a lost
    /// capture or another app in front left the timer planting with no button held). <paramref name="now"/>: no frame
    /// will come for a dab still waiting for one (the view goes away), so it lands on the ground under the cursor now.
    /// </summary>
    private void EndBrushStroke(bool now = false)
    {
        var pointer = _brushPointer;
        _brushPointer = null;
        if (_brushing)
        {
            _brushing = false;
            _brushEndPending = true;
        }

        if (_brushEndPending && _pendingBrush is not { Paint: true })
        {
            _brushEndPending = false;
            PostBrushStrokeEnded();
        }
        else if (_brushEndPending && now)
        {
            ResolveBrush(NoPick); // raises that last dab, then the end
        }

        if (pointer?.Captured == this)
        {
            pointer.Capture(null); // after the flags: the capture-lost call it causes finds nothing left to end
        }
    }

    /// <summary>
    /// Places the brush circle for the queued cursor position and, while painting, raises <see cref="BrushPainted"/>
    /// from the stroke's previous centre to this one (else <see cref="BrushHovered"/>). <paramref name="pick"/> gives
    /// what is drawn under the cursor; it is only asked when the centre needs it.
    /// </summary>
    private void ResolveBrush(Func<Vector3?> pick)
    {
        if (_pendingBrush is not { } brush)
        {
            return;
        }

        _pendingBrush = null;
        SyncHoles();
        var (w, h) = PixelSize();
        var (origin, direction) = _camera.ScreenRay(brush.X, brush.Y, Math.Max(1, w), Math.Max(1, h));
        var centre = BrushCentre(origin, direction, pick);
        _brushPointGl = centre;
        if (brush.Paint)
        {
            var stroke = (UeToGl.ToUePoint(_brushFromGl ?? centre), UeToGl.ToUePoint(centre));
            _brushFromGl = centre;
            Dispatcher.UIThread.Post(() => BrushPainted?.Invoke(this, stroke));
            if (_brushEndPending)
            {
                _brushEndPending = false;
                PostBrushStrokeEnded();
            }
        }
        else
        {
            var at = UeToGl.ToUePoint(centre);
            Dispatcher.UIThread.Post(() => BrushHovered?.Invoke(this, at));
        }
    }

    /// <summary>Raises <see cref="BrushStrokeEnded"/>, then <see cref="BrushHovered"/> where the stroke stopped (the preview takes over there).</summary>
    private void PostBrushStrokeEnded()
    {
        var at = _brushPointGl is { } gl ? UeToGl.ToUePoint(gl) : (FVector?)null;
        Dispatcher.UIThread.Post(() =>
        {
            BrushStrokeEnded?.Invoke(this, EventArgs.Empty);
            if (at is { } stopped)
            {
                BrushHovered?.Invoke(this, stopped);
            }
        });
    }

    /// <summary>
    /// The brush circle's centre (GL) for a mouse ray (owner: "everything inside the circle", not only what the cursor is
    /// on): what the pick hit, else the terrain under the ray (with <see cref="BrushOnGround"/> the terrain first), else
    /// the level plane of the last ground the brush touched, at most <see cref="MaxPickDistanceLarge"/> away across the
    /// ground (towards the sky: that far straight ahead), so the circle follows the cursor over empty ground and the sky too.
    /// </summary>
    private Vector3 BrushCentre(Vector3 origin, Vector3 direction, Func<Vector3?> pick)
    {
        Vector3? Terrain() => (Scene?.HeightField?.RaycastGl(origin, direction, _camera.FarPlane) ?? HitBackdrop?.HeightField?.RaycastGl(origin, direction, _camera.FarPlane)) is { } ground
            ? UeToGl.Point(new FVector(ground.Position.X, ground.Position.Y, ground.Position.Z))
            : null;

        if (BrushHit(pick, Terrain, BrushOnGround) is { } hit)
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

    /// <summary>
    /// What the brush stands on: the pick (a canopy, a roof, the ground) before the terrain, or, <paramref name="onGround"/>,
    /// the terrain before the pick (planting: the cursor over a tree 20 m up still means the ground under it). Each source
    /// is asked only when needed: the GPU pick is skipped wherever the terrain answers.
    /// </summary>
    internal static Vector3? BrushHit(Func<Vector3?> pick, Func<Vector3?> terrain, bool onGround) =>
        onGround ? terrain() ?? pick() : pick() ?? terrain();

    private const float SpotMarkRadius = 35f;
    private const float SpotMarkLift = 20f;
    private const float RingLift = 30f;

    /// <summary>Colour of a spot the brush will plant.</summary>
    internal static readonly Vector4 SpotPlants = new(0.3f, 1f, 0.45f, 1f);

    /// <summary>Colour of a spot the brush leaves out (too close, a road, water...).</summary>
    internal static readonly Vector4 SpotLeftOut = new(1f, 0.35f, 0.3f, 1f);

    /// <summary>Overlay lines for the brush's planned spots: a small ring 20 cm above each, green where it plants, red where it does not.</summary>
    internal static IEnumerable<OverlayLine> SpotMarks(IReadOnlyList<(FVector At, bool Plants)> spots)
    {
        foreach (var (at, plants) in spots)
        {
            var ring = GizmoMath.RingPoints(UeToGl.Point(at) + new Vector3(0f, SpotMarkLift, 0f), SpotMarkRadius, 8);
            for (var i = 1; i < ring.Length; i++)
            {
                yield return new OverlayLine(ring[i - 1], ring[i], plants ? SpotPlants : SpotLeftOut);
            }
        }
    }

    /// <summary>
    /// The brush circle laid over the ground: each point 30 cm above the terrain under it (<paramref name="ground"/>: UE
    /// X, Y to height, null off the terrain, where the point stays at the centre's height).
    /// </summary>
    internal static Vector3[] DrapedRing(Vector3 centreGl, float radiusCm, Func<float, float, float?> ground, int segments = 64)
    {
        var ring = GizmoMath.RingPoints(centreGl, radiusCm, segments);
        for (var i = 0; i < ring.Length; i++)
        {
            var ue = UeToGl.ToUePoint(ring[i]);
            if (ground(ue.X, ue.Y) is { } z)
            {
                ring[i].Y = z + RingLift;
            }
        }

        return ring;
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

    /// <summary>
    /// Hands a pick of <paramref name="drawn"/> (the scene on screen) to the selection, or with <paramref name="toggle"/> to
    /// the multi-selection. While a new scene is staged the page already reads ids in <see cref="Scene"/>'s numbering: a
    /// pick on a level that is going away is dropped, never read as the actor that took its slot (F010).
    /// </summary>
    internal void DeliverPick(PreparedLevelScene? drawn, uint id, InstanceKey? instance, bool toggle)
    {
        if (!LevelScenePreparer.SameActorIn(drawn, Scene, id))
        {
            return;
        }

        if (toggle)
        {
            PickToggled?.Invoke(this, (id, instance));
            return;
        }

        // Instance first: the view model then shows that one tree or rock, not the foliage actor that holds thousands.
        SelectedInstance = instance;
        SelectedId = id;
        ActorPicked?.Invoke(this, id);
    }

    /// <summary>Starts a free drag once the press's pick has selected an actor and its root transform is known.</summary>
    private bool TryBeginFreeDrag()
    {
        // The grab is in the numbering of the scene it was picked in: the same number in the page's may be another actor.
        if (_grabId == 0 || SelectedId != _grabId || SelectedInstance != _grabInstance || SelectedRootWorld is not { } root
            || !LevelScenePreparer.SameActorIn(_grabScene, Scene, _grabId))
        {
            return false;
        }

        PivotOf(root); // the press may have just selected it: place its pivot before the drag freezes it
        _dragScene = Scene;
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
        // A scene shown during the drag whose level took the dragged one's slot: the object snaps back instead of moving another.
        if (preview is not null && !preview.Root.Equals(_dragStartRoot, 1e-3f) && LevelScenePreparer.SameActorIn(_dragScene, Scene, preview.Id))
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

            EndBrushStroke();
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

        if (_brushing && _brushPointer is { Type: PointerType.Mouse } && Up(0x01, 0x02)) // both, as for the look: swapped buttons
        {
            EndBrushStroke();
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
        var (x, y) = (((clip.X / clip.W) * 0.5 + 0.5) * w / scaling, (0.5 - ((clip.Y / clip.W) * 0.5)) * h / scaling);
        return double.IsFinite(x) && double.IsFinite(y) ? new Point(x, y) : null; // NaN in, nothing to point at
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
