namespace ScumStudio.Level.Editing;

public sealed partial class EditState
{
    /// <summary>
    /// A copy of this state in which the placed meshes of <paramref name="actors"/> (each an actor an
    /// <see cref="AddStaticMeshActorOp"/> created) belong to another level under another name, every edit made to them since
    /// (moved, bent, repainted, its loot, its grass setting) going with them, and in which the added instances of
    /// <paramref name="instances"/> are placed meshes of their own instead (the given operation, its transform the
    /// instance's place now). Used by the export only (<see cref="Export.FarPlacement"/>); the journal is not touched.
    /// </summary>
    /// <param name="actors">Added mesh actor to where it goes (level and name).</param>
    /// <param name="instances">Added instance to the placed mesh that stands for it.</param>
    /// <exception cref="ArgumentException">An actor that no <see cref="AddStaticMeshActorOp"/> added, an instance not added, or a target already taken.</exception>
    public EditState WithPlacedMeshesMoved(IReadOnlyDictionary<ActorRef, ActorRef> actors, IReadOnlyDictionary<InstanceRef, AddStaticMeshActorOp> instances)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(instances);
        var copy = Clone();
        foreach (var (from, to) in actors)
        {
            if (!copy._added.TryGetValue(from, out var op) || op is not AddStaticMeshActorOp add)
            {
                throw new ArgumentException($"{from} was not placed as a mesh by an edit.", nameof(actors));
            }

            if (copy._added.ContainsKey(to))
            {
                throw new ArgumentException($"{to} already exists.", nameof(actors));
            }

            copy._added.Remove(from);
            copy._added[to] = add with { Level = to.Level, NewName = to.Actor };
            if (copy._deletedActors.Remove(from))
            {
                copy._deletedActors.Add(to);
            }

            Rekey(copy._transforms, k => Same(k.Actor, from), k => (to, k.Component));
            Rekey(copy._bends, k => Same(k, from), _ => to);
            Rekey(copy._segmentSways, k => Same(k.Actor, from), k => (to, k.Component));
            Rekey(copy._spawnPoints, k => Same(k.Actor, from), k => (to, k.Component, k.Array));
            Rekey(copy._meshes, k => Same(k.Actor, from), k => (to, k.Component));
            Rekey(copy._loot, k => Same(k.Actor, from), k => (to, k.Component));
            Rekey(copy._grass, k => Same(k, from), _ => to);
            Rekey(copy._choices, k => Same(k.Actor, from), k => (to, k.Property));
            foreach (var part in copy._deletedComponents.Where(k => Same(k.Actor, from)).ToList())
            {
                copy._deletedComponents.Remove(part);
                copy._deletedComponents.Add((to, part.Component));
            }
        }

        foreach (var (instance, add) in instances)
        {
            if (!copy._addedInstances.Remove(instance))
            {
                throw new ArgumentException($"{instance} was not added by an edit.", nameof(instances));
            }

            if (copy._added.ContainsKey(add.Created))
            {
                throw new ArgumentException($"{add.Created} already exists.", nameof(instances));
            }

            copy._instanceTransforms.Remove(instance);
            copy._deletedInstances.Remove(instance);
            copy._added[add.Created] = add;
        }

        return copy;

        static bool Same(ActorRef a, ActorRef b) => ActorRef.Comparer.Equals(a, b);

        static void Rekey<TKey, TValue>(Dictionary<TKey, TValue> entries, Func<TKey, bool> match, Func<TKey, TKey> to)
            where TKey : notnull
        {
            foreach (var key in entries.Keys.Where(match).ToList())
            {
                var value = entries[key];
                entries.Remove(key);
                entries[to(key)] = value;
            }
        }
    }
}
