using System.Numerics;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Spawns;

/// <summary>The shape of a <see cref="SpawnArea"/>.</summary>
public enum SpawnAreaShape
{
    /// <summary>A whole map cell.</summary>
    Cell,

    /// <summary>A rectangle along the world axes.</summary>
    Rectangle,

    /// <summary>Any polygon (concave allowed).</summary>
    Polygon,
}

/// <summary>
/// An area of the island picked on the Spawns map (world centimetres): a whole map cell, a rectangle or a polygon. It
/// answers which spawn places it holds: a point when it lies inside (edges count), a threat zone or hunting area when its
/// ellipse or circle touches it.
/// </summary>
public sealed record SpawnArea
{
    private SpawnArea(SpawnAreaShape shape, MapCell? cell, IReadOnlyList<Vector2> points)
    {
        Shape = shape;
        MapCell = cell;
        Points = points;
        var (min, max) = (new Vector2(float.MaxValue), new Vector2(float.MinValue));
        foreach (var p in points)
        {
            (min, max) = (Vector2.Min(min, p), Vector2.Max(max, p));
        }

        (MinX, MinY, MaxX, MaxY) = (min.X, min.Y, max.X, max.Y);
    }

    /// <summary>The shape.</summary>
    public SpawnAreaShape Shape { get; }

    /// <summary>The cell of a <see cref="SpawnAreaShape.Cell"/> area; null otherwise.</summary>
    public MapCell? MapCell { get; }

    /// <summary>The outline (world cm): the four corners of a cell or rectangle, the vertices of a polygon.</summary>
    public IReadOnlyList<Vector2> Points { get; }

    /// <summary>Smallest X of the outline.</summary>
    public float MinX { get; }

    /// <summary>Smallest Y of the outline.</summary>
    public float MinY { get; }

    /// <summary>Largest X of the outline.</summary>
    public float MaxX { get; }

    /// <summary>Largest Y of the outline.</summary>
    public float MaxY { get; }

    /// <summary>A whole map cell.</summary>
    public static SpawnArea Cell(MapCell cell)
    {
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
        return new SpawnArea(SpawnAreaShape.Cell, cell, Corners(minX, minY, maxX, maxY));
    }

    /// <summary>A rectangle along the world axes (corners in any order).</summary>
    public static SpawnArea Rectangle(float minX, float minY, float maxX, float maxY) =>
        new(SpawnAreaShape.Rectangle, null, Corners(MathF.Min(minX, maxX), MathF.Min(minY, maxY), MathF.Max(minX, maxX), MathF.Max(minY, maxY)));

    /// <summary>A polygon through <paramref name="points"/> (at least three; closed implicitly).</summary>
    /// <exception cref="ArgumentException">Fewer than three points.</exception>
    public static SpawnArea Polygon(IReadOnlyList<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Count >= 3
            ? new SpawnArea(SpawnAreaShape.Polygon, null, points.ToArray())
            : throw new ArgumentException("A polygon needs at least three points.", nameof(points));
    }

    /// <summary>True when the world point lies inside the area or on its outline (even-odd rule for polygons).</summary>
    public bool Contains(float x, float y)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY)
        {
            return false;
        }

        if (Shape != SpawnAreaShape.Polygon)
        {
            return true;
        }

        var inside = false;
        var p = new Vector2(x, y);
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var (a, b) = (Points[j], Points[i]);
            if (DistanceToSegment(p, a, b) <= EdgeToleranceCm)
            {
                return true;
            }

            if ((a.Y > y) != (b.Y > y) && x < a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// True when <paramref name="place"/> belongs to the area: a point place when it lies inside; a threat zone (an ellipse
    /// turned by its yaw) or a hunting area (a circle) when any part of it touches the area.
    /// </summary>
    public bool Overlaps(SpawnPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        var at = place.Transform.Translation;
        if (place.Kind is not (SpawnPlaceKind.Zone or SpawnPlaceKind.Animal) || place.SizeX <= 0 || place.SizeY <= 0)
        {
            return Contains(at.X, at.Y);
        }

        var reach = MathF.Max(place.SizeX, place.SizeY);
        if (at.X + reach < MinX || at.X - reach > MaxX || at.Y + reach < MinY || at.Y - reach > MaxY)
        {
            return false;
        }

        if (Contains(at.X, at.Y))
        {
            return true;
        }

        // In the ellipse's own frame scaled to a unit circle, the outline touches it when an edge comes within 1.
        var yaw = place.Kind == SpawnPlaceKind.Zone ? place.Transform.Rotator().Yaw * (MathF.PI / 180f) : 0f;
        var (cos, sin) = (MathF.Cos(-yaw), MathF.Sin(-yaw));
        var centre = new Vector2(at.X, at.Y);
        Vector2 Local(Vector2 p)
        {
            var d = p - centre;
            return new Vector2(((d.X * cos) - (d.Y * sin)) / place.SizeX, ((d.X * sin) + (d.Y * cos)) / place.SizeY);
        }

        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            if (DistanceToSegment(Vector2.Zero, Local(Points[j]), Local(Points[i])) <= 1f)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool Equals(SpawnArea? other) =>
        other is not null && Shape == other.Shape && MapCell == other.MapCell && Points.SequenceEqual(other.Points);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Shape);
        hash.Add(MapCell);
        foreach (var p in Points)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }

    // A point this close to a polygon edge counts as on it (float noise of a vertex dragged onto a place).
    private const float EdgeToleranceCm = 0.5f;

    private static Vector2[] Corners(float minX, float minY, float maxX, float maxY) =>
        [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)];

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        var t = length <= 0f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0f, 1f);
        return Vector2.Distance(p, a + (ab * t));
    }
}

/// <summary>
/// The island's spawn places on a uniform grid, so the places inside an area are found without testing all 30,000: a
/// point sits in one grid cell, a zone or hunting area in every cell its square reach covers. Built once per read (on a
/// worker: about 30 ms for the island), then read-only and thread-safe.
/// </summary>
public sealed class SpawnPlaceIndex
{
    private readonly IReadOnlyList<SpawnPlace> _places;
    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
    private readonly float _cellCm;

    // Places listed in more than one grid cell (zones, hunting areas), answered once per query.
    private readonly bool[] _spans;

    private SpawnPlaceIndex(IReadOnlyList<SpawnPlace> places, float cellCm)
    {
        _places = places;
        _cellCm = cellCm;
        _spans = new bool[places.Count];
    }

    /// <summary>The places indexed, in their original order.</summary>
    public IReadOnlyList<SpawnPlace> Places => _places;

    /// <summary>Indexes <paramref name="places"/> (the list is kept, not copied).</summary>
    public static SpawnPlaceIndex Build(IReadOnlyList<SpawnPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        var index = new SpawnPlaceIndex(places, IslandMap.CellCm / 32f);
        for (var i = 0; i < places.Count; i++)
        {
            var (x0, y0, x1, y1) = index.CellRange(Reach(places[i]));
            index._spans[i] = x1 > x0 || y1 > y0;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (!index._cells.TryGetValue((x, y), out var list))
                    {
                        index._cells[(x, y)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        return index;
    }

    /// <summary>The places that belong to <paramref name="area"/> (<see cref="SpawnArea.Overlaps"/>), in their original order.</summary>
    public IReadOnlyList<SpawnPlace> Inside(SpawnArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var (x0, y0, x1, y1) = CellRange((area.MinX, area.MinY, area.MaxX, area.MaxY));
        var hits = new List<int>();
        var seen = new HashSet<int>();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!_cells.TryGetValue((x, y), out var list))
                {
                    continue;
                }

                foreach (var i in list)
                {
                    if ((!_spans[i] || seen.Add(i)) && area.Overlaps(_places[i]))
                    {
                        hits.Add(i);
                    }
                }
            }
        }

        hits.Sort();
        return hits.Select(i => _places[i]).ToList();
    }

    private static (float MinX, float MinY, float MaxX, float MaxY) Reach(SpawnPlace place)
    {
        var at = place.Transform.Translation;
        var r = place.Kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal ? MathF.Max(0f, MathF.Max(place.SizeX, place.SizeY)) : 0f;
        return (at.X - r, at.Y - r, at.X + r, at.Y + r);
    }

    private (int X0, int Y0, int X1, int Y1) CellRange((float MinX, float MinY, float MaxX, float MaxY) box) =>
        (Cell(box.MinX), Cell(box.MinY), Cell(box.MaxX), Cell(box.MaxY));

    private int Cell(float cm) => (int)MathF.Floor(Math.Clamp(cm, -4e6f, 4e6f) / _cellCm);
}
