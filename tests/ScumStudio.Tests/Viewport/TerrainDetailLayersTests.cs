using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// r37 (owner, 3.4.0: brown wrinkled patches with dark ragged rims near the coast): a coast component's four close-up layer
/// slots go to the layers painted on the land, not to the seabed that covers most of the component under water.
/// </summary>
public sealed class TerrainDetailLayersTests
{
    [Fact]
    public void TheLandsLayersTakeTheSlotsBeforeTheSeabed()
    {
        // 8x8 quads: rows 0-5 under water (seabed), rows 6-8 land: gravel, forest ground, grass, then a dry grass and a sand strip.
        const int quads = 8, n = quads + 1;
        var heights = new float[n * n];
        var seabed = new byte[n * n];
        var gravel = new byte[n * n];
        var forest = new byte[n * n];
        var grass = new byte[n * n];
        var dry = new byte[n * n];
        var sand = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = (y * n) + x;
                heights[i] = y < 6 ? -400f : 460f;
                if (y < 6)
                {
                    seabed[i] = 255;
                    continue;
                }

                (gravel[i], forest[i], grass[i], dry[i], sand[i]) = x switch
                {
                    < 3 => ((byte)255, (byte)0, (byte)0, (byte)0, (byte)0),
                    < 5 => ((byte)0, (byte)255, (byte)0, (byte)0, (byte)0),
                    < 7 => ((byte)0, (byte)0, (byte)255, (byte)0, (byte)0),
                    7 => ((byte)0, (byte)0, (byte)0, (byte)255, (byte)0),
                    _ => ((byte)0, (byte)0, (byte)0, (byte)0, (byte)255),
                };
            }
        }

        var layers = new LandscapeComponentLayers("C", (0, 0), quads,
            [L("Seabed", seabed), L("Gravel", gravel), L("Forest_Ground", forest), L("Grass_Continental", grass), L("Dry_Grass_01", dry), L("Beach_Sand", sand)],
            null, [], 6, []);
        var surface = new LandscapeSurface("C", (0, 0), quads, Matrix4x4.Identity, heights);

        var order = LevelSceneUploader.DetailLayers(layers, surface, seaLevelCm: 0f).Select(l => l.Name).ToList();
        var byCoverage = layers.Layers.OrderByDescending(l => l.Coverage).Take(4).Select(l => l.Name).ToList();

        Assert.Contains("Seabed", byCoverage); // what the slots held before: the dry grass patch got none
        Assert.DoesNotContain("Dry_Grass_01", byCoverage);
        Assert.Equal(["Gravel", "Forest_Ground", "Grass_Continental", "Dry_Grass_01"], order.Take(4));
        Assert.DoesNotContain("Seabed", order.Take(4));
        Assert.Equal(["Seabed", "Gravel"], LevelSceneUploader.DetailLayers(layers, null, 0f).Select(l => l.Name).Take(2)); // no heights: by coverage
    }

    /// <summary>
    /// r37 verifier (B_4 airfield): a dry grass patch (93% where it is painted, 0.8% of its component) stayed a flat brown smudge
    /// because beach sand mixed into the beach gravel weighed more and took the fourth slot. The slots go to the layers that
    /// leave the least ground without grain: the sand adds nothing where the gravel already has a slot, the patch adds all.
    /// </summary>
    [Fact]
    public void AFlatPatchTakesASlotBeforeALayerMixedIntoAnother()
    {
        // 8x8 quads, all land: gravel in columns 0-2, beach gravel 60% + beach sand 40% in 3-5, forest ground in 6-7, dry grass in 8.
        const int quads = 8, n = quads + 1;
        var heights = Enumerable.Repeat(460f, n * n).ToArray();
        var (gravel, beach, sand, forest, dry) = (new byte[n * n], new byte[n * n], new byte[n * n], new byte[n * n], new byte[n * n]);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = (y * n) + x;
                switch (x)
                {
                    case < 3: gravel[i] = 255; break;
                    case < 6: (beach[i], sand[i]) = (153, 102); break;
                    case < 8: forest[i] = 255; break;
                    default: dry[i] = 255; break;
                }
            }
        }

        var layers = new LandscapeComponentLayers("C", (0, 0), quads,
            [L("Gravel", gravel), L("Beach_Gravel", beach), L("Beach_Sand", sand), L("Forest_Ground", forest), L("Dry_Grass_01", dry)], null, [], 5, []);
        var surface = new LandscapeSurface("C", (0, 0), quads, Matrix4x4.Identity, heights);
        var byWeight = layers.Layers.OrderByDescending(l => l.Weights.Sum(w => w)).Take(4).Select(l => l.Name).ToList();

        var slots = LevelSceneUploader.DetailLayers(layers, surface, seaLevelCm: 0f).Take(LevelSceneUploader.DetailSlots).Select(l => l.Name).ToList();

        Assert.DoesNotContain("Dry_Grass_01", byWeight); // the order before: the patch drawn flat
        Assert.Equal(["Gravel", "Beach_Gravel", "Forest_Ground", "Dry_Grass_01"], slots);
        Assert.Equal(1f, LevelSceneUploader.GrainShare(0.6f));
        Assert.Equal(0f, LevelSceneUploader.GrainShare(0.1f));
        Assert.Equal(["Gravel", "Forest_Ground"], LevelSceneUploader.DetailLayers(layers, surface, 0f, l => l.Name is "Gravel" or "Forest_Ground").Take(2).Select(l => l.Name));
    }

    private static LandscapeLayerWeights L(string name, byte[] weights) => new(name, null, true, weights);
}
