using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Core.Geometry;
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
        if (ActorOf(id) is not { } item)
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

        _group.RemoveAll(m => ActorOf(m.Actor) is not { IsDeleted: false });
        var ids = new List<uint>();
        var instances = new List<InstanceKey>();
        var worlds = new List<GroupWorld>();
        foreach (var member in _group)
        {
            Show(member, ActorOf(member.Actor)!, null, ids, instances, worlds);
        }

        ShowGroup(ids, instances, worlds);
    }

    /// <summary>Adds one member to the highlight lists (<paramref name="sel"/>: its instance or part when known, else looked up).</summary>
    private void Show(GroupMember member, ActorItemViewModel item, SelectedInstance? sel, List<uint> ids, List<InstanceKey> instances, List<GroupWorld> worlds)
    {
        if (member.Component is null)
        {
            ids.Add(item.SelectableId);
            worlds.Add(new GroupWorld(item.SelectableId, null, Drawn(item)));
            return;
        }

        var key = InstanceKey.Of(item.SelectableId, member.Component, member.Index);
        instances.Add(key);
        if ((sel ?? InstanceInfo(item, key)) is { } found)
        {
            worlds.Add(new GroupWorld(item.SelectableId, key, CurrentInstanceTransform(found).ToTransform() * SpaceOf(found)));
        }
    }

    private void ShowGroup(List<uint> ids, List<InstanceKey> instances, List<GroupWorld> worlds)
    {
        KindSelectionIds = ids;
        KindSelectionInstances = instances;
        GroupWorlds = worlds;
        KindSelectionText = _group.Count == 0 ? null : Loc.F("Map.Group.Selected", _group.Count.ToString("N0", CultureInfo.CurrentCulture));
        SavePrefabCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The world transform an actor's root is drawn at (scale 1 when bent: the scale is in the curve).</summary>
    private FTransform Drawn(ActorItemViewModel item) => RootWorldOf(item, Drawn(item, CurrentRootTransform(item)));

    /// <summary>The members as items (with their instance or segment), skipping ones no longer loaded.</summary>
    private List<(GroupMember Member, ActorItemViewModel Item, SelectedInstance? Instance)> GroupItems()
    {
        var result = new List<(GroupMember, ActorItemViewModel, SelectedInstance?)>();
        foreach (var member in _group)
        {
            if (ActorOf(member.Actor) is not { } item)
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

        // An actor goes whole when it is a member itself or when its selected parts and instances are the last of it drawn;
        // its parts and instances are then not journaled on their own (the owner's first bulk delete stopped at a tree whose
        // actor was already deleted and left 2000 edits unapplied).
        var members = GroupItems();
        var whole = new HashSet<ActorItemViewModel>(members.Where(m => m.Instance is null).Select(m => m.Item));
        foreach (var byActor in members.Where(m => m.Instance is not null && !whole.Contains(m.Item)).GroupBy(m => m.Item))
        {
            if (LeavesNothingDrawn(byActor.Key, byActor.Select(m => m.Instance!).Where(s => !SpawnMarkers.IsSpawnPart(byActor.Key.Actor, s.Component)).ToList()))
            {
                whole.Add(byActor.Key);
            }
        }

        var ops = new List<EditOp>(whole.Select(item => (EditOp)new DeleteActorOp(item.Reference)));
        foreach (var (_, item, sel) in members)
        {
            if (sel is null || whole.Contains(item))
            {
                continue;
            }

            if (DeleteOpOf(item, sel, project.State) is { } op)
            {
                ops.Add(op); // an instance, an added instance, or a piece scaled to nothing; a spawner is not deleted (see IsSpawnPartSelected)
            }
        }

        // A deleted actor takes its instances and parts with it: their own edits would only make the batch undo-unsafe.
        var gone = ops.OfType<DeleteActorOp>().Select(d => d.Target).ToHashSet(ActorRef.Comparer);
        ops.RemoveAll(o => o is not DeleteActorOp && o.GetPrimaryTarget() is { } target && gone.Contains(target));

        ApplyGroupOps(ops, Loc.F("Map.Group.Deleted", ops.Count));
        ClearKindSelection();
    }

    /// <summary>
    /// True when taking out <paramref name="removed"/> (parts and instances of <paramref name="item"/>) leaves nothing of
    /// the actor drawn: every other mesh part is already scaled to nothing and every other instance deleted. The actor is
    /// then deleted whole, so what no part can pick goes with it: a fire barrel's flames, light and heat, a building's
    /// collision boxes, lights, decals and child actors (Hektor: fires burning in the air where a camp was; the owner's
    /// outpost: invisible walls where buildings were).
    /// </summary>
    private bool LeavesNothingDrawn(ActorItemViewModel item, IReadOnlyCollection<SelectedInstance> removed)
    {
        if (IsImmovable(item) || _services.Projects.Current?.State is not { } state)
        {
            return false;
        }

        var holders = item.Actor.InstanceTransforms.Select(i => i.ComponentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in item.Actor.Components.Where(c => c.StaticMeshPath is not null && !holders.Contains(c.Name)))
        {
            if (!removed.Any(r => r.Instance is null && string.Equals(r.Component.Name, c.Name, StringComparison.OrdinalIgnoreCase))
                && state.GetTransformOverride(item.Reference, c.Name)?.Scale != FVector.Zero)
            {
                return false;
            }
        }

        var instances = item.Actor.InstanceTransforms.Select(i => new InstanceRef(item.Level.PackagePath, item.Name, i.ComponentName, i.InstanceIndex))
            .Concat(state.AddedInstances.Keys.Where(k => ActorRef.Comparer.Equals(k.ActorRef, item.Reference)));
        return instances.All(i => state.IsDeleted(i)
            || removed.Any(r => r.Instance is { } ri && ri.InstanceIndex == i.Index && string.Equals(ri.ComponentName, i.Component, StringComparison.OrdinalIgnoreCase)));
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
                if (ActorOf(member.Actor) is not { } item)
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
            SelectCreatedGroup(created);
        }

        return true;
    }

    /// <summary>Makes the new actors the selection: a multi-selection when there are several, the first one at the gizmo.</summary>
    private void SelectCreatedGroup(IReadOnlyList<ActorRef> created)
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

    /// <summary>Size of the map cells the brush sorts the loaded objects into, cm.</summary>
    private const float BrushCell = 5000f;

    private BrushIndex? _brushIndex;

    /// <summary>
    /// Adds everything the brush covers to the multi-selection (owner: everything inside the circle, wherever the cursor
    /// is): the circle at <paramref name="centre"/> (UE world) swept from <paramref name="from"/> (the stroke's previous
    /// dab), so a fast sweep leaves no gaps. An actor or a part (part mode) counts when what it draws reaches into the
    /// swept circle on the ground (a house whose corner is inside, not only its pivot); a tree, rock or road piece when its
    /// place is inside; an actor with nothing to draw but a pin (a fire, a lamp, an NPC) when its pin is. Upright it is a
    /// cylinder: a house's walls above the ground still count, a bunker far below does not. Nothing is ever taken out.
    /// </summary>
    public void BrushAt(FVector centre, FVector? from = null)
    {
        if (AllActors.Count == 0)
        {
            return; // nothing loaded (the index may still hold the levels unloaded last)
        }

        var start = from ?? centre;
        var radius = (float)BrushRadius * 100f;
        var height = MathF.Max(radius, 1000f);
        var sweep = new BrushSweep(new Vector2(start.X, start.Y), new Vector2(centre.X, centre.Y), radius,
            MathF.Min(start.Z, centre.Z) - height, MathF.Max(start.Z, centre.Z) + height);
        var index = BrushIndexNow();
        var moved = ActorTransforms;
        var movedPieces = InstanceTransforms;
        var have = new HashSet<GroupMember>(_group);
        var added = new List<(GroupMember Member, BrushEntry Entry)>();

        void Consider(BrushEntry e, BrushOutline outline)
        {
            var item = e.Item;
            var wanted = e.Kind switch
            {
                BrushKind.Whole => !e.HasSegments && !(PickParts && e.HasParts),
                BrushKind.Part => PickParts && !IsImmovable(item),
                BrushKind.Segment => !IsImmovable(item),
                _ => true,
            };
            if (wanted && sweep.Touches(outline) && !item.IsDeleted && !HiddenActorIds.Contains(item.SelectableId)
                && (e.Key is not { } key || !HiddenInstanceKeys.Contains(key)) && MemberOf(item, e.Key) is { } member && have.Add(member))
            {
                _group.Add(member);
                added.Add((member, e));
            }
        }

        // The loaded levels as stored, by map cell; what the project moved is tested where it is now.
        var (x0, y0) = BrushCellOf(sweep.Min.X, sweep.Min.Y);
        var (x1, y1) = BrushCellOf(sweep.Max.X, sweep.Max.Y);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!index.Cells.TryGetValue((x, y), out var cell))
                {
                    continue;
                }

                foreach (var e in cell)
                {
                    if (e.Key is { } key ? !movedPieces.ContainsKey(key) : !moved.ContainsKey(e.Item.SelectableId))
                    {
                        Consider(e, e.Outline);
                    }
                }
            }
        }

        foreach (var e in index.Large)
        {
            if (!moved.ContainsKey(e.Item.SelectableId))
            {
                Consider(e, e.Outline);
            }
        }

        foreach (var (e, outline) in MovedBrushEntries(index))
        {
            Consider(e, outline);
        }

        if (added.Count == 0)
        {
            return;
        }

        // Highlighted at once: only the new members are looked up (a sweep through a forest adds a few trees per move).
        var inSync = _kindMesh is null && KindSelectionIds.Count + KindSelectionInstances.Count == _group.Count - added.Count;
        _kindMesh = null; // a hand-made set replaces "all of this kind"
        if (!inSync)
        {
            RefreshGroup();
            return;
        }

        var ids = new List<uint>(KindSelectionIds);
        var instances = new List<InstanceKey>(KindSelectionInstances);
        var worlds = new List<GroupWorld>(GroupWorlds);
        foreach (var (member, e) in added)
        {
            var sel = e.Component is { } part ? new SelectedInstance(e.Item, null, part)
                : e.Instance is { } instance && e.Item.Actor.FindComponent(instance.ComponentName) is { } holder ? new SelectedInstance(e.Item, instance, holder)
                : null;
            Show(member, e.Item, sel, ids, instances, worlds);
        }

        ShowGroup(ids, instances, worlds);
    }

    /// <summary>
    /// What the project moved or added, where it is drawn now (the index holds the stored places): moved actors, moved or
    /// added pieces (copied and planted trees) and the actors the project added (copies, new meshes and Blueprints).
    /// </summary>
    private IEnumerable<(BrushEntry Entry, BrushOutline Outline)> MovedBrushEntries(BrushIndex index)
    {
        foreach (var (id, root) in ActorTransforms)
        {
            if (index.Wholes.TryGetValue(id, out var e) && OutlineOf(e.Item, root) is { } outline)
            {
                yield return (e, outline);
            }
        }

        foreach (var (key, world) in InstanceTransforms)
        {
            if (ActorOf(key.SelectableId) is { } item && key.InstanceIndex is >= 0 or InstanceKey.Segment or InstanceKey.Part)
            {
                var kind = key.InstanceIndex >= 0 ? BrushKind.Instance : key.InstanceIndex == InstanceKey.Segment ? BrushKind.Segment : BrushKind.Part;
                var outline = kind == BrushKind.Part ? BrushOutline.Of(world, MeshBounds(item.Actor.FindComponent(key.Component)?.StaticMeshPath)) : BrushOutline.Point(world.Translation);
                yield return (new BrushEntry(item, kind, outline, key), outline);
            }
        }

        for (var i = index.Actors.Count; i < AllActors.Count; i++)
        {
            // Actors the project added are few: tested where they are drawn now.
            var item = AllActors[i];
            if (!IsImmovable(item) && OutlineOf(item, Drawn(item)) is { } outline)
            {
                yield return (new BrushEntry(item, BrushKind.Whole, outline), outline);
            }
        }
    }

    /// <summary>The brush's map of the loaded levels' own actors, built on the first dab after they changed.</summary>
    private BrushIndex BrushIndexNow()
    {
        if (_brushIndex is { } index && ReferenceEquals(index.Actors, _pristineActors))
        {
            return index;
        }

        index = new BrushIndex(_pristineActors);
        var entries = new List<BrushEntry>();
        foreach (var item in _pristineActors)
        {
            entries.Clear();
            BrushEntries(item, entries);
            foreach (var e in entries)
            {
                if (e.Kind == BrushKind.Whole)
                {
                    index.Wholes[item.SelectableId] = e;
                }

                var box = e.Outline.Box;
                var (x0, y0) = BrushCellOf(box.Min.X, box.Min.Y);
                var (x1, y1) = BrushCellOf(box.Max.X, box.Max.Y);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 256)
                {
                    index.Large.Add(e); // wider than 800 m: tested on every dab
                    continue;
                }

                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        (index.Cells.TryGetValue((x, y), out var cell) ? cell : index.Cells[(x, y)] = []).Add(e);
                    }
                }
            }
        }

        return _brushIndex = index;
    }

    /// <summary>
    /// What the brush can take of <paramref name="item"/> as the level stores it: each tree, rock or plank of its
    /// instanced meshes (by its place); a Blueprint's parts (by what they draw) and a road's pieces (by their place), parts
    /// only in part mode as for a click and a door's leaf never (ScenePlacement.PickKey); else the whole actor by what it
    /// draws, unless it must stay where it is (foliage, terrain).
    /// </summary>
    private void BrushEntries(ActorItemViewModel item, List<BrushEntry> into)
    {
        foreach (var i in item.Actor.InstanceTransforms)
        {
            into.Add(new BrushEntry(item, BrushKind.Instance, BrushOutline.Point(i.WorldTransform.Translation), InstanceKey.Of(item.SelectableId, i.ComponentName, i.InstanceIndex), Instance: i));
        }

        if (IsImmovable(item))
        {
            return;
        }

        var holders = item.Actor.InstanceTransforms.Select(i => i.ComponentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (segments, parts) = (false, false);
        foreach (var c in item.Actor.Components)
        {
            var segment = c.SplineMesh is not null;
            if (c.StaticMeshPath is null || holders.Contains(c.Name)
                || (!segment && (c.IsSynthesized || c.IsNativeSubobject || c.ExportIndex < 0 || c.ExportIndex == item.Actor.RootComponent)))
            {
                continue;
            }

            segments |= segment;
            parts |= !segment;
            into.Add(new BrushEntry(item, segment ? BrushKind.Segment : BrushKind.Part,
                segment ? BrushOutline.Point(c.WorldTransform.Translation) : BrushOutline.Of(c.WorldTransform, MeshBounds(c.StaticMeshPath)),
                InstanceKey.Of(item.SelectableId, c.Name, segment ? InstanceKey.Segment : InstanceKey.Part), Component: c));
        }

        if (OutlineOf(item, StoredRoot(item)) is { } outline)
        {
            into.Add(new BrushEntry(item, BrushKind.Whole, outline, HasSegments: segments, HasParts: parts));
        }
    }

    /// <summary>The root's world transform as the level stores it.</summary>
    private static FTransform StoredRoot(ActorItemViewModel item) => item.Actor.Root?.WorldTransform ?? item.Actor.WorldTransform;

    /// <summary>
    /// What <paramref name="item"/> draws with its root at <paramref name="root"/>: the boxes of its visible meshes (the
    /// places of its meshes when none is visible or loaded), an added mesh actor's mesh, or the pin of an actor that has
    /// nothing else. Null with none of these.
    /// </summary>
    private BrushOutline? OutlineOf(ActorItemViewModel item, FTransform root)
    {
        var stored = StoredRoot(item);
        var meshes = item.Actor.Components.Where(c => c.StaticMeshPath is not null && !c.IsInstanced).ToList();
        var drawn = meshes.Where(c => c.IsVisible).ToList();
        var shapes = (drawn.Count > 0 ? drawn : meshes)
            .Select(c => (World: c.WorldTransform.GetRelativeTransform(stored) * root, Bounds: c.IsVisible ? MeshBounds(c.StaticMeshPath) : null))
            .Select(m => new BrushShape(m.Bounds is { IsEmpty: false } b ? b : default, m.World)).ToList();
        if (shapes.Count == 0 && item.Actor.StaticMeshPath is { } mesh)
        {
            shapes.Add(new BrushShape(MeshBounds(mesh) is { IsEmpty: false } b ? b : default, root));
        }

        if (shapes.Count == 0 && SpawnMarkers.IsPinOnly(item.Actor, out _))
        {
            return BrushOutline.Point(root.TransformPosition(stored.InverseTransformPosition(item.Actor.WorldTransform.Translation)));
        }

        return shapes.Count == 0 ? null : new BrushOutline(shapes.Aggregate(BoundingBox.Empty, (box, s) => box.Union(s.WorldBox())), [.. shapes]);
    }

    private static (int X, int Y) BrushCellOf(float x, float y) => ((int)MathF.Floor(x / BrushCell), (int)MathF.Floor(y / BrushCell));

    private enum BrushKind
    {
        Instance,
        Segment,
        Part,
        Whole,
    }

    /// <summary>One thing the brush can take: a whole actor or one instance or piece of it, with what it draws.</summary>
    private sealed record BrushEntry(ActorItemViewModel Item, BrushKind Kind, BrushOutline Outline, InstanceKey? Key = null,
        ActorInstance? Instance = null, ComponentRecord? Component = null, bool HasSegments = false, bool HasParts = false);

    /// <summary>The brush's entries by map cell (<see cref="BrushCellOf"/>), for one set of loaded levels.</summary>
    private sealed record BrushIndex(IReadOnlyList<ActorItemViewModel> Actors)
    {
        public Dictionary<(int X, int Y), List<BrushEntry>> Cells { get; } = [];

        public List<BrushEntry> Large { get; } = [];

        public Dictionary<uint, BrushEntry> Wholes { get; } = [];
    }

    /// <summary>One drawn mesh: its box in its own space (empty size: just its place) and where it stands.</summary>
    private readonly record struct BrushShape(BoundingBox Local, FTransform World)
    {
        /// <summary>The 8 corners in the world.</summary>
        public void Corners(Span<Vector3> into)
        {
            for (var i = 0; i < 8; i++)
            {
                var c = World.TransformPosition(new FVector((i & 1) == 0 ? Local.Min.X : Local.Max.X, (i & 2) == 0 ? Local.Min.Y : Local.Max.Y, (i & 4) == 0 ? Local.Min.Z : Local.Max.Z));
                into[i] = new Vector3(c.X, c.Y, c.Z);
            }
        }

        public BoundingBox WorldBox()
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            Corners(corners);
            var box = BoundingBox.Empty;
            foreach (var c in corners)
            {
                box = box.Include(c);
            }

            return box;
        }
    }

    /// <summary>What one entry draws: the world box around it and, for meshes, each mesh's own box (null: the box is a point).</summary>
    private sealed record BrushOutline(BoundingBox Box, BrushShape[]? Shapes)
    {
        public static BrushOutline Point(FVector at) => new(new BoundingBox(new Vector3(at.X, at.Y, at.Z), new Vector3(at.X, at.Y, at.Z)), null);

        public static BrushOutline Of(FTransform world, BoundingBox? bounds)
        {
            var shape = new BrushShape(bounds is { IsEmpty: false } b ? b : default, world);
            return new BrushOutline(shape.WorldBox(), [shape]);
        }
    }

    /// <summary>The brush circle swept from <paramref name="A"/> to <paramref name="B"/> on the ground (a capsule), between two heights (UE cm).</summary>
    private readonly record struct BrushSweep(Vector2 A, Vector2 B, float Radius, float Bottom, float Top)
    {
        public Vector2 Min => Vector2.Min(A, B) - new Vector2(Radius);

        public Vector2 Max => Vector2.Max(A, B) + new Vector2(Radius);

        /// <summary>True when something of <paramref name="outline"/> is within the heights and reaches into the capsule on the ground.</summary>
        public bool Touches(BrushOutline outline)
        {
            var box = outline.Box;
            if (box.Max.Z < Bottom || box.Min.Z > Top || box.Max.X < Min.X || box.Max.Y < Min.Y || box.Min.X > Max.X || box.Min.Y > Max.Y)
            {
                return false;
            }

            Span<Vector2> ground = stackalloc Vector2[4];
            if (outline.Shapes is not { } shapes)
            {
                return TouchesQuad(Rectangle(box, ground));
            }

            Span<Vector3> corners = stackalloc Vector3[8];
            foreach (var shape in shapes)
            {
                shape.Corners(corners);
                var (low, high) = (float.MaxValue, float.MinValue);
                foreach (var c in corners)
                {
                    (low, high) = (MathF.Min(low, c.Z), MathF.Max(high, c.Z));
                }

                if (high < Bottom || low > Top)
                {
                    continue;
                }

                // Upright (only turned about Z, the usual) its outline on the ground is its bottom face; tilted, the box around it.
                if (MathF.Abs(shape.World.Rotation.RotateVector(FVector.Up).Z) > 0.999f)
                {
                    (ground[0], ground[1], ground[2], ground[3]) = (Flat(corners[0]), Flat(corners[1]), Flat(corners[3]), Flat(corners[2]));
                }
                else
                {
                    var flat = BoundingBox.Empty;
                    foreach (var c in corners)
                    {
                        flat = flat.Include(c);
                    }

                    Rectangle(flat, ground);
                }

                if (TouchesQuad(ground))
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

        private static Span<Vector2> Rectangle(BoundingBox box, Span<Vector2> into)
        {
            (into[0], into[1], into[2], into[3]) = (new(box.Min.X, box.Min.Y), new(box.Max.X, box.Min.Y), new(box.Max.X, box.Max.Y), new(box.Min.X, box.Max.Y));
            return into;
        }

        /// <summary>True when the convex quad (corners in order, either way round; it may be a line or a point) comes within the radius of the segment.</summary>
        private bool TouchesQuad(ReadOnlySpan<Vector2> quad)
        {
            // Apart, a segment and a convex shape are nearest between an end of one and an edge of the other; else one
            // crosses the other's edge, or the segment lies inside.
            var r2 = Radius * Radius;
            var (positive, negative) = (false, false);
            for (var i = 0; i < 4; i++)
            {
                var (p, q) = (quad[i], quad[(i + 1) % 4]);
                if (SegmentsDistanceSquared(A, B, p, q) <= r2)
                {
                    return true;
                }

                var side = Cross(q - p, A - p);
                positive |= side > 0f;
                negative |= side < 0f;
            }

            return positive != negative; // A strictly on one side of every (non-empty) edge: inside
        }

        private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

        private static float SegmentsDistanceSquared(Vector2 a, Vector2 b, Vector2 p, Vector2 q)
        {
            var (d1, d2, d3, d4) = (Cross(b - a, p - a), Cross(b - a, q - a), Cross(q - p, a - p), Cross(q - p, b - p));
            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
            {
                return 0f; // they cross
            }

            return MathF.Min(MathF.Min(ToSegment(p, a, b), ToSegment(q, a, b)), MathF.Min(ToSegment(a, p, q), ToSegment(b, p, q)));
        }

        private static float ToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var d = b - a;
            var length = d.LengthSquared();
            var t = length < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(point - a, d) / length, 0f, 1f);
            return Vector2.DistanceSquared(point, a + (d * t));
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
