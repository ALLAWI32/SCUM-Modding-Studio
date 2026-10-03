using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>
/// Gives every spline mesh placement (roads, river banks, fences along landscape splines) its own copy of the mesh bent
/// with <see cref="SplineMeshDeformer"/>, so the uploader draws it like any other mesh and the road reads as one
/// continuous ribbon instead of straight slices.
/// </summary>
public static class SplineMeshPlacements
{
    /// <summary>Marker in the mesh key of a bent copy (<c>&lt;mesh path&gt;#spline&lt;n&gt;</c>).</summary>
    public const string KeyMarker = "#spline";

    /// <summary>
    /// Rewrites, in place, every placement whose component carries <see cref="ComponentRecord.SplineMesh"/> and whose mesh
    /// is loaded to point at a bent copy added to <paramref name="meshes"/> (one copy per distinct mesh path and
    /// parameters, texture shared with the original). Returns the number of copies made.
    /// </summary>
    public static int Apply(List<ScenePlacement> placements, Dictionary<string, PreparedMeshAsset> meshes, LevelPrepareCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(meshes);
        // ponytail: one mesh per spline piece (a cell has ~2k); move the bend into the vertex shader with per-instance
        // spline params if whole-island loads make the draw count hurt.
        var copies = new Dictionary<(string Path, SplineMeshParams Spline), string>();
        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            if (placement.Component?.SplineMesh is not { } spline || !meshes.TryGetValue(placement.MeshPath, out var asset))
            {
                continue;
            }

            var cacheKey = (placement.MeshPath.ToLowerInvariant(), spline);
            if (!copies.TryGetValue(cacheKey, out var key))
            {
                if (cache is not null && cache.Splines.TryGetValue(cacheKey, out var kept))
                {
                    // Bent by an earlier preparation: same key, so the GPU keeps its upload too.
                    key = kept.Key;
                    meshes[key] = kept.Asset;
                    cache.Splines[cacheKey] = kept with { Used = cache.Generation };
                }
                else
                {
                    // Keys from the cache's counter stay unique across preparations (a key always means one bend).
                    key = $"{placement.MeshPath}{KeyMarker}{(cache is null ? copies.Count : cache.NextSplineId++)}";
                    // Every LOD is bent over LOD 0's length, or distant road pieces would fall back to straight slices.
                    var lods = asset.Lods.Select(lod => SplineMeshDeformer.Deform(lod, spline, asset.Mesh.Bounds)).ToList();
                    meshes[key] = asset with { MeshPath = key, Mesh = lods[0], Lods = lods };
                    if (cache is not null)
                    {
                        cache.Splines[cacheKey] = (key, meshes[key], cache.Generation);
                    }
                }

                copies[cacheKey] = key;
            }

            placements[i] = placement with { MeshPath = key };
        }

        return copies.Count;
    }
}
