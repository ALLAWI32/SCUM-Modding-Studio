namespace ScumStudio.Level.World;

/// <summary>
/// The island's own map picture (the game's HUD texture <see cref="TexturePath"/>, read from the user's paks) and how it
/// lines up with the world. The picture is a square that covers the landscape exactly (20 x 20 tiles of 762 m),
/// <see cref="SizeCm"/> on a side from <see cref="MinCm"/> on both world axes: world X grows to the picture's right and
/// world Y grows down. The cells follow the world axes: the number (0-4) grows with X, the letter with Y in the order
/// Z, A, B, C, D, so <c>Z_0</c> is the top-left cell, <c>A_0</c> the one below it and <c>D_4</c> the bottom-right one.
/// </summary>
public static class IslandMap
{
    /// <summary>Object path of the game's island map texture (pakchunk14).</summary>
    public const string TexturePath = "/Game/ConZ_Files/UI/HUD/IslandMap.IslandMap";

    /// <summary>World X and Y (cm) of the picture's top-left corner: the landscape's min corner on both axes.</summary>
    public const float MinCm = -904800f;

    /// <summary>Side of the square the picture covers (cm): five cells of 3,048 m.</summary>
    public const float SizeCm = 1524000f;

    /// <summary>Side of one map cell (cm).</summary>
    public const float CellCm = SizeCm / 5f;

    // Cell letters by world Y band, from MinCm up (the picture's top down).
    private static readonly char[] LetterBands = ['Z', 'A', 'B', 'C', 'D'];

    /// <summary>Where the world point (<paramref name="x"/>, <paramref name="y"/>) cm is on the picture: 0..1 from its left and top edges.</summary>
    public static (double U, double V) ToMap(float x, float y) =>
        ((x - (double)MinCm) / SizeCm, (y - (double)MinCm) / SizeCm);

    /// <summary>The world point (cm) at picture position (<paramref name="u"/>, <paramref name="v"/>) (0..1 from the left and top edges).</summary>
    public static (float X, float Y) ToWorld(double u, double v) =>
        ((float)(MinCm + (u * SizeCm)), (float)(MinCm + (v * SizeCm)));

    /// <summary>The world rectangle (cm) of <paramref name="cell"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the 25 cells.</exception>
    public static (float MinX, float MinY, float MaxX, float MaxY) CellRect(MapCell cell)
    {
        var band = Array.IndexOf(LetterBands, cell.Column);
        if (!cell.IsValid || band < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, "Not a map cell.");
        }

        var minX = MinCm + (cell.Row * (double)CellCm);
        var minY = MinCm + (band * (double)CellCm);
        return ((float)minX, (float)minY, (float)(minX + CellCm), (float)(minY + CellCm));
    }

    /// <summary>The cell the world point (<paramref name="x"/>, <paramref name="y"/>) cm lies in, or null outside the map.</summary>
    public static MapCell? CellAt(float x, float y)
    {
        var row = (int)Math.Floor((x - (double)MinCm) / CellCm);
        var band = (int)Math.Floor((y - (double)MinCm) / CellCm);
        if (x == MinCm + SizeCm)
        {
            row = 4; // the far edges belong to the last cells
        }

        if (y == MinCm + SizeCm)
        {
            band = 4;
        }

        return row is >= 0 and < 5 && band is >= 0 and < 5 ? new MapCell(LetterBands[band], row) : null;
    }
}
