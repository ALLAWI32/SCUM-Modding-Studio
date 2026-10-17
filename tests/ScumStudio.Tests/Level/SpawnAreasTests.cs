using System.Diagnostics;
using System.Numerics;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "a 2D map where I select an area: a whole sector, or any area I draw myself". <see cref="SpawnArea"/> answers
/// which spawn places an area holds; <see cref="SpawnPlaceIndex"/> answers it for the island's ~30,000 places at once.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SpawnAreasTests
{
    [Fact]
    public void RectangleAndConcavePolygonContainment()
    {
        var rect = SpawnArea.Rectangle(100, 200, -100, -200); // corners in any order
        Assert.True(rect.Contains(0, 0));
        Assert.True(rect.Contains(100, 200)); // corner and edges count
        Assert.True(rect.Contains(-100, 0));
        Assert.False(rect.Contains(100.5f, 0));

        // An L: the notch (top right) is outside, the arms inside, the inner corner's edges count.
        var l = SpawnArea.Polygon([new(0, 0), new(200, 0), new(200, 100), new(100, 100), new(100, 200), new(0, 200)]);
        Assert.True(l.Contains(50, 150));
        Assert.True(l.Contains(150, 50));
        Assert.False(l.Contains(150, 150));
        Assert.True(l.Contains(100, 150));
        Assert.True(l.Contains(150, 100));
        Assert.True(l.Contains(0, 0));
        Assert.False(l.Contains(-1, 50));
        Assert.Throws<ArgumentException>(() => SpawnArea.Polygon([new(0, 0), new(1, 1)]));
    }

    [Fact]
    public void CellAreaEqualsCellRect()
    {
        foreach (var cell in MapCell.All)
        {
            var area = SpawnArea.Cell(cell);
            var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
            Assert.Equal(SpawnAreaShape.Cell, area.Shape);
            Assert.Equal(cell, area.MapCell);
            Assert.Equal((minX, minY, maxX, maxY), (area.MinX, area.MinY, area.MaxX, area.MaxY));
            Assert.True(area.Contains((minX + maxX) / 2, (minY + maxY) / 2));
            Assert.False(area.Contains(minX - 1, minY));
        }

        Assert.Equal(SpawnArea.Cell(MapCell.Parse("B_2")), SpawnArea.Cell(MapCell.Parse("B_2")));
        Assert.NotEqual(SpawnArea.Cell(MapCell.Parse("B_2")), SpawnArea.Cell(MapCell.Parse("B_3")));
    }

    [Fact]
    public void RotatedZoneEllipseOverlap()
    {
        // A long thin zone (1000 x 100 half sizes) at the origin, turned 90 degrees: it now runs along Y.
        var turned = Zone(0, 0, 1000, 100, yaw: 90);
        var flat = Zone(0, 0, 1000, 100, yaw: 0);
        var above = SpawnArea.Rectangle(-50, 600, 50, 700); // only the turned zone reaches up there
        Assert.True(above.Overlaps(turned));
        Assert.False(above.Overlaps(flat));
        var right = SpawnArea.Rectangle(600, -50, 700, 50);
        Assert.True(right.Overlaps(flat));
        Assert.False(right.Overlaps(turned));

        // The area inside the ellipse, the ellipse inside the area, and a near miss at the diagonal.
        Assert.True(SpawnArea.Rectangle(-10, -10, 10, 10).Overlaps(flat));
        Assert.True(SpawnArea.Rectangle(-5000, -5000, 5000, 5000).Overlaps(flat));
        Assert.False(SpawnArea.Rectangle(720, 72, 800, 120).Overlaps(flat)); // (720,72): (0.72)^2 + (0.72)^2 > 1
        var hunting = new SpawnPlace(SpawnPlaceKind.Animal, 0, At(0, 0, 0), "Biome", 500, 500);
        Assert.True(SpawnArea.Polygon([new(300, 300), new(900, 300), new(900, 900)]).Overlaps(hunting)); // corner (300,300) is 424 away
        Assert.False(SpawnArea.Polygon([new(400, 400), new(900, 400), new(900, 900)]).Overlaps(hunting)); // 566 away

        // Points are in or out by their position only.
        Assert.True(above.Overlaps(new SpawnPlace(SpawnPlaceKind.Vehicle, 0, At(0, 650, 0), "City")));
        Assert.False(above.Overlaps(new SpawnPlace(SpawnPlaceKind.Character, 0, At(0, 750, 0), "Exterior")));
    }

    [Fact]
    public void IndexMatchesBruteForceOn30000SyntheticPlaces()
    {
        var random = new Random(11);
        var places = new List<SpawnPlace>();
        for (var i = 0; i < 30000; i++)
        {
            var (x, y) = (IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm), IslandMap.MinCm + (float)(random.NextDouble() * IslandMap.SizeCm));
            places.Add((i % 40) switch
            {
                0 => Zone(x, y, 2000 + (random.NextSingle() * 60000), 2000 + (random.NextSingle() * 30000), random.NextSingle() * 360),
                1 => new SpawnPlace(SpawnPlaceKind.Animal, i, At(x, y, 0), "Biome", 30000, 30000),
                2 => new SpawnPlace(SpawnPlaceKind.Vehicle, i, At(x, y, 0), "City"),
                3 => new SpawnPlace(SpawnPlaceKind.Zone, i, At(x, y, 0), "Flat", 0, 40000), // no length: a point, yet listed in many grid cells
                _ => new SpawnPlace(SpawnPlaceKind.Character, i, At(x, y, 0), "Exterior"),
            });
        }

        var index = SpawnPlaceIndex.Build(places);
        var areas = new List<SpawnArea>(MapCell.All.Select(SpawnArea.Cell))
        {
            SpawnArea.Rectangle(-300000, -200000, 100000, 50000),
            SpawnArea.Rectangle(IslandMap.MinCm, IslandMap.MinCm, IslandMap.MinCm + IslandMap.SizeCm, IslandMap.MinCm + IslandMap.SizeCm),
            SpawnArea.Polygon([new(-500000, -500000), new(300000, -400000), new(0, 0), new(250000, 400000), new(-600000, 200000)]),
        };
        foreach (var area in areas)
        {
            var expected = places.Where(area.Overlaps).ToList();
            Assert.Equal(expected, index.Inside(area));
        }

        // Answers fast once built (the page asks on every selection change).
        var island = areas[^2];
        index.Inside(island);
        var clock = Stopwatch.StartNew();
        var all = index.Inside(island);
        clock.Stop();
        Assert.True(all.Count > 29000, $"{all.Count} places on the whole island");
        if (!Ci.On) Assert.True(clock.ElapsedMilliseconds < 20, $"the whole island took {clock.ElapsedMilliseconds} ms");
        clock.Restart();
        var cell = index.Inside(areas[7]);
        clock.Stop();
        Assert.NotEmpty(cell);
        if (!Ci.On) Assert.True(clock.ElapsedMilliseconds < 20, $"one cell took {clock.ElapsedMilliseconds} ms");
    }

    private static SpawnPlace Zone(float x, float y, float sizeX, float sizeY, float yaw) =>
        new(SpawnPlaceKind.Zone, 0, At(x, y, yaw), "MTZ_Settlement_Large", sizeX, sizeY);

    private static FTransform At(float x, float y, float yaw) =>
        new(new FRotator(0, yaw, 0).Quaternion(), new FVector(x, y, 0), FVector.One);
}
