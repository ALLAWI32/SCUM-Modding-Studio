namespace ScumStudio.Level.World;

/// <summary>
/// The island's own map picture (the game's HUD texture <see cref="TexturePath"/>, read from the user's paks) and how it
/// lines up with the world. The picture is a square that covers the landscape exactly (20 x 20 tiles of 762 m),
/// <see cref="SizeCm"/> on a side from <see cref="MinCm"/> on both world axes, shown the way the game shows it: the game's
/// spawn screen (<c>UI_SpawnSectorSelector</c>) draws the texture turned 180 degrees (its <c>MapImage</c> has
/// <c>RenderTransform.Angle</c> 180) under a grid of sector buttons that reads D4 D3 D2 D1 D0 on the top row down to
/// Z4 ... Z0 on the bottom row, and the admin map has the same labels. So on the picture world X grows to the LEFT and
/// world Y grows UP, and <see cref="MinCm"/> is its bottom-right corner. The cells follow the world axes: the number (0-4)
/// grows with X, the letter with Y in the order Z, A, B, C, D, so <c>D_4</c> is the top-left cell, <c>D_3</c> the one to
/// its right and <c>Z_0</c> the bottom-right one. Up to 3.5.0 the studio drew the texture as stored, un-turned: the island
/// was upside down and mirrored, while coordinates and cell names were right all along (HANDOFF, Spawns).
/// </summary>
public static class IslandMap
{
    /// <summary>Object path of the game's island map texture (pakchunk14).</summary>
    public const string TexturePath = "/Game/ConZ_Files/UI/HUD/IslandMap.IslandMap";

    /// <summary>World X and Y (cm) of the picture's bottom-right corner: the landscape's min corner on both axes.</summary>
    public const float MinCm = -904800f;

    /// <summary>Side of the square the picture covers (cm): five cells of 3,048 m.</summary>
    public const float SizeCm = 1524000f;

    /// <summary>Side of one map cell (cm).</summary>
    public const float CellCm = SizeCm / 5f;

    // Cell letters by world Y band, from MinCm up (the picture's bottom up).
    private static readonly char[] LetterBands = ['Z', 'A', 'B', 'C', 'D'];

    /// <summary>
    /// Where the world point (<paramref name="x"/>, <paramref name="y"/>) cm is on the picture: 0..1 from its left and top
    /// edges. The picture is turned like the game's: +X is to the left, +Y up.
    /// </summary>
    public static (double U, double V) ToMap(float x, float y) =>
        (1 - ((x - (double)MinCm) / SizeCm), 1 - ((y - (double)MinCm) / SizeCm));

    /// <summary>The world point (cm) at picture position (<paramref name="u"/>, <paramref name="v"/>) (0..1 from the left and top edges).</summary>
    public static (float X, float Y) ToWorld(double u, double v) =>
        ((float)(MinCm + ((1 - u) * SizeCm)), (float)(MinCm + ((1 - v) * SizeCm)));

    /// <summary>
    /// The picture angle (degrees, clockwise from the picture's right) of the way a place with yaw <paramref name="yaw"/>
    /// faces. The picture is turned 180 degrees, so a place facing +X (yaw 0) points left; a larger yaw still turns
    /// clockwise on the picture (a half turn keeps the sense of turning).
    /// </summary>
    public static float ScreenYaw(float yaw) => yaw + 180f;

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
