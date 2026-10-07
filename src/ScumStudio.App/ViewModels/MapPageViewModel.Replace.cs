using Avalonia.Media.Imaging;
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

/// <summary>One alternative in the Replace flyout (see <see cref="ReplaceChoice"/>), with its picture while on screen.</summary>
public sealed class ReplaceCandidate : ThumbnailItem
{
    private readonly Func<ReplaceCandidate, CancellationToken, Task<Bitmap?>>? _thumbnails;

    /// <summary>Wraps <paramref name="choice"/>; <paramref name="thumbnails"/> makes its picture.</summary>
    public ReplaceCandidate(ReplaceChoice choice, Func<ReplaceCandidate, CancellationToken, Task<Bitmap?>>? thumbnails)
    {
        Choice = choice;
        _thumbnails = thumbnails;
    }

    /// <summary>The alternative.</summary>
    public ReplaceChoice Choice { get; }

    /// <summary>Its name.</summary>
    public string Name => Choice.Name;

    /// <summary>True for what the object is now.</summary>
    public bool IsCurrent => Choice.IsCurrent;

    /// <summary>Icon shown until the picture arrives.</summary>
    public string IconKey => Choice.IsBlueprint ? "Icon.Blueprint" : "Icon.Mesh";

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        _thumbnails is { } load ? load(this, cancellationToken) : Task.FromResult<Bitmap?>(null);
}

/// <summary>
/// The Replacer's picking card (Replace menu, tree swap): it shows the picked object's picture and name, and pressing it
/// opens the family list with pictures and a search box. While its menu is open (<see cref="Prefetch"/> to
/// <see cref="Close"/>) the pictures of the first rows are made on workers, so the list opens with them; the disk cache
/// of <see cref="Services.ThumbnailService"/> keeps them for the next time.
/// </summary>
public sealed partial class ObjectPicker : ObservableObject
{
    /// <summary>How many rows get their picture made when the menu opens.</summary>
    public const int PrefetchCount = 24;

    private readonly Action? _picked;
    private readonly List<ThumbnailItem> _pinned = [];
    private IReadOnlyList<ReplaceCandidate> _all = [];
    private bool _active;

    /// <summary>Creates the picker; <paramref name="picked"/> runs when the pick changes.</summary>
    public ObjectPicker(Action? picked = null) => _picked = picked;

    /// <summary>Text typed into the list's search box.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>The family, filtered by <see cref="Search"/> (the current one always stays).</summary>
    [ObservableProperty]
    private IReadOnlyList<ReplaceCandidate> _items = [];

    /// <summary>The picked object, or null.</summary>
    [ObservableProperty]
    private ReplaceCandidate? _selected;

    /// <summary>True while the list is shown.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The whole family.</summary>
    public IReadOnlyList<ReplaceCandidate> All => _all;

    /// <summary>Lists <paramref name="all"/> with nothing picked.</summary>
    public void SetItems(IReadOnlyList<ReplaceCandidate> all)
    {
        _all = all;
        Selected = null;
        Search = string.Empty;
        Filter();
        if (_active)
        {
            Pin();
        }
    }

    /// <summary>The menu opened: the first rows' pictures are made now.</summary>
    public void Prefetch()
    {
        _active = true;
        Pin();
    }

    /// <summary>The menu closed: the list closes and the pictures are let go.</summary>
    public void Close()
    {
        _active = false;
        IsOpen = false;
        Unpin();
    }

    [RelayCommand]
    private void Pick(ReplaceCandidate? candidate)
    {
        Selected = candidate;
        IsOpen = false;
    }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedChanged(ReplaceCandidate? value) => _picked?.Invoke();

    private void Filter()
    {
        var text = Search.Trim();
        Items = text.Length == 0 ? _all : _all.Where(c => c.IsCurrent || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Pin()
    {
        Unpin();
        _pinned.AddRange(_all.Take(PrefetchCount));
        foreach (var item in _pinned)
        {
            item.RequestThumbnail();
        }
    }

    private void Unpin()
    {
        foreach (var item in _pinned)
        {
            item.ReleaseThumbnail();
        }

        _pinned.Clear();
    }
}

/// <summary>
/// The Replace tool (owner, FiveM-editor style): the selection (one object or the multi-selection) becomes another object
/// of the same family (<see cref="ReplaceFamilies"/>), fitted to what it replaces: same place and turn; a road, bridge,
/// wall or fence piece keeps its length, height and curve (<see cref="ReplaceFamilies.FitScale"/>). A stored mesh
/// component is rewritten in place (<see cref="ReplaceMeshOp"/>, so its bend and curve stay); a tree of the foliage, a
/// copy or a Blueprint building leaves and the new object stands where it stood. One undo step for the whole selection.
/// </summary>
public sealed partial class MapPageViewModel
{
    private ObjectPicker? _replacePicker;

    /// <summary>The right card: the family of the selection, the current one first and marked; its pick is what Replace makes.</summary>
    public ObjectPicker ReplacePicker => _replacePicker ??= new ObjectPicker(ReplaceCommand.NotifyCanExecuteChanged);

    /// <summary>The left card: what the selection (its first object) is now.</summary>
    [ObservableProperty]
    private ReplaceCandidate? _replaceCurrent;

    /// <summary>"Selected", or how many objects are replaced together.</summary>
    [ObservableProperty]
    private string _replaceSelectionText = string.Empty;

    /// <summary>"12 alternatives of SM_Road_01", or why there are none.</summary>
    [ObservableProperty]
    private string _replaceCaption = string.Empty;

    /// <summary>Pristine actors drawn with another mesh (viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<uint, string> _replacedMeshes = new Dictionary<uint, string>();

    /// <summary>Road pieces and building parts drawn with another mesh (viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<InstanceKey, string> _partMeshes = new Dictionary<InstanceKey, string>();

    /// <summary>Completes when the last <see cref="ReplaceCommand"/> finished (tests).</summary>
    public Task<bool> ReplaceCompletion { get; private set; } = Task.FromResult(false);

    /// <summary>Fills the two cards: the selection (its first member) and its family; called when the menu opens.</summary>
    public void RefreshReplaceCandidates()
    {
        var members = ReplaceMembers();
        var current = members.Select(m => CurrentPathOf(m.Item, m.Instance)).FirstOrDefault(p => p is not null);
        var choices = current is null ? [] : ReplaceFamilies.Candidates(current, AssetDumper.Packages);
        if (choices.Any(c => c.IsBlueprint) && _services.Workspace.Catalog is { } placedIn)
        {
            // A Blueprint comes in as a copy of a placed one: only those placed somewhere in the world are offered (the
            // building folders also hold animation Blueprints, components and weather masks). The index is built once;
            // the list is filled again when it arrives.
            var index = PlacedIndexAsync(placedIn);
            if (index.IsCompletedSuccessfully)
            {
                choices = choices.Where(c => c.IsCurrent || index.Result.Of(c.PackagePath).Count > 0).ToList();
            }
            else if (!index.IsCompleted)
            {
                _ = index.ContinueWith(_ => _services.Dispatcher.Post(RefreshReplaceCandidates), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }

        var family = choices.Select(c => new ReplaceCandidate(c, LoadReplaceThumbnailAsync)).ToList();
        ReplaceCaption = current is null ? Loc.T("Map.Replace.Nothing")
            : family.Count < 2 ? Loc.F("Map.Replace.NoFamily", ShortName(current))
            : Loc.F("Map.Replace.Caption", family.Count - 1, ShortName(current), ReplaceFamilies.Family(current).ToString());
        ReplaceCurrent = family.FirstOrDefault(c => c.IsCurrent) ?? (current is null ? null
            : new ReplaceCandidate(new ReplaceChoice(ShortName(current), AssetPaths.SplitObjectPath(current).PackagePath, current.EndsWith("_C", StringComparison.Ordinal), true), LoadReplaceThumbnailAsync));
        ReplaceSelectionText = members.Count > 1 ? Loc.F("Map.Replace.SelectedMany", members.Count) : Loc.T("Map.Replace.Selected");
        ReplacePicker.SetItems(family);
    }

    private async Task<Bitmap?> LoadReplaceThumbnailAsync(ReplaceCandidate candidate, CancellationToken cancellationToken)
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return null;
        }

        var entry = new PackageEntry(string.Empty, candidate.Choice.PackagePath, candidate.Choice.IsBlueprint ? "Blueprint" : "StaticMesh");
        var png = await _services.Thumbnails.GetAssetAsync(catalog, entry, cancellationToken).ConfigureAwait(true);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The selection as members: the multi-selection, else the selected object (or its instance, piece or part).</summary>
    private List<(GroupMember Member, ActorItemViewModel Item, SelectedInstance? Instance)> ReplaceMembers() =>
        HasGroup ? GroupItems()
        : SelectedActor is { } item && MemberOf(item, SelectedInstanceKey) is { } single ? [(single, item, SelectedInstanceInfo())]
        : [];

    /// <summary>What the selection is now: its mesh (the project's replacement, else the level's) or its Blueprint class.</summary>
    private string? CurrentPathOf(ActorItemViewModel item, SelectedInstance? sel)
    {
        if (sel is not null)
        {
            return sel.Instance is null ? CurrentMesh(item, sel.Component.Name) : sel.Instance.StaticMeshPath;
        }

        return item.Actor.Kind == ActorKind.Blueprint ? item.Actor.ClassPath : CurrentMesh(item, null);
    }

    /// <summary>The mesh an actor's root (or named component) draws now: the project's replacement, else the level's.</summary>
    private string? CurrentMesh(ActorItemViewModel item, string? component) =>
        _services.Projects.Current?.State.GetMeshOverride(item.Reference, component)
        ?? (component is null ? item.Actor.StaticMeshPath : item.Actor.FindComponent(component)?.StaticMeshPath);

    private bool CanReplace() => ReplacePicker.Selected is { IsCurrent: false } && (HasGroup || SelectedActor is not null);

    /// <summary>Replaces every selected object with the right card's pick (finished in <see cref="ReplaceCompletion"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    private void Replace() => ReplaceCompletion = ReplaceAsync();

    /// <summary>Replaces the selection with the right card's pick as one journal step; false when nothing was replaced.</summary>
    public async Task<bool> ReplaceAsync()
    {
        if (ReplacePicker.Selected is not { } with)
        {
            return false;
        }

        if (PreparedScene is not { Documents.Count: > 0 })
        {
            _services.Notifications.Warning(Loc.T("Map.NoLevelLoaded"), Loc.T("Map.NoLevelLoadedDetail"));
            return false;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Adds"));
            return false;
        }

        if (_services.Workspace.Catalog is not { } catalog)
        {
            return false;
        }

        var members = ReplaceMembers();
        if (members.Count == 0)
        {
            _services.Notifications.Info(Loc.T("Map.Replace"), Loc.T("Map.Replace.Nothing"));
            return false;
        }

        var choice = with.Choice;
        var title = Loc.F("Map.Replace.Done", members.Count, choice.Name);
        try
        {
            // Read from the game files on a worker: a placed Blueprint to copy, or the new mesh's bounds to fit it.
            (string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)? blueprint = null;
            BoundingBox? bounds = null;
            if (choice.IsBlueprint)
            {
                var index = await PlacedIndexAsync(catalog).ConfigureAwait(true);
                blueprint = await Task.Run(() => FirstReadable(catalog, index.Of(choice.PackagePath))).ConfigureAwait(true);
                if (blueprint is null)
                {
                    _services.Notifications.Warning(Loc.T("Map.BlueprintNotPlaced"), Loc.F("Map.BlueprintNotPlacedDetail", choice.Name));
                    return false;
                }
            }
            else
            {
                var support = BendSupportFor(catalog);
                var mesh = choice.ObjectPath;
                bounds = MeshBounds(mesh) ?? await Task.Run(() => support.Describe(mesh)?.Bounds).ConfigureAwait(true);
            }

            if (!ReferenceEquals(_services.Projects.Current, project))
            {
                return false; // the project was closed meanwhile
            }

            var reserved = new HashSet<ActorRef>(ActorRef.Comparer);
            var ops = new List<EditOp>();
            var skipped = 0;
            foreach (var (member, item, sel) in members)
            {
                if (item.IsDeleted)
                {
                    continue;
                }

                var world = WorldOf(item, sel);
                var old = CurrentPathOf(item, sel);
                if (blueprint is { } bp)
                {
                    // Anything becomes a copy of the placed Blueprint where it stood (its own size); the old object leaves.
                    if (DeleteOpOf(item, sel, project.State) is not { } gone)
                    {
                        skipped++;
                        continue;
                    }

                    var reference = new ActorRef(bp.Level, bp.Actor.Name);
                    _foreignPlacements[reference] = bp.Placements;
                    var name = EditOpFactory.UniqueActorName(item.Level, ShortName(bp.Actor.ClassPath) + "_Added", project.State, reserved);
                    ops.Add(gone);
                    ops.Add(new AddBlueprintActorOp(item.Level.PackagePath, name, bp.Actor.ClassPath, reference, TransformValue.FromTransform(world with { Scale3D = FVector.One })));
                    continue;
                }

                var mesh = choice.ObjectPath;
                if (sel is { Instance: null, Component: { IsSynthesized: false } component } && !item.IsAdded)
                {
                    // A road piece or a building's part: its component draws the new mesh (a piece's curve fits it to the old length).
                    ops.Add(new ReplaceMeshOp(item.Reference, component.Name, old ?? string.Empty, mesh));
                    var current = CurrentInstanceTransform(sel);
                    if (component.SplineMesh is null && Fit(old, current.Scale, bounds) is { } scale && !scale.Equals(current.Scale, 0.001f))
                    {
                        ops.Add(EditOpFactory.SetTransform(item.Level, item.Actor, current with { Scale = scale }, project.State, component.Name));
                    }
                }
                else if (sel is null && old is not null && item.Actor.Kind == ActorKind.StaticMeshActor && (!item.IsAdded || project.State.AddedActors.GetValueOrDefault(item.Reference) is AddStaticMeshActorOp))
                {
                    // A mesh actor: in place, so its bend and attachment stay.
                    ops.Add(new ReplaceMeshOp(item.Reference, null, old, mesh));
                    var current = CurrentRootTransform(item);
                    if (Fit(old, current.Scale, bounds) is { } scale && !scale.Equals(current.Scale, 0.001f))
                    {
                        var value = current with { Scale = scale };
                        ops.Add(item.IsAdded ? EditOpFactory.SetAddedActorTransform(item.Reference, value, project.State) : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State));
                    }
                }
                else if (member.Index != InstanceKey.Segment && !IsImmovable(item) && DeleteOpOf(item, sel, project.State) is { } gone)
                {
                    // A tree of the foliage, a copy, a Blueprint building: it leaves and the mesh stands where it stood,
                    // colliding as a copied tree does; a bent copy's bend goes with it.
                    var placed = TransformValue.FromTransform(world);
                    var add = EditOpFactory.AddStaticMeshActor(item.Level, mesh, placed with { Scale = Fit(old, placed.Scale, bounds) ?? placed.Scale }, project.State, reserved)
                        with { CollisionProfile = sel?.Component.CollisionProfile };
                    ops.Add(gone);
                    ops.Add(add);
                    if (sel is null && project.State.GetBendValue(item.Reference) is { IsStraight: false } bend)
                    {
                        ops.Add(new BendActorOp(add.Created, 0f, bend.Degrees, 0f, 0f, bend.Sway1, bend.Sway2, default, default, bend.Start, bend.End, 0f, bend.Legs));
                    }
                }
                else
                {
                    skipped++;
                }
            }

            if (ops.Count == 0)
            {
                _services.Notifications.Info(Loc.T("Map.Replace"), Loc.T("Map.Replace.Nothing"));
                return false;
            }

            var created = ops.OfType<AddStaticMeshActorOp>().Select(a => a.Created).Concat(ops.OfType<AddBlueprintActorOp>().Select(b => b.Created)).ToList();
            var entry = _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(title, ops));
            _services.Notifications.Info(title, entry.Op.Describe() + (skipped > 0 ? " " + Loc.F("Map.Replace.Skipped", skipped) : string.Empty) + Loc.T("Map.CtrlZUndoes"));
            RefreshEdits();
            if (created.Count > 0)
            {
                SelectCreatedGroup(created);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException)
        {
            _services.Notifications.Error(Loc.T("Map.Replace"), ex.Message);
            return false;
        }
    }

    /// <summary>The scale that fits the new mesh (<paramref name="candidate"/> bounds) to the old one, or null when either is unknown.</summary>
    private FVector? Fit(string? oldMesh, FVector scale, BoundingBox? candidate) =>
        oldMesh is not null && candidate is { IsEmpty: false } c && MeshBounds(oldMesh) is { IsEmpty: false } original
            ? ReplaceFamilies.FitScale(original, scale, c, IsLong(oldMesh))
            : null;

    private BendSupport BendSupportFor(AssetCatalog catalog)
    {
        if (!ReferenceEquals(catalog, _bendCatalog))
        {
            _bendCatalog = catalog;
            _bendSupport = new BendSupport(catalog);
        }

        return _bendSupport!;
    }

    /// <summary>
    /// The edit that removes the object: the actor, one instance (a stored one collapsed, an added one taken out), a road
    /// piece scaled to nothing; null for a spawn part (never deleted, see <see cref="IsSpawnPartSelected"/>).
    /// </summary>
    private EditOp? DeleteOpOf(ActorItemViewModel item, SelectedInstance? sel, EditState state)
    {
        if (sel is null)
        {
            return new DeleteActorOp(item.Reference);
        }

        if (sel.Instance is { } instance)
        {
            var reference = new InstanceRef(item.Level.PackagePath, item.Name, instance.ComponentName, instance.InstanceIndex);
            return state.AddedInstances.GetValueOrDefault(reference) is { } added ? new RemoveAddedInstanceOp(added.Target, added) : new DeleteInstanceOp(reference);
        }

        if (SpawnMarkers.IsSpawnPart(item.Actor, sel.Component))
        {
            return null;
        }

        // A road or bridge piece goes the way a single one does: scaled to nothing (see DeleteInstance).
        return EditOpFactory.SetTransform(item.Level, item.Actor, CurrentInstanceTransform(sel) with { Scale = new FVector(0f, 0f, 0f) }, state, sel.Component.Name);
    }

    /// <summary>The meshes the project draws in place of the level's (with every refresh of the edits); their meshes are loaded for the viewport.</summary>
    private void RefreshReplacements()
    {
        var actors = new Dictionary<uint, string>();
        var parts = new Dictionary<InstanceKey, string>();
        if (_services.Projects.Current?.State is { } state)
        {
            foreach (var (actor, component, mesh) in state.MeshOverrides)
            {
                if (PristineOf(actor) is not { } item)
                {
                    continue; // an added actor draws its new mesh through its clone
                }

                if (component.Length == 0)
                {
                    actors[item.SelectableId] = mesh;
                }
                else if (item.Actor.FindComponent(component) is { } c)
                {
                    parts[InstanceKey.Of(item.SelectableId, c.Name, c.SplineMesh is null ? InstanceKey.Part : InstanceKey.Segment)] = mesh;
                }

                EnsureMeshLoaded(mesh);
            }
        }

        ReplacedMeshes = actors;
        PartMeshes = parts;
    }
}
