namespace ScumStudio.Level.Editing;

public sealed partial class EditState
{
    /// <summary>What the project changed on <paramref name="actor"/> besides where it stands (see <see cref="ActorEdits"/>).</summary>
    public ActorEdits EditsOf(ActorRef actor) => EditsOf([actor]).GetValueOrDefault(actor) ?? ActorEdits.None;

    /// <summary>
    /// <see cref="EditsOf(ActorRef)"/> of several actors in one pass over the state (a group copy of hundreds of objects
    /// reads each kind of edit once, not once per object). Actors with no edits are left out.
    /// </summary>
    public IReadOnlyDictionary<ActorRef, ActorEdits> EditsOf(IEnumerable<ActorRef> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        var wanted = new HashSet<ActorRef>(actors, ActorRef.Comparer);
        var edits = new Dictionary<ActorRef, Builder>(ActorRef.Comparer);
        if (wanted.Count == 0)
        {
            return new Dictionary<ActorRef, ActorEdits>(ActorRef.Comparer);
        }

        Builder Of(ActorRef actor) => edits.TryGetValue(actor, out var b) ? b : edits[actor] = new Builder();

        foreach (var actor in wanted)
        {
            if (_bends.TryGetValue(actor, out var bend))
            {
                Of(actor).Bend = bend;
            }

            if (_grass.TryGetValue(actor, out var grass))
            {
                Of(actor).ClearGrass = grass;
            }

            if (_noLoot.Contains(actor))
            {
                Of(actor).NoLoot = true;
            }
        }

        foreach (var ((actor, component), value) in _transforms)
        {
            if (component.Length > 0 && wanted.Contains(actor))
            {
                Of(actor).Parts.Add(new PartEdit(component, value.Base, value.Current));
            }
        }

        foreach (var ((actor, component), value) in _meshes)
        {
            if (wanted.Contains(actor))
            {
                Of(actor).Meshes.Add(new MeshEdit(component.Length == 0 ? null : component, value.Base, value.Current));
            }
        }

        foreach (var ((actor, component), shape) in _segmentSways)
        {
            if (wanted.Contains(actor))
            {
                Of(actor).Sways.Add(new SwayEdit(component, shape));
            }
        }

        foreach (var ((actor, component, array), value) in _spawnPoints)
        {
            if (wanted.Contains(actor))
            {
                Of(actor).Points.Add(new PointsEdit(component.Length == 0 ? null : component, array, value.Base, value.Current));
            }
        }

        foreach (var ((actor, component), value) in _loot)
        {
            if (wanted.Contains(actor))
            {
                Of(actor).Loot.Add(new LootEdit(component.Length == 0 ? null : component, value.Base, value.Current));
            }
        }

        // Instances: the instance dictionaries can hold a whole forest's deletes, so they are read once for all actors.
        foreach (var (instance, add) in _addedInstances)
        {
            if (wanted.Contains(instance.ActorRef))
            {
                Of(instance.ActorRef).Instance(instance, e => e with { Added = add.Transform });
            }
        }

        foreach (var (instance, value) in _instanceTransforms)
        {
            if (wanted.Contains(instance.ActorRef))
            {
                Of(instance.ActorRef).Instance(instance, e => e with { Base = value.Base, Current = value.Current });
            }
        }

        foreach (var instance in _deletedInstances)
        {
            if (wanted.Contains(instance.ActorRef))
            {
                Of(instance.ActorRef).Instance(instance, e => e with { Deleted = true });
            }
        }

        return edits.ToDictionary(e => e.Key, e => e.Value.Build(), ActorRef.Comparer);
    }

    /// <summary>
    /// True when <paramref name="actor"/> is a duplicate whose source's loot of <paramref name="component"/> is set: the
    /// export gives a copy with no loot of its own its source's, so a copy set back to its stock loot keeps that setting
    /// (else it would snap back to its source's: F204).
    /// </summary>
    private bool InheritsLoot(ActorRef actor, string component) =>
        _added.GetValueOrDefault(actor) is DuplicateActorOp duplicate && _loot.ContainsKey((duplicate.Source, component));

    /// <summary>Collects one actor's edits (lists in a fixed order, so the replayed ops do not depend on dictionary order).</summary>
    private sealed class Builder
    {
        private readonly Dictionary<(string Component, int Index), InstanceEdit> _instances = new(InstanceKeyComparer.Instance);

        public BendValue? Bend { get; set; }

        public bool? ClearGrass { get; set; }

        public bool NoLoot { get; set; }

        public List<PartEdit> Parts { get; } = [];

        public List<MeshEdit> Meshes { get; } = [];

        public List<SwayEdit> Sways { get; } = [];

        public List<PointsEdit> Points { get; } = [];

        public List<LootEdit> Loot { get; } = [];

        /// <summary>Changes the edit of one instance (its entry is an immutable record, replaced on each change).</summary>
        public void Instance(InstanceRef instance, Func<InstanceEdit, InstanceEdit> change)
        {
            var key = (instance.Component, instance.Index);
            _instances[key] = change(_instances.TryGetValue(key, out var e) ? e : new InstanceEdit(instance.Component, instance.Index));
        }

        public ActorEdits Build() => new()
        {
            Bend = Bend,
            ClearGrass = ClearGrass,
            NoLoot = NoLoot,
            Parts = Parts.OrderBy(p => p.Component, StringComparer.OrdinalIgnoreCase).ToList(),
            Meshes = Meshes.OrderBy(m => m.Component ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList(),
            Sways = Sways.OrderBy(s => s.Component, StringComparer.OrdinalIgnoreCase).ToList(),
            Points = Points.OrderBy(p => p.Component ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Array, StringComparer.OrdinalIgnoreCase).ToList(),
            Loot = Loot.OrderBy(l => l.Component ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList(),
            Instances = _instances.Values.OrderBy(i => i.Component, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Index).ToList(),
        };
    }

    private sealed class InstanceKeyComparer : IEqualityComparer<(string Component, int Index)>
    {
        public static readonly InstanceKeyComparer Instance = new();

        public bool Equals((string Component, int Index) x, (string Component, int Index) y) =>
            x.Index == y.Index && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component);

        public int GetHashCode((string Component, int Index) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component), obj.Index);
    }
}
