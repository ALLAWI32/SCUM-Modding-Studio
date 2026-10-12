using System.Numerics;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Where the paint brush plants (owner, 3.1.0: "it plants at random, a huge number of trees very fast, in some places and
/// not in others, sometimes on half the road"): evenly and spaced, nearest the cursor first on the timer, never on roads,
/// water or rocks, with the spacing measured at the feet.
/// </summary>
public sealed class BrushScatterTests
{
    private const float Least = 200f;
    private const float Most = 300f;
    private const float Radius = 1500f;

    [Fact]
    public void ASweepNeverBreaksTheSpacing()
    {
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var plants = Sweep(seed, Vector2.Zero);
            Assert.True(plants.Count > 60, $"seed {seed}: {plants.Count} plants");
            for (var i = 0; i < plants.Count; i++)
            {
                for (var j = i + 1; j < plants.Count; j++)
                {
                    // The later one kept its own distance (between the two ends) from the earlier one.
                    Assert.True(Vector2.Distance(plants[i].At, plants[j].At) >= plants[j].Keep - 0.01f, $"seed {seed}: {plants[i]} and {plants[j]}");
                    Assert.InRange(plants[j].Keep, Least, Most);
                }
            }
        }
    }

    [Fact]
    public void ASweepLeavesNoHoles()
    {
        var plants = Sweep(7, new Vector2(12_345f, -6_789f));
        var centre = new Vector2(12_345f, -6_789f);
        for (var y = -Radius; y <= Radius; y += 50f)
        {
            for (var x = -Radius; x <= Radius; x += 50f)
            {
                var p = centre + new Vector2(x, y);
                if (Vector2.Distance(p, centre) <= Radius - Most)
                {
                    Assert.Contains(plants, s => Vector2.Distance(s.At, p) <= 2f * Most);
                }
            }
        }
    }

    [Fact]
    public void TheTimerPlantsNearestFirstOneACallUntilTheCircleIsFull()
    {
        var index = new SpacingIndex(Most);
        var tried = new HashSet<(int X, int Y)>();
        var centre = new Vector2(500f, 500f);
        var cell = BrushScatter.CellSize(Least, 600f);
        var planted = new List<PaintSpot>();
        while (Next() is { } spot)
        {
            // The nearest spot that was still free when it was picked: none nearer could take a plant.
            var nearer = BrushScatter.Nearest(9, cell, centre, 600f, Least, Most).Where(s => Vector2.Distance(s.At, centre) < Vector2.Distance(spot.At, centre) - 0.01f);
            Assert.All(nearer, s => Assert.True(planted.Take(planted.Count - 1).Any(p => Vector2.Distance(p.At, s.At) < s.Keep), $"{s} was free"));
            Assert.True(planted.Count < 100);
        }

        Assert.True(planted.Count > 5);
        Assert.Null(Next()); // full: nothing more, however often asked
        Assert.True(Vector2.Distance(planted[0].At, centre) < cell * 1.5f);

        PaintSpot? Next() => BrushScatter.Next(BrushScatter.Nearest(9, cell, centre, 600f, Least, Most, tried), tried, s =>
        {
            if (index.TooClose(s.At, 0f, s.Keep, PaintClass.Tree))
            {
                return false;
            }

            index.Add(new Vector3(s.At, 0f), PaintClass.Tree);
            planted.Add(s);
            return true;
        });
    }

    [Theory]
    [InlineData("Forest_Ground", 1f, true, PaintVerdict.Plant)]
    [InlineData("Asphalt", 1f, true, PaintVerdict.Road)]
    [InlineData("Asphalt_Rubble", 0.5f, true, PaintVerdict.Road)]
    [InlineData("Gravel_02", 0.4f, true, PaintVerdict.Road)]
    [InlineData("Crushed_Rock_01", 0.39f, true, PaintVerdict.Plant)]
    [InlineData("Mud_Tracks", 0.8f, true, PaintVerdict.Road)]
    [InlineData("Mud", 1f, true, PaintVerdict.Plant)]
    [InlineData("EraseFoliage", 0.5f, false, PaintVerdict.Road)]
    [InlineData("EraseFoliage", 0.49f, false, PaintVerdict.Plant)]
    [InlineData("Seabed", 1f, true, PaintVerdict.Water)]
    [InlineData("River_Pebbles", 0.6f, true, PaintVerdict.Water)]
    [InlineData("Beach_Sand", 1f, true, PaintVerdict.Water)]
    [InlineData("Cliff", 0.7f, true, PaintVerdict.Rock)]
    [InlineData("Coastal_Boulders", 0.45f, true, PaintVerdict.Rock)]
    public void TheGroundSaysWhereNotToPlant(string layer, float weight, bool blended, PaintVerdict expected)
    {
        var layers = new List<TerrainLayerSample> { new(layer, weight, blended) };
        if (blended && weight < 1f)
        {
            layers.Add(new TerrainLayerSample("Forest_Ground", 1f - weight, true));
        }

        Assert.Equal(expected, BrushScatter.GroundVerdict(layers, 100f, 0f));
    }

    [Fact]
    public void TheGroundSetsAddUpTheSeaWinsAndNoLayersPlant()
    {
        // Two road layers of 0.25 each reach the share together; the stronger set wins over a weaker one.
        Assert.Equal(PaintVerdict.Road, BrushScatter.GroundVerdict([new("Asphalt", 0.25f, true), new("Gravel", 0.25f, true), new("Forest_Ground", 0.5f, true)], 100f, 0f));
        Assert.Equal(PaintVerdict.Water, BrushScatter.GroundVerdict([new("Beach_Sand", 0.45f, true), new("Gravel", 0.41f, true)], 100f, 0f));
        Assert.Equal(PaintVerdict.Water, BrushScatter.GroundVerdict([new("Forest_Ground", 1f, true)], -1f, 0f)); // below the sea
        Assert.Equal(PaintVerdict.Plant, BrushScatter.GroundVerdict([new("Forest_Ground", 1f, true)], -1f, null)); // no sea
        Assert.Equal(PaintVerdict.Plant, BrushScatter.GroundVerdict([], 100f, 0f));
    }

    [Theory]
    [InlineData("/Game/ConZ_Files/Models/Road/SM_Road_Asphalt_01.SM_Road_Asphalt_01#spline12", PaintVerdict.Road)]
    [InlineData("/Game/ConZ_Files/Models/Landscape/SM_Border_01.SM_Border_01#spline3", PaintVerdict.Road)]
    [InlineData("/Game/ConZ_Files/Models/Airfield/SM_Runway_200m.SM_Runway_200m", PaintVerdict.Road)]
    [InlineData("/Game/ConZ_Files/Models/Military/SM_ConcreteFloor_10x10.SM_ConcreteFloor_10x10", PaintVerdict.Road)]
    [InlineData("/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1.LakeWaterSurface_C_1", PaintVerdict.Water)]
    [InlineData("/Game/ConZ_Files/Landscape/Rivers/SM_River_Piece.SM_River_Piece#spline1", PaintVerdict.Water)]
    [InlineData("/Game/ConZ_Files/Landscape/Rocks/Granite/SM_Rock_Granite_04.SM_Rock_Granite_04", PaintVerdict.Rock)]
    [InlineData("/Game/ConZ_Files/Models/Buildings/Houses/SM_House_01.SM_House_01", PaintVerdict.OnObject)]
    public void TheMeshUnderASpotSaysWhatItIs(string mesh, PaintVerdict expected) => Assert.Equal(expected, BrushScatter.SurfaceVerdict(mesh));

    [Fact]
    public void FoliageUnderASpotIsLeftToTheSpacing() =>
        Assert.Null(BrushScatter.SurfaceVerdict("/Game/ConZ_Files/Foliage/Trees/Pine/SM_Pine_01.SM_Pine_01"));

    [Fact]
    public void TheSpacingIsMeasuredAtTheFeet()
    {
        var index = new SpacingIndex(Most);
        index.Add(new Vector3(0f, 0f, 0f), PaintClass.Tree);
        index.Add(new Vector3(1000f, 0f, 0f), PaintClass.Bush);

        // The cursor rests on a canopy 25 m up, the candidate's foot is on the ground beside the tree: still too close.
        Assert.True(index.TooClose(new Vector2(150f, 0f), 0f, Most, PaintClass.Tree));
        Assert.False(index.TooClose(new Vector2(150f, 0f), 2500f, Most, PaintClass.Tree)); // a foot on a cliff top 25 m above
        Assert.False(index.TooClose(new Vector2(1150f, 0f), 0f, Most, PaintClass.Tree)); // a tree passes over undergrowth
        Assert.True(index.TooClose(new Vector2(1060f, 0f), 0f, Most, PaintClass.Tree)); // but never grows out of a bush
        Assert.True(index.TooClose(new Vector2(1150f, 0f), 0f, Most, PaintClass.Bush));
        Assert.True(index.TooClose(new Vector2(1150f, 0f), 0f, Most, PaintClass.Other));

        index.Rollback(1);
        Assert.Equal(1, index.Count);
        Assert.False(index.TooClose(new Vector2(1150f, 0f), 0f, Most, PaintClass.Bush));
    }

    [Fact]
    public void AWideSpacingHoldsUpAndDownASlope()
    {
        // The tester's case: 20 m spacing on a 60 % slope put two trees 17.7 m apart and 10.6 m apart in height.
        var index = new SpacingIndex(2000f);
        index.Add(new Vector3(0f, 0f, 0f), PaintClass.Tree);
        Assert.True(index.TooClose(new Vector2(1773f, 0f), -1062f, 2000f, PaintClass.Tree));
        Assert.True(index.TooClose(new Vector2(1500f, 0f), 2900f, 2000f, PaintClass.Tree)); // 63°: still in the window
        Assert.False(index.TooClose(new Vector2(150f, 0f), 2500f, Most, PaintClass.Tree)); // 3 m spacing: a cliff top 25 m up is not
        Assert.Equal(SpacingIndex.Reach, SpacingIndex.Window(Most));
        Assert.Equal(4000f, SpacingIndex.Window(2000f));
    }

    [Fact]
    public void WithNoLandscapeACanopyOrARoofUnderTheCursorIsNeverTheGround()
    {
        (float Bottom, float Top, float Foot) tree = (-50f, 3000f, 0f), roof = (-100f, 650f, -100f), bush = (0f, 120f, 0f);

        Assert.Equal(0f, BrushScatter.Plane(2500f, [tree])); // on the canopy: the tree's foot
        Assert.Equal(-100f, BrushScatter.Plane(600f, [roof])); // on a roof: the house's base
        Assert.Equal(0f, BrushScatter.Plane(2500f, [bush, tree]));
        Assert.Equal(20f, BrushScatter.Plane(20f, [tree])); // on the ground beside the trunk, under the crown
        Assert.Equal(2500f, BrushScatter.Plane(2500f, [bush])); // well above a bush: on something else
        Assert.Equal(-300f, BrushScatter.Plane(-300f, []));

        // With nothing to tell heights apart the spacing holds at any height.
        var index = new SpacingIndex(Most);
        index.Add(new Vector3(0f, 0f, 0f), PaintClass.Tree);
        Assert.False(index.TooClose(new Vector2(150f, 0f), 2500f, Most, PaintClass.Tree));
        Assert.True(index.TooClose(new Vector2(150f, 0f), 2500f, Most, PaintClass.Tree, float.PositiveInfinity));
    }

    /// <summary>One click on empty ground: the circle's spots in hash order, each planted when nothing is within its distance.</summary>
    private static List<PaintSpot> Sweep(ulong seed, Vector2 centre)
    {
        var index = new SpacingIndex(Most);
        var plants = new List<PaintSpot>();
        foreach (var spot in BrushScatter.Sweep(seed, BrushScatter.CellSize(Least, Radius), centre, centre, Radius, Least, Most))
        {
            Assert.True(Vector2.Distance(spot.At, centre) <= Radius);
            if (!index.TooClose(spot.At, 0f, spot.Keep, PaintClass.Tree))
            {
                index.Add(new Vector3(spot.At, 0f), PaintClass.Tree);
                plants.Add(spot);
            }
        }

        return plants;
    }
}
