using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>
/// How far the game draws each instance of a foliage or HISM component. UE fades the instances out between the component's
/// <c>InstanceStartCullDistance</c> and <c>InstanceEndCullDistance</c>, so grass and bushes thin out with distance instead of
/// ending at one line. The viewport draws whole instances, so each one gets a draw distance of its own in that range: a
/// quarter of them go at a quarter of the way, a quarter at half, and so on (four steps keep the renderer's clusters few,
/// it never mixes draw distances in one).
/// </summary>
public static class FoliageThinning
{
    /// <summary>The distance (cm) beyond which <paramref name="instance"/> is not drawn; 0 = always drawn.</summary>
    public static float CullDistanceOf(ActorInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        float start = instance.StartCullDistance, end = instance.EndCullDistance;
        if (!(start > 0f && start < end))
        {
            return end;
        }

        var step = (Hash(instance.ComponentName, instance.InstanceIndex) & 3) + 1;
        return start + ((end - start) * step * 0.25f);
    }

    // FNV-1a of the component name (case-insensitive, as UE names are) and the index, then murmur's finaliser: the same
    // step in every run and every streaming step (string.GetHashCode changes per run), spread evenly over neighbours.
    private static uint Hash(string component, int index)
    {
        var h = 2166136261u;
        foreach (var c in component)
        {
            h = (h ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        h = (h ^ (uint)index) * 16777619u;
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        return h ^ (h >> 16);
    }
}
