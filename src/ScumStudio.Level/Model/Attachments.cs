namespace ScumStudio.Level.Model;

/// <summary>How an actor hangs from another one.</summary>
public enum DependentKind
{
    /// <summary>A child actor the level stores, spawned by the other actor's <c>ChildActorComponent</c> (<see cref="ActorRecord.ParentComponent"/>).</summary>
    ChildActor,

    /// <summary>An actor whose root is attached (<c>AttachParent</c>) to a component of the other actor.</summary>
    Attached,
}

/// <summary>An actor that hangs from a deleted actor (or a deleted component of a kept one).</summary>
/// <param name="Actor">The dependent actor.</param>
/// <param name="Kind">How it hangs.</param>
/// <param name="Parent">The actor it hangs from (deleted, itself a dependent, or the kept owner of a deleted component).</param>
/// <param name="HasDrawnMesh">True when it draws a mesh of its own (<see cref="ActorRoles.HasDrawnMesh"/>): kept, it stands where it is.</param>
public sealed record Dependent(ActorRecord Actor, DependentKind Kind, ActorRecord Parent, bool HasDrawnMesh)
{
    /// <summary>Export index of the component it hangs from (the spawning <c>ChildActorComponent</c> or the root's <c>AttachParent</c>).</summary>
    public int ViaComponent { get; init; }
}

/// <summary>The links between actors of one level: stored child actors and roots attached to another actor's component.</summary>
public static class Attachments
{
    /// <summary>The actor that owns each stored component (by export index); synthesized components are not exports and are left out.</summary>
    public static IReadOnlyDictionary<int, ActorRecord> ComponentOwners(LevelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var owners = new Dictionary<int, ActorRecord>();
        foreach (var actor in document.Actors)
        {
            foreach (var component in actor.Components.Where(c => !c.IsSynthesized && c.ExportIndex >= 0))
            {
                owners.TryAdd(component.ExportIndex, actor);
            }
        }

        return owners;
    }

    /// <summary>
    /// Every actor that hangs, directly or through others, from the <paramref name="deleted"/> actors (names, any case) or
    /// from the <paramref name="deletedComponents"/> (export indices of components of kept actors): stored child actors
    /// whose <c>ParentComponent</c> goes (the exporter's rule) and actors whose root is attached to a component that goes.
    /// Breadth first, in level order; cycles end. The deleted actors themselves are not listed.
    /// </summary>
    public static IReadOnlyList<Dependent> Dependents(LevelDocument document, IReadOnlySet<string> deleted, IReadOnlySet<int>? deletedComponents = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(deleted);
        var owners = ComponentOwners(document);
        var children = new Dictionary<string, List<(ActorRecord Child, DependentKind Kind, int Via)>>(StringComparer.OrdinalIgnoreCase);
        var byComponent = new List<(ActorRecord Child, DependentKind Kind, int Via)>();
        foreach (var actor in document.Actors)
        {
            if (actor.ParentComponent is { } spawner)
            {
                Link(actor, DependentKind.ChildActor, spawner);
            }

            if (actor.Root?.AttachParent is { } parent && actor.Components.All(c => c.ExportIndex != parent))
            {
                Link(actor, DependentKind.Attached, parent);
            }
        }

        var gone = new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase);
        var result = new List<Dependent>();
        var queue = new Queue<string>();
        foreach (var (child, kind, via) in byComponent)
        {
            if (gone.Add(child.Name))
            {
                result.Add(new Dependent(child, kind, owners[via], ActorRoles.HasDrawnMesh(child)) { ViaComponent = via });
                queue.Enqueue(child.Name);
            }
        }

        foreach (var name in deleted.Where(n => document.FindActor(n) is not null).OrderBy(n => document.FindActor(n)!.ExportIndex))
        {
            queue.Enqueue(name);
        }

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!children.TryGetValue(name, out var list) || document.FindActor(name) is not { } parentActor)
            {
                continue;
            }

            foreach (var (child, kind, via) in list)
            {
                if (gone.Add(child.Name))
                {
                    result.Add(new Dependent(child, kind, parentActor, ActorRoles.HasDrawnMesh(child)) { ViaComponent = via });
                    queue.Enqueue(child.Name);
                }
            }
        }

        return result;

        void Link(ActorRecord child, DependentKind kind, int via)
        {
            if (!owners.TryGetValue(via, out var owner) || ReferenceEquals(owner, child))
            {
                return;
            }

            if (deletedComponents?.Contains(via) == true)
            {
                byComponent.Add((child, kind, via));
            }

            (children.TryGetValue(owner.Name, out var list) ? list : children[owner.Name] = []).Add((child, kind, via));
        }
    }
}
