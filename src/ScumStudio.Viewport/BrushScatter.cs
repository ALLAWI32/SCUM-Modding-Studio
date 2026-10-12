using System.Numerics;
using ScumStudio.Level.Export;

namespace ScumStudio.Viewport;

/// <summary>Why the paint brush plants at a spot or leaves it out.</summary>
public enum PaintVerdict
{
    /// <summary>Free ground: planted.</summary>
    Plant,

    /// <summary>Closer than the spacing to a plant or an object.</summary>
    TooClose,

    /// <summary>On a road, track, path, runway or yard.</summary>
    Road,

    /// <summary>Under the sea, in a lake or river, on a beach or the seabed.</summary>
    Water,

    /// <summary>On a cliff or a rock.</summary>
    Rock,

    /// <summary>On top of an object (a roof, a floor, a bridge) while only bare ground is wanted.</summary>
    OnObject,

    /// <summary>No ground loaded under it.</summary>
    NoGround,
}

/// <summary>What a plant or a standing thing is to the spacing: a tree keeps clear of trees and objects, not of undergrowth.</summary>
public enum PaintClass
{
    /// <summary>A tree (or any other plant that is not undergrowth).</summary>
    Tree,

    /// <summary>A bush or tall grass.</summary>
    Bush,

    /// <summary>A rock, a prop, a building, a Blueprint.</summary>
    Other,
}

/// <summary>One candidate place of the brush's lattice.</summary>
/// <param name="X">Lattice cell column.</param>
/// <param name="Y">Lattice cell row.</param>
/// <param name="At">World X/Y (UE cm).</param>
/// <param name="Hash">The spot's own random bits (its order in a sweep, its kind, turn and size).</param>
/// <param name="Keep">The distance it keeps from its neighbours (cm), between the spacing's two ends.</param>
public readonly record struct PaintSpot(int X, int Y, Vector2 At, ulong Hash, float Keep);

/// <summary>
/// Where the paint brush plants (owner, 3.1.0: "it plants at random, a huge number of trees very fast, in some places and
/// not in others, sometimes on half the road"). Candidates come from a world-anchored lattice, one jittered spot per cell
/// fixed by the stroke's seed: a sweep tries them in their hash order (random sequential addition, an even blue-noise
/// spread), the timer nearest the cursor first. A spot is planted only on bare ground (the landscape's paint layers, the
/// sea level and the meshes under it decide) and never closer than its distance to anything measured at its foot. Pure
/// math, UE space.
/// </summary>
public static class BrushScatter
{
    /// <summary>The landscape layer painted under roads and yards so the game grows nothing there (not weight-blended).</summary>
    public const string EraseFoliage = "EraseFoliage";

    /// <summary>Share of a ground's layers (summed) from which a road, water or rock set decides.</summary>
    public const float LayerShare = 0.4f;

    /// <summary>Weight of <see cref="EraseFoliage"/> from which a spot is a road (it is painted on its own, 0 or 1 mostly).</summary>
    public const float EraseFoliageShare = 0.5f;

    /// <summary>How far above a mesh's bottom the cursor rests on the mesh rather than on the ground beside it (cm).</summary>
    public const float OnMesh = 100f;

    // ponytail: the layers of the game's roads, tracks, beds and cliffs (terrain-layers.json); extend when the owner finds one.
    private static readonly HashSet<string> RoadLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Asphalt", "Asphalt_Rubble", "Gravel", "Gravel_02", "Gravel_03_Grass", "White_Gravel", "Crushed_Rock_01", "Mud_Tracks", "Brick_Debris_01", "Scrap_01",
    };

    private static readonly HashSet<string> WaterLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Seabed", "Underwater_Sand", "River_Underwater", "River_Underwater_Moss", "River_Pebbles", "River_Sand", "River_Beach_Gravel", "River_Reeds",
        "River_Weeds", "Beach_Sand", "Beach_Gravel",
    };

    private static readonly HashSet<string> RockLayers = new(StringComparer.OrdinalIgnoreCase) { "Cliff", "Coastal_Rock_01", "Coastal_Rock_02", "Coastal_Boulders" };

    /// <summary>The lattice cell (cm): half the least spacing, coarser for a wide circle (at most ~150 cells across it).</summary>
    public static float CellSize(float least, float radius) => MathF.Max(least / 2f, 2f * radius / 150f);

    /// <summary>The candidate of lattice cell (<paramref name="x"/>, <paramref name="y"/>) for <paramref name="seed"/>.</summary>
    public static PaintSpot SpotAt(ulong seed, float cell, int x, int y, float least, float most)
    {
        var h = Mix(seed ^ Mix(((ulong)(uint)x << 32) | (uint)y));
        return new PaintSpot(x, y, new Vector2((x + Unit(h, 0)) * cell, (y + Unit(h, 1)) * cell), h, least + ((most - least) * Unit(h, 7)));
    }

    /// <summary>
    /// The candidates under the circle of <paramref name="radius"/> swept from <paramref name="a"/> to <paramref name="b"/>
    /// whose cells are not in <paramref name="tried"/>, in hash order.
    /// </summary>
    public static List<PaintSpot> Sweep(ulong seed, float cell, Vector2 a, Vector2 b, float radius, float least, float most, ICollection<(int X, int Y)>? tried = null)
    {
        var (min, max) = (Vector2.Min(a, b) - new Vector2(radius), Vector2.Max(a, b) + new Vector2(radius));
        var ab = b - a;
        var length = ab.LengthSquared();
        var spots = new List<PaintSpot>();
        for (var y = (int)MathF.Floor(min.Y / cell); y <= (int)MathF.Floor(max.Y / cell); y++)
        {
            for (var x = (int)MathF.Floor(min.X / cell); x <= (int)MathF.Floor(max.X / cell); x++)
            {
                if (tried?.Contains((x, y)) == true)
                {
                    continue;
                }

                var spot = SpotAt(seed, cell, x, y, least, most);
                var t = length < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(spot.At - a, ab) / length, 0f, 1f);
                if (Vector2.DistanceSquared(spot.At, a + (ab * t)) <= radius * radius)
                {
                    spots.Add(spot);
                }
            }
        }

        spots.Sort((p, q) => p.Hash.CompareTo(q.Hash));
        return spots;
    }

    /// <summary>The candidates of the circle at <paramref name="centre"/> not in <paramref name="tried"/>, nearest first (the timer's order).</summary>
    public static List<PaintSpot> Nearest(ulong seed, float cell, Vector2 centre, float radius, float least, float most, ICollection<(int X, int Y)>? tried = null)
    {
        var spots = Sweep(seed, cell, centre, centre, radius, least, most, tried);
        spots.Sort((p, q) => Vector2.DistanceSquared(p.At, centre).CompareTo(Vector2.DistanceSquared(q.At, centre)));
        return spots;
    }

    /// <summary>
    /// The timer's next spot: the first of <paramref name="nearest"/> that <paramref name="plants"/> takes (it plants it).
    /// Every spot looked at goes into <paramref name="tried"/>, as nothing frees a refused spot during a stroke. Null when
    /// the circle is full.
    /// </summary>
    public static PaintSpot? Next(IEnumerable<PaintSpot> nearest, ICollection<(int X, int Y)> tried, Func<PaintSpot, bool> plants)
    {
        ArgumentNullException.ThrowIfNull(nearest);
        ArgumentNullException.ThrowIfNull(tried);
        ArgumentNullException.ThrowIfNull(plants);
        foreach (var spot in nearest)
        {
            tried.Add((spot.X, spot.Y));
            if (plants(spot))
            {
                return spot;
            }
        }

        return null;
    }

    /// <summary>
    /// What the landscape at a spot says: water below <paramref name="seaZ"/>, a road under <see cref="EraseFoliage"/> (half
    /// or more), else the road, water or rock set whose layers together reach <see cref="LayerShare"/> (the strongest
    /// wins). No layer data: <see cref="PaintVerdict.Plant"/>.
    /// </summary>
    public static PaintVerdict GroundVerdict(IReadOnlyList<TerrainLayerSample> layers, float groundZ, float? seaZ)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (groundZ < seaZ)
        {
            return PaintVerdict.Water;
        }

        var (road, water, rock) = (0f, 0f, 0f);
        foreach (var layer in layers)
        {
            if (!layer.WeightBlended)
            {
                if (string.Equals(layer.Name, EraseFoliage, StringComparison.OrdinalIgnoreCase) && layer.Weight >= EraseFoliageShare)
                {
                    return PaintVerdict.Road;
                }
            }
            else if (RoadLayers.Contains(layer.Name))
            {
                road += layer.Weight;
            }
            else if (WaterLayers.Contains(layer.Name))
            {
                water += layer.Weight;
            }
            else if (RockLayers.Contains(layer.Name))
            {
                rock += layer.Weight;
            }
        }

        var strongest = MathF.Max(road, MathF.Max(water, rock));
        return strongest < LayerShare ? PaintVerdict.Plant
            : strongest == road ? PaintVerdict.Road
            : strongest == water ? PaintVerdict.Water
            : PaintVerdict.Rock;
    }

    /// <summary>
    /// What a drawn mesh under a spot is (by its mesh key): water, a road piece (every landscape spline piece: roads,
    /// borders, rails, banks; runways and concrete yards), a rock, or any other object. Null for foliage, which the
    /// spacing handles.
    /// </summary>
    public static PaintVerdict? SurfaceVerdict(string meshKey)
    {
        ArgumentNullException.ThrowIfNull(meshKey);
        if (Has(meshKey, "/Foliage/"))
        {
            return null;
        }

        if (Has(meshKey, "/Lake/") || Has(meshKey, "WaterSurface") || Has(meshKey, "/Rivers/") || Has(meshKey, "/Water/"))
        {
            return PaintVerdict.Water;
        }

        if (Has(meshKey, SplineMeshPlacements.KeyMarker) || Has(meshKey, "Runway") || Has(meshKey, "ConcreteFloor") || Has(meshKey, "Concrete_Floor")
            || Has(meshKey, "/Roads/") || Has(meshKey, "Asphalt") || GizmoMath.IsRoadOrBridgePiece(meshKey))
        {
            return PaintVerdict.Road;
        }

        return BendSupport.IsRock(meshKey) ? PaintVerdict.Rock : PaintVerdict.OnObject;
    }

    /// <summary>
    /// The ground of a stroke with no landscape loaded at all: <paramref name="cursorZ"/> (the viewport's pick, or the level
    /// plane it keeps over empty ground), unless the cursor rests on one of the drawn meshes under it (Bottom/Top of its
    /// box, Foot of its pivot or base) more than <see cref="OnMesh"/> above its bottom, as on a canopy or a roof: then the
    /// lowest such foot. The height of a crown is never a foot.
    /// </summary>
    public static float Plane(float cursorZ, IEnumerable<(float Bottom, float Top, float Foot)> underCursor)
    {
        ArgumentNullException.ThrowIfNull(underCursor);
        var plane = cursorZ;
        foreach (var (bottom, top, foot) in underCursor)
        {
            if (cursorZ > bottom + OnMesh && cursorZ <= top + OnMesh)
            {
                plane = MathF.Min(plane, foot);
            }
        }

        return plane;
    }

    /// <summary>splitmix64: the brush's random bits.</summary>
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>A number in [0, 1) from <paramref name="h"/> (a different one per <paramref name="i"/>).</summary>
    public static float Unit(ulong h, int i) => (Mix(h + (ulong)i) >> 40) / (float)(1 << 24);

    private static bool Has(string key, string part) => key.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The plants and standing things a stroke keeps its distance from, by their foot (UE cm) on a grid of cells as large as
/// the largest spacing. Distances are flat; only things within <see cref="Window"/> of a candidate's foot height count, so
/// a tree on a hill next to a cliff top is not blocked by one far below, and the cursor's height never matters.
/// </summary>
public sealed class SpacingIndex
{
    /// <summary>Least height window around a foot in which another foot counts (cm).</summary>
    public const float Reach = 1000f;

    /// <summary>How near a tree's foot comes to a bush's (cm): it passes over undergrowth, but no trunk grows out of a bush.</summary>
    public const float UndergrowthClear = 100f;

    private readonly float _cell;
    private readonly Dictionary<(int X, int Y), List<(Vector3 Foot, PaintClass Kind)>> _points = [];
    private readonly List<(int X, int Y)> _added = [];

    /// <summary>Creates an empty index whose cells are <paramref name="cell"/> cm.</summary>
    public SpacingIndex(float cell) => _cell = MathF.Max(cell, 1f);

    /// <summary>Points added so far (also the mark <see cref="Rollback"/> returns to).</summary>
    public int Count => _added.Count;

    /// <summary>Adds a foot.</summary>
    public void Add(Vector3 foot, PaintClass kind)
    {
        var key = CellOf(new Vector2(foot.X, foot.Y));
        (_points.TryGetValue(key, out var list) ? list : _points[key] = []).Add((foot, kind));
        _added.Add(key);
    }

    /// <summary>Takes out every point added after <paramref name="count"/> points (a preview's trial plants).</summary>
    public void Rollback(int count)
    {
        while (_added.Count > Math.Max(0, count))
        {
            var key = _added[^1];
            _added.RemoveAt(_added.Count - 1);
            var list = _points[key];
            list.RemoveAt(list.Count - 1);
        }
    }

    /// <summary>
    /// The height window for <paramref name="keep"/> (cm): <see cref="Reach"/>, or twice the distance on wide spacings, so a
    /// neighbour up or down a slope of up to 63° is still within it (3.1.0's fixed 10 m let 20 m trees come 17 m apart).
    /// </summary>
    public static float Window(float keep) => MathF.Max(Reach, 2f * keep);

    /// <summary>
    /// True when a point within <paramref name="keep"/> (flat) of <paramref name="at"/> stands within
    /// <paramref name="window"/> (default <see cref="Window"/>; infinity: at any height) of <paramref name="footZ"/>. A tree
    /// candidate keeps only <see cref="UndergrowthClear"/> from bushes; anything else keeps clear of all.
    /// </summary>
    public bool TooClose(Vector2 at, float footZ, float keep, PaintClass kind, float? window = null)
    {
        var (cx, cy) = CellOf(at);
        var reach = Math.Max(1, (int)MathF.Ceiling(keep / _cell));
        var height = window ?? Window(keep);
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (!_points.TryGetValue((cx + dx, cy + dy), out var points))
                {
                    continue;
                }

                foreach (var (foot, other) in points)
                {
                    var d = kind == PaintClass.Tree && other == PaintClass.Bush ? MathF.Min(keep, UndergrowthClear) : keep;
                    if (MathF.Abs(foot.Z - footZ) < height && Vector2.DistanceSquared(new Vector2(foot.X, foot.Y), at) < d * d)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private (int X, int Y) CellOf(Vector2 at) => ((int)MathF.Floor(at.X / _cell), (int)MathF.Floor(at.Y / _cell));
}
