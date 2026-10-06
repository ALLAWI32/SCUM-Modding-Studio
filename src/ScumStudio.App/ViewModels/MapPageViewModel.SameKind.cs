using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Select all of this kind": Ctrl+A highlights every actor and instance drawing the selected object's mesh in the
/// loaded levels (all the oaks of one species around you); Ctrl+Shift+A takes the whole island. Delete then removes
/// them all in one undoable step; Esc or a new click drops the selection.
/// </summary>
public sealed partial class MapPageViewModel
{
    private string? _kindMesh;
    private bool _kindWholeMap;

    /// <summary>Actors of the kind selection (highlighted by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyCollection<uint> _kindSelectionIds = [];

    /// <summary>Instances of the kind selection (highlighted by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyCollection<InstanceKey> _kindSelectionInstances = [];

    /// <summary>"312 × Oak_Forest_1 in the loaded levels …", or null without a kind selection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKindSelection))]
    private string? _kindSelectionText;

    /// <summary>True while a kind selection is active.</summary>
    public bool HasKindSelection => KindSelectionText is not null;

    /// <summary>Selects everything drawing the selected object's mesh: the loaded levels, or the whole island.</summary>
    public void SelectAllOfKind(bool wholeMap)
    {
        var mesh = SelectedInstanceInfo() is { } sel ? MeshOf(sel) : SelectedActor is { } item ? KindMeshOf(item) : null;
        if (mesh is null)
        {
            _services.Notifications.Info("Select an object first", "Click a tree, rock or building, then Ctrl+A selects all of its kind (Ctrl+Shift+A: the whole map).");
            return;
        }

        _group.Clear();
        GroupWorlds = [];
        _kindMesh = mesh;
        _kindWholeMap = wholeMap;
        _kindAnchor = (SelectedInstanceInfo() is { } anchor ? WorldOf(anchor.Item, anchor) : SelectedActor is { } at ? WorldOf(at, null) : (FTransform?)null)?.Translation;
        CollectKind(mesh);
    }

    /// <summary>Drops the kind selection.</summary>
    public void ClearKindSelection()
    {
        _kindMesh = null;
        _group.Clear();
        GroupWorlds = [];
        KindSelectionIds = [];
        KindSelectionInstances = [];
        KindSelectionText = null;
    }

    /// <summary>Re-collects the kind selection over the levels now loaded (after streaming swapped them).</summary>
    private void RefreshKindSelection()
    {
        if (_kindMesh is { } mesh)
        {
            CollectKind(mesh);
        }
        else if (HasGroup)
        {
            RefreshGroup();
        }
    }

    private void CollectKind(string mesh)
    {
        var state = _services.Projects.Current?.State;
        var match = new KindMatch(MatchBy.StaticMesh, mesh);
        var ids = new List<uint>();
        var instances = new List<InstanceKey>();
        // Within the radius (metres) around the object Ctrl+A was pressed on; 0 = everything loaded.
        var radius = _kindWholeMap ? 0f : (float)KindRadius * 100f;
        bool Near(FVector at) => radius <= 0f || _kindAnchor is not { } anchor || FVector.Distance(at, anchor) <= radius;
        foreach (var a in AllActors.Where(a => !a.IsDeleted))
        {
            if (EditOpFactory.Matches(a.Actor, match))
            {
                if (Near(ActorTransforms.TryGetValue(a.SelectableId, out var moved) ? moved.Translation : a.Actor.WorldTransform.Translation))
                {
                    ids.Add(a.SelectableId);
                }

                continue;
            }

            foreach (var i in a.Actor.InstanceTransforms)
            {
                if (i.StaticMeshPath is { } path && EditOpFactory.SameObject(path, mesh)
                    && state?.IsDeleted(new InstanceRef(a.Level.PackagePath, a.Name, i.ComponentName, i.InstanceIndex)) != true
                    && Near(i.WorldTransform.Translation))
                {
                    instances.Add(InstanceKey.Of(a.SelectableId, i.ComponentName, i.InstanceIndex));
                }
            }
        }

        KindSelectionIds = ids;
        KindSelectionInstances = instances;
        var count = ids.Count + instances.Count;
        KindSelectionText = Localization.Loc.F(_kindWholeMap ? "Map.Kind.Island" : radius > 0f ? "Map.Kind.Within" : "Map.Kind.Loaded",
            count.ToString("N0", CultureInfo.CurrentCulture), ShortName(mesh), KindRadius.ToString("0", CultureInfo.CurrentCulture));
        OnPropertyChanged(nameof(HasKindRadius));
    }

    private FVector? _kindAnchor;

    /// <summary>"All of this kind" only within this many metres of the object it started from (0 = every loaded one).</summary>
    [ObservableProperty]
    private double _kindRadius;

    /// <summary>True while an "all of this kind" selection (not the whole island) can be narrowed with <see cref="KindRadius"/>.</summary>
    public bool HasKindRadius => _kindMesh is not null && !_kindWholeMap;

    /// <summary><see cref="KindRadius"/> as text ("all" at 0).</summary>
    public string KindRadiusText => KindRadius <= 0 ? Localization.Loc.T("Map.Kind.RadiusAll") : string.Create(CultureInfo.CurrentCulture, $"{KindRadius:0} m");

    partial void OnKindRadiusChanged(double value)
    {
        OnPropertyChanged(nameof(KindRadiusText));
        RefreshKindSelection();
    }

    /// <summary>Deletes the kind selection in one journal step (the whole island reads every level that imports the mesh).</summary>
    public async Task DeleteKindSelectionAsync()
    {
        if (HasGroup)
        {
            DeleteGroup();
            return;
        }

        if (_kindMesh is not { } mesh || PreparedScene is not { } scene)
        {
            return;
        }

        if (!_kindWholeMap && KindRadius > 0)
        {
            DeleteHighlighted(mesh);
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning("No project open", "Create or open a project first; deletions are recorded in its journal.");
            return;
        }

        var wholeMap = _kindWholeMap;
        IReadOnlyList<LevelDocument> levels = scene.Documents;
        if (wholeMap && _services.Workspace.Catalog is { } catalog && World is { } world)
        {
            LoadStatus = Localization.Loc.F("Map.Kind.Looking", ShortName(mesh));
            levels = await Task.Run(() => LevelsUsing(catalog, world, mesh, scene.Documents)).ConfigureAwait(true);
        }

        try
        {
            var op = EditOpFactory.DeleteAllOfKind(levels, new KindMatch(MatchBy.StaticMesh, mesh), wholeMap ? EditScope.Island : ScopeOfLoaded(scene),
                project.State, includeInstances: true);
            if (op.Actors.Count == 0 && op.Instances.Count == 0)
            {
                _services.Notifications.Info("Nothing to delete", $"No {ShortName(mesh)} is left.");
                return;
            }

            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(string.Create(CultureInfo.CurrentCulture,
                $"Deleted {op.Actors.Count + op.Instances.Count:N0} × {ShortName(mesh)}{(wholeMap ? " on the whole island" : string.Empty)}"), entry.Op.Describe() + " — Ctrl+Z undoes it");
            ClearKindSelection();
            RefreshHiddenIds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error("Delete failed", ex.Message);
        }
        finally
        {
            LoadStatus = string.Empty;
        }
    }

    /// <summary>Deletes exactly what the kind selection highlights (the radius-limited case) in one journal step.</summary>
    private void DeleteHighlighted(string mesh)
    {
        if (_services.Projects.Current is not { } project || PreparedScene is not { } scene)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Moves"));
            return;
        }

        var byId = AllActors.ToDictionary(a => a.SelectableId);
        var actors = KindSelectionIds.Where(byId.ContainsKey).Select(id => byId[id].Reference).ToList();
        var instances = KindSelectionInstances.Where(k => byId.ContainsKey(k.SelectableId))
            .Select(k => new InstanceRef(byId[k.SelectableId].Level.PackagePath, byId[k.SelectableId].Name, k.Component, k.InstanceIndex)).ToList();
        if (actors.Count + instances.Count == 0)
        {
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new DeleteAllOfKindOp(new KindMatch(MatchBy.StaticMesh, mesh), ScopeOfLoaded(scene), actors, instances));
            _services.Notifications.Info(Localization.Loc.F("Map.Kind.DeletedNear", actors.Count + instances.Count, ShortName(mesh), KindRadius.ToString("0", CultureInfo.CurrentCulture)),
                entry.Op.Describe() + Localization.Loc.T("Map.CtrlZUndoes"));
            ClearKindSelection();
            RefreshHiddenIds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DeleteFailed"), ex.Message);
        }
    }

    private static EditScope ScopeOfLoaded(PreparedLevelScene scene) =>
        scene.Documents.Count == 1 ? EditScope.ForLevel(scene.Documents[0].PackagePath) : EditScope.Island;

    /// <summary>
    /// The loaded documents plus every other map sublevel whose header imports the mesh's package (read only for those).
    /// Worker thread.
    /// </summary>
    private List<LevelDocument> LevelsUsing(AssetCatalog catalog, WorldIndex world, string mesh, IReadOnlyList<LevelDocument> loaded)
    {
        // ponytail: finds levels that import the mesh directly; a Blueprint that places it is not followed.
        var meshPackage = AssetPaths.SplitObjectPath(mesh).PackagePath;
        var have = loaded.Select(d => d.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var walker = new PackageDependencyWalker(catalog, _services.Logger);
        var candidates = world.Packages
            .Where(p => p.IsContentLevel
                        && !have.Contains(p.PackagePath))
            .Select(p => p.PackagePath)
            .ToList();
        var using_ = candidates.AsParallel().WithDegreeOfParallelism(4).Where(p =>
        {
            try
            {
                return walker.ReadReferencedPackages(p).Contains(meshPackage, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                _services.Logger.LogDebug("{Level}: {Message}", p, ex.Message);
                return false;
            }
        }).ToList();

        var reader = new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions(), _services.Logger);
        return [.. loaded, .. using_.Select(p => LevelDocument.Load(reader, p))];
    }
}
