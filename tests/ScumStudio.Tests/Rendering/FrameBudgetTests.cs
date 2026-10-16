using ScumStudio.Rendering;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// The Smooth switch's ladder (<see cref="FrameBudget"/>, owner: "high FPS AND far render distance"): what it lowers first
/// when frames take too long, when it brings it back, that it holds still around the target, and that it costs nothing a frame.
/// </summary>
public sealed class FrameBudgetTests
{
    private static readonly RenderSettings Ultra = new() { Shadows = true, ShadowDistance = 15_000f, CullPixelSize = 2f, LodBias = 1.25f };

    [Fact]
    public void OverloadLowersFoliageBeforeShadowsAndShadowsBeforeObjects()
    {
        var budget = new FrameBudget();
        double? foliage = null, lods = null, shadowDistance = null, shadowsOff = null, smallObjects = null, farView = null;
        for (var t = 0.0; t < 4.0; t += 0.030)
        {
            budget.Add(30.0, 28.0, t);
            var s = budget.Apply(Ultra);
            foliage ??= s.ViewDistanceScale < 1f ? t : null;
            lods ??= s.LodBias < Ultra.LodBias ? t : null;
            shadowDistance ??= s.ShadowDistance < Ultra.ShadowDistance ? t : null;
            shadowsOff ??= !s.Shadows ? t : null;
            smallObjects ??= s.CullPixelSize > Ultra.CullPixelSize ? t : null;
            farView ??= budget.RenderDistance(2) < 2 ? t : null;
        }

        Assert.True(foliage is not null && lods is not null && shadowDistance is not null && shadowsOff is not null && smallObjects is not null && farView is not null,
            $"after 4 s of 30 ms frames: foliage {foliage}, LODs {lods}, shadow distance {shadowDistance}, shadows off {shadowsOff}, small objects {smallObjects}, far view {farView}");
        Assert.True(foliage < shadowDistance, "foliage distance goes before the shadows");
        Assert.True(lods < shadowDistance);
        Assert.True(shadowDistance < shadowsOff);
        Assert.True(shadowsOff < smallObjects, "shadows go before small objects");
        Assert.True(smallObjects < farView, "the far view goes last");
        Assert.True(foliage >= 0.5, $"lowered after {foliage:0.00} s: one slow frame must not lower it");
        Assert.Equal(FrameBudget.MaxLevel, budget.Level);
    }

    [Fact]
    public void HeadroomRaisesBackAfterTwoSeconds()
    {
        var budget = new FrameBudget();
        var t = 0.0;
        for (; budget.Level < 1; t += 0.030)
        {
            budget.Add(30.0, 30.0, t);
            Assert.True(t < 2.0, "never lowered");
        }

        var start = t;
        double? back = null;
        for (; t < start + 5.0; t += 0.008)
        {
            budget.Add(8.0, 7.0, t);
            if (budget.Level == 0)
            {
                back = t - start;
                break;
            }
        }

        Assert.True(back is not null, "8 ms frames for 5 s did not bring the view back");
        Assert.InRange(back!.Value, 2.0, 2.5);
        Assert.Same(Ultra, budget.Apply(Ultra));
    }

    [Fact]
    public void NoOscillationAroundTarget()
    {
        var budget = new FrameBudget();
        var (changes, level) = (0, budget.Level);
        var i = 0;
        for (var t = 0.0; t < 10.0; t += 0.017, i++)
        {
            budget.Add(i % 2 == 0 ? 16.0 : 18.0, double.NaN, t);
            if (budget.Level != level)
            {
                (changes, level) = (changes + 1, budget.Level);
            }
        }

        Assert.True(changes <= 1, $"{changes} level changes in 10 s of 16/18 ms frames");
    }

    [Fact]
    public void ApplyAllocatesNothingInSteadyState()
    {
        var budget = new FrameBudget();
        var t = 0.0;
        for (; budget.Level < 2; t += 0.030)
        {
            budget.Add(30.0, 30.0, t);
            Assert.True(t < 3.0, "never lowered");
        }

        // Inside the band (neither over nor well under the target): the level holds and the lowered settings are reused.
        for (var i = 0; i < 10; i++, t += 0.016)
        {
            budget.Add(15.0, 15.0, t);
            _ = budget.Apply(Ultra);
        }

        var lowered = budget.Apply(Ultra);
        Assert.True(lowered.ViewDistanceScale < 1f && lowered.LodBias < Ultra.LodBias);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++, t += 0.016)
        {
            budget.Add(15.0, 15.0, t);
            lowered = budget.Apply(Ultra);
            _ = budget.RenderDistance(3);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(2, budget.Level);
        Assert.Same(lowered, budget.Apply(Ultra));
    }
}
