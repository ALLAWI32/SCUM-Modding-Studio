using Microsoft.Extensions.Logging;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Placing a Blueprint by its path (Assets "Place in map", the Add object box). Discord user igor: "It would be preferable
/// to create blueprints like these, with the option to install them on the map". A Blueprint is placed by copying one the
/// game placed somewhere on the island (<see cref="PlacedBlueprintIndex"/>); its level need not be loaded, so the copy is
/// drawn with that actor's placements read from its level.
/// </summary>
public sealed partial class MapPageViewModel
{
    private Task<PlacedBlueprintIndex>? _placedIndex;
    private AssetCatalog? _placedCatalog;

    // Per copied actor, so a copy's clone stays equal between refreshes (the viewport rebuilds a clone that changed).
    private readonly Dictionary<ActorRef, IReadOnlyList<ScenePlacement>> _foreignPlacements = new(ActorRef.Comparer);
    private readonly HashSet<string> _foreignReads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Completes when the last Blueprint placement (<see cref="AddBlueprintAsync"/>) finished (tests).</summary>
    public Task<bool> AddCompletion { get; private set; } = Task.FromResult(false);

    /// <summary>
    /// The level new objects go to: of the loaded levels that are not landscape tiles or the island's spawn data, the one
    /// whose objects come nearest to <paramref name="at"/> (the game streams a level in by where its objects are, so a copy
    /// put into a far level showed only when that one loaded — the owner pasted a building far away and found nothing);
    /// without a place, the first of them.
    /// </summary>
    private static LevelDocument NewObjectLevel(PreparedLevelScene scene, FVector? at = null)
    {
        var levels = scene.Documents.Where(d => !d.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(d.PackagePath, Level.Spawns.SpawnPlaces.StaticDataPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (levels.Count == 0)
        {
            return scene.Documents[0];
        }

        if (at is not { } p || levels.Count == 1)
        {
            return levels[0];
        }

        return levels.MinBy(d => d.Actors.Where(a => a.Kind != ActorKind.Other)
            .Select(a => a.WorldTransform.Translation)
            .Select(t => ((t.X - p.X) * (t.X - p.X)) + ((t.Y - p.Y) * (t.Y - p.Y)))
            .DefaultIfEmpty(float.MaxValue)
            .Min())!;
    }

    /// <summary>
    /// Adds the Blueprint <paramref name="classPackage"/> (its package path) where the viewport aims, in the level new objects
    /// go to, as a copy of one placed somewhere on the island (only that one's level is read). An item (a drill press, a
    /// chest) is never placed as an actor: a copy of one of the game's world item spawners is set to spawn it.
    /// </summary>
    public async Task<bool> AddBlueprintAsync(string classPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classPackage);
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

        if (_services.Workspace.Catalog is not { } catalog)
        {
            return false;
        }

        // Where and in which level are taken now: the camera can move while the source is found and read.
        var at = AimPointProvider?.Invoke() ?? FVector.Zero;
        var target = NewObjectLevel(scene, at);
        var item = ItemClassOf(catalog, classPackage);
        try
        {
            var index = await PlacedIndexAsync(catalog).ConfigureAwait(true);
            var candidates = item is not null ? index.SpawnersFor(item) : index.Of(classPackage);
            if (await Task.Run(() => FirstReadable(catalog, candidates)).ConfigureAwait(true) is not { } found)
            {
                _services.Notifications.Warning(Localization.Loc.T("Map.BlueprintNotPlaced"), Localization.Loc.F("Map.BlueprintNotPlacedDetail", ShortName(classPackage)));
                return false;
            }

            var (level, source, placements) = found;
            if (!ReferenceEquals(_services.Projects.Current, project))
            {
                return false; // the project was closed meanwhile
            }

            var reference = new ActorRef(level, source.Name);
            _foreignPlacements[reference] = placements;
            var name = EditOpFactory.UniqueActorName(target, ShortName(item ?? source.ClassPath) + "_Added", project.State);
            var op = new AddBlueprintActorOp(target.PackagePath, name, source.ClassPath, reference,
                TransformValue.FromTransform(source.WorldTransform with { Translation = at }))
            {
                Item = item,
            };
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Added"), entry.Op.Describe());
            SelectCreated(op);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.AddFailed"), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The item class a Blueprint package is when it is an item (its class derives from one of the game's item classes):
    /// <c>…/Work_Drillpress_01</c> gives <c>…/Work_Drillpress_01.Work_Drillpress_01_C</c>, and an <c>_ES</c> variant its base
    /// item's. Null for other Blueprints.
    /// </summary>
    private static string? ItemClassOf(AssetCatalog catalog, string classPackage)
    {
        var package = AssetPaths.SplitObjectPath(classPackage).PackagePath;
        if (package.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) && catalog.PackageExists(package[..^3]))
        {
            package = package[..^3];
        }

        return IsItemClass(catalog, package) ? package + "." + package[(package.LastIndexOf('/') + 1)..] + "_C" : null;
    }

    // Native SCUM item classes whose names do not end in "Item".
    private static readonly HashSet<string> NativeItemClasses = new(StringComparer.Ordinal) { "ItemContainer", "MountedLamp", "Bedroll" };

    /// <summary>
    /// True when the Blueprint in <paramref name="package"/> is an item: the native class its parent chain ends in is one of the
    /// game's item classes (EquipmentItem, ChestItem, WeaponItem …). An Items folder also holds bullets, loot presets and
    /// decorations, which a world item spawner must never be set to (review of the Assets "Place in map").
    /// </summary>
    private static bool IsItemClass(AssetCatalog catalog, string package)
    {
        for (var depth = 0; depth < 16 && catalog.TryLoadPackage(package, out var loaded); depth++)
        {
            string path;
            try
            {
                if (loaded.GetExports().OfType<CUE4Parse.UE4.Objects.UObject.UStruct>().FirstOrDefault(s => s.SuperStruct is { IsNull: false })?.SuperStruct?.ResolvedObject is not { } super)
                {
                    return false;
                }

                path = super.GetPathName();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }

            if (path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            {
                var name = path[(path.LastIndexOf('.') + 1)..];
                return name.EndsWith("Item", StringComparison.Ordinal) || NativeItemClasses.Contains(name);
            }

            package = AssetPaths.SplitObjectPath(AssetPaths.NormalizeObjectPath(path, catalog.ProjectName)).PackagePath;
        }

        return false;
    }

    /// <summary>The island's placed Blueprints for <paramref name="catalog"/>, built once on a worker.</summary>
    private Task<PlacedBlueprintIndex> PlacedIndexAsync(AssetCatalog catalog)
    {
        if (_placedIndex is null || _placedIndex.IsFaulted || !ReferenceEquals(_placedCatalog, catalog))
        {
            var world = World;
            _placedCatalog = catalog;
            _placedIndex = Task.Run(() => PlacedBlueprintIndex.Build(catalog, world ?? WorldIndex.FromCatalog(catalog)));
        }

        return _placedIndex;
    }

    /// <summary>The first candidate whose level reads and holds it, with its placements (worker thread); null when none does.</summary>
    private (string Level, ActorRecord Actor, IReadOnlyList<ScenePlacement> Placements)? FirstReadable(AssetCatalog catalog, IReadOnlyList<PlacedActor> candidates)
    {
        // ponytail: three tries; an unreadable level is rare
        foreach (var candidate in candidates.Take(3))
        {
            try
            {
                var document = ReadDocument(catalog, candidate.Level, CancellationToken.None);
                if (document.FindActor(candidate.Actor) is { } actor)
                {
                    return (document.PackagePath, actor, PlacementsOf(document, actor.Name));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
            {
                _services.Logger.LogWarning("{Level} could not be read for {Actor}: {Message}", candidate.Level, candidate.Actor, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// The placements of a copied actor whose level is not loaded, or null while that level is read on a worker (the edits
    /// are refreshed when it arrives). Old pastes and reopened projects come this way.
    /// </summary>
    private IReadOnlyList<ScenePlacement>? ForeignPlacements(ActorRef source)
    {
        if (_foreignPlacements.TryGetValue(source, out var cached))
        {
            return cached;
        }

        if (_services.Workspace.Catalog is { } catalog && _services.Projects.Current?.State is { } state && _foreignReads.Add(source.Level))
        {
            var wanted = state.AddedActors.Values.OfType<AddBlueprintActorOp>().Select(b => b.Source)
                .Where(s => string.Equals(s.Level, source.Level, StringComparison.OrdinalIgnoreCase)).Distinct(ActorRef.Comparer).ToList();
            _ = Task.Run(() =>
                {
                    var document = ReadDocument(catalog, source.Level, CancellationToken.None);
                    return wanted.ToDictionary(s => s, s => PlacementsOf(document, s.Actor), ActorRef.Comparer);
                })
                .ContinueWith(t => _services.Dispatcher.Invoke(() =>
                {
                    _foreignReads.Remove(source.Level);
                    if (t.IsFaulted)
                    {
                        var reason = t.Exception?.GetBaseException().Message ?? string.Empty;
                        _services.Logger.LogWarning("{Level} could not be read to draw the copies made from it: {Message}", source.Level, reason);
                        _services.Notifications.Warning(Localization.Loc.T("Map.CopyNotDrawn"), Localization.Loc.F("Map.CopyNotDrawnDetail", source.Level[(source.Level.LastIndexOf('/') + 1)..], reason));
                    }

                    foreach (var reference in wanted)
                    {
                        var placements = t.IsCompletedSuccessfully ? t.Result[reference] : [];
                        if (t.IsCompletedSuccessfully && placements.Count == 0)
                        {
                            _services.Notifications.Warning(Localization.Loc.T("Map.CopyNotDrawn"), Localization.Loc.F("Map.CopyNothingToDraw", reference.Actor, reference.Level[(reference.Level.LastIndexOf('/') + 1)..]));
                        }

                        _foreignPlacements.TryAdd(reference, placements);
                    }

                    if (ReferenceEquals(_services.Workspace.Catalog, catalog))
                    {
                        RefreshEdits();
                    }
                }), TaskScheduler.Default);
        }

        return null;
    }

    /// <summary>The placements of one actor of <paramref name="document"/> (its meshes and spawn pins), as its level draws them.</summary>
    private IReadOnlyList<ScenePlacement> PlacementsOf(LevelDocument document, string actorName) =>
        document.FindActor(actorName) is { } actor
            ? LevelScenePreparer.CollectPlacements(document, 0, new LevelSceneOptions { Filter = actor.Name }, _prepareCache.SpawnModels).Where(p => ReferenceEquals(p.Actor, actor)).ToList()
            : [];
}
