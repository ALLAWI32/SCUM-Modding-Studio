using System.Globalization;
using System.Text;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Level.Editing;

/// <summary>
/// The net effect of the applied journal entries, kept in memory for the editor: deleted actors and instances,
/// transform overrides and added actors. Pristine level packages are never modified; the exporter replays the journal
/// instead. Applying an operation and then its <see cref="EditOp.Inverse"/> restores the previous state exactly
/// (overrides that return to their original value are dropped).
/// </summary>
public sealed partial class EditState
{
    private readonly HashSet<ActorRef> _deletedActors = new(ActorRef.Comparer);
    private readonly HashSet<InstanceRef> _deletedInstances = new(InstanceRef.Comparer);
    private readonly Dictionary<(ActorRef Actor, string Component), Override> _transforms = new(TransformKeyComparer.Instance);
    private readonly Dictionary<InstanceRef, Override> _instanceTransforms = new(InstanceRef.Comparer);
    private readonly Dictionary<ActorRef, EditOp> _added = new(ActorRef.Comparer);
    private readonly Dictionary<InstanceRef, AddInstanceOp> _addedInstances = new(InstanceRef.Comparer);
    private readonly Dictionary<ActorRef, BendValue> _bends = new(ActorRef.Comparer);
    private readonly Dictionary<(ActorRef Actor, string Component), BendValue> _segmentSways = new(TransformKeyComparer.Instance);
    private readonly Dictionary<(ActorRef Actor, string Component, string Array), PointsOverride> _spawnPoints = new(PointsKeyComparer.Instance);
    private readonly Dictionary<(ActorRef Actor, string Component), MeshOverride> _meshes = new(TransformKeyComparer.Instance);
    private readonly Dictionary<(ActorRef Actor, string Component), (LootSetting Base, LootSetting Current)> _loot = new(TransformKeyComparer.Instance);
    private readonly Dictionary<ActorRef, bool> _grass = new(ActorRef.Comparer);
    private readonly HashSet<ActorRef> _noLoot = new(ActorRef.Comparer);
    private readonly HashSet<(ActorRef Actor, string Component)> _deletedComponents = new(TransformKeyComparer.Instance);
    private readonly Dictionary<(ActorRef Actor, string Property), string> _choices = new(TransformKeyComparer.Instance);
    private readonly List<TerrainHole> _holes = [];
    private readonly List<(ActorRef Zone, SubZone SubZone)> _subZones = [];

    // The new spawn places of _added, for readers on another thread: replaced on every change, never changed.
    private volatile AddSpawnPlaceOp[] _newPlaces = [];

    /// <summary>True when no operation has a net effect.</summary>
    public bool IsEmpty =>
        _deletedActors.Count == 0 && _deletedInstances.Count == 0 && _transforms.Count == 0 && _instanceTransforms.Count == 0 && _added.Count == 0
        && _addedInstances.Count == 0 && _clones.Count == 0 && _values.Count == 0 && _replacements.Count == 0 && _bends.Count == 0 && _segmentSways.Count == 0 && _spawnPoints.Count == 0
        && _meshes.Count == 0 && _loot.Count == 0 && _deletedComponents.Count == 0 && _choices.Count == 0 && _holes.Count == 0 && _subZones.Count == 0;

    /// <summary>Sub-zones added to stock threat zones (see <see cref="AddSubZoneOp"/>), oldest first.</summary>
    public IReadOnlyList<(ActorRef Zone, SubZone SubZone)> SubZones => _subZones;

    /// <summary>Holes cut into the landscape (see <see cref="TerrainHoleOp"/>), oldest first. Not in <see cref="ChangedLevels"/>: the export cuts them after the levels.</summary>
    public IReadOnlyList<TerrainHole> TerrainHoles => _holes;

    /// <summary>Deleted components of kept actors (see <see cref="DeleteComponentsOp"/>).</summary>
    public IReadOnlyCollection<(ActorRef Actor, string Component)> DeletedComponents => _deletedComponents;

    /// <summary>True when the component is deleted (directly, or because its actor is).</summary>
    public bool IsDeleted(ActorRef actor, string component) => _deletedComponents.Contains((actor, component)) || _deletedActors.Contains(actor);

    /// <summary>Every mesh component drawing another mesh (see <see cref="ReplaceMeshOp"/>): the actor, the component (empty = the root) and the mesh now.</summary>
    public IEnumerable<(ActorRef Actor, string Component, string Mesh)> MeshOverrides =>
        _meshes.Select(m => (m.Key.Actor, m.Key.Component, m.Value.Current));

    /// <summary>The mesh an actor's root (or named component) draws instead of its own, or null when it draws its own.</summary>
    public string? GetMeshOverride(ActorRef actor, string? component = null) =>
        _meshes.TryGetValue((actor, component ?? string.Empty), out var o) ? o.Current : null;

    /// <summary>Every mesh component whose loot was set (see <see cref="SetLootOp"/>): the actor, the component (empty = the root) and its loot now.</summary>
    public IEnumerable<(ActorRef Actor, string Component, LootSetting Loot)> LootOverrides => _loot.Select(l => (l.Key.Actor, l.Key.Component, l.Value.Current));

    /// <summary>The loot set on a component, or null when it is as the level (or its copy source) has it.</summary>
    public LootSetting? GetLoot(ActorRef actor, string? component = null) => _loot.TryGetValue((actor, component ?? string.Empty), out var l) ? l.Current : null;

    /// <summary>The "clear grass under it" setting of an actor as set (see <see cref="SetClearGrassOp"/>), or null for the default.</summary>
    public bool? GetClearGrass(ActorRef actor) => _grass.TryGetValue(actor, out var on) ? on : null;

    /// <summary>Every spawner whose choice was set (see <see cref="SetSpawnChoiceOp"/>): the actor, which choice, and its value now.</summary>
    public IEnumerable<(ActorRef Actor, string Property, string Value)> SpawnChoices => _choices.Select(c => (c.Key.Actor, c.Key.Property, c.Value));

    /// <summary>What a spawner spawns as set (see <see cref="SetSpawnChoiceOp"/>), or null when it is as the game has it.</summary>
    public string? GetSpawnChoice(ActorRef actor, string property) => _choices.TryGetValue((actor, property), out var v) ? v : null;

    /// <summary>True when the export clears the grass under the actor: as set, else on for an actor the project added.</summary>
    public bool ClearsGrass(ActorRef actor) => GetClearGrass(actor) ?? IsAdded(actor);

    /// <summary>True when the placed actor has "No loot inside" (see <see cref="SetNoLootOp"/>).</summary>
    public bool IsNoLoot(ActorRef actor) => _noLoot.Contains(actor);

    /// <summary>The placed actors with "No loot inside" (see <see cref="SetNoLootOp"/>).</summary>
    public IReadOnlyCollection<ActorRef> NoLootActors => _noLoot;

    /// <summary>Deleted actors (pristine or added).</summary>
    public IReadOnlyCollection<ActorRef> DeletedActors => _deletedActors;

    /// <summary>Deleted ISM/HISM instances.</summary>
    public IReadOnlyCollection<InstanceRef> DeletedInstances => _deletedInstances;

    /// <summary>Actors created by duplicate/add operations, with the operation that created them.</summary>
    public IReadOnlyDictionary<ActorRef, EditOp> AddedActors => _added;

    /// <summary>
    /// The new spawn places (<see cref="AddSpawnPlaceOp"/>) of the project, in the order they were made. Safe to read on
    /// another thread while edits are applied (the map's loading worker): the list is replaced on a change, never changed.
    /// </summary>
    public IReadOnlyList<AddSpawnPlaceOp> NewSpawnPlaces => _newPlaces;

    /// <summary>ISM/HISM instances the project added (copied trees), with the operation that created them.</summary>
    public IReadOnlyDictionary<InstanceRef, AddInstanceOp> AddedInstances => _addedInstances;

    /// <summary>
    /// Every net transform override: the actor, the component it applies to (empty = the root component) and the current
    /// relative transform. The exporter patches these into the component exports.
    /// </summary>
    public IEnumerable<(ActorRef Actor, string Component, TransformValue Value)> TransformOverrides =>
        _transforms.Select(t => (t.Key.Actor, t.Key.Component, t.Value.Current));

    /// <summary>Every net ISM/HISM instance transform override.</summary>
    public IEnumerable<(InstanceRef Instance, TransformValue Value)> InstanceOverrides =>
        _instanceTransforms.Select(t => (t.Key, t.Value.Current));

    /// <summary>Bent actors and their shape (see <see cref="BendActorOp"/>).</summary>
    public IReadOnlyDictionary<ActorRef, BendValue> Bends => _bends;

    /// <summary>The actor's bend in degrees (0 = straight).</summary>
    public float GetBend(ActorRef actor) => _bends.GetValueOrDefault(actor).Degrees;

    /// <summary>The actor's whole shape (bend and handles; default = straight).</summary>
    public BendValue GetBendValue(ActorRef actor) => _bends.GetValueOrDefault(actor);

    /// <summary>Road, rail and bridge pieces pushed sideways (see <see cref="SwaySegmentOp"/>).</summary>
    public IEnumerable<(ActorRef Actor, string Component, BendValue Shape)> SegmentSways =>
        _segmentSways.Select(s => (s.Key.Actor, s.Key.Component, s.Value));

    /// <summary>The handles' pushes of one spline piece (0, 0 = as the level has it).</summary>
    public BendValue GetSegmentShape(ActorRef actor, string component) => _segmentSways.GetValueOrDefault((actor, component));

    /// <summary>
    /// Every spawner point array with a net change (see <see cref="SetSpawnPointsOp"/>): the actor, the component storing
    /// the array (null = the actor's own), the array and its current points. The exporter rewrites these arrays.
    /// </summary>
    public IEnumerable<(ActorRef Actor, string? Component, string Array, IReadOnlyList<SpawnPoint> Points)> SpawnPointOverrides =>
        _spawnPoints.Select(p => (p.Key.Actor, p.Key.Component.Length == 0 ? null : p.Key.Component, p.Key.Array, p.Value.Current));

    /// <summary>The overridden points of a spawner's array, or null when it is as the level stores it.</summary>
    public IReadOnlyList<SpawnPoint>? GetSpawnPoints(ActorRef actor, string? component, string array) =>
        _spawnPoints.TryGetValue((actor, component ?? string.Empty, array), out var o) ? o.Current : null;

    /// <summary>Levels with a net change.</summary>
    public IReadOnlyList<string> ChangedLevels =>
        _deletedActors.Select(a => a.Level)
            .Concat(_deletedInstances.Select(i => i.Level))
            .Concat(_transforms.Keys.Select(k => k.Actor.Level))
            .Concat(_instanceTransforms.Keys.Select(i => i.Level))
            .Concat(_added.Keys.Select(a => a.Level))
            .Concat(_addedInstances.Keys.Select(i => i.Level))
            .Concat(_bends.Keys.Select(a => a.Level))
            .Concat(_segmentSways.Keys.Select(k => k.Actor.Level))
            .Concat(_spawnPoints.Keys.Select(k => k.Actor.Level))
            .Concat(_meshes.Keys.Select(k => k.Actor.Level))
            .Concat(_loot.Keys.Select(k => k.Actor.Level))
            .Concat(_deletedComponents.Select(k => k.Actor.Level))
            .Concat(_choices.Keys.Select(k => k.Actor.Level))
            .Concat(_subZones.Select(s => s.Zone.Level))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>True when the actor is deleted.</summary>
    public bool IsDeleted(ActorRef actor) => _deletedActors.Contains(actor);

    /// <summary>True when the instance is deleted (directly, or because its actor is).</summary>
    public bool IsDeleted(InstanceRef instance) => _deletedInstances.Contains(instance) || _deletedActors.Contains(instance.ActorRef);

    /// <summary>True when the actor was created by an edit.</summary>
    public bool IsAdded(ActorRef actor) => _added.ContainsKey(actor);

    /// <summary>True when the instance was created by an edit (see <see cref="AddInstanceOp"/>).</summary>
    public bool IsAdded(InstanceRef instance) => _addedInstances.ContainsKey(instance);

    /// <summary>The effective transform of an added instance (its creation transform or a later override), or null when it was not added.</summary>
    public TransformValue? GetAddedInstanceTransform(InstanceRef instance) =>
        _addedInstances.TryGetValue(instance, out var op) ? GetInstanceOverride(instance) ?? op.Transform : null;

    /// <summary>The overridden relative transform of an actor's root (or named component), if any.</summary>
    public TransformValue? GetTransformOverride(ActorRef actor, string? component = null) =>
        _transforms.TryGetValue((actor, component ?? string.Empty), out var o) ? o.Current : null;

    /// <summary>The overridden transform of an instance, if any.</summary>
    public TransformValue? GetInstanceOverride(InstanceRef instance) =>
        _instanceTransforms.TryGetValue(instance, out var o) ? o.Current : null;

    /// <summary>The transform an edit puts something at, or null for an edit that places nothing.</summary>
    private static TransformValue? NewTransform(EditOp op) => op switch
    {
        SetTransformOp s => s.New,
        SetInstanceTransformOp i => i.New,
        AddInstanceOp a => a.Transform,
        DuplicateActorOp d => d.Transform,
        AddStaticMeshActorOp s => s.Transform,
        AddBlueprintActorOp b => b.Transform,
        AddSpawnPlaceOp p => p.Transform,
        _ => null,
    };

    /// <summary>True when every value of <paramref name="t"/> is a finite number and the place is within <see cref="MaxCoordinate"/>.</summary>
    public static bool IsSane(TransformValue t)
    {
        static bool Finite(float v) => float.IsFinite(v);
        var (l, r, s) = (t.Location, t.Rotation, t.Scale);
        return Finite(l.X) && Finite(l.Y) && Finite(l.Z) && Finite(r.Pitch) && Finite(r.Yaw) && Finite(r.Roll) && Finite(s.X) && Finite(s.Y) && Finite(s.Z)
               && MathF.Abs(l.X) <= MaxCoordinate && MathF.Abs(l.Y) <= MaxCoordinate && MathF.Abs(l.Z) <= MaxCoordinate;
    }

    /// <summary>
    /// The effective root transform of an added actor (its creation transform or a later override), or null when the
    /// actor was not added.
    /// </summary>
    public TransformValue? GetAddedTransform(ActorRef actor)
    {
        if (!_added.TryGetValue(actor, out var op))
        {
            return null;
        }

        return GetTransformOverride(actor) ?? op switch
        {
            DuplicateActorOp d => d.Transform,
            AddStaticMeshActorOp s => s.Transform,
            AddBlueprintActorOp b => b.Transform,
            AddSpawnPlaceOp p => p.Transform,
            _ => null,
        };
    }

    /// <summary>How far from the island's centre (cm, on any axis) an edit may put something: 100 km, many times the map.</summary>
    public const float MaxCoordinate = 10_000_000f;

    /// <summary>Returns why <paramref name="op"/> cannot be applied to the current state, or null when it can.</summary>
    public string? Validate(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (NewTransform(op) is { } placed && !IsSane(placed))
        {
            // Discord (seviscache): 175 x "Function does not accept floating point Not-a-Number values" after a drag flung
            // an object to the far side of the map; such a value must never reach the journal or the exported level.
            return $"{op.Describe()}: the position, turn or scale is not a usable number ({placed}).";
        }

        switch (op)
        {
            case DeleteActorOp d:
                return _deletedActors.Contains(d.Target) ? $"{d.Target} is already deleted." : null;
            case RestoreActorOp r:
                return _deletedActors.Contains(r.Target) ? null : $"{r.Target} is not deleted.";
            case DeleteComponentsOp dc:
                return ValidateComponents(dc.Target, dc.Components)
                       ?? FirstError(dc.Components.Where(c => _deletedComponents.Contains((dc.Target, c))).Select(c => $"{dc.Target}.{c} is already deleted."));
            case RestoreComponentsOp rc:
                return ValidateComponents(rc.Target, rc.Components)
                       ?? FirstError(rc.Components.Where(c => !_deletedComponents.Contains((rc.Target, c))).Select(c => $"{rc.Target}.{c} is not deleted."));
            case DeleteAllOfKindOp all:
                if (all.Actors.Count == 0 && all.Instances.Count == 0)
                {
                    return "Nothing matches.";
                }

                return FirstError(
                    all.Actors.Where(_deletedActors.Contains).Select(a => $"{a} is already deleted."),
                    all.Instances.Where(IsDeleted).Select(i => $"{i} is already deleted."),
                    Duplicates(all.Actors, ActorRef.Comparer), Duplicates(all.Instances, InstanceRef.Comparer));
            case RestoreAllOfKindOp all:
                return FirstError(
                    all.Actors.Where(a => !_deletedActors.Contains(a)).Select(a => $"{a} is not deleted."),
                    all.Instances.Where(i => !_deletedInstances.Contains(i)).Select(i => $"{i} is not deleted."));
            case DuplicateActorOp dup:
                // A deleted source is fine: the copy is made from its stored exports before the export deletes it (a copy of a
                // copy is a copy of the original, which may be deleted by then; audit F203).
                return ValidateNewActor(dup.Created);
            case AddStaticMeshActorOp add:
                return string.IsNullOrWhiteSpace(add.StaticMesh) ? "No static mesh given." : ValidateNewActor(add.Created);
            case AddBlueprintActorOp bp:
                return string.IsNullOrWhiteSpace(bp.ClassPath) ? "No Blueprint class given." : ValidateNewActor(bp.Created);
            case AddSpawnPlaceOp place:
                return !Enum.IsDefined(place.Kind) ? $"Unknown kind of spawn place ({place.Kind})."
                    : string.IsNullOrWhiteSpace(place.Setting) ? "A new spawn place needs what spawns there (a group, a type, zone settings or a biome)."
                    : place.Tags is { Count: > 0 } tags && (place.Kind != SpawnPlaceKind.TaggedCharacter || tags.Any(string.IsNullOrWhiteSpace)) ? "Only a tagged zombie / NPC point has tags, and each tag needs a name."
                    : (place.Kind == SpawnPlaceKind.CharacterGroup) != (place.Points is { Count: > 0 }) ? "A group of zombie / NPC points needs its posts, and only a group has posts."
                    : place.Points is { Count: > SpawnPlaces.MaxGroupPosts } ? $"A group of zombie / NPC points holds at most {SpawnPlaces.MaxGroupPosts} posts."
                    : place.Points?.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) == true ? "A post of the group is not a number."
                    : SpawnPlaces.ParseName(place.NewName) is not null ? $"{place.NewName} is the name of a stock spawn place."
                    : ValidateNewActor(place.Created);
            case RemoveAddedActorOp remove:
                if (!_added.TryGetValue(remove.Target, out var original))
                {
                    return $"{remove.Target} was not added by an edit.";
                }

                return original.Equals(remove.Original) ? null : $"{remove.Target} was added by a different operation.";
            case SetLootOp loot:
                return _deletedActors.Contains(loot.Target) ? $"{loot.Target} is deleted." : null;
            case SetSpawnChoiceOp choice:
                return _deletedActors.Contains(choice.Target) ? $"{choice.Target} is deleted."
                    : GetSpawnChoice(choice.Target, choice.Property) != choice.Old ? $"{choice.Target}: what it spawns is not as expected (edit is out of date)." : null;
            case AddSubZoneOp add:
                return SpawnPlaces.ParseName(add.Zone.Actor) is not { Kind: SpawnPlaceKind.Zone } || !SpawnPlaces.IsStaticData(add.Zone.Level)
                    ? $"{add.Zone} is not a threat zone of the island data (only those hold sub-zones)."
                    : _deletedActors.Contains(add.Zone) ? $"{add.Zone} is deleted."
                    : string.IsNullOrWhiteSpace(add.SubZone.Setting) ? "A sub-zone needs its zone settings."
                    : !IsSaneBox(add.SubZone) ? "The sub-zone's box is not usable (each corner a number on the island, the lower one below the upper one)."
                    : IndexOfSubZone(add.Zone, add.SubZone) >= 0 ? $"{add.Zone} already has this sub-zone." : null;
            case RemoveSubZoneOp remove:
                return IndexOfSubZone(remove.Zone, remove.SubZone) >= 0 ? null : $"{remove.Zone} has no such sub-zone.";
            case TerrainHoleOp hole:
                return hole.Hole.Validate()
                       ?? (hole.Cut == _holes.Contains(hole.Hole) ? $"The {hole.Hole} is {(hole.Cut ? "already cut" : "not cut")}." : null);
            case SetClearGrassOp grass:
                return _deletedActors.Contains(grass.Target) ? $"{grass.Target} is deleted."
                    : GetClearGrass(grass.Target) != grass.Old ? $"{grass.Target}: the grass setting is not as expected (edit is out of date)." : null;
            case SetNoLootOp noLoot:
                return _deletedActors.Contains(noLoot.Target) ? $"{noLoot.Target} is deleted."
                    : !_added.ContainsKey(noLoot.Target) ? $"{noLoot.Target} is not an object the project placed; take the loot out of its searchable parts one by one instead."
                    : IsNoLoot(noLoot.Target) != noLoot.Old ? $"{noLoot.Target}: the loot setting is not as expected (edit is out of date)." : null;
            case SetTransformOp set:
                if (_deletedActors.Contains(set.Target))
                {
                    return $"{set.Target} is deleted.";
                }

                return GetTransformOverride(set.Target, set.Component) is { } current && current != set.Old
                    ? $"{set.Target} is not at the expected transform (edit is out of date)."
                    : null;
            case SetInstanceTransformOp setInstance:
                if (IsDeleted(setInstance.Target))
                {
                    return $"{setInstance.Target} is deleted.";
                }

                return GetInstanceOverride(setInstance.Target) is { } currentInstance && currentInstance != setInstance.Old
                    ? $"{setInstance.Target} is not at the expected transform (edit is out of date)."
                    : null;
            case BendActorOp bend:
                if (_deletedActors.Contains(bend.Target))
                {
                    return $"{bend.Target} is deleted.";
                }

                if (!float.IsFinite(bend.New) || MathF.Abs(bend.New) > BendShape.MaxDegrees)
                {
                    return $"A bend must be between -{BendShape.MaxDegrees} and {BendShape.MaxDegrees} degrees.";
                }

                if (!bend.NewValue.IsValid)
                {
                    return "A bend's handles and ends must be usable numbers.";
                }

                return GetBendValue(bend.Target).IsNearly(bend.OldValue) ? null : $"{bend.Target} is not bent as expected (edit is out of date).";
            case SwaySegmentOp sway:
                if (_deletedActors.Contains(sway.Target))
                {
                    return $"{sway.Target} is deleted.";
                }

                return !GetSegmentShape(sway.Target, sway.Component).IsNearly(sway.OldValue)
                    ? $"{sway.Target}.{sway.Component} is not shaped as expected (edit is out of date)."
                    : !sway.NewValue.IsValid ? "Handle pushes and ends must be usable numbers." : null;
            case DeleteInstanceOp di:
                return IsDeleted(di.Target) ? $"{di.Target} is already deleted." : null;
            case RestoreInstanceOp ri:
                return _deletedInstances.Contains(ri.Target) ? null : $"{ri.Target} is not deleted.";
            case AddInstanceOp ai:
                return ai.Target.Index < 0 ? "An instance index cannot be negative."
                    : _addedInstances.ContainsKey(ai.Target) ? $"{ai.Target} already exists." : null;
            case RemoveAddedInstanceOp removeInstance:
                if (!_addedInstances.TryGetValue(removeInstance.Target, out var originalInstance))
                {
                    return $"{removeInstance.Target} was not added by an edit.";
                }

                return originalInstance.Equals(removeInstance.Original) ? null : $"{removeInstance.Target} was added by a different operation.";
            case SetSpawnPointsOp points:
                if (_deletedActors.Contains(points.Target))
                {
                    return $"{points.Target} is deleted.";
                }

                if (string.IsNullOrWhiteSpace(points.Array))
                {
                    return "No point array given.";
                }

                if (points.New.Any(p => p.Source < 0))
                {
                    return "Every point must be a copy of a stored point.";
                }

                return GetSpawnPoints(points.Target, points.Component, points.Array) is { } currentPoints && !currentPoints.SequenceEqual(points.Old)
                    ? $"{points.Target} does not have the expected spawn points (edit is out of date)."
                    : null;
            case ReplaceMeshOp mesh:
                if (_deletedActors.Contains(mesh.Target))
                {
                    return $"{mesh.Target} is deleted.";
                }

                if (string.IsNullOrWhiteSpace(mesh.New))
                {
                    return "No static mesh given.";
                }

                return GetMeshOverride(mesh.Target, mesh.Component) is { } currentMesh && !EditOpFactory.SameObject(currentMesh, mesh.Old)
                    ? $"{mesh.Target} does not draw the expected mesh (edit is out of date)."
                    : null;
            case CloneAssetOp or RemoveAssetCloneOp or SetAssetValueOp or ReplaceAssetOp:
                return ValidateAsset(op);
            case BatchOp batch:
                if (batch.Ops.Count == 0)
                {
                    return "Nothing to do.";
                }

                // Each edit must be valid once the ones before it are applied (a dry run on a copy: a batch that stops halfway
                // must never be journaled), and no two may create (or remove) the same actor.
                var created = batch.Ops.Select(o => o switch
                {
                    AddStaticMeshActorOp a => a.Created,
                    AddBlueprintActorOp b => b.Created,
                    DuplicateActorOp d => d.Created,
                    AddSpawnPlaceOp p => p.Created,
                    RemoveAddedActorOp r => r.Target,
                    _ => null,
                }).OfType<ActorRef>();
                var dry = Clone();
                foreach (var child in batch.Ops)
                {
                    if (dry.IsCoveredInBatch(child))
                    {
                        continue;
                    }

                    if (dry.Validate(child) is { } childError)
                    {
                        return childError;
                    }

                    dry.Apply(child);
                }

                return Duplicates(created, ActorRef.Comparer).FirstOrDefault()
                       ?? Duplicates(batch.Ops.OfType<AddInstanceOp>().Select(a => a.Target), InstanceRef.Comparer).FirstOrDefault();
            case EconomyOp:
                return null;
            default:
                return $"Unsupported operation {op.GetType().Name}.";
        }
    }

    /// <summary>Applies <paramref name="op"/>.</summary>
    /// <exception cref="InvalidOperationException">The operation is not valid for the current state (see <see cref="Validate"/>).</exception>
    public void Apply(EditOp op)
    {
        if (Validate(op) is { } error)
        {
            throw new InvalidOperationException($"Cannot apply '{op.Describe()}': {error}");
        }

        switch (op)
        {
            case DeleteActorOp d:
                _deletedActors.Add(d.Target);
                break;
            case RestoreActorOp r:
                _deletedActors.Remove(r.Target);
                break;
            case DeleteComponentsOp dc:
                _deletedComponents.UnionWith(dc.Components.Select(c => (dc.Target, c)));
                break;
            case RestoreComponentsOp rc:
                _deletedComponents.ExceptWith(rc.Components.Select(c => (rc.Target, c)));
                break;
            case DeleteAllOfKindOp all:
                _deletedActors.UnionWith(all.Actors);
                _deletedInstances.UnionWith(all.Instances);
                break;
            case RestoreAllOfKindOp all:
                _deletedActors.ExceptWith(all.Actors);
                _deletedInstances.ExceptWith(all.Instances);
                break;
            case DuplicateActorOp dup:
                _added[dup.Created] = dup;
                break;
            case AddStaticMeshActorOp add:
                _added[add.Created] = add;
                break;
            case AddBlueprintActorOp bp:
                _added[bp.Created] = bp;
                break;
            case AddSpawnPlaceOp place:
                _added[place.Created] = place;
                _newPlaces = [.. _newPlaces, place];
                break;
            case RemoveAddedActorOp remove:
                _added.Remove(remove.Target);
                if (_newPlaces.Any(p => ActorRef.Comparer.Equals(p.Created, remove.Target)))
                {
                    _newPlaces = _newPlaces.Where(p => !ActorRef.Comparer.Equals(p.Created, remove.Target)).ToArray();
                }

                _deletedActors.Remove(remove.Target);
                foreach (var key in _transforms.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _transforms.Remove(key);
                }

                foreach (var key in _deletedInstances.Where(i => ActorRef.Comparer.Equals(i.ActorRef, remove.Target)).ToList())
                {
                    _deletedInstances.Remove(key);
                }

                foreach (var key in _instanceTransforms.Keys.Where(i => ActorRef.Comparer.Equals(i.ActorRef, remove.Target)).ToList())
                {
                    _instanceTransforms.Remove(key);
                }

                _bends.Remove(remove.Target);
                _grass.Remove(remove.Target);
                foreach (var key in _choices.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _choices.Remove(key);
                }

                foreach (var key in _spawnPoints.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _spawnPoints.Remove(key);
                }

                foreach (var key in _meshes.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _meshes.Remove(key);
                }

                foreach (var key in _loot.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _loot.Remove(key);
                }

                // A copy carries its source's sways and grass setting (see ActorEdits): they go with it.
                foreach (var key in _segmentSways.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _segmentSways.Remove(key);
                }

                _grass.Remove(remove.Target);
                _noLoot.Remove(remove.Target);
                break;
            case ReplaceMeshOp mesh:
                var meshKey = (mesh.Target, mesh.Component ?? string.Empty);
                if (!_meshes.TryGetValue(meshKey, out var drawn))
                {
                    if (!EditOpFactory.SameObject(mesh.Old, mesh.New))
                    {
                        _meshes[meshKey] = new MeshOverride(mesh.Old, mesh.New);
                    }
                }
                else if (EditOpFactory.SameObject(drawn.Base, mesh.New))
                {
                    _meshes.Remove(meshKey);
                }
                else
                {
                    _meshes[meshKey] = drawn with { Current = mesh.New };
                }

                break;
            case SetLootOp loot:
                var lootKey = (loot.Target, loot.Component ?? string.Empty);
                var lootBase = _loot.TryGetValue(lootKey, out var known) ? known.Base : loot.Old;
                if (lootBase.Equals(loot.New) && !InheritsLoot(loot.Target, lootKey.Item2))
                {
                    _loot.Remove(lootKey);
                }
                else
                {
                    _loot[lootKey] = (lootBase, loot.New);
                }

                break;
            case SetSpawnChoiceOp choice:
                if (choice.New is { } value)
                {
                    _choices[(choice.Target, choice.Property)] = value;
                }
                else
                {
                    _choices.Remove((choice.Target, choice.Property));
                }

                break;
            case SetClearGrassOp grass:
                if (grass.New is { } on)
                {
                    _grass[grass.Target] = on;
                }
                else
                {
                    _grass.Remove(grass.Target);
                }

                break;
            case SetNoLootOp noLoot:
                if (noLoot.New)
                {
                    _noLoot.Add(noLoot.Target);
                }
                else
                {
                    _noLoot.Remove(noLoot.Target);
                }

                break;
            case SetTransformOp set:
                SetOverride(_transforms, (set.Target, set.Component ?? string.Empty), set.Old, set.New);
                break;
            case AddSubZoneOp add:
                _subZones.Add((add.Zone, add.SubZone));
                break;
            case RemoveSubZoneOp remove:
                _subZones.RemoveAt(IndexOfSubZone(remove.Zone, remove.SubZone));
                break;
            case TerrainHoleOp hole:
                if (hole.Cut)
                {
                    _holes.Add(hole.Hole);
                }
                else
                {
                    _holes.Remove(hole.Hole);
                }

                break;
            case SetSpawnPointsOp points:
                var pointsKey = (points.Target, points.Component ?? string.Empty, points.Array);
                if (!_spawnPoints.TryGetValue(pointsKey, out var entry))
                {
                    if (!points.Old.SequenceEqual(points.New))
                    {
                        _spawnPoints[pointsKey] = new PointsOverride(points.Old, points.New);
                    }
                }
                else if (entry.Base.SequenceEqual(points.New))
                {
                    _spawnPoints.Remove(pointsKey);
                }
                else
                {
                    _spawnPoints[pointsKey] = entry with { Current = points.New };
                }

                break;
            case SetInstanceTransformOp setInstance:
                SetOverride(_instanceTransforms, setInstance.Target, setInstance.Old, setInstance.New);
                break;
            case BendActorOp bend:
                if (bend.NewValue.IsStraight)
                {
                    _bends.Remove(bend.Target);
                }
                else
                {
                    _bends[bend.Target] = bend.NewValue;
                }

                break;
            case SwaySegmentOp sway:
                if (sway.NewValue.IsStraight)
                {
                    _segmentSways.Remove((sway.Target, sway.Component));
                }
                else
                {
                    _segmentSways[(sway.Target, sway.Component)] = sway.NewValue;
                }

                break;
            case DeleteInstanceOp di:
                _deletedInstances.Add(di.Target);
                break;
            case RestoreInstanceOp ri:
                _deletedInstances.Remove(ri.Target);
                break;
            case AddInstanceOp ai:
                _addedInstances[ai.Target] = ai;
                break;
            case RemoveAddedInstanceOp removeInstance:
                _addedInstances.Remove(removeInstance.Target);
                _instanceTransforms.Remove(removeInstance.Target);
                _deletedInstances.Remove(removeInstance.Target);
                break;
            case CloneAssetOp or RemoveAssetCloneOp or SetAssetValueOp or ReplaceAssetOp:
                ApplyAsset(op);
                break;
            case BatchOp batch:
                foreach (var child in batch.Ops)
                {
                    if (!IsCoveredInBatch(child))
                    {
                        Apply(child);
                    }
                }

                break;
            case EconomyOp:
                break;
        }
    }

    /// <summary>
    /// True for a child of a batch that an actor delete in the same batch already covers: an instance delete of an actor
    /// that is deleted (the brush adds a tree and then its whole actor; the owner's first bulk delete stopped at such a
    /// child and left 2000 edits unapplied), and the matching instance restore on the way back (an instance deleted before
    /// the batch stays deleted: its actor's restore comes after it in the inverse, so nothing of the batch's own is lost).
    /// </summary>
    private bool IsCoveredInBatch(EditOp op) => op switch
    {
        DeleteInstanceOp d => _deletedActors.Contains(d.Target.ActorRef),
        RestoreInstanceOp r => _deletedActors.Contains(r.Target.ActorRef),
        // A part scaled to nothing, a bend or a sway of an actor the batch already deleted: nothing left to edit.
        SetTransformOp { Component: not null } t => _deletedActors.Contains(t.Target),
        SetInstanceTransformOp it => _deletedActors.Contains(it.Target.ActorRef),
        BendActorOp bend => _deletedActors.Contains(bend.Target),
        // The fire of a fireplace the batch deletes whole goes with it; on the way back the actor's restore comes after.
        DeleteComponentsOp dc => _deletedActors.Contains(dc.Target),
        RestoreComponentsOp rc => _deletedActors.Contains(rc.Target),
        _ => false,
    };

    /// <summary>A copy of this state (the entries are immutable records).</summary>
    public EditState Clone()
    {
        var copy = new EditState();
        copy._deletedActors.UnionWith(_deletedActors);
        copy._deletedInstances.UnionWith(_deletedInstances);
        copy._deletedComponents.UnionWith(_deletedComponents);
        copy._noLoot.UnionWith(_noLoot);
        Copy(_transforms, copy._transforms);
        Copy(_instanceTransforms, copy._instanceTransforms);
        Copy(_added, copy._added);
        copy._newPlaces = _newPlaces;
        Copy(_addedInstances, copy._addedInstances);
        Copy(_bends, copy._bends);
        Copy(_segmentSways, copy._segmentSways);
        Copy(_spawnPoints, copy._spawnPoints);
        Copy(_loot, copy._loot);
        Copy(_meshes, copy._meshes);
        Copy(_grass, copy._grass);
        Copy(_choices, copy._choices);
        copy._holes.AddRange(_holes);
        copy._subZones.AddRange(_subZones);
        CopyAssets(copy);
        return copy;

        static void Copy<TKey, TValue>(Dictionary<TKey, TValue> from, Dictionary<TKey, TValue> to)
            where TKey : notnull
        {
            foreach (var (key, value) in from)
            {
                to[key] = value;
            }
        }
    }

    /// <summary>Replays <paramref name="ops"/> onto a new state.</summary>
    public static EditState Replay(IEnumerable<EditOp> ops)
    {
        var state = new EditState();
        foreach (var op in ops)
        {
            state.Apply(op);
        }

        return state;
    }

    /// <summary>
    /// Canonical, order-independent text of the state (sorted), for comparisons and diagnostics.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>();
        lines.AddRange(_deletedActors.Select(a => "delete " + Key(a)));
        lines.AddRange(_deletedInstances.Select(i => "delete-instance " + Key(i)));
        lines.AddRange(_deletedComponents.Select(c => $"delete-component {Key(c.Actor)}.{c.Component.ToLowerInvariant()}"));
        lines.AddRange(_transforms.Select(t => $"transform {Key(t.Key.Actor)}.{t.Key.Component.ToLowerInvariant()} {t.Value.Base} -> {t.Value.Current}"));
        lines.AddRange(_instanceTransforms.Select(t => $"instance-transform {Key(t.Key)} {t.Value.Base} -> {t.Value.Current}"));
        lines.AddRange(_added.Select(a => $"added {Key(a.Key)} by {a.Value.GetType().Name}"));
        lines.AddRange(_addedInstances.Select(a => $"added-instance {Key(a.Key)} at {a.Value.Transform}"));
        lines.AddRange(_bends.Select(b => FormattableString.Invariant($"bend {Key(b.Key)} {b.Value.Degrees:0.###} {b.Value.Sway1:0.##} {b.Value.Sway2:0.##}{b.Value.DescribeEnds()}")));
        lines.AddRange(_segmentSways.Select(s => FormattableString.Invariant($"sway {Key(s.Key.Actor)}.{s.Key.Component.ToLowerInvariant()} {s.Value.Sway1:0.##} {s.Value.Sway2:0.##}{s.Value.DescribeEnds()}")));
        lines.AddRange(_spawnPoints.Select(p => FormattableString.Invariant(
            $"spawn-points {Key(p.Key.Actor)}.{p.Key.Component.ToLowerInvariant()}.{p.Key.Array.ToLowerInvariant()} {p.Value.Base.Count} -> {p.Value.Current.Count}: {string.Join(" ", p.Value.Current.Select(x => $"{x.Source}@{x.Local}"))}")));
        lines.AddRange(_meshes.Select(m => $"mesh {Key(m.Key.Actor)}.{m.Key.Component.ToLowerInvariant()} {m.Value.Base} -> {m.Value.Current}"));
        lines.AddRange(_subZones.Select(s => $"sub-zone {Key(s.Zone)} {s.SubZone}"));
        lines.AddRange(_noLoot.Select(a => "no-loot " + Key(a)));
        lines.AddRange(DescribeAssets());
        lines.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();

        static string Key(object o) => (o.ToString() ?? string.Empty).ToLower(CultureInfo.InvariantCulture);
    }

    // What both component ops refuse: a deleted or added actor (its copy's exports are its source's), no name, a name twice.
    private string? ValidateComponents(ActorRef actor, IReadOnlyList<string> components) =>
        _deletedActors.Contains(actor) ? $"{actor} is deleted."
        : _added.ContainsKey(actor) ? $"{actor} was added by an edit; delete the whole copy instead."
        : components.Count == 0 ? "No component given."
        : components.Any(string.IsNullOrWhiteSpace) ? "A component needs a name."
        : Duplicates(components, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private int IndexOfSubZone(ActorRef zone, SubZone subZone) =>
        _subZones.FindIndex(s => ActorRef.Comparer.Equals(s.Zone, zone) && s.SubZone == subZone);

    private static bool IsSaneBox(SubZone s) =>
        new[] { s.Min.X, s.Min.Y, s.Min.Z, s.Max.X, s.Max.Y, s.Max.Z }.All(v => float.IsFinite(v) && MathF.Abs(v) <= MaxCoordinate)
        && s.Min.X <= s.Max.X && s.Min.Y <= s.Max.Y && s.Min.Z <= s.Max.Z;

    private string? ValidateNewActor(ActorRef created)
    {
        if (string.IsNullOrWhiteSpace(created.Actor))
        {
            return "The new actor needs a name.";
        }

        return _added.ContainsKey(created) ? $"{created} already exists." : null;
    }

    private static void SetOverride<TKey>(Dictionary<TKey, Override> map, TKey key, TransformValue old, TransformValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var entry))
        {
            if (old != value)
            {
                map[key] = new Override(old, value);
            }

            return;
        }

        if (entry.Base == value)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = entry with { Current = value };
        }
    }

    private static IEnumerable<string> Duplicates<T>(IEnumerable<T> items, IEqualityComparer<T> comparer)
    {
        var seen = new HashSet<T>(comparer);
        return items.Where(i => !seen.Add(i)).Select(i => $"{i} is listed twice.");
    }

    private static string? FirstError(params IEnumerable<string>[] errors) =>
        errors.SelectMany(e => e).FirstOrDefault();

    private sealed record Override(TransformValue Base, TransformValue Current);

    private sealed record PointsOverride(IReadOnlyList<SpawnPoint> Base, IReadOnlyList<SpawnPoint> Current);

    private sealed record MeshOverride(string Base, string Current);

    private sealed class PointsKeyComparer : IEqualityComparer<(ActorRef Actor, string Component, string Array)>
    {
        public static readonly PointsKeyComparer Instance = new();

        public bool Equals((ActorRef Actor, string Component, string Array) x, (ActorRef Actor, string Component, string Array) y) =>
            ActorRef.Comparer.Equals(x.Actor, y.Actor) && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Array, y.Array);

        public int GetHashCode((ActorRef Actor, string Component, string Array) obj) =>
            HashCode.Combine(ActorRef.Comparer.GetHashCode(obj.Actor), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Array));
    }

    private sealed class TransformKeyComparer : IEqualityComparer<(ActorRef Actor, string Component)>
    {
        public static readonly TransformKeyComparer Instance = new();

        public bool Equals((ActorRef Actor, string Component) x, (ActorRef Actor, string Component) y) =>
            ActorRef.Comparer.Equals(x.Actor, y.Actor) && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component);

        public int GetHashCode((ActorRef Actor, string Component) obj) =>
            HashCode.Combine(ActorRef.Comparer.GetHashCode(obj.Actor), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component));
    }
}
