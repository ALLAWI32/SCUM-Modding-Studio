using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Spawns;

/// <summary>
/// One point of a spawner's point array (a sentry's patrol point, a loot point of an item spawner): a copy of the stored
/// element <paramref name="Source"/> (every other field of it kept) placed at <paramref name="Local"/>, relative to the
/// array's owner (the actor's root for <see cref="SpawnPointArrays.PatrolPoints"/>, the component for
/// <see cref="SpawnPointArrays.SpawnerMarkers"/>). A patrol point keeps only the location.
/// </summary>
/// <param name="Source">Index of the stored element the point is a copy of.</param>
/// <param name="Local">Where it is, relative to its owner.</param>
public readonly record struct SpawnPoint(int Source, TransformValue Local);

/// <summary>One editable point array of a spawner actor.</summary>
/// <param name="Component">The component storing the array, or null for the actor's own array.</param>
/// <param name="Array">The array property (<see cref="SpawnPointArrays.PatrolPoints"/> or <see cref="SpawnPointArrays.SpawnerMarkers"/>).</param>
/// <param name="Points">The stored points (each its own source).</param>
public sealed record SpawnPointArray(string? Component, string Array, IReadOnlyList<SpawnPoint> Points)
{
    /// <summary>The name a viewport key carries for this array: the component's name, or the array's for the actor's own.</summary>
    public string Key => Component ?? Array;
}

/// <summary>
/// The point arrays a level stores on its spawners (Discord JimTheCoffeeGuy: "the tool shows the patrol points but does not
/// let you move, add or remove them"): a sentry's <c>PatrolPoints</c> (array of <c>SentryPatrolPoint</c>, one
/// <c>LocationRelativeToSentry</c> vector each) and an item spawner component's <c>SpawnerMarkers</c> (array of
/// <c>ItemSpawnerMarker</c>: a <c>Transform</c> and the preset with its overrides). Only arrays the level package stores
/// can be rewritten; a building's loot points that come from its Blueprint template are not here.
/// </summary>
public static class SpawnPointArrays
{
    /// <summary>A sentry spawner's patrol path (relative to its root).</summary>
    public const string PatrolPoints = "PatrolPoints";

    /// <summary>An item spawner component's loot points (relative to the component).</summary>
    public const string SpawnerMarkers = "SpawnerMarkers";

    /// <summary>Every editable point array of <paramref name="actor"/>.</summary>
    public static IEnumerable<SpawnPointArray> Of(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.PatrolPoints.Count > 0 && actor.PropertyNames.Contains(PatrolPoints, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SpawnPointArray(null, PatrolPoints, actor.PatrolPoints.Select((p, i) => new SpawnPoint(i, TransformValue.At(p.X, p.Y, p.Z))).ToList());
        }

        foreach (var component in actor.Components)
        {
            if (component is { IsSynthesized: false, ExportIndex: >= 0, SpawnMarkers.Count: > 0 } && component.PropertyNames.Contains(SpawnerMarkers, StringComparer.OrdinalIgnoreCase))
            {
                yield return new SpawnPointArray(component.Name, SpawnerMarkers, component.SpawnMarkers.Select((m, i) => new SpawnPoint(i, TransformValue.FromTransform(m.Local))).ToList());
            }
        }
    }

    /// <summary>The array a viewport key names (see <see cref="SpawnPointArray.Key"/>), or null.</summary>
    public static SpawnPointArray? Find(ActorRecord actor, string key) =>
        Of(actor).FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The array stored on <paramref name="component"/> (null = the actor's own), or null.</summary>
    public static SpawnPointArray? FindByComponent(ActorRecord actor, string? component) =>
        Of(actor).FirstOrDefault(a => string.Equals(a.Component, component, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="actor"/> as it is with <paramref name="points"/> in place of the array on <paramref name="component"/>
    /// (null = its patrol points): what a viewport draws the pins from. Unknown sources copy the first stored point.
    /// </summary>
    public static ActorRecord WithPoints(ActorRecord actor, string? component, IReadOnlyList<SpawnPoint> points)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(points);
        if (component is null)
        {
            return actor with { PatrolPoints = points.Select(p => p.Local.Location).ToList() };
        }

        return actor with
        {
            Components = actor.Components.Select(c => !string.Equals(c.Name, component, StringComparison.OrdinalIgnoreCase) || c.SpawnMarkers.Count == 0 ? c
                : c with { SpawnMarkers = points.Select(p => c.SpawnMarkers[Math.Clamp(p.Source, 0, c.SpawnMarkers.Count - 1)] with { Local = p.Local.ToTransform() }).ToList() }).ToList(),
        };
    }
}
