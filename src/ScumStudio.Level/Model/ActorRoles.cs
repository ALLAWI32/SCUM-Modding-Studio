using ScumStudio.Level.Spawns;

namespace ScumStudio.Level.Model;

/// <summary>
/// What an actor or component is to its level, by class: part of the level itself, a world-wide effect, an effect at its
/// spot (a fire, a lamp, a sound), or something drawn. The map's pins and the delete companions share these rules.
/// </summary>
public static class ActorRoles
{
    /// <summary>Actors that stand for the level itself, not for a thing in it: never a pin, never taken along.</summary>
    public static IReadOnlySet<string> StructuralClasses { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "WorldSettings", "ConZWorldSettings", "LevelBounds", "LevelScriptActor", "InstancedFoliageActor", "Brush", "DefaultPhysicsVolume",
        "NavMeshBoundsVolume", "RecastNavMesh", "DistantLevelManager", "WorldComposition", "SphereReflectionCapture", "BoxReflectionCapture",
        "PlanarReflection", "LightmassImportanceVolume", "PrecomputedVisibilityVolume", "CullDistanceVolume", "WorldPartitionMiniMap",
    };

    /// <summary>True for a class in <see cref="StructuralClasses"/>.</summary>
    public static bool IsStructural(string className) => StructuralClasses.Contains(className);

    /// <summary>
    /// True for a light, particle, Niagara, audio, fog, emitter, fire or smoke class, and for Wwise sound classes
    /// (<c>AkAmbientSound</c>, <c>AkComponent</c>, anything with <c>Sound</c>).
    /// </summary>
    public static bool IsEffectClass(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        return className.Contains("Light", StringComparison.Ordinal) || className.Contains("Particle", StringComparison.Ordinal)
            || className.Contains("Niagara", StringComparison.Ordinal) || className.Contains("Audio", StringComparison.Ordinal)
            || className.Contains("Fog", StringComparison.Ordinal) || className.Contains("Emitter", StringComparison.Ordinal)
            || className.Contains("Fire", StringComparison.Ordinal) || className.Contains("Smoke", StringComparison.Ordinal)
            || className.StartsWith("Ak", StringComparison.Ordinal) || className.Contains("Sound", StringComparison.Ordinal);
    }

    /// <summary>True for an effect that lights or tints the whole world (the sun, the sky, the height fog, post process): never one object's.</summary>
    public static bool IsGlobalEffect(string className) =>
        className is "DirectionalLight" or "SkyLight" or "SkyAtmosphere" or "ExponentialHeightFog" or "AtmosphericFog" or "PostProcessVolume";

    /// <summary>
    /// True for a component that burns, shines, smokes or plays at its place: lights (not the sun or sky), particle and
    /// Niagara systems, audio and Wwise components, decals, and SCUM's fire, smoke and heat classes.
    /// </summary>
    public static bool IsEffectComponent(ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var c = component.ClassName;
        if (c is "DirectionalLightComponent" or "SkyLightComponent")
        {
            return false;
        }

        return c.Contains("LightComponent", StringComparison.Ordinal) || c.StartsWith("ParticleSystem", StringComparison.Ordinal)
            || c.StartsWith("Niagara", StringComparison.Ordinal) || c.StartsWith("Audio", StringComparison.Ordinal) || c.StartsWith("Ak", StringComparison.Ordinal)
            || c.StartsWith("Decal", StringComparison.Ordinal) || c.Contains("Fire", StringComparison.Ordinal)
            || c.Contains("Smoke", StringComparison.Ordinal) || c.Contains("Heat", StringComparison.Ordinal);
    }

    /// <summary>True for a mesh component of <paramref name="actor"/> the game draws: visible, not instanced, not a helper (<see cref="HelperMeshes"/>).</summary>
    public static bool IsDrawnMesh(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return component is { StaticMeshPath: not null, IsVisible: true, IsInstanced: false } && !HelperMeshes.IsHelper(actor, component);
    }

    /// <summary>True when <paramref name="actor"/> has a mesh component the game draws (<see cref="IsDrawnMesh"/>).</summary>
    public static bool HasDrawnMesh(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Components.Any(c => IsDrawnMesh(actor, c));
    }

    /// <summary>
    /// True for an actor a delete never takes along on its own: the level's (its script actor, structural and world-wide
    /// classes, volumes, the landscape) and what players use (trade posts, spawners, sentry spawners). Attached to a deleted
    /// object, it stays where it stands.
    /// </summary>
    public static bool IsNeverTakenAlong(LevelDocument document, ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(actor);
        var c = actor.ClassName;
        return actor.ExportIndex == document.Data.LevelScriptActorIndex || IsStructural(c) || IsGlobalEffect(c) || actor.Kind == ActorKind.Volume
            || c.StartsWith("Landscape", StringComparison.Ordinal) || actor.TraderMarkers.Count > 0 || c.Contains("Spawn", StringComparison.Ordinal)
            || SpawnPlaces.KindOfClass(c) is not null || actor.Components.Any(x => x.SpawnMarkers.Count > 0);
    }

    /// <summary>True for a decal actor (or an actor whose decal component is its point).</summary>
    public static bool IsDecal(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.ClassName == "DecalActor" || actor.Root?.ClassName == "DecalComponent";
    }
}
