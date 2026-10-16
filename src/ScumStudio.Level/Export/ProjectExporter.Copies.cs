using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Export;

/// <summary>
/// Edits of copies (seviscache: a duplicate lost what was done to it; igor8802: a duplicated sentry's route could not be
/// changed). A copy is written from its source's exports under its own name with the components named as the source's, so
/// its part moves, mesh replacements, sways, instance edits and spawn points are planned as a stored actor's, looked up on
/// the source's record.
/// </summary>
public sealed partial class ProjectExporter
{
    /// <summary>
    /// Where the edits of <paramref name="actor"/> are written: its export name in the level, the record its stored components
    /// are looked up in (a copy's source's: the same names, the pristine values) and the operation that added it (null for an
    /// actor of the level). Null when the actor is not in the level.
    /// </summary>
    private static StoredActor? StoredTarget(EditState state, ActorRef actor, LevelDocument? document, Func<string, LevelDocument?>? sourceDocuments) =>
        state.AddedActors.GetValueOrDefault(actor) switch
        {
            null => document?.FindActor(actor.Actor) is { } pristine ? new StoredActor(actor.Actor, pristine, null) : null,
            DuplicateActorOp d => new StoredActor(d.NewName, document?.FindActor(d.Source.Actor), d),
            AddBlueprintActorOp b => new StoredActor(b.NewName, sourceDocuments?.Invoke(b.Source.Level)?.FindActor(b.Source.Actor), b), // ImportActors keeps component names
            var op => new StoredActor(actor.Actor, null, op),
        };

    /// <summary>True when the added actor is created as a bent spline piece (<see cref="PlanBends"/>): one component, no stored parts.</summary>
    private static bool CreatedBent(ActorRef actor, List<StaticMeshActorAdd> meshAdds) =>
        meshAdds.Any(a => a.Spline is not null && string.Equals(a.NewName, actor.Actor, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moved, scaled and deleted parts of the actors this level adds: patched into the copy's own component exports. The root
    /// is placed by the creation (its transform is the added actor's); a bent copy is one new piece with no parts to patch.
    /// </summary>
    private static void PlanCopyParts(EditState state, string level, List<string> warnings, List<StaticMeshActorAdd> meshAdds, List<TransformPatch> transforms)
    {
        foreach (var (actor, component, value) in state.TransformOverrides)
        {
            if (!SameLevel(actor.Level, level) || !state.IsAdded(actor) || state.IsDeleted(actor) || component.Length == 0)
            {
                continue;
            }

            if (CreatedBent(actor, meshAdds))
            {
                warnings.Add($"{actor}.{component}: the copy is exported bent as one new piece; this part's move was not written.");
                continue;
            }

            transforms.Add(new TransformPatch(actor.Actor, component, value));
        }
    }

    /// <summary>An actor as the level stores it (see <see cref="StoredTarget"/>).</summary>
    /// <param name="Name">Its export name.</param>
    /// <param name="Record">The record its stored components are looked up in, or null when unknown.</param>
    /// <param name="Added">The operation that added it; null for an actor of the level.</param>
    private sealed record StoredActor(string Name, ActorRecord? Record, EditOp? Added);
}
