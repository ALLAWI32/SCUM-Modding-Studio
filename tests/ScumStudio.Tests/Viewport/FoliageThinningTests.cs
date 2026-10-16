using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Grass and bushes thin out between the game's <c>InstanceStartCullDistance</c> and <c>InstanceEndCullDistance</c>, as UE
/// fades HISM instances: past the start a growing share of them is not drawn, past the end none is.
/// </summary>
public sealed class FoliageThinningTests
{
    private const string Grass = "FoliageInstancedStaticMeshComponent_3";

    private static ActorInstance Instance(int index, int start, int end) =>
        new(1, Grass, index, "/Game/Foliage/SM_Grass.SM_Grass", FTransform.Identity, FTransform.Identity) { StartCullDistance = start, EndCullDistance = end };

    private static float[] Distances(int count, int start, int end) =>
        Enumerable.Range(0, count).Select(i => FoliageThinning.CullDistanceOf(Instance(i, start, end))).ToArray();

    [Fact]
    public void InstancesSpreadEvenlyOverFourStepsBetweenStartAndEnd()
    {
        var distances = Distances(10_000, 2_000, 6_000);

        foreach (var step in new[] { 3_000f, 4_000f, 5_000f, 6_000f })
        {
            Assert.InRange(distances.Count(d => d == step), 2_200, 2_800); // 25% ± 3%
        }

        Assert.Equal(10_000, distances.Count(d => d is 3_000f or 4_000f or 5_000f or 6_000f));
    }

    [Fact]
    public void TheShareDrawnFallsWithDistanceAsInTheGame()
    {
        var distances = Distances(10_000, 2_000, 6_000);
        double Drawn(float at) => distances.Count(d => at <= d) / 10_000.0; // LodMath culls beyond the draw distance

        Assert.Equal(1.0, Drawn(3_000f));
        Assert.InRange(Drawn(4_500f), 0.47, 0.53);
        Assert.InRange(Drawn(5_900f), 0.22, 0.28);
        Assert.Equal(0.0, Drawn(6_001f));
    }

    [Fact]
    public void WithoutAStartDistanceEveryInstanceGoesAsFarAsTheEnd()
    {
        Assert.All(Distances(1_000, 0, 6_000), d => Assert.Equal(6_000f, d));
        Assert.All(Distances(100, 6_000, 6_000), d => Assert.Equal(6_000f, d)); // nothing to fade over
        Assert.All(Distances(100, 2_000, 0), d => Assert.Equal(0f, d)); // never culled stays never culled
    }

    [Fact]
    public void TheStepOfAnInstanceIsTheSameInEveryRun()
    {
        // Fixed values: string.GetHashCode changes from one run to the next, the thinning must not (a bush would blink
        // in and out each time the level streams in again).
        Assert.Equal([5_000f, 6_000f, 6_000f, 6_000f, 3_000f, 4_000f, 4_000f, 4_000f], Distances(8, 2_000, 6_000));
        Assert.Equal(FoliageThinning.CullDistanceOf(Instance(4, 2_000, 6_000)),
            FoliageThinning.CullDistanceOf(Instance(4, 2_000, 6_000) with { ComponentName = Grass.ToUpperInvariant() }));
    }

    [Fact]
    public void CollectPlacementsThinsTheInstancesOfAFoliageComponent()
    {
        var instances = Enumerable.Range(0, 400).Select(i => new FTransform(new FVector(i * 100f, 0f, 0f))).ToList();
        var data = new LevelData
        {
            PackagePath = "/Game/Maps/Landscape_Z_9_1",
            ActorIndices = [0],
            Exports =
            [
                new LevelExportData { Index = 0, Name = "InstancedFoliageActor_0", ClassName = "InstancedFoliageActor", IsLoaded = true, RootComponent = 1 },
                new LevelExportData
                {
                    Index = 1, Name = Grass, ClassName = "FoliageInstancedStaticMeshComponent", OuterIndex = 0, IsLoaded = true, IsComponent = true,
                    IsSceneComponent = true, IsStaticMeshComponent = true, StaticMesh = "/Game/Foliage/SM_Grass.SM_Grass", Instances = instances,
                    InstanceStartCullDistance = 2_000, InstanceEndCullDistance = 6_000,
                },
            ],
        };

        var placements = LevelScenePreparer.CollectPlacements(LevelDocument.FromData(data), 0);

        Assert.Equal(400, placements.Count);
        Assert.Equal(4, placements.Select(p => p.CullDistance).Distinct().Count());
        Assert.Equal(3_000f, placements.Min(p => p.CullDistance));
        Assert.Equal(6_000f, placements.Max(p => p.CullDistance));
        Assert.All(placements, p => Assert.Equal(FoliageThinning.CullDistanceOf(p.Instance!), p.CullDistance));
    }
}
