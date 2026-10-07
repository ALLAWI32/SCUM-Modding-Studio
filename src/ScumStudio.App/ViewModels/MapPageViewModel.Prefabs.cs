using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>One saved prefab in the Prefabs menu.</summary>
public sealed class PrefabRow
{
    /// <summary>Creates the row over <paramref name="entry"/>.</summary>
    public PrefabRow(PrefabEntry entry, Action<PrefabRow> place, Func<PrefabRow, Task> export, Action<PrefabRow> delete)
    {
        Entry = entry;
        Place = new RelayCommand(() => place(this));
        Export = new AsyncRelayCommand(() => export(this));
        Delete = new RelayCommand(() => delete(this));
    }

    /// <summary>The prefab and its file.</summary>
    public PrefabEntry Entry { get; }

    /// <summary>Its name.</summary>
    public string Name => Entry.Name;

    /// <summary>"3 objects · Oct 6, 14:02".</summary>
    public string Caption => Loc.F("Map.Prefabs.Row", Entry.Prefab.Parts.Count, Entry.Prefab.Created.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture));

    /// <summary>Places copies of it where the camera aims.</summary>
    public ICommand Place { get; }

    /// <summary>Saves it as a file to share.</summary>
    public ICommand Export { get; }

    /// <summary>Removes it from the library.</summary>
    public ICommand Delete { get; }
}

/// <summary>
/// Prefabs (owner request): the selection (a whole building, any set of objects) saved under a name in a library that
/// belongs to the user, not the project; placed again anywhere as copies (one undo step) and shared as plain JSON files
/// (<see cref="Prefab"/>) that show the objects, their meshes and positions.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The library, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrefabs))]
    private IReadOnlyList<PrefabRow> _prefabs = [];

    /// <summary>Name for the next saved prefab (the object's name when empty).</summary>
    [ObservableProperty]
    private string _prefabName = string.Empty;

    /// <summary>True when the library holds something.</summary>
    public bool HasPrefabs => Prefabs.Count > 0;

    /// <summary>Re-reads the library.</summary>
    public void RefreshPrefabs() =>
        Prefabs = _services.Prefabs.List().Select(e => new PrefabRow(e, PlacePrefab, ExportPrefabAsync, DeletePrefab)).ToList();

    private void OnPrefabsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(RefreshPrefabs);

    private bool CanSavePrefab() => HasGroup || SelectedActor is not null;

    /// <summary>Saves the selection (the multi-selection, else the selected object) as a prefab named <see cref="PrefabName"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePrefab))]
    private void SavePrefab()
    {
        if (BuildPrefab(PrefabName) is not { } prefab)
        {
            _services.Notifications.Info(Loc.T("Map.Prefabs.Nothing"), Loc.T("Map.Prefabs.NothingDetail"));
            return;
        }

        try
        {
            var entry = _services.Prefabs.Save(prefab);
            _services.Notifications.Success(Loc.T("Map.Prefabs.Saved"), Loc.F("Map.Prefabs.SavedDetail", entry.Name, prefab.Parts.Count));
            PrefabName = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Map.Prefabs.SaveFailed"), ex.Message);
        }
    }

    /// <summary>
    /// The selection as a prefab: every object with its place relative to the set's centre on the ground. Null when
    /// nothing of it can be placed again (terrain, containers, road pieces, spawn parts).
    /// </summary>
    public Prefab? BuildPrefab(string? name)
    {
        var members = HasGroup
            ? GroupItems().Select(m => (m.Item, m.Instance)).ToList()
            : SelectedActor is { } one ? [(one, SelectedInstanceInfo())] : [];
        var parts = new List<(PrefabPart Part, FTransform World)>();
        foreach (var (item, sel) in members)
        {
            if (PartOf(item, sel) is { } part)
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var pivot = new FVector(parts.Average(p => p.World.Translation.X), parts.Average(p => p.World.Translation.Y), parts.Min(p => p.World.Translation.Z));
        return new Prefab
        {
            Name = string.IsNullOrWhiteSpace(name) ? parts.Count == 1 ? parts[0].Part.DisplayName : Loc.F("Map.Prefabs.DefaultName", parts.Count) : name.Trim(),
            Created = DateTimeOffset.UtcNow,
            AppVersion = MainWindowViewModel.ShortVersion,
            Pivot = pivot,
            Parts = parts.Select(p => p.Part with { Transform = TransformValue.FromTransform(p.World with { Translation = p.World.Translation - pivot }) }).ToList(),
        };
    }

    /// <summary>The part for one member (a whole actor, or one instance / part of it) with its world transform now, or null.</summary>
    private (PrefabPart Part, FTransform World)? PartOf(ActorItemViewModel item, SelectedInstance? sel)
    {
        var state = _services.Projects.Current?.State;
        if (sel is not null)
        {
            // A road piece is bent by the game and a spawn part has no mesh: neither can be placed again.
            if (sel.Component.SplineMesh is not null || MeshOf(sel) is not { } mesh)
            {
                return null;
            }

            var world = CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel);
            var part = StoredInstanceOf(sel) is { } source
                ? new PrefabPart { Kind = PrefabPartKind.FoliageInstance, Level = source.Level, Actor = source.Actor, Component = source.Component, Mesh = mesh, Collision = sel.Component.CollisionProfile }
                : new PrefabPart { Kind = PrefabPartKind.StaticMesh, Mesh = mesh, Collision = sel.Component.CollisionProfile };
            return (part, world);
        }

        if (IsImmovable(item))
        {
            return null;
        }

        var root = RootWorldOf(item, CurrentRootTransform(item));
        var bend = state?.GetBendValue(item.Reference) is { IsStraight: false } b ? b : (BendValue?)null;
        var source2 = item;
        if (item.IsAdded)
        {
            // What the project added is saved as what it was made from, so copies of copies work too (see CopyOp).
            switch (state?.AddedActors.GetValueOrDefault(item.Reference))
            {
                case AddStaticMeshActorOp meshActor:
                    return (new PrefabPart { Kind = PrefabPartKind.StaticMesh, Mesh = meshActor.StaticMesh, Class = "/Script/Engine.StaticMeshActor", Collision = meshActor.CollisionProfile, Bend = bend }, root);
                case AddBlueprintActorOp blueprint:
                    return (new PrefabPart { Kind = PrefabPartKind.Blueprint, Level = blueprint.Source.Level, Actor = blueprint.Source.Actor, Class = blueprint.ClassPath, Item = blueprint.Item }, root);
                case DuplicateActorOp duplicate when PristineOf(duplicate.Source) is { } pristine:
                    source2 = pristine;
                    break;
                default:
                    return null;
            }
        }

        return (new PrefabPart
        {
            Kind = PrefabPartKind.StockActor,
            Level = source2.Level.PackagePath,
            Actor = source2.Name,
            Class = source2.Actor.ClassPath,
            Mesh = source2.Actor.Kind == ActorKind.StaticMeshActor ? source2.Actor.StaticMeshPath : null,
            Collision = source2.Actor.Root?.CollisionProfile,
            Bend = bend,
        }, root);
    }

    /// <summary>
    /// Places the prefab where the camera aims: one journal step of copy/add ops that keeps the saved layout; parts whose
    /// game files are gone are skipped and said so. The new objects become the selection.
    /// </summary>
    private void PlacePrefab(PrefabRow row)
    {
        if (PreparedScene is not { } scene || scene.Documents.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Map.NoLevelLoaded"), Loc.T("Map.NoLevelLoadedDetail"));
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Adds"));
            return;
        }

        var target = NewObjectLevel(scene);
        var at = AimPointProvider?.Invoke() ?? FVector.Zero;
        var catalog = _services.Workspace.Catalog;
        var reserved = new HashSet<ActorRef>(ActorRef.Comparer);
        var reservedInstances = new HashSet<InstanceRef>(InstanceRef.Comparer);
        var ops = new List<EditOp>();
        var created = new List<ActorRef>();
        var skipped = new List<string>();
        try
        {
            foreach (var part in row.Entry.Prefab.Parts)
            {
                var world = part.Transform.ToTransform() with { Translation = part.Transform.Location + at };
                if (PlaceOp(part, world, target, project.State, catalog, reserved, reservedInstances) is not { } op)
                {
                    skipped.Add(part.DisplayName);
                    continue;
                }

                ops.Add(op);
                if (op.GetPrimaryTarget() is { } actor && op is not AddInstanceOp)
                {
                    created.Add(actor);
                }

                // A bent wall or bridge is bent again (the exporter bends mesh actors and duplicates).
                if (part.Bend is { IsStraight: false } bend && op is AddStaticMeshActorOp or DuplicateActorOp && op.GetPrimaryTarget() is { } bent)
                {
                    ops.Add(new BendActorOp(bent, 0f, bend.Degrees, NewSway1: bend.Sway1, NewSway2: bend.Sway2, NewStart: bend.Start, NewEnd: bend.End, NewLegs: bend.Legs));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Map.Prefabs.PlaceFailed"), ex.Message);
            return;
        }

        if (ops.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Map.Prefabs.PlaceFailed"), Loc.T("Map.Prefabs.NothingKnown"));
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new BatchOp(Loc.F("Map.Prefabs.PlaceTitle", row.Name), ops));
            _services.Notifications.Info(Loc.T("Map.Prefabs.Placed"), entry.Op.Describe() + Loc.T("Map.CtrlZUndoes"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.Prefabs.PlaceFailed"), ex.Message);
            return;
        }

        RefreshEdits();
        SelectCreatedGroup(created);
        if (skipped.Count > 0)
        {
            _services.Notifications.Warning(Loc.T("Map.Prefabs.Placed"), Loc.F("Map.Prefabs.Skipped", skipped.Count, string.Join(", ", skipped.Take(5))));
        }
    }

    /// <summary>
    /// The op placing one part at <paramref name="world"/> in <paramref name="target"/>: a stock actor whose level is
    /// loaded goes the way Paste copies it (a duplicate in its own level, with everything it stores), one from an unloaded
    /// level as a copy read from there, a mesh as a new mesh actor, a foliage instance as a new instance of its component
    /// when that level is the target, else as a mesh. Null when the game files no longer hold what the part needs.
    /// </summary>
    private EditOp? PlaceOp(PrefabPart part, FTransform world, LevelDocument target, EditState state, AssetCatalog? catalog, ISet<ActorRef> reserved, ISet<InstanceRef> reservedInstances)
    {
        var transform = TransformValue.FromTransform(world);
        bool Known(string? path) => path is not null && (catalog is null || catalog.PackageExists(AssetPaths.SplitObjectPath(path).PackagePath));
        AddStaticMeshActorOp? MeshActor() => Known(part.Mesh)
            ? EditOpFactory.AddStaticMeshActor(target, part.Mesh!, transform, state, reserved) with { CollisionProfile = part.Collision }
            : null;

        switch (part.Kind)
        {
            case PrefabPartKind.StaticMesh:
                return MeshActor();
            case PrefabPartKind.FoliageInstance:
                if (part.Level is not null && part.Actor is not null && part.Component is not null
                    && string.Equals(target.PackagePath, part.Level, StringComparison.OrdinalIgnoreCase)
                    && PristineOf(new ActorRef(part.Level, part.Actor)) is { } owner
                    && owner.Actor.FindComponent(part.Component) is { IsInstanced: true, IsSynthesized: false } component)
                {
                    return EditOpFactory.AddInstance(owner.Level, owner.Actor, component.Name, TransformValue.FromTransform(world.GetRelativeTransform(component.WorldTransform)), state, reservedInstances);
                }

                return MeshActor();
            default:
                if (part.Level is null || part.Actor is null)
                {
                    return null;
                }

                var source = new ActorRef(part.Level, part.Actor);
                // A spawner saved with its own item keeps it: a duplicate of the stock spawner would spawn the stock item.
                if (part.Item is null && PristineOf(source) is { } item)
                {
                    return CopyOp(item, target, world, state, reserved);
                }

                if (part.Kind == PrefabPartKind.StockActor && part.Mesh is not null && part.Class?.EndsWith(".StaticMeshActor", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return MeshActor();
                }

                if (part.Class is null || !Known(part.Level))
                {
                    return null;
                }

                var className = part.Class[(part.Class.LastIndexOf('.') + 1)..];
                var baseName = className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className;
                return new AddBlueprintActorOp(target.PackagePath, EditOpFactory.UniqueActorName(target, baseName + "_Added", state, reserved), part.Class, source, transform) { Item = part.Item };
        }
    }

    private async Task ExportPrefabAsync(PrefabRow row)
    {
        var path = await _services.Dialogs.SaveFileAsync(Loc.T("Map.Prefabs.Export.Title"), row.Name + Prefab.Extension, Prefab.Extension[1..], Loc.T("Map.Prefabs.FileType")).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            _services.Prefabs.Export(row.Entry, path);
            _services.Notifications.Success(Loc.T("Map.Prefabs.Exported"), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Map.Prefabs.ExportFailed"), ex.Message);
        }
    }

    /// <summary>Import…: a .ssprefab file someone gave the user goes into the library.</summary>
    [RelayCommand]
    private async Task ImportPrefabAsync()
    {
        if (await _services.Dialogs.OpenFileAsync(Loc.T("Map.Prefabs.Import.Title"), Prefab.Extension[1..], Loc.T("Map.Prefabs.FileType"), _services.Prefabs.Folder).ConfigureAwait(true) is { } path)
        {
            _services.Prefabs.ImportFile(path, _services.Workspace.Catalog, _services.Notifications);
        }
    }

    private void DeletePrefab(PrefabRow row)
    {
        try
        {
            _services.Prefabs.Delete(row.Entry);
            _services.Notifications.Info(Loc.T("Map.Prefabs.Deleted"), row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Map.Prefabs.Deleted"), ex.Message);
        }
    }
}
