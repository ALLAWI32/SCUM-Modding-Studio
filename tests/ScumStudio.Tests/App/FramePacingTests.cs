using System.Diagnostics;
using System.Globalization;
using ScumStudio.App.Services;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "camera motion is not smooth like the game". The Map is drawn when Avalonia's compositor ticks, 60 times a
/// second by a timer; these measure how evenly such a timer ticks on this PC without and with <see cref="FramePacing"/>.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class FramePacingTests(ITestOutputHelper output)
{
    private const double FrameMs = 1000.0 / 60.0;

    [Fact]
    public void TheCompositorTimerTicksEvenlyWhileTheViewFlies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the 15.6 ms system tick is a Windows matter
        }

        Assert.False(FramePacing.Held);
        var before = Measure("without FramePacing");
        FramePacing.Hold();
        Pacing after;
        try
        {
            Assert.True(FramePacing.Held, "Windows refused the 1 ms clock");
            after = Measure("with FramePacing.Hold");
        }
        finally
        {
            FramePacing.Release();
        }

        Assert.False(FramePacing.Held);
        if (!Ci.On)
        {
            // An even 60: about 16.7 ms a tick, and hardly a tick late by a whole frame.
            Assert.True(after.MeanMs is > 15.5 and < 18.0 && after.LateShare < 0.05, $"with the hold: {after}");
            Assert.True(before.LateShare > after.LateShare * 2 || Math.Abs(before.MeanMs - FrameMs) > 1.5, $"without the hold the ticks were already even: {before}");
        }
    }

    [Fact]
    public void HoldsNestAndTheLastReleaseGivesTheClockBack()
    {
        FramePacing.Hold();
        FramePacing.Hold();
        FramePacing.Release();
        Assert.Equal(OperatingSystem.IsWindows(), FramePacing.Held);
        FramePacing.Release();
        Assert.False(FramePacing.Held);
        FramePacing.Release(); // one too many: ignored
        Assert.False(FramePacing.Held);
    }

    /// <summary>Ticks of Avalonia's own render timer (the one Avalonia.Win32 binds), else of a 60 Hz threading timer, for 1.5 s.</summary>
    private Pacing Measure(string label)
    {
        var ticks = new List<double>();
        var clock = Stopwatch.StartNew();
        var source = "Avalonia DefaultRenderTimer(60)";
        try
        {
            // Avalonia keeps the timer out of its reference assemblies (private API): reached by reflection.
            var type = typeof(Avalonia.AvaloniaObject).Assembly.GetType("Avalonia.Rendering.DefaultRenderTimer", throwOnError: true)!;
            var timer = Activator.CreateInstance(type, 60)!;
            var tick = type.GetEvent("Tick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? type.GetInterfaces().Select(i => i.GetEvent("Tick")).First(e => e is not null)!;
            Action<TimeSpan> onTick = _ =>
            {
                lock (ticks)
                {
                    ticks.Add(clock.Elapsed.TotalMilliseconds);
                }
            };
            tick.AddEventHandler(timer, onTick);
            Thread.Sleep(1500);
            tick.RemoveEventHandler(timer, onTick);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or KeyNotFoundException or System.Reflection.TargetInvocationException or TypeLoadException or MissingMethodException)
        {
            source = $"System.Threading.Timer (DefaultRenderTimer: {ex.GetType().Name})";
            ticks.Clear();
            clock.Restart();
            using var timer = new Timer(_ =>
            {
                lock (ticks)
                {
                    ticks.Add(clock.Elapsed.TotalMilliseconds);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(FrameMs));
            Thread.Sleep(1500);
        }

        double[] intervals;
        lock (ticks)
        {
            intervals = [.. ticks.Zip(ticks.Skip(1), (a, b) => b - a).Skip(2)];
        }

        var pacing = new Pacing(source, intervals.Length, intervals.Length == 0 ? 0 : intervals.Average(),
            intervals.Length == 0 ? 0 : intervals.Count(i => i >= FrameMs * 1.5) / (double)intervals.Length,
            intervals.Length == 0 ? 0 : intervals.Select(i => Math.Abs(i - FrameMs)).Order().ElementAt((int)(intervals.Length * 0.95)));
        output.WriteLine($"{label}: {pacing}; first intervals: {string.Join(" ", intervals.Take(24).Select(i => i.ToString("0.0", CultureInfo.InvariantCulture)))}");
        return pacing;
    }

    private sealed record Pacing(string Source, int Ticks, double MeanMs, double LateShare, double P95OffMs)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Source}: {Ticks} intervals, mean {MeanMs:0.00} ms, {LateShare:P0} a frame or more late, p95 off by {P95OffMs:0.0} ms");
    }
}
