using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The multi-selection (owner request): Ctrl+click adds an object (or one tree, rock or road piece) or takes it out;
/// Delete removes them all, Ctrl+C / Ctrl+V copy them all keeping their layout, dragging one moves them all, and
/// "Extend" lays a copy of the whole set right after it. Each of these is one undo step. A plain click on another object
/// (or on nothing) ends the selection; a plain click on one of its members makes that member the one the gizmo shows.
/// </summary>
public sealed partial class MapPageViewModel
{
    private readonly List<GroupMember> _group = [];
    private List<(GroupMember Member, FTransform World)>? _copiedGroup;

    /// <summary>Members of the multi-selection with their current world transform, for the viewport's group drag.</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupWorld> _groupWorlds = [];

    /// <summary>True while more than nothing is in the multi-selection.</summary>
    public bool HasGroup => _group.Count > 0;

    /// <summary>Adds the object under a Ctrl+click to the multi-selection, or takes it out (the selected object joins first).</summary>
    public void ToggleGroup(uint id, InstanceKey? instance)
    {
        if (AllActors.FirstOrDefault(a => a.SelectableId == id) is not { } item)
        {
            return;
        }

        _kindMesh = null; // a hand-made set replaces "all of this kind"
        if (_group.Count == 0 && SelectedActor is { } current && MemberOf(current, SelectedInstanceKey) is { } first)
        {
            _group.Add(first);
        }

        if (MemberOf(item, instance) is { } member && !_group.Remove(member))
        {
            _group.Add(member);
        }

        RefreshGroup();
    }

    /// <summary>The member for <paramref name="item"/> (or one of its instances), or null for terrain and containers.</summary>
    private static GroupMember? MemberOf(ActorItemViewModel item, InstanceKey? instance)
    {
        if (instance is { } key && key.SelectableId == item.SelectableId)
        {
            return new GroupMember(item.Reference, key.Component, key.InstanceIndex);
        }

        return IsImmovable(item) ? null : new GroupMember(item.Reference, null, 0);
    }

    /// <summary>True when the current selection is one of the members (a plain click on a member keeps the set).</summary>
    private bool SelectionInGroup() =>
        SelectedActor is { } item && MemberOf(item, SelectedInstanceKey) is { } member && _group.Contains(member);

    /// <summary>Highlights the members in the loaded levels (ids change when levels stream) and drops the ones that are gone.</summary>
    private void RefreshGroup()
    {
        if (_group.Count == 0)
        {
            GroupWorlds = [];
            return;
        }

        var byReference = new Dictionary<ActorRef, ActorItemViewModel>(ActorRef.Comparer);
        foreach (var a in AllActors)
        {
            byReference.TryAdd(a.Reference, a);
        }

        _group.RemoveAll(m => !byReference.TryGetValue(m.Actor, out var a) || a.IsDeleted);
        var ids = new List<uint>();
        var instances = new List<InstanceKey>();
        var worlds = new List<GroupWorld>();
        foreach (var member in _group)
        {
            var item = byReference[member.Actor];
            if (member.Component is null)
            {
                ids.Add(item.SelectableId);
                worlds.Add(new GroupWorld(item.SelectableId, null, Drawn(item)));
            }
            else
            {
                var key = InstanceKey.Of(item.SelectableId, member.Component, member.Index);
                instances.Add(key);
                if (InstanceInfo(item, key) is { } sel)
                {
                    worlds.Add(new GroupWorld(item.SelectableId, key, CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel)));
                }
            }
        }

        KindSelectionIds = ids;
        KindSelectionInstances = instances;
        GroupWorlds = worlds;
        KindSelectionText = _group.Count == 0 ? null : Loc.F("Map.Group.Selected", _group.Count.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>The world transform an actor's root is drawn at (scale 1 when bent: the scale is in the curve).</summary>
    private FTransform Drawn(ActorItemViewModel item) => RootWorldOf(item, Drawn(item, CurrentRootTransform(item)));

    /// <summary>The members as items (with their instance or segment), skipping ones no longer loaded.</summary>
    private List<(GroupMember Member, ActorItemViewModel Item, SelectedInstance? Instance)> GroupItems()
    {
        var result = new List<(GroupMember, ActorItemViewModel, SelectedInstance?)>();
        foreach (var member in _group)
        {
            if (AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, member.Actor)) is not { } item)
            {
                continue;
            }

            if (member.Component is null)
            {
                result.Add((member, item, null));
            }
            else if (InstanceInfo(item, InstanceKey.Of(item.SelectableId, member.Component, member.Index)) is { } sel)
            {
                result.Add((member, item, sel));
            }
        }

        return result;
    }

    /// <summary>World transform of a member now.</summary>
    private FTransform WorldOf(ActorItemViewModel item, SelectedInstance? sel) =>
        sel is null ? RootWorldOf(item, CurrentRootTransform(item)) : CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel);

    /// <summary>Deletes every member in one journal step.</summary>
    private void DeleteGroup()
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Moves"));
            return;
        }

        var ops = new List<EditOp>();
        foreach (var (_, item, sel) in GroupItems())
        {
            if (sel is null)
            {
                ops.Add(new DeleteActorOp(item.Reference));
            }
            else if (sel.Instance is { } instance)
            {
                ops.Add(new DeleteInstanceOp(new InstanceRef(item.Level.PackagePath, item.Name, instance.ComponentName, instance.InstanceIndex)));
            }
            else
            {
                // A road or bridge piece goes the way a single one does: scaled to nothing (see DeleteInstance).
                ops.Add(EditOpFactory.SetTransform(item.Level, item.Actor, CurrentInstanceTransform(sel) with { Scale = new FVector(0f, 0f, 0f) }, project.State, sel.Component.Name));
            }
        }

        ApplyGroupOps(ops, Loc.F("Map.Group.Deleted", ops.Count));
        ClearKindSelection();
    }

    /// <summary>Remembers every member and where it is, for <see cref="TryPasteGroup"/>.</summary>
    private void CopyGroup()
    {
        _copiedGroup = GroupItems().Select(m => (m.Member, WorldOf(m.Item, m.Instance))).ToList();
        CopiedActor = null;
        _copiedInstance = null;
        OnPropertyChanged(nameof(HasCopiedActor));
        OnPropertyChanged(nameof(PasteTip));
        PasteCommand.NotifyCanExecuteChanged();
        _services.Notifications.Info(Loc.T("Assets.Copied"), Loc.F("Map.Group.Copied", _copiedGroup.Count));
    }

    /// <summary>Places copies of the copied set with its first object at the aim point, the others where they were around it.</summary>
    private bool TryPasteGroup()
    {
        if (_copiedGroup is not { Count: > 0 } copied)
        {
            return false;
        }

        var aim = AimPointProvider?.Invoke() ?? copied[0].World.Translation;
        var offset = aim - copied[0].World.Translation;
        return PlaceCopies(copied, offset, Loc.T("Map.Pasted"));
    }

    /// <summary>
    /// "Extend": a copy of the selection (or the whole multi-selection) laid right after it along its length, and selected,
    /// so pressing it again keeps building the wall, road or fence.
    /// </summary>
    public void Extend(bool backwards = false)
    {
        var members = HasGroup
            ? GroupItems().Select(m => (m.Member, World: WorldOf(m.Item, m.Instance), m.Item, m.Instance)).ToList()
            : SelectedActor is { } item && MemberOf(item, SelectedInstanceKey) is { } single
                ? [(single, WorldOf(item, SelectedInstanceInfo()), item, SelectedInstanceInfo())]
                : [];
        if (members.Count == 0)
        {
            _services.Notifications.Info(Loc.T("Map.Extend"), Loc.T("Map.Extend.Nothing"));
            return;
        }

        // Along the first object's length (its longer horizontal axis), by the set's size on that axis.
        var (lead, leadWorld, leadItem, leadInstance) = members[0];
        var mesh = leadInstance is { } ls ? MeshOf(ls) : leadItem.Actor.StaticMeshPath;
        var bounds = MeshBounds(mesh);
        var alongY = bounds is { } b && b.Size.Y * MathF.Abs(leadWorld.Scale3D.Y) > b.Size.X * MathF.Abs(leadWorld.Scale3D.X);
        var axis = leadWorld.Rotation.RotateVector(alongY ? FVector.Right : FVector.Forward);
        axis = new FVector(axis.X, axis.Y, 0f).GetSafeNormal();
        var pieceLength = bounds is { } mb ? (alongY ? mb.Size.Y * MathF.Abs(leadWorld.Scale3D.Y) : mb.Size.X * MathF.Abs(leadWorld.Scale3D.X)) : 200f;
        var along = members.Select(m => FVector.Dot(m.World.Translation, axis)).ToList();
        var step = (along.Max() - along.Min()) + pieceLength;
        var offset = axis * (backwards ? -step : step);
        PlaceCopies(members.Select(m => (m.Member, m.World)).ToList(), offset, Loc.T("Map.Extend"), selectCopies: true);
    }

    /// <summary>Copies of the members moved by <paramref name="offset"/>, as one journal step; the copies are selected.</summary>
    private bool PlaceCopies(IReadOnlyList<(GroupMember Member, FTransform World)> members, FVector offset, string title, bool selectCopies = true)
    {
        if (_services.Projects.Current is not { } project || PreparedScene is null)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Copies"));
            return true;
        }

        var reserved = new HashSet<ActorRef>(ActorRef.Comparer);
        var ops = new List<EditOp>();
        try
        {
            foreach (var (member, world) in members)
            {
                if (AllActors.FirstOrDefault(a => ActorRef.Comparer.Equals(a.Reference, member.Actor)) is not { } item)
                {
                    continue;
                }

                if (member.Component is null)
                {
                    var current = RootWorldOf(item, CurrentRootTransform(item));
                    ops.Add(CopyOp(item, item.Level, current with { Translation = current.Translation + offset }, project.State, reserved));
                }
                else if (member.Index != InstanceKey.Segment
                         && InstanceInfo(item, InstanceKey.Of(item.SelectableId, member.Component, member.Index)) is { } sel && MeshOf(sel) is { } mesh)
                {
                    // One tree or rock becomes a mesh actor of its own.
                    var placed = TransformValue.FromTransform(world);
                    ops.Add(EditOpFactory.AddStaticMeshActor(item.Level, mesh, placed with { Location = placed.Location + offset }, project.State, reserved)
                        with { CollisionProfile = sel.Component.CollisionProfile }); // it collides as what it was copied from
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Map.PasteFailed"), ex.Message);
            return true;
        }

        if (ops.Count == 0)
        {
            return true;
        }

        var created = ops.Select(o => o.GetPrimaryTarget()).OfType<ActorRef>().ToList();
        ApplyGroupOps(ops, title);
        if (selectCopies)
        {
            _group.Clear();
            if (created.Count > 1)
            {
                _group.AddRange(created.Select(c => new GroupMember(c, null, 0)));
            }

            RefreshGroup();
            SelectedInstanceKey = null;
            _restoringSelection = true; // keep the new set while the first copy becomes the selection
            try
            {
                SelectedActor = AllActors.FirstOrDefault(a => a.IsAdded && created.Count > 0 && ActorRef.Comparer.Equals(a.Reference, created[0])) ?? SelectedActor;
            }
            finally
            {
                _restoringSelection = false;
            }
        }

        return true;
    }

    /// <summary>
    /// A drag of one member moved it from <paramref name="before"/> to <paramref name="after"/>: every member moves the
    /// same way (as one rigid set) in one journal step. False when the dragged object is not in the set.
    /// </summary>
    private bool TryMoveGroup(ActorItemViewModel dragged, InstanceKey? instance, FTransform before, FTransform after)
    {
        if (!HasGroup || MemberOf(dragged, instance) is not { } moved || !_group.Contains(moved) || _services.Projects.Current is not { } project)
        {
            return false;
        }

        var motion = before.Inverse() * after;
        var ops = new List<EditOp>();
        try
        {
            foreach (var (_, item, sel) in GroupItems())
            {
                var world = WorldOf(item, sel) * motion;
                if (sel is null)
                {
                    if (IsImmovable(item))
                    {
                        continue;
                    }

                    var value = RelativeOf(item, world) with { Scale = CurrentRootTransform(item).Scale };
                    ops.Add(item.IsAdded ? EditOpFactory.SetAddedActorTransform(item.Reference, value, project.State) : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State));
                }
                else
                {
                    var value = TransformValue.FromTransform(world.GetRelativeTransform(SpaceOf(sel))) with { Scale = CurrentInstanceTransform(sel).Scale };
                    ops.Add(sel.Instance is { } i
                        ? EditOpFactory.SetInstanceTransform(item.Level, item.Actor, i.ComponentName, i.InstanceIndex, value, project.State)
                        : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State, sel.Component.Name));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Map.MoveFailed"), ex.Message);
            return true;
        }

        ApplyGroupOps(ops, Loc.T("Map.Moved"));
        RefreshGroup();
        return true;
    }

    private void ApplyGroupOps(IReadOnlyList<EditOp> ops, string title)
    {
        if (ops.Count == 0)
        {
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(title, ops));
            _services.Notifications.Info(title, entry.Op.Describe() + Loc.T("Map.CtrlZUndoes"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(title, ex.Message);
        }

        RefreshEdits();
    }

    /// <summary>
    /// Brush selection (owner request): while on, holding the left mouse button over the map paints a circle that adds
    /// everything under it to the multi-selection. Turning it off keeps the selection; Ctrl+click then takes out what
    /// should stay and Delete removes the rest.
    /// </summary>
    [ObservableProperty]
    private bool _brushSelect;

    /// <summary>Radius of the brush circle in metres.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushRadiusText))]
    private double _brushRadius = 10;

    /// <summary><see cref="BrushRadius"/> as text ("10 m").</summary>
    public string BrushRadiusText => string.Create(CultureInfo.CurrentCulture, $"{BrushRadius:0} m");

    /// <summary>Adds every object drawn within the brush circle around <paramref name="centre"/> (UE world) to the multi-selection.</summary>
    public void BrushAt(FVector centre)
    {
        var radius = (float)BrushRadius * 100f;
        var height = MathF.Max(radius, 1000f); // a cylinder: a house's walls above the ground still count, a bunker far below does not
        bool Inside(FVector at)
        {
            var dx = at.X - centre.X;
            var dy = at.Y - centre.Y;
            return (dx * dx) + (dy * dy) <= radius * radius && MathF.Abs(at.Z - centre.Z) <= height;
        }

        var have = new HashSet<GroupMember>(_group);
        var added = false;
        void Add(ActorItemViewModel item, InstanceKey? key)
        {
            if (MemberOf(item, key) is { } member && have.Add(member))
            {
                _group.Add(member);
                added = true;
            }
        }

        FVector Where(InstanceKey key, FVector stored) => InstanceTransforms.TryGetValue(key, out var moved) ? moved.Translation : stored;

        foreach (var item in AllActors)
        {
            if (item.IsDeleted || HiddenActorIds.Contains(item.SelectableId))
            {
                continue;
            }

            // One tree, rock or plank of a foliage/ISM actor at a time.
            foreach (var i in item.Actor.InstanceTransforms)
            {
                var key = InstanceKey.Of(item.SelectableId, i.ComponentName, i.InstanceIndex);
                if (!HiddenInstanceKeys.Contains(key) && Inside(Where(key, i.WorldTransform.Translation)))
                {
                    Add(item, key);
                }
            }

            if (IsImmovable(item))
            {
                continue;
            }

            // A Blueprint's parts and a road's pieces count on their own (parts only in part mode, as a click does).
            var holders = item.Actor.InstanceTransforms.Select(i => i.ComponentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var meshes = item.Actor.Components.Where(c => c.StaticMeshPath is not null && !holders.Contains(c.Name)).ToList();
            var pieces = meshes.Where(c => c.SplineMesh is not null
                || (PickParts && !c.IsSynthesized && c.ExportIndex >= 0 && c.ExportIndex != item.Actor.RootComponent)).ToList();
            if (pieces.Count > 0)
            {
                foreach (var c in pieces)
                {
                    var key = InstanceKey.Of(item.SelectableId, c.Name, c.SplineMesh is not null ? InstanceKey.Segment : InstanceKey.Part);
                    if (!HiddenInstanceKeys.Contains(key) && Inside(Where(key, c.WorldTransform.Translation)))
                    {
                        Add(item, key);
                    }
                }

                continue;
            }

            // Anything else joins whole when one of its meshes is in the circle (a moved actor: where it is now).
            if (meshes.Count > 0 && (ActorTransforms.TryGetValue(item.SelectableId, out var movedActor)
                    ? Inside(movedActor.Translation)
                    : meshes.Any(c => Inside(c.WorldTransform.Translation))))
            {
                Add(item, null);
            }
        }

        if (added)
        {
            _kindMesh = null; // a hand-made set replaces "all of this kind"
            RefreshGroup();
        }
    }

    /// <summary>One object of the multi-selection: a whole actor (Component null) or one instance / road piece of it.</summary>
    private sealed record GroupMember(ActorRef Actor, string? Component, int Index)
    {
        public bool Equals(GroupMember? other) =>
            other is not null && ActorRef.Comparer.Equals(Actor, other.Actor) && string.Equals(Component, other.Component, StringComparison.OrdinalIgnoreCase) && Index == other.Index;

        public override int GetHashCode() => HashCode.Combine(ActorRef.Comparer.GetHashCode(Actor), Component?.ToLowerInvariant(), Index);
    }
}
