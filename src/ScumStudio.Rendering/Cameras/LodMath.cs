using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Rendering.Cameras;

/// <summary>
/// Pure per-frame decisions of the renderer: projected size of a bounding sphere (UE's <c>ComputeBoundsScreenSize</c>),
/// the LOD it selects (UE's <c>ComputeStaticMeshLOD</c>) and whether an instance cluster is drawn at all.
/// </summary>
public static class LodMath
{
    /// <summary>
    /// Projected diameter of a sphere as a fraction of the view height: <c>radius / (distance * tan(fovY / 2))</c>,
    /// with the distance clamped to at least 1 (UE's convention, so cooked LOD screen sizes apply directly).
    /// </summary>
    public static float ScreenSize(float radius, float distance, float tanHalfFovY) =>
        radius / (MathF.Max(distance, 1f) * MathF.Max(tanHalfFovY, 1e-6f));

    /// <summary>
    /// The LOD to draw for a projected <paramref name="screenSize"/>: walking from the coarsest LOD, the first whose
    /// threshold is above the size (so LOD i is used while <c>sizes[i] &gt; size &gt;= sizes[i + 1]</c>); <paramref name="minLod"/>
    /// when the size is above every threshold. Thresholds are the cooked <c>FStaticMeshRenderData.ScreenSize</c> values.
    /// </summary>
    public static int ChooseLod(ReadOnlySpan<float> lodScreenSizes, float screenSize, int minLod = 0)
    {
        for (var i = lodScreenSizes.Length - 1; i >= 0; i--)
        {
            if (lodScreenSizes[i] > screenSize)
            {
                return Math.Max(i, minLod);
            }
        }

        return minLod;
    }

    /// <summary>Distance from <paramref name="point"/> to the nearest point of <paramref name="box"/> (0 inside).</summary>
    public static float DistanceToBox(Vector3 point, in BoundingBox box) =>
        Vector3.Distance(point, Vector3.Clamp(point, box.Min, box.Max));

    /// <summary>
    /// Decides whether <paramref name="cluster"/> is drawn this frame and with which LOD: returns the LOD index, or -1
    /// when the cluster is outside <paramref name="frustum"/>, beyond its <see cref="InstanceCluster.MaxDrawDistance"/>
    /// (scaled by <paramref name="distanceScale"/>), or when its largest instance projects below
    /// <paramref name="minScreenSize"/>. Sizes are measured at the cluster's nearest point, so a cluster never gets a
    /// coarser LOD than its nearest instance would.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="frustum">View frustum, or null to skip the test.</param>
    /// <param name="eye">Camera position.</param>
    /// <param name="tanHalfFovY">Tangent of half the vertical field of view.</param>
    /// <param name="minScreenSize">Smallest projected size (fraction of the view height) still drawn; 0 draws everything.</param>
    /// <param name="distanceScale">Multiplier on cull distances (<see cref="RenderSettings.ViewDistanceScale"/>).</param>
    /// <param name="lodScreenSizes">LOD thresholds of the mesh (<see cref="ChooseLod"/>); one entry = always LOD 0.</param>
    /// <param name="objectDistance">Cap on the draw distance of clusters that have one (<see cref="RenderSettings.ObjectDrawDistance"/>); 0 = none.</param>
    /// <param name="lodBias">Multiplier on the projected size used to pick the LOD (<see cref="RenderSettings.LodBias"/>).</param>
    /// <param name="previousLod">The LOD the cluster was drawn at in the last frame (-1: not drawn), held near a threshold (<see cref="ChooseLodSteady"/>).</param>
    public static int Classify(in InstanceCluster cluster, Frustum? frustum, Vector3 eye, float tanHalfFovY, float minScreenSize, float distanceScale,
        ReadOnlySpan<float> lodScreenSizes, float objectDistance = 0f, float lodBias = 1f, int previousLod = -1)
    {
        if (frustum is { } f && !f.Intersects(cluster.Bounds))
        {
            return -1;
        }

        var distance = DistanceToBox(eye, cluster.Bounds);
        if (cluster.MaxDrawDistance > 0f
            && (distance > cluster.MaxDrawDistance * distanceScale || (objectDistance > 0f && distance > objectDistance)))
        {
            return -1;
        }

        var size = ScreenSize(cluster.Radius, distance, tanHalfFovY);
        if (minScreenSize > 0f && size < minScreenSize)
        {
            return -1;
        }

        return lodScreenSizes.Length > 1 ? ChooseLodSteady(lodScreenSizes, size * lodBias, previousLod) : 0;
    }

    /// <summary>
    /// How far (a share of the projected size) past a LOD threshold the size must go before a cluster drawn at another LOD
    /// switches (<see cref="ChooseLodSteady"/>).
    /// </summary>
    public const float LodHysteresis = 0.1f;

    /// <summary>
    /// <see cref="ChooseLod"/> that keeps <paramref name="previousLod"/> (the LOD the cluster was drawn at in the last frame,
    /// -1 when it was not drawn) while the size stays within <see cref="LodHysteresis"/> of the threshold between them
    /// (owner: trees "flicker" in motion; a camera hovering, or sliding sideways past a forest, at a threshold swapped a whole
    /// cluster's LOD, or its impostor card, back and forth every few frames). A size well past the threshold switches as before.
    /// </summary>
    public static int ChooseLodSteady(ReadOnlySpan<float> lodScreenSizes, float screenSize, int previousLod)
    {
        var lod = ChooseLod(lodScreenSizes, screenSize);
        if (previousLod < 0 || previousLod == lod || previousLod >= lodScreenSizes.Length)
        {
            return lod;
        }

        var finest = ChooseLod(lodScreenSizes, screenSize * (1f + LodHysteresis));
        var coarsest = ChooseLod(lodScreenSizes, screenSize * (1f - LodHysteresis));
        return previousLod >= finest && previousLod <= coarsest ? previousLod : lod;
    }
}
