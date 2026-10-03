using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>A node of the world tree (group, cell, category or level package).</summary>
public sealed partial class WorldTreeNode : ViewModelBase
{
    /// <summary>Creates a node.</summary>
    public WorldTreeNode(string title, string iconKey, IReadOnlyList<WorldTreeNode>? children = null, WorldPackage? package = null, string? countText = null)
    {
        Title = title;
        IconKey = iconKey;
        Children = children ?? [];
        Package = package;
        CountText = countText;
    }

    /// <summary>Label.</summary>
    public string Title { get; }

    /// <summary>Icon resource key.</summary>
    public string IconKey { get; }

    /// <summary>Count shown at the right ("76"), or null.</summary>
    public string? CountText { get; }

    /// <summary>True when <see cref="CountText"/> is set.</summary>
    public bool HasCount => CountText is not null;

    /// <summary>Child nodes.</summary>
    public IReadOnlyList<WorldTreeNode> Children { get; }

    /// <summary>The package for leaf nodes.</summary>
    public WorldPackage? Package { get; }

    /// <summary>The map cell for cell nodes (selecting one loads the whole cell).</summary>
    public MapCell? Cell { get; init; }

    /// <summary>Expansion state.</summary>
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// Map editor page: world tree (cells, categories, sublevels) from <see cref="WorldIndex"/>, viewport and entity list
/// placeholders until Stage 1, a properties panel for the selected package and the project history (undo/redo on the
/// project journal).
/// </summary>
public sealed partial class MapPageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openSetup;
    private Task _loadTask = Task.CompletedTask;

    /// <summary>Creates the page.</summary>
    public MapPageViewModel(AppServices services, Action? openSetup = null)
        : base("map", "Map editor", "The_Island: cells, sublevels and your edit history")
    {
        var ui = services.Settings.Load().Ui;
        _translationSnap = ui.TranslationSnap;
        _rotationSnap = ui.RotationSnapDegrees;
        _renderQuality = ui.RenderQuality;
        _showSpawns = services.UiState.Current.ShowSpawnPoints;
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        _services.Projects.PropertyChanged += OnProjectsPropertyChanged;
        _services.Projects.Changed += OnProjectChanged;
        _services.SettingsChanged += OnSettingsChangedQuality;
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadWorldAsync(catalog);
        }

        UpdateEmptyState();
    }

    /// <summary>The project session (history, undo/redo state, name).</summary>
    public ProjectSession Session => _services.Projects;

    /// <summary>The world index shown, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorld))]
    private WorldIndex? _world;

    /// <summary>Root nodes of the tree.</summary>
    [ObservableProperty]
    private IReadOnlyList<WorldTreeNode> _nodes = [];

    /// <summary>Package counts per category.</summary>
    [ObservableProperty]
    private IReadOnlyList<CountChip> _kindChips = [];

    /// <summary>Selected tree node.</summary>
    [ObservableProperty]
    private WorldTreeNode? _selectedNode;

    /// <summary>Package of the selected node.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPackage), nameof(ViewportCaption))]
    private WorldPackage? _selectedPackage;

    /// <summary>Properties of <see cref="SelectedPackage"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyRow> _selectedProperties = [];

    /// <summary>True while the world index is built.</summary>
    [ObservableProperty]
    private bool _isLoadingWorld;

    /// <summary>Empty-state title of the tree.</summary>
    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    /// <summary>Empty-state text of the tree.</summary>
    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    /// <summary>True when the empty state is shown instead of the tree.</summary>
    [ObservableProperty]
    private bool _showEmptyState = true;

    /// <summary>True when the empty state offers the setup button.</summary>
    [ObservableProperty]
    private bool _emptyStateNeedsSetup = true;

    /// <summary>Name for a new project.</summary>
    [ObservableProperty]
    private string _newProjectName = "MyMapMod";

    /// <summary>Current tree filter.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    // ----- viewport / entities (Stage 1) -----

    /// <summary>Scene shown in the 3D viewport (null = nothing loaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScene), nameof(HasView))]
    private PreparedLevelScene? _preparedScene;

    /// <summary>True while levels are read and meshes prepared.</summary>
    [ObservableProperty]
    private bool _isLoadingLevel;

    /// <summary>Progress / result text of the last load.</summary>
    [ObservableProperty]
    private string _loadStatus = string.Empty;

    /// <summary>Caption of the loaded level(s).</summary>
    [ObservableProperty]
    private string _loadedLevelsCaption = Localization.Loc.T("Map.NoLevelLoaded");

    /// <summary>Every actor of the loaded levels.</summary>
    [ObservableProperty]
    private IReadOnlyList<ActorItemViewModel> _allActors = [];

    /// <summary>Actors after <see cref="EntityFilter"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntityCountText))]
    private IReadOnlyList<ActorItemViewModel> _actors = [];

    /// <summary>Entity list filter.</summary>
    [ObservableProperty]
    private string _entityFilter = string.Empty;

    /// <summary>Selected actor (entity list and viewport share it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedActor), nameof(DeleteAllOfMeshTip), nameof(DeleteAllOfClassTip))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(FrameSelectionCommand), nameof(DeleteAllOfMeshCommand), nameof(DeleteAllOfClassCommand), nameof(DuplicateSelectedCommand), nameof(ApplyTransformCommand), nameof(CopySelectedCommand))]
    private ActorItemViewModel? _selectedActor;

    /// <summary>The cell whose sublevels are loaded (null when a single sublevel is loaded).</summary>
    private MapCell? _loadedCell;

    /// <summary>True while a reload puts the previous selection back (that must not end a kind selection).</summary>
    private bool _restoringSelection;

    /// <summary>Menu text of "delete all with the same mesh" for the selected actor, with the live counts (actors + instances).</summary>
    public string DeleteAllOfMeshTip
    {
        get
        {
            if (SelectedActor is not { } item || KindMeshOf(item) is not { } mesh)
            {
                return Localization.Loc.T("Map.DeleteAllMesh.None");
            }

            var actors = CountOfKind(new KindMatch(MatchBy.StaticMesh, mesh));
            var instances = CountInstancesOfMesh(mesh);
            return string.Create(CultureInfo.CurrentCulture,
                $"{Localization.Loc.F("Map.DeleteAllMesh", actors, ShortName(mesh), instances > 0 ? Localization.Loc.F("Map.PlusInstances", instances) : string.Empty)}");
        }
    }

    /// <summary>Menu text of "delete all of the same class" for the selected actor, with the live count.</summary>
    public string DeleteAllOfClassTip =>
        SelectedActor is { } item
            ? Localization.Loc.F("Map.DeleteAllClass", CountOfKind(new KindMatch(MatchBy.Class, item.Actor.ClassPath)), item.ClassName)
            : Localization.Loc.T("Map.DeleteAllClass.None");

    /// <summary>Selectable id of <see cref="SelectedActor"/> (bound two-way to the viewport).</summary>
    [ObservableProperty]
    private uint _selectedActorId;

    /// <summary>Ids of actors deleted in the open project (hidden in the viewport).</summary>
    [ObservableProperty]
    private IReadOnlySet<uint> _hiddenActorIds = new HashSet<uint>();

    /// <summary>ISM/HISM instances deleted in the open project whose actors are kept (hidden in the viewport).</summary>
    [ObservableProperty]
    private IReadOnlySet<InstanceKey> _hiddenInstanceKeys = new HashSet<InstanceKey>();

    /// <summary>Root world transforms of loaded actors the project moved, by selectable id (drawn by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<uint, FTransform> _actorTransforms = new Dictionary<uint, FTransform>();

    /// <summary>Actors the project added as copies of loaded actors (drawn as clones by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyList<ActorClone> _clones = [];

    /// <summary>Editable location of the selected actor's root, "x, y, z" in cm.</summary>
    [ObservableProperty]
    private string _editLocation = string.Empty;

    /// <summary>Editable rotation of the selected actor's root, "pitch, yaw, roll" in degrees.</summary>
    [ObservableProperty]
    private string _editRotation = string.Empty;

    /// <summary>Editable scale of the selected actor's root, "x, y, z".</summary>
    [ObservableProperty]
    private string _editScale = string.Empty;

    /// <summary>Current root world transform of the selected actor (gizmo origin), or null.</summary>
    [ObservableProperty]
    private FTransform? _selectedRootWorld;

    /// <summary>Gizmo snap step in cm (from the UI preferences; 0 = free).</summary>
    [ObservableProperty]
    private float _translationSnap;

    /// <summary>Yaw-ring snap step in degrees (from the UI preferences; 0 = free).</summary>
    [ObservableProperty]
    private float _rotationSnap;

    /// <summary>Meshes of new mesh actors, loaded after the scene was prepared (the viewport uploads them).</summary>
    [ObservableProperty]
    private IReadOnlyList<ExtraMesh> _extraMeshes = [];

    /// <summary>Where new actors are placed (UE world cm); the view supplies the viewport's aim point.</summary>
    public Func<FVector>? AimPointProvider { get; set; }

    /// <summary>The pristine actor copied with Copy (Ctrl+C); Paste places a copy of it in the loaded level.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    [NotifyPropertyChangedFor(nameof(HasCopiedActor), nameof(PasteTip))]
    private ActorItemViewModel? _copiedActor;

    /// <summary>True when Copy stored an actor.</summary>
    public bool HasCopiedActor => CopiedActor is not null || _copiedInstance is not null || _copiedGroup is { Count: > 0 };

    /// <summary>Menu text of Paste.</summary>
    public string PasteTip => _copiedGroup is { Count: > 0 } g ? Localization.Loc.F("Map.Group.Paste.Tip", g.Count)
        : CopiedActor is { } c ? Localization.Loc.F("Map.PasteActor.Tip", c.Name, c.ClassName)
        : _copiedInstance is { } i ? Localization.Loc.F("Map.PasteMesh.Tip", ShortName(i.Mesh)) : Localization.Loc.T("Map.Paste.None");

    /// <summary>Distance in front of the camera at which new actors are placed, cm.</summary>
    public const float PlacementDistance = 1500f;

    private readonly HashSet<string> _loadingMeshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Selectable ids of actors added by the project start here (above every document's ids).</summary>
    public const uint AddedIdBase = 0x8000_0000u;

    // An added actor keeps its id for the whole session: adding another one must not shift the ids (and the selection).
    private readonly Dictionary<ActorRef, uint> _addedIds = new(ActorRef.Comparer);

    private IReadOnlyList<ActorItemViewModel> _pristineActors = [];

    /// <summary>Properties of the selected actor.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyRow> _actorProperties = [];

    private CancellationTokenSource? _loadCts;

    /// <summary>Opacity of an entity row: dimmed when the actor is deleted in the project.</summary>
    public static Avalonia.Data.Converters.IValueConverter DeletedOpacity { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(deleted => deleted ? 0.4 : 1.0);

    /// <summary>Raised when the view should frame the camera on the selected actor.</summary>
    public event EventHandler? FrameSelectionRequested;

    /// <summary>True when a scene is loaded.</summary>
    public bool HasScene => PreparedScene is not null;

    /// <summary>True when the 3D view has something to show: a loaded scene or the whole-island backdrop.</summary>
    public bool HasView => HasScene || WorldBackdrop is not null;

    /// <summary>True when an actor is selected.</summary>
    public bool HasSelectedActor => SelectedActor is not null;

    /// <summary>"12 of 510 actors".</summary>
    public string EntityCountText => AllActors.Count == 0
        ? Localization.Loc.T("Map.NoActors")
        : Actors.Count == AllActors.Count
            ? Localization.Loc.F("Map.Actors", AllActors.Count)
            : Localization.Loc.F("Map.ActorsOf", Actors.Count, AllActors.Count);

    /// <summary>Completes when the current level load finished (tests).</summary>
    public Task LevelLoadCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>True when a world index is loaded.</summary>
    public bool HasWorld => World is not null;

    /// <summary>True when a level package is selected.</summary>
    public bool HasSelectedPackage => SelectedPackage is not null;

    /// <summary>Caption at the top of the viewport.</summary>
    public string ViewportCaption => SelectedPackage is { } p ? p.Name : Localization.Loc.T("Map.NoLevelSelected");

    /// <summary>Completes when the current world load finished (tests).</summary>
    public Task LoadCompletion => _loadTask;

    /// <inheritdoc />
    public void ApplySearch(string? text) => FilterText = text ?? string.Empty;

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        if (World is { } index)
        {
            var levels = index.Packages.Count(p => p.IsMap);
            Subtitle = levels == 0
                ? Localization.Loc.T("Map.Subtitle.NoLevels")
                : Localization.Loc.F("Map.Subtitle", index.Sublevels.Count(), index.SublevelCountByCell().Count);
        }

        UpdateEmptyState();
        RefreshKindSelection();
    }

    /// <summary>Shows <paramref name="index"/> in the tree.</summary>
    public void LoadWorld(WorldIndex index)
    {
        World = index;
        KindChips = index.CountByKind()
            .Select(kv => new CountChip(KindLabel(kv.Key), kv.Value))
            .ToList();
        var levels = index.Packages.Count(p => p.IsMap);
        Subtitle = levels == 0
            ? Localization.Loc.T("Map.Subtitle.NoLevels")
            : Localization.Loc.F("Map.Subtitle", index.Sublevels.Count(), index.SublevelCountByCell().Count);
        RebuildTree();
        UpdateEmptyState();
    }

    /// <summary>Builds the tree nodes for <paramref name="index"/>, keeping packages accepted by <paramref name="filter"/>.</summary>
    public static IReadOnlyList<WorldTreeNode> BuildTree(WorldIndex index, Func<WorldPackage, bool>? filter = null)
    {
        filter ??= _ => true;
        var nodes = new List<WorldTreeNode>();

        if (index.PersistentLevel is { } persistent && filter(persistent))
        {
            nodes.Add(new WorldTreeNode(persistent.Name, "Icon.World", package: persistent, countText: "persistent"));
        }

        var visible = index.Packages
            .Where(p => p.Kind is not WorldPackageKind.Persistent and not WorldPackageKind.BuiltData)
            .Where(filter)
            .ToList();

        foreach (var cellGroup in visible.Where(p => p.Cell is not null).GroupBy(p => p.Cell!.Value).OrderBy(g => g.Key))
        {
            var categories = CategoryNodes(cellGroup);
            var levels = cellGroup.Count(p => p.IsMap);
            nodes.Add(new WorldTreeNode("Cell " + cellGroup.Key, "Icon.Cell", categories,
                countText: levels.ToString("N0", CultureInfo.CurrentCulture)) { Cell = cellGroup.Key });
        }

        var other = visible.Where(p => p.Cell is null).ToList();
        if (other.Count > 0)
        {
            nodes.Add(new WorldTreeNode("Other", "Icon.Folder", CategoryNodes(other),
                countText: other.Count.ToString("N0", CultureInfo.CurrentCulture)));
        }

        return nodes;

        static IReadOnlyList<WorldTreeNode> CategoryNodes(IEnumerable<WorldPackage> packages) =>
            packages.GroupBy(p => p.Kind).OrderBy(g => g.Key)
                .Select(g => new WorldTreeNode(KindLabel(g.Key), KindIcon(g.Key),
                    g.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(p => new WorldTreeNode(p.Name, p.IsMap ? KindIcon(p.Kind) : "Icon.File", package: p))
                        .ToList(),
                    countText: g.Count().ToString("N0", CultureInfo.CurrentCulture)))
                .ToList();
    }

    /// <summary>Friendly label of a package kind.</summary>
    public static string KindLabel(WorldPackageKind kind) => kind switch
    {
        WorldPackageKind.Persistent => "Persistent",
        WorldPackageKind.Poi => "POI / grid",
        WorldPackageKind.Landscape => "Landscape",
        WorldPackageKind.TvBase => "TV bases",
        WorldPackageKind.Pripyat => "Abandoned city",
        WorldPackageKind.BuiltData => "Built data",
        WorldPackageKind.Hlod => "HLOD",
        _ => "Misc",
    };

    /// <summary>Icon of a package kind.</summary>
    public static string KindIcon(WorldPackageKind kind) => kind switch
    {
        WorldPackageKind.Persistent => "Icon.World",
        WorldPackageKind.Landscape => "Icon.Landscape",
        WorldPackageKind.Hlod => "Icon.Mesh",
        WorldPackageKind.BuiltData => "Icon.File",
        _ => "Icon.Level",
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _loadCts?.Cancel();
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
        _services.Projects.PropertyChanged -= OnProjectsPropertyChanged;
        _services.Projects.Changed -= OnProjectChanged;
        _services.SettingsChanged -= OnSettingsChangedQuality;
    }

    /// <summary>Creates a project in <paramref name="parentFolder"/> named <see cref="NewProjectName"/>.</summary>
    public async Task<bool> CreateProjectAsync(string parentFolder)
    {
        var name = string.IsNullOrWhiteSpace(NewProjectName) ? "MyMapMod" : NewProjectName.Trim();
        var (ok, project) = await _services.Operations.RunAsync(Localization.Loc.F("Projects.Creating", name),
            (_, ct) => _services.Projects.CreateAsync(parentFolder, name, ct)).ConfigureAwait(true);
        if (ok && project is not null)
        {
            _services.Notifications.Success(Localization.Loc.T("Projects.Created"), project.DirectoryPath);
        }

        return ok;
    }

    /// <summary>Opens the project in <paramref name="path"/>.</summary>
    public async Task<bool> OpenProjectAsync(string path)
    {
        var (ok, _) = await _services.Operations.RunAsync(Localization.Loc.T("Projects.Opening"),
            (_, ct) => _services.Projects.OpenAsync(path, ct)).ConfigureAwait(true);
        return ok;
    }

    partial void OnFilterTextChanged(string value) => RebuildTree();

    partial void OnSelectedNodeChanged(WorldTreeNode? value)
    {
        SelectedPackage = value?.Package;
        SelectedProperties = value?.Package is { } p ? Describe(p) : [];
        if (value?.Package is { IsMap: true } level && level.Kind != WorldPackageKind.Persistent)
        {
            IsWorldMode = false;
            _loadedCell = null;
            LevelLoadCompletion = LoadLevelsAsync([level.PackagePath], landscapeStep: 1);
        }
        else if (value?.Cell is { } cell && World is { } world)
        {
            _loadedCell = cell;
            LevelLoadCompletion = LoadLevelsAsync(CellPackages(world, cell), landscapeStep: 4);
        }
    }

    /// <summary>POI sublevels of <paramref name="cell"/> plus its landscape tiles.</summary>
    public static IReadOnlyList<string> CellPackages(WorldIndex world, MapCell cell) =>
        world.InCell(cell)
            .Where(p => p.IsMap && p.Kind is WorldPackageKind.Poi or WorldPackageKind.Landscape or WorldPackageKind.TvBase or WorldPackageKind.Misc)
            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
            .Select(p => p.PackagePath)
            .ToList();

    /// <summary>
    /// Rivers and lakes live in one shared level (WaterSplines) for the whole island: its water actors with a piece over
    /// the landscape tiles among <paramref name="packagePaths"/>, or null (no tiles, or none of the water is there).
    /// </summary>
    internal static LevelDocument? WaterOver(LevelData data, WorldIndex world, IReadOnlyList<string> packagePaths)
    {
        var tiles = world.Packages
            .Where(p => p.Kind == WorldPackageKind.Landscape && p.Tile is { BoundsValid: true } && packagePaths.Contains(p.PackagePath, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Tile!)
            .ToList();
        if (tiles.Count == 0)
        {
            return null;
        }

        var keep = LevelDocument.FromData(data).Actors
            .Where(a => a.Components.Any(c => c.StaticMeshPath is not null && Over(c.WorldTransform.TransformPosition(c.SplineMesh?.StartPos ?? FVector.Zero))))
            .Select(a => a.ExportIndex)
            .ToHashSet();
        return keep.Count == 0 ? null : LevelDocument.FromData(data with { ActorIndices = data.ActorIndices.Where(keep.Contains).ToList() });

        bool Over(FVector p) => tiles.Any(t => p.X >= t.BoundsMin.X && p.X <= t.BoundsMax.X && p.Y >= t.BoundsMin.Y && p.Y <= t.BoundsMax.Y);
    }

    /// <summary>Reads <paramref name="packagePaths"/> and prepares their scene on a worker thread, then shows it.</summary>
    public async Task LoadLevelsAsync(IReadOnlyList<string> packagePaths, int landscapeStep = 1, bool seaPlane = true)
    {
        if (_services.Workspace.Catalog is not { } catalog || packagePaths.Count == 0)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        IsLoadingLevel = true;
        LoadStatus = packagePaths.Count == 1 ? Localization.Loc.T("Map.ReadingLevel") : Localization.Loc.F("Map.ReadingLevels", packagePaths.Count);
        var world = World;
        try
        {
            var textureSize = Quality.TextureSize;
            var prepared = await Task.Run(() =>
            {
                var documents = ReadDocuments(catalog, world, packagePaths, ct);
                var options = new LevelSceneOptions { TextureSize = textureSize, LandscapeStep = Math.Max(1, landscapeStep), SeaPlane = seaPlane ? null : false };
                var progress = new Progress<(int Done, int Total, string Item)>(p =>
                    LoadStatus = Localization.Loc.F("Map.Preparing", p.Done + 1, p.Total, p.Item[(p.Item.LastIndexOf('/') + 1)..]));
                return new LevelScenePreparer(catalog, _services.Logger).Prepare(documents, options, progress, ct, _prepareCache);
            }, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            ShowScene(prepared);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer selection
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Level load failed: {Message}", ex.Message);
            _services.Notifications.Warning(Localization.Loc.T("Map.CouldNotLoad"), ex.Message);
            LoadStatus = Localization.Loc.F("Map.LoadFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoadingLevel = false;
            }
        }
    }

    /// <summary>Shows <paramref name="prepared"/> in the viewport and entity list.</summary>
    public void ShowScene(PreparedLevelScene prepared)
    {
        // A streamed reload keeps the selection when its level is still there (ids change with the level set).
        var keep = SelectedActor?.Reference;
        var keepInstance = SelectedInstanceKey;
        SelectedActor = null;
        ExtraMeshes = [];
        _loadingMeshes.Clear();
        PreparedScene = prepared;
        _pristineActors = prepared.Documents
            .SelectMany((d, i) => d.Actors.Select(a => new ActorItemViewModel(d, a, LevelScenePreparer.SelectableIdOf(i, a))))
            .ToList();
        RefreshEdits();
        _restoringSelection = true;
        try
        {
            if (keep is not null && AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, keep)) is { } again)
            {
                if (keepInstance is { } k)
                {
                    SelectedInstanceKey = InstanceKey.Of(again.SelectableId, k.Component, k.InstanceIndex);
                }

                SelectedActor = again;
            }
        }
        finally
        {
            _restoringSelection = false;
        }

        RefreshKindSelection();

        PasteCommand.NotifyCanExecuteChanged();
        LoadedLevelsCaption = prepared.Documents.Count == 1
            ? prepared.Documents[0].Name
            : Localization.Loc.F("Map.MoreLevels", prepared.Documents[0].Name, prepared.Documents.Count - 1);
        LoadStatus = Localization.Loc.F("Map.LoadSummary", AllActors.Count, prepared.Meshes.Count, prepared.MissingMeshes.Count, prepared.Terrain.Count, prepared.Elapsed.TotalSeconds);
    }

    partial void OnEntityFilterChanged(string value) => ApplyEntityFilter();

    partial void OnSelectedActorChanged(ActorItemViewModel? value)
    {
        if (!_restoringSelection && !(HasGroup && SelectionInGroup()))
        {
            ClearKindSelection(); // a new pick ends "all of this kind" and the multi-selection (a member of it keeps it)
        }

        var id = value?.SelectableId ?? 0u;
        if (SelectedActorId != id)
        {
            SelectedActorId = id;
        }

        if (SelectedInstanceKey is { } key && key.SelectableId != id)
        {
            SelectedInstanceKey = null; // the instance belonged to the previous actor
        }

        if (value is not null && !_restoringSelection)
        {
            Remember(value);
        }

        ShowSelection(value);
    }

    partial void OnSelectedActorIdChanged(uint value)
    {
        var item = value == 0 ? null : AllActors.FirstOrDefault(a => a.SelectableId == value);
        if (!ReferenceEquals(item, SelectedActor))
        {
            SelectedActor = item;
        }
    }

    private void ApplyEntityFilter()
    {
        var text = EntityFilter.Trim();
        Actors = text.Length == 0 ? AllActors : AllActors.Where(a => a.Matches(text)).ToList();
    }

    /// <summary>Recomputes which actors the open project has deleted.</summary>
    public void RefreshHiddenIds()
    {
        var state = _services.Projects.Current?.State;
        var hidden = new HashSet<uint>();
        var byReference = new Dictionary<ActorRef, uint>(ActorRef.Comparer);
        foreach (var actor in AllActors)
        {
            actor.IsDeleted = state?.IsDeleted(actor.Reference) == true;
            byReference.TryAdd(actor.Reference, actor.SelectableId);
            if (actor.IsDeleted)
            {
                hidden.Add(actor.SelectableId);
            }
        }

        var hiddenInstances = new HashSet<InstanceKey>();
        if (state is not null)
        {
            foreach (var instance in state.DeletedInstances)
            {
                if (byReference.TryGetValue(instance.ActorRef, out var id) && !hidden.Contains(id))
                {
                    hiddenInstances.Add(InstanceKey.Of(id, instance.Component, instance.Index));
                }
            }
        }

        // Spawn pins off: every pin's id (the spawn-only actors' and the pins inside buildings) is hidden.
        if (!ShowSpawns && PreparedScene is { } scene)
        {
            hidden.UnionWith(scene.Placements.Where(p => SpawnMarkers.IsMarker(p.MeshPath)).Select(p => p.SelectableId));
        }

        HiddenActorIds = hidden;
        HiddenInstanceKeys = hiddenInstances;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteAllOfMeshCommand.NotifyCanExecuteChanged();
        DeleteAllOfClassCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        ApplyTransformCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DeleteAllOfMeshTip));
        OnPropertyChanged(nameof(DeleteAllOfClassTip));
    }

    /// <summary>Re-derives everything the project's edits change in the loaded scene: added actors, hidden ids, moved actors.</summary>
    public void RefreshEdits()
    {
        // Added actors get new items here, so the entity list drops its selection and writes null back: the selection
        // (actor and instance) is put back by id afterwards, and "all of this kind" survives.
        var keepId = SelectedActorId;
        var keepInstance = SelectedInstanceKey;
        var restoring = _restoringSelection;
        _restoringSelection = true;
        try
        {
            RefreshAddedActors();
            RefreshHiddenIds();
            RefreshTransforms();
            RefreshBends();
            RefreshGroup();
            var again = keepId == 0 ? null : AllActors.FirstOrDefault(a => a.SelectableId == keepId);
            if (again is null)
            {
                SelectedActor = null;
                SelectedRootWorld = null;
                return;
            }

            SelectedInstanceKey = keepInstance;
            if (ReferenceEquals(again, SelectedActor))
            {
                ShowSelection(again);
            }
            else
            {
                SelectedActor = again;
            }
        }
        finally
        {
            _restoringSelection = restoring;
        }
    }

    /// <summary>
    /// Applies a gizmo drag: the actor's root moved to <paramref name="rootWorld"/> (UE world space); converted back to
    /// the root's relative transform and journaled like <see cref="ApplyTransformCommand"/>.
    /// </summary>
    public void ApplyDraggedTransform(uint selectableId, FTransform rootWorld)
    {
        // Dragging one object of the multi-selection moves them all (rigidly, scale untouched).
        if (HasGroup && AllActors.FirstOrDefault(a => a.SelectableId == selectableId) is { } dragged)
        {
            var key = SelectedInstanceKey is { } k && k.SelectableId == selectableId ? k : (InstanceKey?)null;
            var before = WorldOf(dragged, key is { } kk ? InstanceInfo(dragged, kk) : null) with { Scale3D = FVector.One };
            if (TryMoveGroup(dragged, key, before, rootWorld with { Scale3D = FVector.One }))
            {
                return;
            }
        }

        if (TryApplyInstanceDrag(selectableId, rootWorld))
        {
            return;
        }

        if (AllActors.FirstOrDefault(a => a.SelectableId == selectableId) is not { } item)
        {
            return;
        }

        // A drag moves and turns; the scale stays (a bent actor is drawn at scale 1, its scale is in the curve).
        ApplyRootTransform(item, RelativeOf(item, rootWorld) with { Scale = CurrentRootTransform(item).Scale }, Localization.Loc.T("Map.Moved"));
    }

    /// <summary>Journals a new relative root transform for <paramref name="item"/> (pristine or added) and refreshes the scene.</summary>
    private void ApplyRootTransform(ActorItemViewModel item, TransformValue value, string title)
    {
        if (IsImmovable(item))
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.NotMovable"), Localization.Loc.T("Map.NotMovableDetail"));
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Moves"));
            return;
        }

        if (value.IsNearlyEqual(CurrentRootTransform(item)))
        {
            return;
        }

        try
        {
            var op = item.IsAdded
                ? EditOpFactory.SetAddedActorTransform(item.Reference, value, project.State)
                : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(title, entry.Op.Describe());
            RefreshEdits();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.MoveFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the entity list: the pristine actors plus the actors the project added to the loaded levels (duplicates
    /// as synthetic records of their source, new mesh actors as bare StaticMeshActors), with ids above <see cref="AddedIdBase"/>.
    /// </summary>
    private void RefreshAddedActors()
    {
        var state = _services.Projects.Current?.State;
        var added = new List<ActorItemViewModel>();
        if (state is not null && PreparedScene is { } scene)
        {
            var levels = scene.Documents.ToDictionary(d => d.PackagePath, StringComparer.OrdinalIgnoreCase);
            foreach (var (reference, op) in state.AddedActors.OrderBy(a => a.Key.Level, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key.Actor, StringComparer.OrdinalIgnoreCase))
            {
                if (!levels.TryGetValue(reference.Level, out var document))
                {
                    continue;
                }

                if (!_addedIds.TryGetValue(reference, out var id))
                {
                    _addedIds[reference] = id = AddedIdBase + (uint)_addedIds.Count;
                }

                var transform = state.GetAddedTransform(reference) ?? TransformValue.Identity;
                ActorItemViewModel? item = null;
                switch (op)
                {
                    case DuplicateActorOp duplicate when _pristineActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, duplicate.Source)) is { } source:
                        var world = RootWorldOf(source, transform);
                        item = new ActorItemViewModel(document, source.Actor with { Name = duplicate.NewName, ExportIndex = -1 - (int)(id - AddedIdBase), WorldTransform = world }, id)
                        {
                            IsAdded = true,
                            SourceId = source.SelectableId,
                        };
                        break;
                    case AddStaticMeshActorOp mesh:
                        var record = new ActorRecord(-1 - (int)(id - AddedIdBase), mesh.NewName, "/Script/Engine.StaticMeshActor", null, [], transform.ToTransform(),
                            ActorKind.StaticMeshActor, mesh.StaticMesh, [])
                        {
                            ClassName = "StaticMeshActor",
                        };
                        item = new ActorItemViewModel(document, record, id) { IsAdded = true };
                        break;
                    case AddBlueprintActorOp blueprint when _pristineActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, blueprint.Source)) is { } bpSource:
                        // The source level is loaded too: draw the copy by cloning the source's placements.
                        item = new ActorItemViewModel(document, bpSource.Actor with { Name = blueprint.NewName, ExportIndex = -1 - (int)(id - AddedIdBase), WorldTransform = RootWorldOf(bpSource, transform) }, id)
                        {
                            IsAdded = true,
                            SourceId = bpSource.SelectableId,
                        };
                        break;
                    case AddBlueprintActorOp blueprint:
                        var bpRecord = new ActorRecord(-1 - (int)(id - AddedIdBase), blueprint.NewName, blueprint.ClassPath, null, [], transform.ToTransform(),
                            ActorKind.Blueprint, null, [])
                        {
                            ClassName = blueprint.ClassPath[(blueprint.ClassPath.LastIndexOf('.') + 1)..],
                        };
                        item = new ActorItemViewModel(document, bpRecord, id) { IsAdded = true };
                        break;
                }

                if (item is not null)
                {
                    added.Add(item);
                }
            }
        }

        AllActors = added.Count == 0 ? _pristineActors : [.. _pristineActors, .. added];
        ApplyEntityFilter();
        var clones = new List<ActorClone>();
        foreach (var item in added)
        {
            var drawnAt = IsBent(item) ? item.Actor.WorldTransform with { Scale3D = FVector.One } : item.Actor.WorldTransform;
            if (item.SourceId != 0)
            {
                clones.Add(new ActorClone(item.SelectableId, item.SourceId, drawnAt, item.Name));
            }
            else if (item.Actor.StaticMeshPath is { } mesh)
            {
                clones.Add(new ActorClone(item.SelectableId, 0, drawnAt, item.Name, mesh));
                EnsureMeshLoaded(mesh);
            }
        }

        Clones = clones;
    }

    /// <summary>Loads a mesh the prepared scene does not have (new mesh actors) on a worker and hands it to the viewport.</summary>
    private void EnsureMeshLoaded(string meshPath)
    {
        if (PreparedScene is not { } scene || _services.Workspace.Catalog is not { } catalog
            || scene.Meshes.ContainsKey(meshPath) || ExtraMeshes.Any(m => string.Equals(m.Asset.MeshPath, meshPath, StringComparison.OrdinalIgnoreCase))
            || !_loadingMeshes.Add(meshPath))
        {
            return;
        }

        _ = Task.Run(() => new LevelScenePreparer(catalog, _services.Logger).PrepareMesh(meshPath, new LevelSceneOptions { TextureSize = 512 }))
            .ContinueWith(t => _services.Dispatcher.Invoke(() =>
            {
                _loadingMeshes.Remove(meshPath);
                if (t.IsCompletedSuccessfully && t.Result is { } extra && ReferenceEquals(PreparedScene, scene))
                {
                    ExtraMeshes = [.. ExtraMeshes, extra];
                }
                else if (t.IsFaulted)
                {
                    _services.Logger.LogWarning("Mesh {Mesh} could not be loaded for the viewport: {Message}", meshPath, t.Exception?.GetBaseException().Message);
                }
            }), TaskScheduler.Default);
    }

    /// <summary>Text of the Map's "Add object" box: a path copied from Assets, or just the object's name.</summary>
    [ObservableProperty]
    private string _addObjectText = string.Empty;

    [RelayCommand]
    private void AddObjectFromText()
    {
        if (AddObject(AddObjectText))
        {
            AddObjectText = string.Empty;
        }
    }

    /// <summary>
    /// Adds the object <paramref name="text"/> names (<c>/Game/…/SM_Crate</c>, its object path, or just <c>SM_Crate</c>)
    /// in front of the camera. Meshes only: a Blueprint is placed by copying one that is already in a level.
    /// </summary>
    public bool AddObject(string text)
    {
        var name = text.Trim().Trim('"', '\'');
        if (name.Length == 0)
        {
            return false;
        }

        var package = name.Contains('/', StringComparison.Ordinal) ? AssetPaths.SplitObjectPath(name).PackagePath : null;
        var known = AssetDumper.Packages.FirstOrDefault(p => package is not null
            ? string.Equals(p.PackagePath, package, StringComparison.OrdinalIgnoreCase)
            : AssetsPageViewModel.IsObjectClass(p.ClassName) && p.PackagePath.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
        package ??= known.PackagePath;
        if (package is null)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.ObjectNotFound"), Localization.Loc.F("Map.ObjectNotFoundDetail", name));
            return false;
        }

        if (known.ClassName == "Blueprint")
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.NoBlueprintByPath"),
                Localization.Loc.T("Map.NoBlueprintByPathDetail"));
            return false;
        }

        return AddMeshActor(package + "." + package[(package.LastIndexOf('/') + 1)..]);
    }

    /// <summary>
    /// Adds a new StaticMeshActor drawing <paramref name="meshObjectPath"/> to the loaded level, placed where the viewport
    /// aims (<see cref="AimPointProvider"/>), and selects it. Needs a loaded scene and an open project.
    /// </summary>
    public bool AddMeshActor(string meshObjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshObjectPath);
        if (PreparedScene is not { } scene || scene.Documents.Count == 0)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.NoLevelLoaded"), Localization.Loc.T("Map.NoLevelLoadedDetail"));
            return false;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Adds"));
            return false;
        }

        var document = scene.Documents.FirstOrDefault(d => !d.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase)) ?? scene.Documents[0];
        var at = AimPointProvider?.Invoke() ?? FVector.Zero;
        try
        {
            var op = EditOpFactory.AddStaticMeshActor(document, meshObjectPath, TransformValue.At(at.X, at.Y, at.Z), project.State);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Added"), entry.Op.Describe());
            RefreshEdits();
            SelectedActor = AllActors.FirstOrDefault(a => a.IsAdded && a.Name == op.NewName) ?? SelectedActor;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.AddFailed"), ex.Message);
            return false;
        }
    }

    /// <summary>Collects the root world transforms of moved pristine actors for the viewport.</summary>
    private void RefreshTransforms()
    {
        var state = _services.Projects.Current?.State;
        var moved = new Dictionary<uint, FTransform>();
        if (state is not null)
        {
            foreach (var item in _pristineActors)
            {
                if (state.GetTransformOverride(item.Reference) is { } relative)
                {
                    moved[item.SelectableId] = RootWorldOf(item, Drawn(item, relative));
                }
                else if (IsBent(item))
                {
                    moved[item.SelectableId] = RootWorldOf(item, Drawn(item, CurrentRootTransform(item)));
                }
            }
        }

        ActorTransforms = moved;
        InstanceTransforms = CollectInstanceTransforms(state);
    }

    /// <summary>The current relative transform of the selected actor's root: the project's override or addition, else the pristine value.</summary>
    private TransformValue CurrentRootTransform(ActorItemViewModel item)
    {
        var state = _services.Projects.Current?.State;
        if (item.IsAdded)
        {
            return state?.GetAddedTransform(item.Reference) ?? TransformValue.FromTransform(item.Actor.WorldTransform);
        }

        return state?.GetTransformOverride(item.Reference) ?? item.Actor.Root?.Relative ?? TransformValue.FromTransform(item.Actor.WorldTransform);
    }

    /// <summary>
    /// World transform of an actor's root when its relative transform is <paramref name="relative"/>: level actors' roots
    /// have no parent, a stored child actor's root is attached to a component of another actor.
    /// </summary>
    private FTransform RootWorldOf(ActorItemViewModel item, TransformValue relative) =>
        ParentOf(item) is { } parent ? relative.ToTransform() * parent.WorldTransform : relative.ToTransform();

    /// <summary>
    /// The relative root transform that puts an actor's root at <paramref name="world"/>: the inverse of
    /// <see cref="RootWorldOf"/>. A copy of an attached actor (a bridge's fence) stays attached to the same parent, so it
    /// needs this too (a world place stored as relative sent copies kilometres off the map).
    /// </summary>
    private TransformValue RelativeOf(ActorItemViewModel item, FTransform world) =>
        TransformValue.FromTransform(ParentOf(item) is { } parent ? world.GetRelativeTransform(parent.WorldTransform) : world);

    /// <summary>The component an actor's root is attached to, or null for a root of its own.</summary>
    private static ComponentRecord? ParentOf(ActorItemViewModel item) =>
        item.Actor.Root?.AttachParent is { } parentIndex
            ? item.Level.Actors.SelectMany(a => a.Components).FirstOrDefault(c => c.ExportIndex == parentIndex)
            : null;

    private IReadOnlyList<PropertyRow> DescribeSelected(ActorItemViewModel item) =>
        DescribeActor(item, RootWorldOf(item, CurrentRootTransform(item)));

    private static string Fmt3(float x, float y, float z, string format) =>
        string.Create(CultureInfo.InvariantCulture, $"{x.ToString(format, CultureInfo.InvariantCulture)}, {y.ToString(format, CultureInfo.InvariantCulture)}, {z.ToString(format, CultureInfo.InvariantCulture)}");

    private static bool TryParse3(string text, out FVector value)
    {
        value = default;
        var parts = (text ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var v = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
            {
                return false;
            }
        }

        value = new FVector(v[0], v[1], v[2]);
        return true;
    }

    private bool CanApplyTransform() => SelectedActor is { IsDeleted: false };

    /// <summary>Writes the edited location/rotation/scale of the selected actor's root into the project.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyTransform))]
    private void ApplyTransform()
    {
        if (SelectedActor is not { } item)
        {
            return;
        }

        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Moves"));
            return;
        }

        if (!TryParse3(EditLocation, out var location) || !TryParse3(EditRotation, out var rotation) || !TryParse3(EditScale, out var scale))
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.InvalidTransform"), Localization.Loc.T("Map.InvalidTransformDetail"));
            return;
        }

        var value = new TransformValue(location, new FRotator(rotation.X, rotation.Y, rotation.Z), scale);
        if (SelectedInstanceInfo() is { } sel)
        {
            ApplyInstanceTransform(sel, value);
            return;
        }

        ApplyRootTransform(item, value, Localization.Loc.T("Map.Moved"));
    }

    private bool CanCopySelected() => SelectedActor is not null;

    /// <summary>Remembers the selected actor (or instance) for Paste.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        if (HasGroup)
        {
            CopyGroup();
            return;
        }

        _copiedGroup = null;
        if (SelectedInstanceInfo() is { } sel)
        {
            CopyInstance(sel);
            return;
        }

        if (SelectedActor is { } item)
        {
            _copiedInstance = null;
            CopiedActor = item;
            _services.Notifications.Info(Localization.Loc.T("Assets.Copied"), Localization.Loc.F("Map.CopiedActor", item.Name));
        }
    }

    private bool CanPaste() => HasCopiedActor && PreparedScene is not null;

    /// <summary>
    /// Places a copy of the copied actor in front of the camera, in the level of the selected actor (else the first loaded
    /// level): a duplicate inside its own level, a new mesh actor for a StaticMeshActor, otherwise a copy of the source
    /// actor (Blueprint building with its child actors) from the other level.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (TryPasteGroup())
        {
            return;
        }

        if (_copiedInstance is not null && CopiedActor is null && TryPasteInstance())
        {
            return;
        }

        if (CopiedActor is not { } copied || PreparedScene is not { } scene || scene.Documents.Count == 0)
        {
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Copies"));
            return;
        }

        var target = SelectedActor?.Level ?? scene.Documents.FirstOrDefault(d => !d.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase)) ?? scene.Documents[0];
        var at = AimPointProvider?.Invoke() ?? copied.Actor.WorldTransform.Translation;
        var world = RootWorldOf(copied, CurrentRootTransform(copied)) with { Translation = at };
        try
        {
            var op = CopyOp(copied, target, world, project.State);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Pasted"), entry.Op.Describe());
            SelectCreated(op);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.PasteFailed"), ex.Message);
        }
    }

    /// <summary>
    /// The op that places a copy of <paramref name="item"/> in <paramref name="target"/>: a duplicate inside its own level, a
    /// new mesh actor for a StaticMeshActor, otherwise a copy of the source actor (Blueprint building with its child actors).
    /// A copy of something the project added is a copy of what that was made from, so copies of copies work too.
    /// </summary>
    private EditOp CopyOp(ActorItemViewModel item, LevelDocument target, FTransform world, EditState state, ISet<ActorRef>? reserved = null)
    {
        var transform = TransformValue.FromTransform(world); // a new actor of its own stands in the world
        if (item.IsAdded)
        {
            switch (state.AddedActors.GetValueOrDefault(item.Reference))
            {
                case AddStaticMeshActorOp mesh:
                    return EditOpFactory.AddStaticMeshActor(target, mesh.StaticMesh, transform, state, reserved);
                case AddBlueprintActorOp blueprint:
                    return new AddBlueprintActorOp(target.PackagePath, EditOpFactory.UniqueActorName(target, BaseName(item) + "_Added", state, reserved), blueprint.ClassPath, blueprint.Source, transform);
                case DuplicateActorOp duplicate when _pristineActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, duplicate.Source)) is { } source:
                    item = source;
                    break;
                default:
                    throw new InvalidOperationException(item.Name); // undone since it was copied
            }
        }

        if (string.Equals(target.PackagePath, item.Level.PackagePath, StringComparison.OrdinalIgnoreCase))
        {
            // A duplicate keeps its source's parent: its place is relative to it.
            return new DuplicateActorOp(item.Reference, EditOpFactory.UniqueActorName(target, item.Name + "_Copy", state, reserved), RelativeOf(item, world));
        }

        return item.Actor.Kind == ActorKind.StaticMeshActor && item.Actor.StaticMeshPath is { } meshPath
            ? EditOpFactory.AddStaticMeshActor(target, meshPath, transform, state, reserved)
            : new AddBlueprintActorOp(target.PackagePath, EditOpFactory.UniqueActorName(target, BaseName(item) + "_Added", state, reserved), item.Actor.ClassPath, item.Reference, transform);
    }

    private static string BaseName(ActorItemViewModel item) => item.ClassName.EndsWith("_C", StringComparison.Ordinal) ? item.ClassName[..^2] : item.ClassName;

    /// <summary>Refreshes the scene after <paramref name="op"/> added an actor and selects the new actor.</summary>
    private void SelectCreated(EditOp op)
    {
        RefreshEdits();
        var created = op.GetPrimaryTarget();
        SelectedInstanceKey = null;
        SelectedActor = AllActors.FirstOrDefault(a => a.IsAdded && created is not null && ActorRef.Comparer.Equals(a.Reference, created)) ?? SelectedActor;
    }

    private bool CanDuplicateSelected() => SelectedActor is { IsDeleted: false };

    /// <summary>Copies the selected pristine actor 2 m along +X (the copy is exported as a copy of the source's exports).</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    private void DuplicateSelected()
    {
        if (SelectedActor is not { } item)
        {
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Copies"));
            return;
        }

        if (SelectedInstanceInfo() is { } sel)
        {
            DuplicateInstance(sel);
            return;
        }

        try
        {
            var world = RootWorldOf(item, CurrentRootTransform(item));
            var op = CopyOp(item, item.Level, world with { Translation = world.Translation + new FVector(200f, 0f, 0f) }, project.State);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Duplicated"), entry.Op.Describe());
            SelectCreated(op);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DuplicateFailed"), ex.Message);
        }
    }

    private static IReadOnlyList<PropertyRow> DescribeActor(ActorItemViewModel item, FTransform t)
    {
        var a = item.Actor;
        var rows = new List<PropertyRow>
        {
            new(Localization.Loc.T("Map.Row.Name"), a.Name),
            new(Localization.Loc.T("Map.Row.Class"), a.ClassPath),
            new(Localization.Loc.T("Map.Row.Level"), item.Level.PackagePath),
            new(Localization.Loc.T("Map.Row.Location"), Fmt($"{t.Translation.X:0.#}, {t.Translation.Y:0.#}, {t.Translation.Z:0.#} cm")),
            new(Localization.Loc.T("Map.Row.Rotation"), Fmt($"{t.Rotation.Rotator().Pitch:0.##}, {t.Rotation.Rotator().Yaw:0.##}, {t.Rotation.Rotator().Roll:0.##} deg")),
            new(Localization.Loc.T("Map.Row.Scale"), Fmt($"{t.Scale3D.X:0.###}, {t.Scale3D.Y:0.###}, {t.Scale3D.Z:0.###}")),
            new(Localization.Loc.T("Map.Row.Kind"), a.Kind.ToString()),
            new(Localization.Loc.T("Map.Row.Components"), a.Components.Count.ToString(CultureInfo.InvariantCulture)),
        };
        if (a.StaticMeshPath is { } mesh)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.Mesh"), mesh));
        }

        if (a.InstanceTransforms.Count > 0)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.Instances"), a.InstanceTransforms.Count.ToString("N0", CultureInfo.InvariantCulture)));
        }

        rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.Export"), item.IsAdded ? Localization.Loc.T("Map.State.AddedShort") : "#" + a.ExportIndex.ToString(CultureInfo.InvariantCulture)));
        if (item.IsDeleted)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Deleted")));
        }
        else if (item.IsAdded)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Added")));
        }

        return rows;

        static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    private bool CanDeleteSelected() => SelectedActor is { IsDeleted: false };

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (SelectedActor is not { } item)
        {
            return;
        }

        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Deletes"));
            return;
        }

        if (SelectedInstanceInfo() is { } sel)
        {
            DeleteInstance(sel);
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new DeleteActorOp(item.Reference));
            _services.Notifications.Info(Localization.Loc.T("Map.Deleted"), entry.Op.Describe());
            RefreshHiddenIds();
            ActorProperties = DescribeSelected(item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DeleteFailed"), ex.Message);
        }
    }

    private bool CanDeleteAllOfMesh() => SelectedActor is { IsDeleted: false } item && KindMeshOf(item) is not null;

    /// <summary>Deletes every loaded actor drawing the selected actor's mesh (Blueprints: their root's mesh).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteAllOfMesh))]
    private void DeleteAllOfMesh() => DeleteAllOfKind(MatchBy.StaticMesh);

    private bool CanDeleteAllOfClass() => SelectedActor is { IsDeleted: false };

    /// <summary>Deletes every loaded actor of the selected actor's class.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteAllOfClass))]
    private void DeleteAllOfClass() => DeleteAllOfKind(MatchBy.Class);

    /// <summary>
    /// "Delete all of the same kind" over the loaded levels. ISM/HISM instances are left alone for now: the viewport hides
    /// whole actors only and the exporter does not rewrite instance arrays yet.
    /// </summary>
    private void DeleteAllOfKind(MatchBy by)
    {
        if (SelectedActor is not { } item || PreparedScene is not { } scene)
        {
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Deletes"));
            return;
        }

        var path = by == MatchBy.StaticMesh ? KindMeshOf(item) : item.Actor.ClassPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var match = new KindMatch(by, path);
        var scope = scene.Documents.Count == 1
            ? EditScope.ForLevel(scene.Documents[0].PackagePath)
            : _loadedCell is { } cell ? EditScope.ForCell(cell) : EditScope.Island;
        try
        {
            // Same-mesh deletes include the ISM/HISM instances drawing that mesh (foliage, rocks); the viewport hides them
            // individually and the exporter collapses them in place.
            var op = EditOpFactory.DeleteAllOfKind(scene.Documents, match, scope, project.State, includeInstances: by == MatchBy.StaticMesh);
            if (op.Actors.Count == 0 && op.Instances.Count == 0)
            {
                _services.Notifications.Info(Localization.Loc.T("Map.NothingToDelete"), Localization.Loc.T(by == MatchBy.StaticMesh ? "Map.NothingToDelete.Mesh" : "Map.NothingToDelete.Class"));
                return;
            }

            var entry = _services.Projects.Apply(op);
            var title = string.Create(CultureInfo.CurrentCulture,
                $"{Localization.Loc.F("Map.DeletedActors", op.Actors.Count, op.Instances.Count > 0 ? Localization.Loc.F("Map.PlusInstances", op.Instances.Count) : string.Empty)}");
            _services.Notifications.Info(title, entry.Op.Describe() + Localization.Loc.T("Map.CtrlZUndoes"));
            RefreshHiddenIds();
            ActorProperties = DescribeSelected(item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DeleteFailed"), ex.Message);
        }
    }

    /// <summary>The mesh that defines an actor's "kind": a Blueprint's root mesh, otherwise the actor's mesh.</summary>
    private static string? KindMeshOf(ActorItemViewModel item) =>
        item.Actor.Kind == ActorKind.Blueprint ? item.Actor.Root?.StaticMeshPath : item.Actor.StaticMeshPath;

    private int CountOfKind(KindMatch match) => AllActors.Count(a => !a.IsDeleted && EditOpFactory.Matches(a.Actor, match));

    /// <summary>Live ISM/HISM instances (not deleted, owner kept) of the loaded levels that draw <paramref name="mesh"/>.</summary>
    private int CountInstancesOfMesh(string mesh)
    {
        var state = _services.Projects.Current?.State;
        var count = 0;
        foreach (var actor in AllActors.Where(a => !a.IsDeleted))
        {
            foreach (var instance in actor.Actor.InstanceTransforms)
            {
                if (instance.StaticMeshPath is { } path && EditOpFactory.SameObject(path, mesh)
                    && state?.IsDeleted(new InstanceRef(actor.Level.PackagePath, actor.Name, instance.ComponentName, instance.InstanceIndex)) != true)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static string ShortName(string objectPath)
    {
        var name = objectPath[(objectPath.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private bool CanFrameSelection() => SelectedActor is not null;

    [RelayCommand(CanExecute = nameof(CanFrameSelection))]
    private void FrameSelection() => FrameSelectionRequested?.Invoke(this, EventArgs.Empty);

    // The last picks (newest last), by reference: ids change when levels stream in and out.
    private readonly List<(ActorRef Actor, string? Component, int Index)> _picks = [];

    private void Remember(ActorItemViewModel item)
    {
        var pick = (item.Reference, SelectedInstanceKey?.Component, SelectedInstanceKey?.InstanceIndex ?? 0);
        _picks.RemoveAll(p => SamePick(p, pick));
        _picks.Add(pick);
        if (_picks.Count > 30)
        {
            _picks.RemoveAt(0);
        }

        SelectPreviousCommand.NotifyCanExecuteChanged();
    }

    private static bool SamePick((ActorRef Actor, string? Component, int Index) a, (ActorRef Actor, string? Component, int Index) b) =>
        ActorRef.Comparer.Equals(a.Actor, b.Actor) && string.Equals(a.Component, b.Component, StringComparison.OrdinalIgnoreCase) && a.Index == b.Index;

    private bool CanSelectPrevious() => _picks.Count > 0;

    /// <summary>Back to the object picked before this one (or the last one, after a click on nothing), and frames it.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectPrevious))]
    private void SelectPrevious() => GoToPrevious(frame: true);

    /// <summary>Selects the object picked before this one (framing it when <paramref name="frame"/>); false when there is none.</summary>
    public bool GoToPrevious(bool frame)
    {
        var current = SelectedActor is { } selected
            ? (selected.Reference, SelectedInstanceKey?.Component, SelectedInstanceKey?.InstanceIndex ?? 0)
            : default;
        for (var i = _picks.Count - 1; i >= 0; i--)
        {
            var pick = _picks[i];
            if ((SelectedActor is not null && SamePick(pick, current))
                || AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, pick.Actor)) is not { } item)
            {
                continue;
            }

            SelectedInstanceKey = pick.Component is null ? null : InstanceKey.Of(item.SelectableId, pick.Component, pick.Index);
            SelectedActor = item;
            Remember(item);
            if (frame)
            {
                FrameSelectionRequested?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        return false;
    }

    private void OnProjectsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectSession.CanUndo) or nameof(ProjectSession.CanRedo) or nameof(ProjectSession.Current))
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Any journal change (this page, another page or an AI client): re-derive the edits shown in the scene.</summary>
    private void OnProjectChanged(object? sender, EventArgs e) => RefreshEdits();

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadWorldAsync(catalog);
        }
        else
        {
            _loadCts?.Cancel();
            World = null;
            Nodes = [];
            KindChips = [];
            PreparedScene = null;
            AllActors = [];
            Actors = [];
            SelectedActor = null;
            LoadedLevelsCaption = Localization.Loc.T("Map.NoLevelLoaded");
            LoadStatus = string.Empty;
            UpdateEmptyState();
        }
    }

    private async Task LoadWorldAsync(AssetCatalog catalog)
    {
        IsLoadingWorld = true;
        UpdateEmptyState();
        try
        {
            // Names first, then each sublevel's World Composition tile (position, bounds, layer, parent) from its header.
            var index = await Task.Run(() => WorldIndex.FromCatalog(catalog).WithTileInfo(catalog, _services.Logger)).ConfigureAwait(true);
            if (!ReferenceEquals(_services.Workspace.Catalog, catalog))
            {
                return;
            }

            LoadWorld(index);
            _services.Logger.LogInformation("World index: {Count} packages under {Root}.", index.Packages.Count, index.Root);

            // With the game's paks connected the Map opens on the whole island; a cell or level picked later loads over it.
            if (catalog.SourceKind == AssetSourceKind.Paks && PreparedScene is null && WorldBackdrop is null
                && index.Packages.Any(p => p.Kind == WorldPackageKind.Landscape))
            {
                _ = OpenWholeIslandAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Could not index The_Island: {Message}", ex.Message);
            _services.Notifications.Warning(Localization.Loc.T("Map.WorldIndexFailed"), ex.Message);
        }
        finally
        {
            IsLoadingWorld = false;
            UpdateEmptyState();
        }
    }

    private void RebuildTree()
    {
        if (World is not { } world)
        {
            Nodes = [];
            return;
        }

        var text = FilterText.Trim();
        var nodes = text.Length == 0
            ? BuildTree(world)
            : BuildTree(world, p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (text.Length > 0)
        {
            foreach (var node in nodes)
            {
                ExpandAll(node);
            }
        }

        Nodes = nodes;
        UpdateEmptyState();

        static void ExpandAll(WorldTreeNode node)
        {
            if (node.Children.Count == 0)
            {
                return;
            }

            node.IsExpanded = true;
            foreach (var child in node.Children)
            {
                ExpandAll(child);
            }
        }
    }

    private void UpdateEmptyState()
    {
        EmptyStateNeedsSetup = false;
        if (IsLoadingWorld)
        {
            EmptyTitle = Localization.Loc.T("Map.Empty.Reading");
            EmptyMessage = Localization.Loc.T("Map.Empty.ReadingBody");
            ShowEmptyState = true;
        }
        else if (World is null)
        {
            EmptyTitle = Localization.Loc.T("Map.Empty.NoGame");
            EmptyMessage = Localization.Loc.T("Map.Empty.NoGameBody");
            EmptyStateNeedsSetup = true;
            ShowEmptyState = true;
        }
        else if (Nodes.Count == 0)
        {
            EmptyTitle = FilterText.Length > 0 ? Localization.Loc.T("Map.Empty.NoMatch") : Localization.Loc.T("Map.Empty.NoLevels");
            EmptyMessage = FilterText.Length > 0
                ? Localization.Loc.F("Map.Empty.NoMatchBody", FilterText.Trim())
                : Localization.Loc.T("Map.Empty.NoLevelsBody");
            ShowEmptyState = true;
        }
        else
        {
            ShowEmptyState = false;
        }
    }

    private static IReadOnlyList<PropertyRow> Describe(WorldPackage p)
    {
        var rows = new List<PropertyRow>
        {
            new(Localization.Loc.T("Map.Row.Name"), p.Name),
            new(Localization.Loc.T("Map.Row.Kind"), KindLabel(p.Kind) + (p.IsMap ? Localization.Loc.T("Map.Kind.Level") : Localization.Loc.T("Map.Kind.Asset"))),
            new("Cell", p.Cell?.ToString() ?? "-"),
            new(Localization.Loc.T("Assets.Row.Package"), p.PackagePath),
            new(Localization.Loc.T("Assets.Row.File"), p.FilePath),
        };
        if (p.Folder.Length > 0)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Projects.Row.Folder"), p.Folder));
        }

        if (p.LandscapeQuadrant is { } quadrant)
        {
            rows.Add(new PropertyRow("Quadrant", quadrant.ToString(CultureInfo.InvariantCulture) + (p.LandscapeVariant is { } v ? v.ToString() : string.Empty)));
        }

        if (p.Owner is { } owner)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.BelongsTo"), owner));
        }

        if (p.IsMap)
        {
            rows.Add(new PropertyRow("Built data", p.BuiltDataPackage is { } bd ? bd[(bd.LastIndexOf('/') + 1)..] : "none"));
            rows.Add(new PropertyRow("Streamed", p.IsStreamed switch { true => "yes", false => "no (not in StreamingLevels)", null => "not checked" }));
        }

        if (p.Tile is { } tile)
        {
            var size = tile.Size;
            var center = tile.Center;
            rows.Add(new PropertyRow("Tile position", string.Create(CultureInfo.InvariantCulture, $"{tile.Position.X}, {tile.Position.Y}, {tile.Position.Z}")));
            rows.Add(new PropertyRow("Tile bounds", tile.BoundsValid
                ? string.Create(CultureInfo.InvariantCulture, $"{size.X / 100f:0} × {size.Y / 100f:0} × {size.Z / 100f:0} m around ({center.X / 100f:0}, {center.Y / 100f:0}, {center.Z / 100f:0}) m")
                : "not set"));
            rows.Add(new PropertyRow("Layer", tile.Layer.DistanceStreamingEnabled
                ? string.Create(CultureInfo.InvariantCulture, $"{tile.Layer.Name} (streams within {tile.Layer.StreamingDistance / 100f:0} m)")
                : $"{tile.Layer.Name} (no distance streaming)"));
            rows.Add(new PropertyRow("Parent tile", p.ParentPackagePath is { } parent ? parent[(parent.LastIndexOf('/') + 1)..] : tile.HasParent ? tile.ParentTilePackageName : "none (root tile)"));
            if (tile.ZOrder != 0)
            {
                rows.Add(new PropertyRow("Z order", tile.ZOrder.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return rows;
    }

    private bool CanUndo() => _services.Projects.CanUndo;

    private bool CanRedo() => _services.Projects.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        try
        {
            if (_services.Projects.Undo() is { } entry)
            {
                _services.Notifications.Info(Localization.Loc.T("Module.Undone"), entry.Op.Describe());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Shell.UndoFailed"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        try
        {
            if (_services.Projects.Redo() is { } entry)
            {
                _services.Notifications.Info(Localization.Loc.T("Module.Redone"), entry.Op.Describe());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Shell.RedoFailed"), ex.Message);
        }
    }

    /// <summary>One click: creates the project in Documents\ScumStudio Projects, or opens it when that name already exists.</summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        var folder = ProjectsPageViewModel.DefaultProjectsFolder();
        var name = string.IsNullOrWhiteSpace(NewProjectName) ? "MyMapMod" : NewProjectName.Trim();
        var existing = Path.Combine(folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + Project.FolderExtension);
        if (Directory.Exists(existing))
        {
            await OpenProjectAsync(existing).ConfigureAwait(true);
        }
        else
        {
            await CreateProjectAsync(folder).ConfigureAwait(true);
        }
    }

    /// <summary>One click: builds the mod from the project (see <see cref="ModExportService.QuickExportAsync"/>).</summary>
    [RelayCommand]
    private async Task ExportModAsync()
    {
        var project = _services.Projects.Current;
        if (ModExportService.Blocker(_services, project) is { } blocker)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.CannotExportYet"), blocker);
            return;
        }

        var (ok, export) = await _services.Operations.RunAsync(Localization.Loc.F("Projects.Exporting", project!.Manifest.Name),
            (progress, ct) => ModExportService.QuickExportAsync(_services, project, ProjectsPageViewModel.DefaultExportFolder(), progress, ct)).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        var (results, installed) = export;
        var client = results[0];
        var summary = string.Create(CultureInfo.CurrentCulture,
            $"{client.PakPath}{(installed.Count > 0 ? Localization.Loc.F("Map.ServerPakCopied", Path.GetDirectoryName(installed[0])) : string.Empty)}");
        if (results.Count == 1 && ModExportService.ResolveServerPaks(_services.Settings.Load().ServerPaksFolder) is null)
        {
            summary += Localization.Loc.T("Map.NoServerPak");
        }

        _services.Notifications.Success(Localization.Loc.T("Map.ModReady"), summary);
    }

    [RelayCommand]
    private async Task OpenProjectPickerAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Localization.Loc.T("Projects.PickProject")).ConfigureAwait(true) is { } folder)
        {
            await OpenProjectAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CloseProject() => _services.Projects.Close();

    [RelayCommand]
    private void OpenSetup() => _openSetup();
}
