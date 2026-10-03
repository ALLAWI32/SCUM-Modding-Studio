using System.Buffers.Binary;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Export;

/// <summary>
/// The area a level streams in by. SCUM's map is a World Composition: the game loads a level while the player is within
/// its layer's streaming distance of the bounds in its <see cref="WorldTileInfo"/> (e.g. 200 m for the Dr Tudman bridge),
/// and unloads it past that, with everything in it. An export grows those bounds over what it added or moved, or a
/// bridge built on from a level unloads under the player once they walk past the level's old area (owner: "the whole
/// bridge vanished, no collision, no picture, and I fell into the water").
/// </summary>
public static class StreamingArea
{
    /// <summary>
    /// World box of <paramref name="actors"/> as the game draws them: each mesh component's bounds (spline pieces bent,
    /// instances placed); a component whose mesh <paramref name="meshes"/> does not know counts by its place. An actor of
    /// instances (foliage, rock groups) counts by its instances only: its components sit at the world's origin. Null when
    /// there is nothing to measure.
    /// </summary>
    public static (FVector Min, FVector Max)? Of(IEnumerable<ActorRecord> actors, Func<string, BendMesh?> meshes)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(meshes);
        (FVector Min, FVector Max)? box = null;
        void Add(FVector p) => box = box is { } b ? (FVector.Min(b.Min, p), FVector.Max(b.Max, p)) : (p, p);

        foreach (var actor in actors)
        {
            var instanced = actor.Components.Any(c => c.IsInstanced);
            foreach (var c in actor.Components.Where(c => c.IsSceneComponent && (c.IsInstanced || !instanced)))
            {
                var known = c.StaticMeshPath is null ? null : meshes(c.StaticMeshPath)?.Bounds;
                if (c.IsInstanced)
                {
                    foreach (var instance in c.Instances)
                    {
                        foreach (var corner in known is { IsEmpty: false } b ? Corners(b) : [FVector.Zero])
                        {
                            Add(c.WorldTransform.TransformPosition(instance.TransformPosition(corner)));
                        }
                    }

                    continue;
                }

                if (known is not { IsEmpty: false } bounds)
                {
                    Add(c.WorldTransform.Translation);
                    continue;
                }

                if (c.SplineMesh is { } spline)
                {
                    // Along the curve: the mesh's cross-section at 17 places, as the game bends it.
                    var k = spline.ForwardAxis switch { SplineMeshAxis.Y => 1, SplineMeshAxis.Z => 2, _ => 0 };
                    var (from, to) = (Get(bounds.Min, k), Get(bounds.Max, k));
                    for (var i = 0; i <= 16; i++)
                    {
                        var slice = SplineMeshDeformer.CalcSliceTransform(spline, bounds, from + ((to - from) * i / 16f));
                        foreach (var corner in Corners(bounds))
                        {
                            Add(c.WorldTransform.TransformPosition(slice.TransformPosition(Set(corner, k, 0f))));
                        }
                    }
                }
                else
                {
                    foreach (var corner in Corners(bounds))
                    {
                        Add(c.WorldTransform.TransformPosition(corner));
                    }
                }
            }
        }

        return box;
    }

    /// <summary>
    /// Grows the tile bounds stored in the level header <paramref name="headerFile"/> (<c>.umap</c>, next to its
    /// <c>.uexp</c>) to cover <paramref name="min"/>..<paramref name="max"/> (world cm), in place (same size). False when
    /// the level is no World Composition tile or already covers them.
    /// </summary>
    /// <remarks>ponytail: bounds are taken relative to the tile's own <see cref="WorldTileInfo.Position"/> (SCUM's are all 0), not its parents'.</remarks>
    public static bool Grow(string headerFile, FVector min, FVector max)
    {
        var package = CookedPackage.Load(headerFile);
        if (WorldTileInfo.TryRead(package) is not { } tile)
        {
            return false;
        }

        var shift = new FVector(tile.Position.X, tile.Position.Y, tile.Position.Z);
        var low = tile.BoundsValid ? FVector.Min(tile.BoundsMin, min - shift) : min - shift;
        var high = tile.BoundsValid ? FVector.Max(tile.BoundsMax, max - shift) : max - shift;
        if (tile.BoundsValid && low == tile.BoundsMin && high == tile.BoundsMax)
        {
            return false;
        }

        // FWorldTileInfo: Position (3 x int32), then Bounds (Min, Max as 3 x float each, IsValid byte).
        var header = File.ReadAllBytes(headerFile);
        var at = header.AsSpan(package.Summary.WorldTileInfoDataOffset + 12);
        float[] values = [low.X, low.Y, low.Z, high.X, high.Y, high.Z];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(at[(i * 4)..], values[i]);
        }

        at[24] = 1;
        File.WriteAllBytes(headerFile, header);
        return true;
    }

    private static IEnumerable<FVector> Corners(BoundingBox b)
    {
        for (var k = 0; k < 8; k++)
        {
            yield return new FVector((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z);
        }
    }

    private static float Get(System.Numerics.Vector3 v, int k) => k switch { 1 => v.Y, 2 => v.Z, _ => v.X };

    private static FVector Set(FVector v, int k, float value) => k switch { 1 => v with { Y = value }, 2 => v with { Z = value }, _ => v with { X = value } };
}
