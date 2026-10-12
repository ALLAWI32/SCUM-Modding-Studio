namespace ScumStudio.Level.Model;

/// <summary>What counts as a part of an actor the map draws (and a delete can leave nothing of).</summary>
public static class PartRules
{
    /// <summary>
    /// True for a mesh the map draws as a part of <paramref name="actor"/>: a visible, non-instanced static mesh that is not
    /// an editor helper (<see cref="HelperMeshes.IsHelper"/>). A hidden collision box, a spawn arrow or an editor-only mesh
    /// never shows, so it never keeps an actor whose drawn parts are all deleted.
    /// </summary>
    public static bool IsDrawnPart(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return component.StaticMeshPath is not null && !component.IsInstanced && component.IsVisible && !HelperMeshes.IsHelper(actor, component);
    }

    /// <summary>
    /// Why <paramref name="component"/> cannot be deleted from <paramref name="actor"/> while the actor stays (its root, not
    /// stored in the level, made by the actor's C++ class), or null when it can.
    /// </summary>
    public static string? WhyNotDeletable(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return component.ExportIndex == actor.RootComponent ? "is its root (delete the whole actor instead)"
            : component.IsSynthesized || component.ExportIndex < 0 ? "comes from its Blueprint template and is not stored in the level"
            : component.IsNativeSubobject ? "is made by the actor's own class (the game would miss it)"
            : null;
    }

    /// <summary>
    /// The first stored component of <paramref name="level"/> (of any actor) that hangs, directly or through others, under one
    /// of the <paramref name="gone"/> components (export indices) and that <paramref name="goesToo"/> does not take along: it
    /// would lose its place in the game. Null when none.
    /// </summary>
    public static (ActorRecord Actor, ComponentRecord Component)? KeptUnder(LevelDocument level, IEnumerable<int> gone, Func<ActorRecord, ComponentRecord, bool> goesToo)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(gone);
        ArgumentNullException.ThrowIfNull(goesToo);
        // In level order, so the nearest kept thing of the actor itself is named first.
        var attached = level.Actors.SelectMany(a => a.Components.Where(c => !c.IsSynthesized && c.AttachParent is not null).Select(c => (Actor: a, Component: c))).ToList();
        var seen = gone.ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (a, c) in attached)
            {
                if (!seen.Contains(c.AttachParent!.Value) || seen.Contains(c.ExportIndex))
                {
                    continue;
                }

                if (!goesToo(a, c))
                {
                    return (a, c);
                }

                seen.Add(c.ExportIndex);
                grew = true;
            }
        }

        return null;
    }
}
