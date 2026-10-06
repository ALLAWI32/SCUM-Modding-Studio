using System.Globalization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Editing;

/// <summary>
/// Creates edit operations from the read-side model: fills in "old" values, resolves bulk targets and picks unique names,
/// so the journal only ever contains complete, deterministic operations.
/// </summary>
public static class EditOpFactory
{
    /// <summary>
    /// Resolves "delete all of the same kind" over <paramref name="levels"/>: every actor whose class (or root / StaticMeshActor
    /// mesh) matches, and with <paramref name="includeInstances"/> every ISM/HISM instance drawing the mesh. Targets that are
    /// already deleted in <paramref name="state"/> are skipped.
    /// </summary>
    public static DeleteAllOfKindOp DeleteAllOfKind(
        IEnumerable<LevelDocument> levels, KindMatch match, EditScope scope, EditState? state = null, bool includeInstances = true)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(scope);
        var actors = new List<ActorRef>();
        var instances = new List<InstanceRef>();
        foreach (var level in levels)
        {
            foreach (var actor in level.Actors)
            {
                var actorRef = new ActorRef(level.PackagePath, actor.Name);
                if (state?.IsDeleted(actorRef) == true)
                {
                    continue;
                }

                if (Matches(actor, match))
                {
                    actors.Add(actorRef);
                    continue;
                }

                if (includeInstances && match.By == MatchBy.StaticMesh)
                {
                    foreach (var instance in actor.InstanceTransforms)
                    {
                        var instanceRef = new InstanceRef(level.PackagePath, actor.Name, instance.ComponentName, instance.InstanceIndex);
                        if (instance.StaticMeshPath is { } mesh && SameObject(mesh, match.Path) && state?.IsDeleted(instanceRef) != true)
                        {
                            instances.Add(instanceRef);
                        }
                    }
                }
            }
        }

        return new DeleteAllOfKindOp(match, scope, actors, instances);
    }

    /// <summary>
    /// True when <paramref name="actor"/> is "of the kind": for <see cref="MatchBy.Class"/> its class path (or short class
    /// name when <see cref="KindMatch.Path"/> has no '/') equals the match; for <see cref="MatchBy.StaticMesh"/> its
    /// <see cref="ActorRecord.StaticMeshPath"/> refers to the same mesh (for a Blueprint: its root component's mesh). ISM
    /// instances are not considered here.
    /// </summary>
    public static bool Matches(ActorRecord actor, KindMatch match)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(match);
        return match.By switch
        {
            MatchBy.Class => match.Path.Contains('/')
                ? string.Equals(actor.ClassPath, match.Path, StringComparison.OrdinalIgnoreCase)
                  || (!match.Path.Contains('.') && SameObject(actor.ClassPath, match.Path + "." + ShortName(match.Path) + "_C"))
                : string.Equals(actor.ClassName, match.Path, StringComparison.OrdinalIgnoreCase),
            // A Blueprint's StaticMeshPath may be a representative (non-root) mesh; "of the kind" keeps meaning its root's mesh.
            _ => (actor.Kind == ActorKind.Blueprint ? actor.Root?.StaticMeshPath : actor.StaticMeshPath) is { } mesh && SameObject(mesh, match.Path),
        };
    }

    /// <summary>
    /// A <see cref="SetTransformOp"/> moving an actor's root (or a named component) to <paramref name="newValue"/>; the old
    /// value is the current override in <paramref name="state"/>, else the added actor's transform, else the pristine one.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The component does not exist, is not a scene component, or is synthesized from a Blueprint template
    /// (<see cref="ComponentRecord.IsSynthesized"/>).
    /// </exception>
    public static SetTransformOp SetTransform(LevelDocument level, ActorRecord actor, TransformValue newValue, EditState? state = null, string? component = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(actor);
        var actorRef = new ActorRef(level.PackagePath, actor.Name);
        var target = component is null ? actor.Root : actor.FindComponent(component);
        if (target is not { IsSceneComponent: true })
        {
            throw new ArgumentException(component is null
                ? $"{actorRef} has no root scene component."
                : $"{actorRef} has no scene component '{component}'.", nameof(component));
        }

        if (target.IsSynthesized)
        {
            throw new ArgumentException(
                $"{actorRef}: component '{target.Name}' comes from its Blueprint template and is not stored in the level; it cannot be edited.",
                nameof(component));
        }

        var old = state?.GetTransformOverride(actorRef, component) ?? target.Relative;
        return new SetTransformOp(actorRef, old, newValue, component);
    }

    /// <summary>
    /// A <see cref="SetSpawnPointsOp"/> giving the point array on <paramref name="component"/> of <paramref name="actor"/>
    /// (null = its patrol points) the points <paramref name="points"/>; the old list is the current override in
    /// <paramref name="state"/>, else the stored one.
    /// </summary>
    /// <exception cref="ArgumentException">The actor stores no such array.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A point copies a stored point that does not exist.</exception>
    public static SetSpawnPointsOp SetSpawnPoints(LevelDocument level, ActorRecord actor, string? component, IReadOnlyList<Spawns.SpawnPoint> points, EditState? state = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(points);
        var actorRef = new ActorRef(level.PackagePath, actor.Name);
        var array = Spawns.SpawnPointArrays.FindByComponent(actor, component)
                    ?? throw new ArgumentException($"{actorRef} stores no spawn points{(component is null ? string.Empty : $" on '{component}'")}.", nameof(component));
        foreach (var point in points)
        {
            if (point.Source < 0 || point.Source >= array.Points.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(points), $"Point source {point.Source} is outside the {array.Points.Count} stored point(s) of {actorRef}.");
            }
        }

        var old = state?.GetSpawnPoints(actorRef, component, array.Array) ?? array.Points;
        return new SetSpawnPointsOp(actorRef, component, array.Array, old, points);
    }

    /// <summary>A <see cref="SetTransformOp"/> for an actor added by an edit (its old value comes from <paramref name="state"/>).</summary>
    /// <exception cref="ArgumentException">The actor was not added.</exception>
    public static SetTransformOp SetAddedActorTransform(ActorRef added, TransformValue newValue, EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var old = state.GetAddedTransform(added) ?? throw new ArgumentException($"{added} was not added by an edit.", nameof(added));
        return new SetTransformOp(added, old, newValue);
    }

    /// <summary>A <see cref="SetInstanceTransformOp"/> for instance <paramref name="index"/> of <paramref name="component"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No such instance.</exception>
    /// <exception cref="ArgumentException">The component is synthesized from a Blueprint template.</exception>
    public static SetInstanceTransformOp SetInstanceTransform(
        LevelDocument level, ActorRecord actor, string component, int index, TransformValue newValue, EditState? state = null)
    {
        var target = FindInstance(level, actor, component, index, state);
        var old = state?.GetInstanceOverride(target) ?? state?.GetAddedInstanceTransform(target)
                  ?? TransformValue.FromTransform(actor.FindComponent(component)!.Instances[index]);
        return new SetInstanceTransformOp(target, old, newValue);
    }

    /// <summary>A <see cref="DeleteInstanceOp"/> for instance <paramref name="index"/> of <paramref name="component"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No such instance.</exception>
    /// <exception cref="ArgumentException">The component is synthesized from a Blueprint template.</exception>
    public static DeleteInstanceOp DeleteInstance(LevelDocument level, ActorRecord actor, string component, int index, EditState? state = null) =>
        new(FindInstance(level, actor, component, index, state));

    /// <summary>
    /// An <see cref="AddInstanceOp"/> adding an instance at <paramref name="local"/> (component space) to the stored
    /// ISM/HISM/foliage component <paramref name="component"/>, under the first index after its stored and already added ones.
    /// </summary>
    /// <exception cref="ArgumentException">The component is not an instanced mesh component stored in the level.</exception>
    public static AddInstanceOp AddInstance(LevelDocument level, ActorRecord actor, string component, TransformValue local, EditState? state = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.FindComponent(component) is not { IsInstanced: true, IsSynthesized: false } c)
        {
            throw new ArgumentException($"{actor.Name}.{component} is not an instanced mesh component stored in the level; no instance can be added to it.", nameof(component));
        }

        var index = c.Instances.Count;
        while (state?.IsAdded(new InstanceRef(level.PackagePath, actor.Name, c.Name, index)) == true)
        {
            index++;
        }

        return new AddInstanceOp(new InstanceRef(level.PackagePath, actor.Name, c.Name, index), local);
    }

    /// <summary>
    /// A <see cref="DuplicateActorOp"/> copying <paramref name="actor"/> within its level, named
    /// <c>&lt;Name&gt;_Copy</c>, <c>&lt;Name&gt;_Copy2</c>, ... (unique among pristine and added actors), placed at
    /// <paramref name="transform"/> (default: the source's root transform).
    /// </summary>
    public static DuplicateActorOp Duplicate(LevelDocument level, ActorRecord actor, TransformValue? transform = null, EditState? state = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(actor);
        var source = new ActorRef(level.PackagePath, actor.Name);
        var value = transform ?? state?.GetTransformOverride(source)
                    ?? actor.Root?.Relative ?? TransformValue.Identity;
        return new DuplicateActorOp(source, UniqueActorName(level, actor.Name + "_Copy", state), value);
    }

    /// <summary>An <see cref="AddStaticMeshActorOp"/> with a unique name derived from the mesh (<c>SM_Rock</c> gives <c>SM_Rock_Added</c>, ...).</summary>
    public static AddStaticMeshActorOp AddStaticMeshActor(LevelDocument level, string staticMesh, TransformValue transform, EditState? state = null, ISet<ActorRef>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(staticMesh);
        return new AddStaticMeshActorOp(level.PackagePath, UniqueActorName(level, ShortName(staticMesh) + "_Added", state, reserved), staticMesh, transform);
    }

    /// <summary>
    /// An <see cref="AddBlueprintActorOp"/> transplanting <paramref name="source"/> (an existing instance of a Blueprint class,
    /// from any level) into <paramref name="level"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The source actor is not a Blueprint instance.</exception>
    public static AddBlueprintActorOp AddBlueprintActor(LevelDocument level, LevelDocument sourceLevel, ActorRecord source, TransformValue transform, EditState? state = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(sourceLevel);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != ActorKind.Blueprint)
        {
            throw new ArgumentException($"{source.Name} is a {source.ClassName}, not a Blueprint instance.", nameof(source));
        }

        var baseName = source.ClassName.EndsWith("_C", StringComparison.Ordinal) ? source.ClassName[..^2] : source.ClassName;
        return new AddBlueprintActorOp(
            level.PackagePath,
            UniqueActorName(level, baseName + "_Added", state),
            source.ClassPath,
            new ActorRef(sourceLevel.PackagePath, source.Name),
            transform);
    }

    /// <summary>
    /// <paramref name="baseName"/>, or <paramref name="baseName"/> followed by 2, 3, ... when taken by a pristine actor of
    /// <paramref name="level"/> or an actor added in <paramref name="state"/>. With <paramref name="reserved"/> (the names
    /// already given to other new actors of one batch) those count as taken too, and the result is added to it.
    /// </summary>
    public static string UniqueActorName(LevelDocument level, string baseName, EditState? state = null, ISet<ActorRef>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        bool Taken(string name) =>
            level.FindActor(name) is not null || state?.IsAdded(new ActorRef(level.PackagePath, name)) == true
            || reserved?.Contains(new ActorRef(level.PackagePath, name)) == true;

        var result = baseName;
        for (var i = 2; Taken(result); i++)
        {
            result = string.Create(CultureInfo.InvariantCulture, $"{baseName}{i}");
        }

        reserved?.Add(new ActorRef(level.PackagePath, result));
        return result;
    }

    /// <summary>True when two object/package paths name the same object (<c>/Game/X/SM_A</c> equals <c>/Game/X/SM_A.SM_A</c>).</summary>
    public static bool SameObject(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (pa, na) = AssetPaths.SplitObjectPath(a);
        var (pb, nb) = AssetPaths.SplitObjectPath(b);
        return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase) && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static InstanceRef FindInstance(LevelDocument level, ActorRecord actor, string component, int index, EditState? state)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(actor);
        var c = actor.FindComponent(component);
        // An instance the project added (a copied tree) sits past the stored ones.
        if (c is not { IsInstanced: true } || index < 0 || (index >= c.Instances.Count && state?.IsAdded(new InstanceRef(level.PackagePath, actor.Name, c.Name, index)) != true))
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"{actor.Name}.{component} has no instance {index}.");
        }

        if (c.IsSynthesized)
        {
            throw new ArgumentException(
                $"{actor.Name}.{component} comes from its Blueprint template and is not stored in the level; its instances cannot be edited.",
                nameof(component));
        }

        return new InstanceRef(level.PackagePath, actor.Name, c.Name, index);
    }

    private static string ShortName(string path)
    {
        var (package, name) = AssetPaths.SplitObjectPath(path);
        return string.IsNullOrEmpty(name) ? package[(package.LastIndexOf('/') + 1)..] : name;
    }
}
