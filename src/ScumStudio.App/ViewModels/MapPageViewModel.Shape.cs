using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The shape of the selected object (owner request): bend a wall, bridge or road piece left or right without it coming
/// apart (<see cref="BendActorOp"/>, drawn by the game as a spline mesh), and make anything longer, wider, taller or
/// bigger (its scale on its own axes; one tree or rock too). Sliders preview at once; the edit is journaled when a
/// slider is let go, or half a second after a key or click, bend and scale together as one undo step.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>Bend, degrees end to end (negative left, positive right).</summary>
    [ObservableProperty]
    private double _shapeBend;

    /// <summary>Scale along the object's length (its longer horizontal axis).</summary>
    [ObservableProperty]
    private double _shapeLength = 1;

    /// <summary>Scale across it.</summary>
    [ObservableProperty]
    private double _shapeWidth = 1;

    /// <summary>Scale up.</summary>
    [ObservableProperty]
    private double _shapeHeight = 1;

    /// <summary>All three together (their geometric mean); moving it scales the three by the same factor.</summary>
    [ObservableProperty]
    private double _shapeSize = 1;

    /// <summary>True when the selection has a shape to edit (not terrain, foliage containers or a road segment).</summary>
    [ObservableProperty]
    private bool _hasShape;

    /// <summary>True when the selected object can be drawn bent in the game.</summary>
    [ObservableProperty]
    private bool _canBend;

    /// <summary>Why the object cannot bend (or that it is being checked); empty when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBendNote))]
    private string _bendNote = string.Empty;

    /// <summary>Bent actors drawn by the viewport: id → the pieces of its curve in component space (see <see cref="BendShape.Pieces"/>).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<uint, IReadOnlyList<SplineMeshParams>> _bends = new Dictionary<uint, IReadOnlyList<SplineMeshParams>>();

    /// <summary>Road, rail and bridge pieces drawn with a new curve: piece → curve in component space.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<InstanceKey, SplineMeshParams> _segmentBends = new Dictionary<InstanceKey, SplineMeshParams>();

    /// <summary>First handle's sideways push, metres (positive right).</summary>
    [ObservableProperty]
    private double _shapeSway1;

    /// <summary>Second handle's sideways push, metres.</summary>
    [ObservableProperty]
    private double _shapeSway2;

    /// <summary>How far a handle may be pushed either way, metres.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShapeSwayMin))]
    private double _shapeSwayMax = 10;

    /// <summary>True when the selection's handles can be pushed (a bendable object or a road/bridge piece).</summary>
    [ObservableProperty]
    private bool _canSway;

    /// <summary>True when length, width, height and size apply (not to a road piece, whose ends are tied to its neighbours).</summary>
    [ObservableProperty]
    private bool _canScale = true;

    /// <summary>The curve and its handles for the viewport to draw and drag, or null.</summary>
    [ObservableProperty]
    private ShapeHandleInfo? _shapeHandles;

    /// <summary>How much longer the selected object's legs are, metres (its foot lower, its top where it is).</summary>
    [ObservableProperty]
    private double _shapeLegs;

    /// <summary>Where the viewport draws the legs arrow (the foot of the selected object, UE world), or null.</summary>
    [ObservableProperty]
    private FVector? _legHandle;

    /// <summary><see cref="ShapeLegs"/> as text.</summary>
    public string ShapeLegsText => string.Create(CultureInfo.CurrentCulture, $"{ShapeLegs:0.0} m");

    partial void OnShapeLegsChanged(double value) => ShapeChanged(nameof(ShapeLegsText));

    private double? _legsDragFrom;

    /// <summary>The legs arrow dragged in the viewport: <paramref name="downCm"/> lower than where the drag began.</summary>
    public void DragLegs(float downCm)
    {
        _legsDragFrom ??= ShapeLegs;
        ShapeLegs = Math.Clamp(_legsDragFrom.Value + (downCm / 100.0), 0, 300);
    }

    /// <summary>Lean towards its front or back, degrees (the object's pitch; owner: a house tilted like a slope, up to 90° either way).</summary>
    [ObservableProperty]
    private double _shapePitch;

    /// <summary>Lean to its left or right, degrees (the object's roll).</summary>
    [ObservableProperty]
    private double _shapeRoll;

    /// <summary>Which way it faces, degrees (the object's yaw).</summary>
    [ObservableProperty]
    private double _shapeYaw;

    /// <summary><see cref="ShapePitch"/> as text.</summary>
    public string ShapePitchText => string.Create(CultureInfo.CurrentCulture, $"{ShapePitch:0}°");

    /// <summary><see cref="ShapeRoll"/> as text.</summary>
    public string ShapeRollText => string.Create(CultureInfo.CurrentCulture, $"{ShapeRoll:0}°");

    /// <summary><see cref="ShapeYaw"/> as text.</summary>
    public string ShapeYawText => string.Create(CultureInfo.CurrentCulture, $"{ShapeYaw:0}°");

    partial void OnShapePitchChanged(double value) => ShapeChanged(nameof(ShapePitchText));

    partial void OnShapeRollChanged(double value) => ShapeChanged(nameof(ShapeRollText));

    partial void OnShapeYawChanged(double value) => ShapeChanged(nameof(ShapeYawText));

    private FRotator ShapeRotation() => new((float)ShapePitch, (float)ShapeYaw, (float)ShapeRoll);

    /// <summary>The selected single object's scale handles (longer, shorter, bigger, smaller) when it has no curve handles.</summary>
    [ObservableProperty]
    private ScaleHandleInfo? _scaleHandles;

    // The ends' edits (moved, turned, widened) of the selected piece, and where a scale handle put the object (relative place).
    private SplineEnd _shapeStart;
    private SplineEnd _shapeEnd;
    private FVector? _shapePlace;

    /// <summary><see cref="ShapeSwayMax"/> to the left.</summary>
    public double ShapeSwayMin => -ShapeSwayMax;

    /// <summary><see cref="ShapeSway1"/> as text.</summary>
    public string ShapeSway1Text => string.Create(CultureInfo.CurrentCulture, $"{ShapeSway1:0.0} m");

    /// <summary><see cref="ShapeSway2"/> as text.</summary>
    public string ShapeSway2Text => string.Create(CultureInfo.CurrentCulture, $"{ShapeSway2:0.0} m");

    partial void OnShapeSway1Changed(double value) => ShapeChanged(nameof(ShapeSway1Text));

    partial void OnShapeSway2Changed(double value) => ShapeChanged(nameof(ShapeSway2Text));

    /// <summary>An end or corner handle dragged in the viewport: that end's new edit (welded: the push next to it goes).</summary>
    public void DragShapeEnd(bool atEnd, SplineEnd value, bool welded)
    {
        if (atEnd)
        {
            _shapeEnd = value;
        }
        else
        {
            _shapeStart = value;
        }

        if (welded)
        {
            _syncingShape = true;
            try
            {
                if (atEnd)
                {
                    ShapeSway2 = 0;
                }
                else
                {
                    ShapeSway1 = 0;
                }
            }
            finally
            {
                _syncingShape = false;
            }
        }

        ShapeChanged(nameof(ShapeBendText));
    }

    /// <summary>A scale handle dragged in the viewport: the object's new world transform (scale, and place so one end stays).</summary>
    public void DragScale(FTransform world)
    {
        if (_shapeItem is not { } item)
        {
            return;
        }

        var relative = _shapeInstance is { Instance: not null } sel
            ? TransformValue.FromTransform(world.GetRelativeTransform(SpaceOf(sel)))
            : RelativeOf(item, world);
        _syncingShape = true;
        try
        {
            var s = relative.Scale;
            ShapeLength = _shapeAlongY ? s.Y : s.X;
            ShapeWidth = _shapeAlongY ? s.X : s.Y;
            ShapeHeight = s.Z;
            ShapeSize = Math.Cbrt(Math.Abs(ShapeLength * ShapeWidth * ShapeHeight));
        }
        finally
        {
            _syncingShape = false;
        }

        _shapePlace = relative.Location;
        ShapeChanged(nameof(ShapeLengthText));
    }

    /// <summary>A handle dragged in the viewport: its push in cm (index 0 = first handle).</summary>
    public void DragShapeHandle(int index, float swayCm)
    {
        var metres = Math.Clamp(swayCm / 100.0, ShapeSwayMin, ShapeSwayMax);
        if (index == 0)
        {
            ShapeSway1 = metres;
        }
        else
        {
            ShapeSway2 = metres;
        }
    }

    private bool _syncingShape;
    private bool _shapeAlongY;
    private bool _shapePending;
    private int _shapeVersion;
    private int _bendCheck;
    private ActorItemViewModel? _shapeItem;
    private SelectedInstance? _shapeInstance;
    private BendSupport? _bendSupport;
    private AssetCatalog? _bendCatalog;

    /// <summary>True while a shape slider is held: the edit is journaled when it is let go.</summary>
    public bool IsShapeDragging { get; set; }

    /// <summary>True when <see cref="BendNote"/> has something to say.</summary>
    public bool HasBendNote => BendNote.Length > 0;

    /// <summary><see cref="ShapeBend"/> as text.</summary>
    public string ShapeBendText => string.Create(CultureInfo.CurrentCulture, $"{ShapeBend:0}°");

    /// <summary><see cref="ShapeLength"/> as a percentage.</summary>
    public string ShapeLengthText => Percent(ShapeLength);

    /// <summary><see cref="ShapeWidth"/> as a percentage.</summary>
    public string ShapeWidthText => Percent(ShapeWidth);

    /// <summary><see cref="ShapeHeight"/> as a percentage.</summary>
    public string ShapeHeightText => Percent(ShapeHeight);

    /// <summary><see cref="ShapeSize"/> as a percentage.</summary>
    public string ShapeSizeText => Percent(ShapeSize);

    private static string Percent(double value) => string.Create(CultureInfo.CurrentCulture, $"{value * 100:0}%");

    partial void OnShapeBendChanged(double value) => ShapeChanged(nameof(ShapeBendText));

    partial void OnShapeLengthChanged(double value) => ShapeChanged(nameof(ShapeLengthText));

    partial void OnShapeWidthChanged(double value) => ShapeChanged(nameof(ShapeWidthText));

    partial void OnShapeHeightChanged(double value) => ShapeChanged(nameof(ShapeHeightText));

    partial void OnShapeSizeChanged(double oldValue, double newValue)
    {
        OnPropertyChanged(nameof(ShapeSizeText));
        if (_syncingShape || oldValue <= 0)
        {
            return;
        }

        var factor = newValue / oldValue;
        _syncingShape = true;
        try
        {
            ShapeLength *= factor;
            ShapeWidth *= factor;
            ShapeHeight *= factor;
        }
        finally
        {
            _syncingShape = false;
        }

        ShapeChanged(nameof(ShapeLengthText));
    }

    private void ShapeChanged(string textProperty)
    {
        OnPropertyChanged(textProperty);
        if (_syncingShape)
        {
            return;
        }

        if (textProperty is not (nameof(ShapeBendText) or nameof(ShapeSway1Text) or nameof(ShapeSway2Text) or nameof(ShapeLegsText)
            or nameof(ShapePitchText) or nameof(ShapeRollText) or nameof(ShapeYawText)))
        {
            _syncingShape = true;
            ShapeSize = Math.Cbrt(ShapeLength * ShapeWidth * ShapeHeight);
            _syncingShape = false;
        }

        _shapePending = true;
        PreviewShape();
        var version = ++_shapeVersion;
        _ = Task.Delay(500).ContinueWith(_ => _services.Dispatcher.Post(() =>
        {
            if (version == _shapeVersion && !IsShapeDragging)
            {
                CommitShape();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Journals the shape the sliders show (called when a slider is let go; otherwise after a short pause).</summary>
    public void CommitShape()
    {
        _legsDragFrom = null;
        if (PendingShape() is { } change)
        {
            Commit(change);
        }
    }

    /// <summary>Straight again, at the size the level (or the edit that added it) gave it.</summary>
    [RelayCommand]
    private void ResetShape()
    {
        var original = OriginalScale();
        _syncingShape = true;
        try
        {
            ShapeBend = 0;
            ShapeSway1 = ShapeSway2 = 0;
            ShapeLegs = 0;
            _shapeStart = _shapeEnd = default;
            _shapePlace = null;
            ShapeLength = _shapeAlongY ? original.Y : original.X;
            ShapeWidth = _shapeAlongY ? original.X : original.Y;
            ShapeHeight = original.Z;
            var stood = OriginalRotation().GetNormalized(); // upright again; which way it faces stays
            ShapePitch = stood.Pitch;
            ShapeRoll = stood.Roll;
            ShapeSize = Math.Cbrt(Math.Abs(ShapeLength * ShapeWidth * ShapeHeight));
        }
        finally
        {
            _syncingShape = false;
        }

        _shapePending = true;
        PreviewShape();
        CommitShape();
    }

    /// <summary>Fills the sliders from the selection (called whenever the selection is shown).</summary>
    private void LoadShape(ActorItemViewModel? item)
    {
        if (PendingShape() is { } pending)
        {
            // A change still waiting belongs to the object it was made on: journal it once this selection change is done.
            _shapePending = false;
            _services.Dispatcher.Post(() => Commit(pending));
        }

        var sel = SelectedInstanceInfo();
        _shapeItem = item;
        _shapeInstance = sel;
        _shapeStart = _shapeEnd = default;
        _shapePlace = null;
        _syncingShape = true;
        try
        {
            var segment = sel is { Instance: null, Component.SplineMesh: not null } ? sel : null;
            HasShape = item is not null && !IsImmovable(item) && (sel is null || sel.Instance is not null || segment is not null)
                || segment is not null;
            if (!HasShape)
            {
                CanBend = CanSway = false;
                BendNote = string.Empty;
                ShapeHandles = null;
                ScaleHandles = null;
                LegHandle = null;
                return;
            }

            if (segment is not null)
            {
                // A road, rail or bridge piece: its two handles push it sideways, its ends stay on its neighbours.
                var piece = _services.Projects.Current?.State.GetSegmentShape(item!.Reference, segment.Component.Name) ?? default;
                ShapeSwayMax = Math.Max(1, Math.Round(SplineSway.Span(segment.Component.SplineMesh!) / 100.0, 1));
                ShapeSway1 = piece.Sway1 / 100.0;
                ShapeSway2 = piece.Sway2 / 100.0;
                ShapeLegs = 0;
                (_shapeStart, _shapeEnd) = (piece.Start, piece.End);
                ShapeBend = 0;
                ShapePitch = ShapeRoll = ShapeYaw = 0;
                CanScale = false;
                CanSway = true;
                CanBend = false;
                BendNote = string.Empty;
                _shapeAlongY = false;
                UpdateShapeHandles();
                return;
            }

            CanScale = true;
            var t = sel is not null ? CurrentInstanceTransform(sel) : CurrentRootTransform(item!);
            _shapeAlongY = MeshBounds(sel is not null ? MeshOf(sel) : item!.Actor.StaticMeshPath) is { } b
                && b.Size.Y * MathF.Abs(t.Scale.Y) > b.Size.X * MathF.Abs(t.Scale.X);
            ShapeLength = _shapeAlongY ? t.Scale.Y : t.Scale.X;
            ShapeWidth = _shapeAlongY ? t.Scale.X : t.Scale.Y;
            ShapeHeight = t.Scale.Z;
            ShapeSize = Math.Cbrt(Math.Abs(ShapeLength * ShapeWidth * ShapeHeight));
            var turned = t.Rotation.GetNormalized();
            (ShapePitch, ShapeRoll, ShapeYaw) = (turned.Pitch, turned.Roll, turned.Yaw);
            var shape = sel is null ? _services.Projects.Current?.State.GetBendValue(item!.Reference) ?? default : default;
            ShapeBend = shape.Degrees;
            ShapeSway1 = shape.Sway1 / 100.0;
            ShapeSway2 = shape.Sway2 / 100.0;
            ShapeLegs = shape.Legs / 100.0;
            (_shapeStart, _shapeEnd) = (shape.Start, shape.End);
            if (MeshBounds(item!.Actor.StaticMeshPath) is { } bounds && sel is null)
            {
                ShapeSwayMax = Math.Max(1, Math.Round((_shapeAlongY ? bounds.Size.Y * MathF.Abs(t.Scale.Y) : bounds.Size.X * MathF.Abs(t.Scale.X)) / 100.0, 1));
            }
        }
        finally
        {
            _syncingShape = false;
        }

        CheckBend(item!, sel);
        UpdateShapeHandles();
    }

    /// <summary>
    /// The handles the viewport shows for the selection: the curve's (push, ends, corners) for a piece that bends, else
    /// scale handles on a single object (a tree, a rock, a mesh actor), else none.
    /// </summary>
    private void UpdateShapeHandles()
    {
        ShapeHandles = CurveHandles();
        ScaleHandles = ShapeHandles is null && HasShape && CanScale ? ScaleHandlesOf() : null;
        LegHandle = LegHandleOf();
    }

    /// <summary>The foot of the selected object (the middle of its bottom, legs included), for the legs arrow; null unless it can bend.</summary>
    private FVector? LegHandleOf()
    {
        if (_shapeItem is not { } item || !CanBend || _shapeInstance is not null || MeshBounds(item.Actor.StaticMeshPath) is not { } bounds)
        {
            return null;
        }

        var scale = ShapeScale();
        var root = RootWorldOf(item, CurrentRootTransform(item) with { Scale = FVector.One });
        var c = bounds.Center;
        return root.TransformPosition(new FVector(c.X * scale.X, c.Y * scale.Y, (bounds.Min.Z * MathF.Abs(scale.Z)) - ((float)ShapeLegs * 100f)));
    }

    private ShapeHandleInfo? CurveHandles()
    {
        if (_shapeItem is not { } item || !CanSway || ShapeLegs != 0)
        {
            return null; // longer legs stand the object up along a vertical spline: no curve to push
        }

        var (sway1, sway2) = ((float)ShapeSway1 * 100f, (float)ShapeSway2 * 100f);
        if (_shapeInstance is { Instance: null, Component: { SplineMesh: { } spline } component })
        {
            var straight = component.StaticMeshPath is { } mesh && PreparedScene?.SplineSources.TryGetValue(mesh, out var source) == true ? source.Mesh.Bounds : BoundingBox.Empty;
            return new ShapeHandleInfo(component.WorldTransform, spline, sway1, sway2, straight, _shapeStart, _shapeEnd);
        }

        if (_shapeInstance is null && MeshBounds(item.Actor.StaticMeshPath) is { } bounds)
        {
            var root = RootWorldOf(item, CurrentRootTransform(item) with { Scale = FVector.One });
            return new ShapeHandleInfo(root, BendShape.For(bounds, ShapeScale(), (float)ShapeBend), sway1, sway2, bounds, _shapeStart, _shapeEnd);
        }

        return null;
    }

    /// <summary>Scale handles for the selected mesh actor or instance at the size the sliders show, or null.</summary>
    private ScaleHandleInfo? ScaleHandlesOf()
    {
        if (_shapeItem is not { } item)
        {
            return null;
        }

        if (_shapeInstance is { Instance: not null } sel)
        {
            var current = CurrentInstanceTransform(sel);
            var value = current with { Scale = ShapeScale(), Location = _shapePlace ?? current.Location, Rotation = ShapeRotation() };
            return MeshBounds(MeshOf(sel)) is { } b ? new ScaleHandleInfo(value.ToTransform() * SpaceOf(sel), b) : null;
        }

        // A mesh actor attached to another (a bridge's fence) scales too: its place stays relative to its parent.
        if (_shapeInstance is null && item.Actor.Kind == ActorKind.StaticMeshActor && MeshBounds(item.Actor.StaticMeshPath) is { } bounds)
        {
            var current = CurrentRootTransform(item);
            return new ScaleHandleInfo(RootWorldOf(item, current with { Scale = ShapeScale(), Location = _shapePlace ?? current.Location, Rotation = ShapeRotation() }), bounds);
        }

        return null;
    }

    /// <summary>Finds out (on a worker) whether the game can draw the object bent: one mesh, and materials made for spline meshes.</summary>
    private void CheckBend(ActorItemViewModel item, SelectedInstance? sel)
    {
        var check = ++_bendCheck;
        CanBend = CanSway = false;
        if (sel is not null)
        {
            BendNote = Loc.T("Map.Shape.NoBendInstance");
            return;
        }

        if (!IsSingleMesh(item) || item.Actor.StaticMeshPath is not { } mesh)
        {
            BendNote = Loc.T("Map.Shape.NoBendKind");
            return;
        }

        if (IsBuilding(mesh))
        {
            // Owner: houses and churches do not bend like bridges and roads (bending a church froze the app); they tilt.
            BendNote = Loc.T("Map.Shape.NoBendBuilding");
            return;
        }

        if (_services.Workspace.Catalog is not { } catalog)
        {
            BendNote = string.Empty;
            return;
        }

        if (!ReferenceEquals(catalog, _bendCatalog))
        {
            _bendCatalog = catalog;
            _bendSupport = new BendSupport(catalog);
        }

        var support = _bendSupport!;
        BendNote = Loc.T("Map.Shape.Checking");
        _ = Task.Run(() => support.Describe(mesh)).ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (check != _bendCheck)
            {
                return;
            }

            var info = t.IsCompletedSuccessfully ? t.Result : null;
            CanBend = info is { FlatMaterial: null };
            CanSway = CanBend;
            UpdateShapeHandles();
            BendNote = info is null ? Loc.T("Map.Shape.NoBounds")
                : info.FlatMaterial is { } flat ? Loc.F("Map.Shape.NoBendMaterial", flat[(flat.LastIndexOf('/') + 1)..])
                : string.Empty;
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// A StaticMeshActor (pristine, added or a copy of one; attached to another too, like a bridge's fence, which is then
    /// written in its world place): what a SplineMeshActor can replace.
    /// </summary>
    private static bool IsSingleMesh(ActorItemViewModel item) =>
        item.Actor.Kind == ActorKind.StaticMeshActor && item.Actor.StaticMeshPath is not null;

    // ponytail: the game's building folder and a boxy footprint; a long wall piece kept in that folder still bends.
    /// <summary>True for a house, church or hall: a mesh of the game's building folder that is not long and thin.</summary>
    private bool IsBuilding(string mesh) =>
        mesh.Contains("/Models/Buildings/", StringComparison.OrdinalIgnoreCase)
        && (MeshBounds(mesh) is not { } b || MathF.Max(b.Size.X, b.Size.Y) < 4f * MathF.Min(b.Size.X, b.Size.Y));

    /// <summary>Bounds of a mesh the scene has (prepared with it or loaded later).</summary>
    private BoundingBox? MeshBounds(string? meshPath)
    {
        if (meshPath is null)
        {
            return null;
        }

        if (PreparedScene?.Meshes.TryGetValue(meshPath, out var asset) == true)
        {
            return asset.Mesh.Bounds;
        }

        return ExtraMeshes.FirstOrDefault(m => string.Equals(m.Asset.MeshPath, meshPath, StringComparison.OrdinalIgnoreCase))?.Asset.Mesh.Bounds;
    }

    /// <summary>The selection's scale before any edit: the level's, or the one it was added with.</summary>
    private FVector OriginalScale() => OriginalTransform().Scale;

    private FRotator OriginalRotation() => OriginalTransform().Rotation;

    /// <summary>The selection's transform before any edit: the level's, or the one it was added with.</summary>
    private TransformValue OriginalTransform()
    {
        if (_shapeInstance is { Instance: { } instance })
        {
            return TransformValue.FromTransform(instance.LocalTransform);
        }

        if (_shapeItem is not { } item)
        {
            return TransformValue.Identity;
        }

        return _services.Projects.Current?.State.AddedActors.GetValueOrDefault(item.Reference) switch
        {
            AddStaticMeshActorOp add => add.Transform,
            DuplicateActorOp copy => copy.Transform,
            AddBlueprintActorOp blueprint => blueprint.Transform,
            _ => item.Actor.Root?.Relative ?? TransformValue.Identity,
        };
    }

    private FVector ShapeScale() => _shapeAlongY
        ? new FVector((float)ShapeWidth, (float)ShapeLength, (float)ShapeHeight)
        : new FVector((float)ShapeLength, (float)ShapeWidth, (float)ShapeHeight);

    private ShapeChange? PendingShape() =>
        _shapePending && _shapeItem is { } item
            ? new ShapeChange(item, _shapeInstance, ShapeScale(), ShapeRotation(), (float)Math.Round(ShapeBend, 1), (float)Math.Round(ShapeSway1 * 100, 1), (float)Math.Round(ShapeSway2 * 100, 1),
                _shapeStart, _shapeEnd, _shapePlace, (float)Math.Round(ShapeLegs * 100, 1))
            : null;

    /// <summary>Shows the sliders' shape in the viewport without journaling it.</summary>
    private void PreviewShape()
    {
        if (_shapeItem is not { } item)
        {
            return;
        }

        var scale = ShapeScale();
        if (_shapeInstance is { Instance: null, Component: { SplineMesh: { } original } component })
        {
            var piece = InstanceKey.Of(item.SelectableId, component.Name, InstanceKey.Segment);
            var reshaped = new Dictionary<InstanceKey, SplineMeshParams>(SegmentBends);
            var shape = new BendValue(0f, (float)ShapeSway1 * 100f, (float)ShapeSway2 * 100f, _shapeStart, _shapeEnd);
            if (shape.IsStraight)
            {
                reshaped.Remove(piece);
            }
            else
            {
                reshaped[piece] = SplineEnds.Shape(original, shape.Sway1, shape.Sway2, shape.Start, shape.End);
            }

            SegmentBends = reshaped;
            UpdateShapeHandles();
            return;
        }

        if (_shapeInstance is { Instance: { } instance } sel)
        {
            var key = InstanceKey.Of(item.SelectableId, instance.ComponentName, instance.InstanceIndex);
            var current = CurrentInstanceTransform(sel);
            InstanceTransforms = new Dictionary<InstanceKey, FTransform>(InstanceTransforms)
            {
                [key] = (current with { Scale = scale, Location = _shapePlace ?? current.Location, Rotation = ShapeRotation() }).ToTransform() * SpaceOf(sel),
            };
            UpdateShapeHandles();
            return;
        }

        var bend = (float)ShapeBend;
        var curved = bend != 0 || ShapeSway1 != 0 || ShapeSway2 != 0 || !_shapeStart.IsNone || !_shapeEnd.IsNone || ShapeLegs != 0;
        var root = CurrentRootTransform(item);
        var value = root with { Scale = curved ? FVector.One : scale, Location = _shapePlace ?? root.Location, Rotation = ShapeRotation() }; // a bent actor carries its scale in the curve
        var world = RootWorldOf(item, value);
        if (item.IsAdded)
        {
            Clones = Clones.Select(c => c.Id == item.SelectableId ? c with { RootWorld = world } : c).ToList();
        }
        else
        {
            ActorTransforms = new Dictionary<uint, FTransform>(ActorTransforms) { [item.SelectableId] = world };
        }

        var bends = new Dictionary<uint, IReadOnlyList<SplineMeshParams>>(Bends);
        if (curved && MeshBounds(item.Actor.StaticMeshPath) is { } bounds)
        {
            bends[item.SelectableId] = BendShape.Pieces(bounds, scale, new BendValue(bend, (float)ShapeSway1 * 100f, (float)ShapeSway2 * 100f, _shapeStart, _shapeEnd, (float)ShapeLegs * 100f));
        }
        else
        {
            bends.Remove(item.SelectableId);
        }

        Bends = bends;
        UpdateShapeHandles();
    }

    private void Commit(ShapeChange change)
    {
        _shapePending = false;
        _shapeVersion++;
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Moves"));
            RefreshEdits();
            return;
        }

        _shapePlace = null;
        var ops = new List<EditOp>();
        try
        {
            if (change.Instance is { Instance: null, Component: { SplineMesh: not null } piece })
            {
                var old = project.State.GetSegmentShape(change.Item.Reference, piece.Name);
                var wanted = new BendValue(0f, change.Sway1, change.Sway2, change.Start, change.End);
                if (!old.IsNearly(wanted))
                {
                    ops.Add(new SwaySegmentOp(change.Item.Reference, piece.Name, old.Sway1, old.Sway2, wanted.Sway1, wanted.Sway2, old.Start, old.End, wanted.Start, wanted.End));
                }
            }
            else if (change.Instance is { } sel)
            {
                var current = CurrentInstanceTransform(sel);
                var value = current with { Scale = change.Scale, Location = change.Place ?? current.Location, Rotation = Turned(current.Rotation, change.Rotation) };
                if (!value.IsNearlyEqual(current) && sel.Instance is { } instance)
                {
                    ops.Add(EditOpFactory.SetInstanceTransform(sel.Item.Level, sel.Item.Actor, instance.ComponentName, instance.InstanceIndex, value, project.State));
                }
            }
            else
            {
                var item = change.Item;
                var current = CurrentRootTransform(item);
                var value = current with { Scale = change.Scale, Location = change.Place ?? current.Location, Rotation = Turned(current.Rotation, change.Rotation) };
                if (!value.IsNearlyEqual(current))
                {
                    ops.Add(item.IsAdded
                        ? EditOpFactory.SetAddedActorTransform(item.Reference, value, project.State)
                        : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State));
                }

                var old = project.State.GetBendValue(item.Reference);
                var wanted = new BendValue(change.Bend, change.Sway1, change.Sway2, change.Start, change.End, change.Legs);
                if (!old.IsNearly(wanted))
                {
                    ops.Add(new BendActorOp(item.Reference, old.Degrees, wanted.Degrees, old.Sway1, old.Sway2, wanted.Sway1, wanted.Sway2, old.Start, old.End, wanted.Start, wanted.End,
                        old.Legs, wanted.Legs));
                }
            }

            if (ops.Count > 0)
            {
                _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(Loc.F("Map.Shape.Batch", change.Item.Name), ops));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.MoveFailed"), ex.Message);
        }

        RefreshEdits();
    }

    /// <summary>The viewport's bent actors from the journal (called with every refresh of the edits).</summary>
    private void RefreshBends()
    {
        var bends = new Dictionary<uint, IReadOnlyList<SplineMeshParams>>();
        if (_services.Projects.Current?.State is { Bends.Count: > 0 } state)
        {
            var byReference = new Dictionary<ActorRef, ActorItemViewModel>(ActorRef.Comparer);
            foreach (var a in AllActors)
            {
                byReference.TryAdd(a.Reference, a);
            }

            foreach (var (actor, shape) in state.Bends)
            {
                if (byReference.TryGetValue(actor, out var item) && MeshBounds(item.Actor.StaticMeshPath) is { } bounds)
                {
                    bends[item.SelectableId] = BendShape.Pieces(bounds, CurrentRootTransform(item).Scale, shape);
                }
            }
        }

        Bends = bends;

        var pieces = new Dictionary<InstanceKey, SplineMeshParams>();
        if (_services.Projects.Current?.State is { } current)
        {
            foreach (var (actor, component, shape) in current.SegmentSways)
            {
                if (AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, actor)) is { } item
                    && item.Actor.FindComponent(component) is { SplineMesh: { } spline } found)
                {
                    pieces[InstanceKey.Of(item.SelectableId, found.Name, InstanceKey.Segment)] = SplineEnds.Shape(spline, shape.Sway1, shape.Sway2, shape.Start, shape.End);
                }
            }
        }

        SegmentBends = pieces;
    }

    /// <summary>True when the project bends the actor (its root is then drawn at scale 1: the scale is in the curve).</summary>
    private bool IsBent(ActorItemViewModel item) => _services.Projects.Current?.State.GetBendValue(item.Reference) is { IsStraight: false };

    /// <summary>The root transform the viewport draws an actor with.</summary>
    private TransformValue Drawn(ActorItemViewModel item, TransformValue relative) => IsBent(item) ? relative with { Scale = FVector.One } : relative;

    /// <summary>The sliders' rotation, or the current one when they show the same turn (no edit from a float round trip).</summary>
    private static FRotator Turned(FRotator current, FRotator wanted) =>
        (current.GetNormalized() - wanted.GetNormalized()).GetNormalized() is var d && MathF.Abs(d.Pitch) < 0.01f && MathF.Abs(d.Yaw) < 0.01f && MathF.Abs(d.Roll) < 0.01f
            ? current
            : wanted;

    private sealed record ShapeChange(ActorItemViewModel Item, SelectedInstance? Instance, FVector Scale, FRotator Rotation, float Bend, float Sway1, float Sway2,
        SplineEnd Start, SplineEnd End, FVector? Place, float Legs);
}
