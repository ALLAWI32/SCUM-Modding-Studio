using System.Globalization;
using System.Numerics;

namespace ScumStudio.Level.Editing;

/// <summary>The outline of a <see cref="TerrainHole"/>.</summary>
public enum TerrainHoleShape
{
    /// <summary>A square (turned by <see cref="TerrainHole.Yaw"/>).</summary>
    Square,

    /// <summary>A circle.</summary>
    Circle,
}

/// <summary>
/// A hole cut into the landscape for a bunker entrance (owner, 2026-10-16: "a square or a circle on the ground, press
/// Delete, and it opens a hole: no collision, a player walks into it and falls through, like the game's own bunker
/// entrances"). Every landscape quad (1.5 m on SCUM's island) whose centre lies inside the outline is cut, in whichever
/// tiles and components it falls; <see cref="Tile"/> is the tile under the centre, for history lists.
/// </summary>
/// <param name="Tile">Landscape tile level under the centre (e.g. <c>/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1c</c>).</param>
/// <param name="Shape">Square or circle.</param>
/// <param name="X">World X of the centre (cm).</param>
/// <param name="Y">World Y of the centre (cm).</param>
/// <param name="Size">Side of the square or diameter of the circle (cm).</param>
/// <param name="Yaw">Turn of the square around its centre (degrees).</param>
public sealed record TerrainHole(string Tile, TerrainHoleShape Shape, float X, float Y, float Size, float Yaw = 0f)
{
    /// <summary>Edge of one landscape quad on SCUM's island (cm): the grid holes snap to.</summary>
    public const float QuadCm = 150f;

    /// <summary>Smallest hole (cm): one quad.</summary>
    public const float MinSize = QuadCm;

    /// <summary>Largest hole (cm): a bunker entrance, not a crater.</summary>
    public const float MaxSize = 50 * QuadCm;

    /// <summary>Why the hole cannot be cut, or null when it can.</summary>
    public string? Validate() =>
        string.IsNullOrWhiteSpace(Tile) ? "No landscape tile given."
        : !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Size) || !float.IsFinite(Yaw) || MathF.Abs(X) > EditState.MaxCoordinate || MathF.Abs(Y) > EditState.MaxCoordinate
            ? "The hole's place or size is not a usable number."
        : Size < MinSize - 0.5f || Size > MaxSize + 0.5f ? string.Create(CultureInfo.InvariantCulture, $"A hole is {MinSize / 100f:0.#} to {MaxSize / 100f:0} m across.")
        : null;

    /// <summary>True when world (<paramref name="x"/>, <paramref name="y"/>) lies inside the outline.</summary>
    public bool Contains(float x, float y)
    {
        var (u, v) = Local(x, y);
        var half = Size / 2f;
        return Shape == TerrainHoleShape.Circle ? (u * u) + (v * v) <= half * half : MathF.Abs(u) <= half && MathF.Abs(v) <= half;
    }

    /// <summary>World X/Y box around the outline.</summary>
    public (Vector2 Min, Vector2 Max) Bounds
    {
        get
        {
            var reach = Shape == TerrainHoleShape.Circle ? Size / 2f : Size / 2f * MathF.Sqrt(2f);
            return (new Vector2(X - reach, Y - reach), new Vector2(X + reach, Y + reach));
        }
    }

    /// <summary>The outline as a closed polygon in world X/Y (the first point repeated at the end).</summary>
    public Vector2[] Outline(int circleSegments = 64)
    {
        var half = Size / 2f;
        var local = Shape == TerrainHoleShape.Circle
            ? Enumerable.Range(0, circleSegments).Select(i => new Vector2(MathF.Cos(i * MathF.Tau / circleSegments), MathF.Sin(i * MathF.Tau / circleSegments)) * half).ToArray()
            : [new(-half, -half), new(half, -half), new(half, half), new(-half, half)];
        var (sin, cos) = MathF.SinCos(Yaw * MathF.PI / 180f);
        return [.. local.Select(p => new Vector2(X + (p.X * cos) - (p.Y * sin), Y + (p.X * sin) + (p.Y * cos))), new Vector2(X + (local[0].X * cos) - (local[0].Y * sin), Y + (local[0].X * sin) + (local[0].Y * cos))];
    }

    /// <summary>
    /// The hole snapped to the landscape grid: the size to whole quads, the centre to a quad corner (an even number of
    /// quads across) or a quad centre (odd), so an unturned square cuts exactly size² quads.
    /// </summary>
    public TerrainHole Snapped()
    {
        var quads = Math.Clamp((int)MathF.Round(Size / QuadCm), 1, (int)(MaxSize / QuadCm));
        var offset = quads % 2 == 0 ? 0f : QuadCm / 2f;
        float Snap(float v) => (MathF.Round((v - offset) / QuadCm) * QuadCm) + offset;
        return this with { X = Snap(X), Y = Snap(Y), Size = quads * QuadCm, Yaw = Shape == TerrainHoleShape.Circle ? 0f : Yaw };
    }

    /// <summary>
    /// The world quads the hole cuts, as grid indices (<c>floor(x / QuadCm)</c>, <c>floor(y / QuadCm)</c>): the export's
    /// rule on a landscape whose quads start at world 0, as every tile of SCUM's island does.
    /// </summary>
    public IEnumerable<(int X, int Y)> WorldQuads()
    {
        var (min, max) = Bounds;
        for (var y = (int)MathF.Floor(min.Y / QuadCm); y <= (int)MathF.Floor(max.Y / QuadCm); y++)
        {
            for (var x = (int)MathF.Floor(min.X / QuadCm); x <= (int)MathF.Floor(max.X / QuadCm); x++)
            {
                if (Contains((x + 0.5f) * QuadCm, (y + 0.5f) * QuadCm))
                {
                    yield return (x, y);
                }
            }
        }
    }

    /// <summary>True when the world quad under (<paramref name="x"/>, <paramref name="y"/>) is cut by one of <paramref name="holes"/> (see <see cref="WorldQuads"/>).</summary>
    public static bool CutsQuadAt(IEnumerable<TerrainHole> holes, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(holes);
        var (cx, cy) = ((MathF.Floor(x / QuadCm) + 0.5f) * QuadCm, (MathF.Floor(y / QuadCm) + 0.5f) * QuadCm);
        return holes.Any(h => h.Contains(cx, cy));
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{(Shape == TerrainHoleShape.Circle ? "round" : "square")} hole {Size / 100f:0.#} m at ({X:0}, {Y:0})");

    private (float U, float V) Local(float x, float y)
    {
        var (dx, dy) = (x - X, y - Y);
        if (Yaw == 0f || Shape == TerrainHoleShape.Circle)
        {
            return (dx, dy);
        }

        var (sin, cos) = MathF.SinCos(-Yaw * MathF.PI / 180f);
        return ((dx * cos) - (dy * sin), (dx * sin) + (dy * cos));
    }
}

/// <summary>
/// Cuts a hole into the landscape (<paramref name="Cut"/> true) or fills one back in (false): the export marks the
/// landscape collision under it as PhysX holes in the client and server tiles (see <see cref="Export.TerrainHoles"/>).
/// </summary>
/// <param name="Hole">The hole.</param>
/// <param name="Cut">True to cut it, false to fill it back in.</param>
public sealed record TerrainHoleOp(TerrainHole Hole, bool Cut = true) : EditOp
{
    /// <inheritdoc />
    public override EditOp Inverse() => this with { Cut = !Cut };

    /// <inheritdoc />
    public override string Describe() => $"{(Cut ? "Cut" : "Fill")} {Hole} in {Short(Hole.Tile)}";

    /// <inheritdoc />
    public override IReadOnlyList<string> GetTouchedLevels() => [Hole.Tile];

    /// <inheritdoc />
    public override ActorRef? GetPrimaryTarget() => null;
}
