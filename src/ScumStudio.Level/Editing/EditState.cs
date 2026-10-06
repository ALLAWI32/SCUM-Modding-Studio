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

    /// <summary>True when no operation has a net effect.</summary>
    public bool IsEmpty =>
        _deletedActors.Count == 0 && _deletedInstances.Count == 0 && _transforms.Count == 0 && _instanceTransforms.Count == 0 && _added.Count == 0
        && _addedInstances.Count == 0 && _clones.Count == 0 && _values.Count == 0 && _replacements.Count == 0 && _bends.Count == 0 && _segmentSways.Count == 0 && _spawnPoints.Count == 0;

    /// <summary>Deleted actors (pristine or added).</summary>
    public IReadOnlyCollection<ActorRef> DeletedActors => _deletedActors;

    /// <summary>Deleted ISM/HISM instances.</summary>
    public IReadOnlyCollection<InstanceRef> DeletedInstances => _deletedInstances;

    /// <summary>Actors created by duplicate/add operations, with the operation that created them.</summary>
    public IReadOnlyDictionary<ActorRef, EditOp> AddedActors => _added;

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
            _ => null,
        };
    }

    /// <summary>Returns why <paramref name="op"/> cannot be applied to the current state, or null when it can.</summary>
    public string? Validate(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        switch (op)
        {
            case DeleteActorOp d:
                return _deletedActors.Contains(d.Target) ? $"{d.Target} is already deleted." : null;
            case RestoreActorOp r:
                return _deletedActors.Contains(r.Target) ? null : $"{r.Target} is not deleted.";
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
                return ValidateNewActor(dup.Created) ?? (_deletedActors.Contains(dup.Source) ? $"{dup.Source} is deleted." : null);
            case AddStaticMeshActorOp add:
                return string.IsNullOrWhiteSpace(add.StaticMesh) ? "No static mesh given." : ValidateNewActor(add.Created);
            case AddBlueprintActorOp bp:
                return string.IsNullOrWhiteSpace(bp.ClassPath) ? "No Blueprint class given." : ValidateNewActor(bp.Created);
            case RemoveAddedActorOp remove:
                if (!_added.TryGetValue(remove.Target, out var original))
                {
                    return $"{remove.Target} was not added by an edit.";
                }

                return original.Equals(remove.Original) ? null : $"{remove.Target} was added by a different operation.";
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
            case CloneAssetOp or RemoveAssetCloneOp or SetAssetValueOp or ReplaceAssetOp:
                return ValidateAsset(op);
            case BatchOp batch:
                if (batch.Ops.Count == 0)
                {
                    return "Nothing to do.";
                }

                // Independent edits: each must be valid now, and no two may create (or remove) the same actor.
                var created = batch.Ops.Select(o => o switch
                {
                    AddStaticMeshActorOp a => a.Created,
                    AddBlueprintActorOp b => b.Created,
                    DuplicateActorOp d => d.Created,
                    RemoveAddedActorOp r => r.Target,
                    _ => null,
                }).OfType<ActorRef>();
                return batch.Ops.Select(Validate).FirstOrDefault(e => e is not null)
                       ?? Duplicates(created, ActorRef.Comparer).FirstOrDefault()
                       ?? Duplicates(batch.Ops.OfType<AddInstanceOp>().Select(a => a.Target), InstanceRef.Comparer).FirstOrDefault();
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
            case RemoveAddedActorOp remove:
                _added.Remove(remove.Target);
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
                foreach (var key in _spawnPoints.Keys.Where(k => ActorRef.Comparer.Equals(k.Actor, remove.Target)).ToList())
                {
                    _spawnPoints.Remove(key);
                }

                break;
            case SetTransformOp set:
                SetOverride(_transforms, (set.Target, set.Component ?? string.Empty), set.Old, set.New);
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
                    Apply(child);
                }

                break;
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
        lines.AddRange(_transforms.Select(t => $"transform {Key(t.Key.Actor)}.{t.Key.Component.ToLowerInvariant()} {t.Value.Base} -> {t.Value.Current}"));
        lines.AddRange(_instanceTransforms.Select(t => $"instance-transform {Key(t.Key)} {t.Value.Base} -> {t.Value.Current}"));
        lines.AddRange(_added.Select(a => $"added {Key(a.Key)} by {a.Value.GetType().Name}"));
        lines.AddRange(_addedInstances.Select(a => $"added-instance {Key(a.Key)} at {a.Value.Transform}"));
        lines.AddRange(_bends.Select(b => FormattableString.Invariant($"bend {Key(b.Key)} {b.Value.Degrees:0.###} {b.Value.Sway1:0.##} {b.Value.Sway2:0.##}{b.Value.DescribeEnds()}")));
        lines.AddRange(_segmentSways.Select(s => FormattableString.Invariant($"sway {Key(s.Key.Actor)}.{s.Key.Component.ToLowerInvariant()} {s.Value.Sway1:0.##} {s.Value.Sway2:0.##}{s.Value.DescribeEnds()}")));
        lines.AddRange(_spawnPoints.Select(p => FormattableString.Invariant(
            $"spawn-points {Key(p.Key.Actor)}.{p.Key.Component.ToLowerInvariant()}.{p.Key.Array.ToLowerInvariant()} {p.Value.Base.Count} -> {p.Value.Current.Count}: {string.Join(" ", p.Value.Current.Select(x => $"{x.Source}@{x.Local}"))}")));
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
