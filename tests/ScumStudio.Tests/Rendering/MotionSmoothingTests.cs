using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Owner: "camera motion is not smooth like the game (fly with WASD/mouse)". The pure parts of the Map's motion: the time
/// step of each frame (<see cref="FrameClock"/>), the mouse turn of each frame (<see cref="LookSmoother"/>), the cluster
/// LODs held near a threshold (<see cref="LodMath.ChooseLodSteady"/>) and the Smooth switch's back-off
/// (<see cref="FrameBudget"/>), each against the old behaviour on the same scripted input.
/// </summary>
public sealed class MotionSmoothingTests(ITestOutputHelper output)
{
    private const double Display = 1.0 / 60.0;

    [Fact]
    public void FramesShownEvenlyMoveTheCameraByEvenSteps()
    {
        // The screen shows a frame every 16.7 ms; each frame starts 0-6 ms late (the staging and page work before the clock
        // was read). Flying at a steady speed, the step from frame to frame should be the same every frame.
        var random = new Random(5);
        var starts = Enumerable.Range(0, 600).Select(k => (k * Display) + (random.NextDouble() * 0.006)).ToArray();
        var (raw, smooth) = (Steps(starts, smooth: false), Steps(starts, smooth: true));
        var (rawSpread, smoothSpread) = (Spread(raw), Spread(smooth));
        output.WriteLine($"step per frame at 60 Hz with 0-6 ms jitter: measured interval spread {rawSpread:P1}, smoothed {smoothSpread:P1}; distance {raw.Sum():0.000} s vs {smooth.Sum():0.000} s worth");
        Assert.True(rawSpread > 0.1, $"the raw steps were already even ({rawSpread:P1})");
        Assert.True(smoothSpread < rawSpread * 0.35, $"smoothed steps spread {smoothSpread:P1}, raw {rawSpread:P1}");
        Assert.True(Math.Abs(smooth.Sum() - raw.Sum()) < 0.25 * Display * 10, "the smoothed camera must still fly a second's worth every second");
    }

    [Fact]
    public void TheFirstFrameAfterARestDoesNotJumpAndAHitchIsSpread()
    {
        var clock = new FrameClock();
        Assert.Equal(0f, clock.Step(10.0)); // the first frame ever
        for (var k = 1; k <= 60; k++)
        {
            clock.Step(10.0 + (k * Display));
        }

        // Two seconds still, then a key: the old code moved the camera by 0.1 s worth in the first frame.
        var first = clock.Step(10.0 + (60 * Display) + 2.0);
        Assert.InRange(first, (float)Display * 0.9f, (float)Display * 1.1f);

        // A 50 ms hitch among 60 Hz frames: spread over the next frames, no single step of it, and none of it lost.
        var t = 20.0;
        clock.Reset();
        var steps = new List<float>();
        for (var k = 0; k < 120; k++)
        {
            t += k == 60 ? 0.05 : Display;
            steps.Add(clock.Step(t));
        }

        output.WriteLine($"around a 50 ms hitch: steps {string.Join(" ", steps.Skip(58).Take(8).Select(s => (s * 1000f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} ms");
        Assert.True(steps.Max() < 0.03f, $"a step of {steps.Max() * 1000f:0.0} ms");
        Assert.True(Math.Abs(steps.Sum() - (t - 20.0 - Display)) < 0.01, $"{steps.Sum():0.000} s given out of {t - 20.0 - Display:0.000} s");

        clock.Smooth = false;
        clock.Step(t);
        Assert.Equal(0.02f, clock.Step(t + 0.02), 4); // off: the measured interval
    }

    [Fact]
    public void MouseTurnsAreEvenPerFrameAndNothingIsLost()
    {
        // A 125 Hz mouse moved at an even 600 px a second reports whole pixels every 8 ms; at 60 frames a second a frame
        // gets one or two reports, so turned report by report the view turns by uneven amounts per frame.
        var reports = new List<(double At, float Dx)>();
        var (carry, sent) = (0.0, 0f);
        for (var k = 1; k * 0.008 < 3.0; k++)
        {
            carry += 600.0 * 0.008;
            var whole = (float)Math.Floor(carry);
            carry -= whole;
            reports.Add((k * 0.008, whole));
            sent += whole;
        }

        var random = new Random(9);
        var raw = Turns(new LookSmoother { Smooth = false });
        var smooth = Turns(new LookSmoother());
        var (rawSpread, smoothSpread) = (Spread(raw.Skip(20).Take(140)), Spread(smooth.Skip(20).Take(140)));
        output.WriteLine($"turn per frame: raw spread {rawSpread:P1}, smoothed {smoothSpread:P1}; turned {raw.Sum():0} and {smooth.Sum():0} of {sent:0} px");
        Assert.True(smoothSpread < rawSpread * 0.7, $"smoothed turns spread {smoothSpread:P1}, raw {rawSpread:P1}");
        Assert.Equal(sent, raw.Sum(), 1);
        Assert.Equal(sent, smooth.Sum(), 1);

        List<float> Turns(LookSmoother look)
        {
            var turns = new List<float>();
            var next = 0;
            var last = 0.0;
            for (var frame = 1; frame < 200; frame++)
            {
                var at = (frame * Display) + (random.NextDouble() * 0.004);
                for (; next < reports.Count && reports[next].At <= at; next++)
                {
                    look.Add(reports[next].Dx, 0f);
                }

                turns.Add(look.Take((float)(at - last)).X);
                last = at;
            }

            return turns;
        }
    }

    [Fact]
    public void AClusterHoveringAtALodThresholdKeepsItsLod()
    {
        // A camera hovering (or sliding past a forest) where a cluster's projected size wobbles 3 % around a threshold.
        float[] sizes = [1f, 0.5f, 0.25f, 0.1f];
        var (plain, steady) = (0, 0);
        var (lastPlain, lastSteady) = (-1, -1);
        for (var frame = 0; frame < 240; frame++)
        {
            var size = 0.5f * (1f + (0.03f * MathF.Sin(frame * 0.7f)));
            var p = LodMath.ChooseLod(sizes, size);
            var s = LodMath.ChooseLodSteady(sizes, size, lastSteady);
            (plain, steady) = (plain + (lastPlain >= 0 && p != lastPlain ? 1 : 0), steady + (lastSteady >= 0 && s != lastSteady ? 1 : 0));
            (lastPlain, lastSteady) = (p, s);
        }

        output.WriteLine($"LOD swaps over 240 frames at a threshold: {plain} plain, {steady} held");
        Assert.True(plain > 40, $"{plain}");
        Assert.Equal(0, steady);

        // Well past the threshold it switches as before, both ways.
        Assert.Equal(2, LodMath.ChooseLodSteady(sizes, 0.2f, 1));
        Assert.Equal(0, LodMath.ChooseLodSteady(sizes, 0.6f, 1));
        Assert.Equal(0, LodMath.ChooseLodSteady(sizes, 0.47f, 0)); // 6 % under the threshold: held
        Assert.Equal(1, LodMath.ChooseLodSteady(sizes, 0.47f, 2)); // two LODs off: no hold
        Assert.Equal(2, LodMath.ChooseLodSteady(sizes, 0.26f, 2)); // 4 % over the next threshold: held
    }

    [Fact]
    public void TheSmoothSwitchDoesNotSwapDetailEveryFewSeconds()
    {
        // A view that costs 20 ms at full detail and 11 ms one step down: lowered after half a second, raised two seconds
        // later, too slow again... the foliage distance went in and out every 2.5 s while flying.
        var budget = new FrameBudget();
        var (changes, raises) = (0, 0);
        for (var frame = 0; frame < 60 * 60; frame++)
        {
            var cost = budget.Level == 0 ? 20.0 : 11.0;
            var before = budget.Level;
            if (budget.Add(cost, double.NaN, frame * Display))
            {
                changes++;
                raises += budget.Level < before ? 1 : 0;
            }
        }

        output.WriteLine($"one minute at 20 ms / 11 ms: {changes} detail changes ({raises} raises)");
        Assert.True(changes <= 10, $"{changes} detail changes in a minute");
        Assert.True(raises >= 2, "it still tries to bring the detail back");
    }

    private static List<float> Steps(double[] starts, bool smooth)
    {
        var clock = new FrameClock { Smooth = smooth };
        return [.. starts.Select(clock.Step).Skip(30)];
    }

    /// <summary>Standard deviation over mean.</summary>
    private static double Spread(IEnumerable<float> values)
    {
        var list = values.Select(v => (double)v).ToList();
        var mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / list.Count) / mean;
    }
}
