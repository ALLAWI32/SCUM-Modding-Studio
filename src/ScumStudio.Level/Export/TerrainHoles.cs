using System.Numerics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Export;

/// <summary>
/// Cuts <see cref="TerrainHole"/>s into the cooked landscape collision of a tile, the way the game's own bunker entrances
/// are cut (read from SCUM's island, 2026-10-16: 173 of its 1,600 collision components carry holes).
/// <para>Each render quad (qx, qy) of a component is the PhysX cell (row <c>ComponentSizeQuads - 1 - qx</c>, column
/// <c>qy</c>): UE cooks the heightfield with its rows running back along X (sample (r, c) = render sample
/// (<c>ComponentSizeQuads - r</c>, c), 65,025 of 65,025 equal on every component read). A hole cell has the PhysX hole
/// material (127) in both material bytes; bit 7 of material byte 1 marks a collision vertex: 0 for a vertex whose four
/// cells are all holes, 1 on the rim of a hole (all 120 such vertices of three of the game's holes). Everything else stays
/// as cooked: heights, bounds and every other byte.</para>
/// </summary>
public static class TerrainHoles
{
    /// <summary>World X/Y of a collision component's quad grid: world = Origin + qx·AxisX + qy·AxisY.</summary>
    /// <param name="Origin">World X/Y of quad corner (0, 0).</param>
    /// <param name="AxisX">World X/Y step of one quad along the component's X.</param>
    /// <param name="AxisY">World X/Y step of one quad along the component's Y.</param>
    /// <param name="SizeQuads">Quads per side (<c>CollisionSizeQuads</c>).</param>
    public readonly record struct Grid(Vector2 Origin, Vector2 AxisX, Vector2 AxisY, int SizeQuads)
    {
        /// <summary>World X/Y of quad coordinates (<paramref name="qx"/>, <paramref name="qy"/>).</summary>
        public Vector2 World(float qx, float qy) => Origin + (qx * AxisX) + (qy * AxisY);
    }

    /// <summary>
    /// The quad grid of the collision component export <paramref name="export"/> from the cooked properties alone (its
    /// <c>RelativeLocation</c> on the landscape's root component, whose scale and turn place it in the world): the same in
    /// the client and the server cook, whose heightmaps a server may not carry. Null when the component is placed in a way
    /// the island never uses (pitched, rolled, scaled or turned on its own).
    /// </summary>
    public static Grid? GridOf(CookedPackage package, int export, PropertyBlock block)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(block);
        var size = (block.Find("CollisionSizeQuads")?.Value as IntValue)?.Value ?? 0;
        if (size < 1 || block.Find("RelativeRotation") is not null || block.Find("RelativeScale3D") is not null
            || block.Find("AttachParent")?.Value is not ObjectValue { Index: > 0 } parent || parent.Index > package.Exports.Count)
        {
            return null;
        }

        var root = package.ReadProperties(parent.Index - 1);
        var location = root.Find("RelativeLocation")?.Value as VectorValue ?? new VectorValue(0, 0, 0);
        var scale = root.Find("RelativeScale3D")?.Value as VectorValue ?? new VectorValue(1, 1, 1);
        var rotation = root.Find("RelativeRotation")?.Value as RotatorValue ?? new RotatorValue(0, 0, 0);
        if (rotation.Pitch != 0f || rotation.Roll != 0f || root.Find("AttachParent") is not null)
        {
            return null;
        }

        var own = block.Find("RelativeLocation")?.Value as VectorValue ?? new VectorValue(0, 0, 0);
        var (sin, cos) = MathF.SinCos(rotation.Yaw * MathF.PI / 180f);
        Vector2 Turn(float x, float y) => new((x * cos) - (y * sin), (x * sin) + (y * cos));
        var origin = new Vector2(location.X, location.Y) + Turn(own.X * scale.X, own.Y * scale.Y);
        return new Grid(origin, Turn(scale.X, 0f), Turn(0f, scale.Y), size);
    }

    /// <summary>The landscape tiles of <paramref name="catalog"/> with their world X/Y bounds (reads every tile's header: a second or two).</summary>
    public static IReadOnlyList<(string Tile, Vector2 Min, Vector2 Max)> Tiles(Assets.Catalog.AssetCatalog catalog) =>
        GroundHeights.LandscapeTiles(catalog).Select(t => (t.Path, new Vector2(t.Tile.BoundsMin.X, t.Tile.BoundsMin.Y), new Vector2(t.Tile.BoundsMax.X, t.Tile.BoundsMax.Y))).ToList();

    /// <summary>The tile of <paramref name="tiles"/> under world (<paramref name="x"/>, <paramref name="y"/>) (the smallest, where bounds overlap), or null off the landscape.</summary>
    public static string? TileAt(IEnumerable<(string Tile, Vector2 Min, Vector2 Max)> tiles, float x, float y) =>
        tiles.Where(t => t.Min.X <= x && x <= t.Max.X && t.Min.Y <= y && y <= t.Max.Y)
            .OrderBy(t => (t.Max.X - t.Min.X) * (t.Max.Y - t.Min.Y)).Select(t => t.Tile).FirstOrDefault();

    /// <summary>The quads (component coordinates) whose centre lies inside one of <paramref name="holes"/>.</summary>
    public static List<(int X, int Y)> Quads(Grid grid, IEnumerable<TerrainHole> holes)
    {
        ArgumentNullException.ThrowIfNull(holes);
        var result = new HashSet<(int, int)>();
        var det = (grid.AxisX.X * grid.AxisY.Y) - (grid.AxisY.X * grid.AxisX.Y);
        if (MathF.Abs(det) < 1e-6f)
        {
            return [];
        }

        Vector2 ToQuads(Vector2 world)
        {
            var d = world - grid.Origin;
            return new Vector2(((d.X * grid.AxisY.Y) - (d.Y * grid.AxisY.X)) / det, ((d.Y * grid.AxisX.X) - (d.X * grid.AxisX.Y)) / det);
        }

        foreach (var hole in holes)
        {
            var (min, max) = hole.Bounds;
            var corners = new[] { ToQuads(min), ToQuads(max), ToQuads(new Vector2(min.X, max.Y)), ToQuads(new Vector2(max.X, min.Y)) };
            var (x0, x1) = (Math.Max(0, (int)MathF.Floor(corners.Min(c => c.X)) - 1), Math.Min(grid.SizeQuads - 1, (int)MathF.Ceiling(corners.Max(c => c.X)) + 1));
            var (y0, y1) = (Math.Max(0, (int)MathF.Floor(corners.Min(c => c.Y)) - 1), Math.Min(grid.SizeQuads - 1, (int)MathF.Ceiling(corners.Max(c => c.Y)) + 1));
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var centre = grid.World(x + 0.5f, y + 0.5f);
                    if (hole.Contains(centre.X, centre.Y))
                    {
                        result.Add((x, y));
                    }
                }
            }
        }

        return [.. result.OrderBy(q => q.Item2).ThenBy(q => q.Item1)];
    }

    /// <summary>The PhysX cell (row, column) of render quad (<paramref name="qx"/>, <paramref name="qy"/>).</summary>
    public static (int Row, int Column) CellOf(LandscapeCollision.Heightfield field, int qx, int qy) => (field.Rows - 2 - qx, qy);

    /// <summary>True when PhysX cell (<paramref name="row"/>, <paramref name="column"/>) is a hole.</summary>
    public static bool IsHole(ReadOnlySpan<byte> payload, LandscapeCollision.Heightfield field, int row, int column) =>
        (payload[field.SampleAt(row, column) + 2] & 0x7F) == LandscapeCollision.HoleMaterial;

    /// <summary>
    /// Marks the cells of <paramref name="quads"/> as holes in the heightfield <paramref name="field"/> of
    /// <paramref name="payload"/> (in place) and sets the collision-vertex bits around them as PhysX does; returns how many
    /// cells became holes (0 when they all were).
    /// </summary>
    /// <exception cref="InvalidDataException">The heightfield is not the one landscape collision cooks on SCUM's island.</exception>
    public static int Cut(Span<byte> payload, LandscapeCollision.Heightfield field, IEnumerable<(int X, int Y)> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (field.Rows != field.Columns || field.Format != 1 || field.Flags != 1 || field.SampleStride != 4)
        {
            throw new InvalidDataException($"a {field.Rows}x{field.Columns} heightfield, format {field.Format}, flags {field.Flags}: not cooked as the island's are");
        }

        var cut = 0;
        var touched = new HashSet<(int, int)>();
        foreach (var (qx, qy) in quads)
        {
            var (row, column) = CellOf(field, qx, qy);
            if (row < 0 || column < 0 || row > field.Rows - 2 || column > field.Columns - 2)
            {
                continue;
            }

            if (!IsHole(payload, field, row, column))
            {
                var at = field.SampleAt(row, column);
                payload[at + 2] = (byte)((payload[at + 2] & 0x80) | LandscapeCollision.HoleMaterial);
                payload[at + 3] = (byte)((payload[at + 3] & 0x80) | LandscapeCollision.HoleMaterial);
                cut++;
            }

            touched.UnionWith([(row, column), (row + 1, column), (row, column + 1), (row + 1, column + 1)]);
        }

        // The collision-vertex bit of every corner of a cut cell; the border vertices never carry one (eNO_BOUNDARY_EDGES).
        foreach (var (row, column) in touched)
        {
            if (row < 1 || column < 1 || row > field.Rows - 2 || column > field.Columns - 2)
            {
                continue;
            }

            var holes = 0;
            foreach (var (r, c) in new[] { (row - 1, column - 1), (row - 1, column), (row, column - 1), (row, column) })
            {
                holes += IsHole(payload, field, r, c) ? 1 : 0;
            }

            var at = field.SampleAt(row, column) + 3;
            payload[at] = holes == 4 ? (byte)(payload[at] & 0x7F) : holes > 0 ? (byte)(payload[at] | 0x80) : payload[at];
        }

        return cut;
    }
}
