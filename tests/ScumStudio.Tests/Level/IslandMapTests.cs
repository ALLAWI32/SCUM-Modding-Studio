using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The Spawns map lays the game's island picture under the spawn places: <see cref="IslandMap"/> turns world centimetres
/// into picture positions and map cells. Facts from the game files (HANDOFF): the A_0 outpost's tile bounds
/// (-640862,-575097)..(-603362,-537597) and the landscape tile Landscape_A_0_1 (-922838,-600177)..(-828600,-523800), whose
/// terrain (the Map page's terrain cache) spans (-904800,-600000)..(-828600,-523800); 20 x 20 such tiles make the island.
/// </summary>
public sealed class IslandMapTests
{
    [Fact]
    public void CornersMapToThePictureCorners()
    {
        // World X grows to the picture's right, world Y down.
        Assert.Equal((0d, 0d), Round(IslandMap.ToMap(IslandMap.MinCm, IslandMap.MinCm)));
        Assert.Equal((1d, 1d), Round(IslandMap.ToMap(IslandMap.MinCm + IslandMap.SizeCm, IslandMap.MinCm + IslandMap.SizeCm)));
        Assert.Equal((1d, 0d), Round(IslandMap.ToMap(IslandMap.MinCm + IslandMap.SizeCm, IslandMap.MinCm)));
    }

    [Fact]
    public void TheCalibrationIsTheLandscapeGrid()
    {
        // Landscape_A_0_1's terrain is the top-left quarter-cell tile of A_0; 20 tiles of 76,200 cm end at 619,200 cm.
        var a0 = IslandMap.CellRect(MapCell.Parse("A_0"));
        Assert.Equal((-904800f, -600000f), (a0.MinX, a0.MinY));
        Assert.Equal(76200f, IslandMap.CellCm / 4f);
        Assert.Equal(619200f, IslandMap.MinCm + IslandMap.SizeCm);

        // Checked on the game's picture (2048 px export): the A_0 outpost compound is drawn around pixel (380, 468).
        var (u, v) = IslandMap.ToMap(-622112f, -556347f);
        Assert.InRange(Math.Abs((u * 2048) - 380), 0, 4);
        Assert.InRange(Math.Abs((v * 2048) - 468), 0, 4);
    }

    [Fact]
    public void WorldToMapToWorldRoundTripsWithinOneCentimetre()
    {
        var random = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var x = IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm);
            var y = IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm);
            var (u, v) = IslandMap.ToMap(x, y);
            var (bx, by) = IslandMap.ToWorld(u, v);
            Assert.InRange(MathF.Abs(bx - x), 0f, 1f);
            Assert.InRange(MathF.Abs(by - y), 0f, 1f);
        }
    }

    [Fact]
    public void KnownPlacesFallInTheirCells()
    {
        Assert.Equal(MapCell.Parse("A_0"), IslandMap.CellAt(-622112f, -556347f)); // A_0_Outpost_Exterior centre
        Assert.Equal(MapCell.Parse("A_0"), IslandMap.CellAt(-875719f, -561988f)); // Landscape_A_0_1 centre
        Assert.Null(IslandMap.CellAt(IslandMap.MinCm - 1000f, 0f));
        Assert.Null(IslandMap.CellAt(0f, IslandMap.MinCm + IslandMap.SizeCm + 1000f));
    }

    [Fact]
    public void EveryCellIsAFifthOfTheMapAndTheyTileIt()
    {
        var rects = MapCell.All.Select(c => (Cell: c, Rect: IslandMap.CellRect(c))).ToList();
        Assert.Equal(25, rects.Count);
        foreach (var (cell, (minX, minY, maxX, maxY)) in rects)
        {
            Assert.Equal(IslandMap.SizeCm / 5f, maxX - minX, 1f);
            Assert.Equal(IslandMap.SizeCm / 5f, maxY - minY, 1f);
            Assert.Equal(cell, IslandMap.CellAt((minX + maxX) / 2f, (minY + maxY) / 2f));
        }

        // No gaps or overlaps: every sample of the map square lies in exactly one cell, and the cells span the square.
        var random = new Random(3);
        for (var i = 0; i < 2000; i++)
        {
            var x = IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm);
            var y = IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm);
            Assert.Single(rects, r => x >= r.Rect.MinX && x < r.Rect.MaxX && y >= r.Rect.MinY && y < r.Rect.MaxY);
        }

        Assert.Equal(IslandMap.MinCm, rects.Min(r => r.Rect.MinX), 1f);
        Assert.Equal(IslandMap.MinCm, rects.Min(r => r.Rect.MinY), 1f);
        Assert.Equal(IslandMap.MinCm + IslandMap.SizeCm, rects.Max(r => r.Rect.MaxX), 1f);
        Assert.Equal(IslandMap.MinCm + IslandMap.SizeCm, rects.Max(r => r.Rect.MaxY), 1f);
    }

    [Fact]
    public void ThePictureShowsZZeroTopLeftAndDFourBottomRight()
    {
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(MapCell.Parse("Z_0"));
        var (u, v) = IslandMap.ToMap((minX + maxX) / 2f, (minY + maxY) / 2f);
        Assert.Equal((0.1, 0.1), (Math.Round(u, 3), Math.Round(v, 3)));
        (minX, minY, maxX, maxY) = IslandMap.CellRect(MapCell.Parse("D_4"));
        (u, v) = IslandMap.ToMap((minX + maxX) / 2f, (minY + maxY) / 2f);
        Assert.Equal((0.9, 0.9), (Math.Round(u, 3), Math.Round(v, 3)));
    }

    private static (double, double) Round((double U, double V) p) => (Math.Round(p.U, 6), Math.Round(p.V, 6));
}
