using System.Numerics;
using ScumStudio.Rendering;
using Xunit;

namespace ScumStudio.Tests.Rendering;

public sealed class TimeOfDayTests
{
    private static readonly RenderSettings Outdoor = RenderSettings.Outdoor;

    [Fact]
    public void HalfPastOne_IsTheIslandsOwnLook()
    {
        var noon = Outdoor.AtHour(13.5f);
        Assert.Equal(Outdoor.LightColor, noon.LightColor);
        Assert.Equal(Outdoor.SkyZenithColor, noon.SkyZenithColor);
        Assert.Equal(Outdoor.SkyHorizonColor, noon.SkyHorizonColor);
        Assert.Equal(Outdoor.SkyColor, noon.SkyColor);
        Assert.Equal(Outdoor.Exposure, noon.Exposure, 4);
        Assert.True(Vector3.Distance(RenderSettings.IslandSunDirection, noon.LightDirection) < 1e-4f);
        Assert.Equal(0f, noon.Stars);
    }

    /// <summary>SCUM's default clock: the sun rises at 06:00 and sets at 21:00.</summary>
    [Fact]
    public void TheSunRisesAndSetsOnTheGamesClock()
    {
        Assert.InRange(Outdoor.AtHour(6f).LightDirection.Y, -1e-3f, 1e-3f);
        Assert.InRange(Outdoor.AtHour(21f).LightDirection.Y, -1e-3f, 1e-3f);
        Assert.True(Outdoor.AtHour(20f).LightDirection.Y < -0.05f, "the sun is still up at 20:00");
        Assert.True(Outdoor.AtHour(5f).Stars > 0f, "it is still night at 05:00");
    }

    [Fact]
    public void Dusk_IsGolden_AndNight_IsTheGamesMoonlitNight()
    {
        var dusk = Outdoor.AtHour(20.6f);
        Assert.True(dusk.SkyHorizonColor.X > dusk.SkyHorizonColor.Z, $"dusk horizon {dusk.SkyHorizonColor} is not warm");
        Assert.True(dusk.LightColor.X > dusk.LightColor.Z * 1.8f, $"dusk sun {dusk.LightColor} is not golden");

        var night = Outdoor.AtHour(0f);
        Assert.True(night.LightDirection.Y < -0.3f, "the moon shines down from high in the sky");
        Assert.True(night.LightColor.Z > night.LightColor.X && night.LightColor.Length() < Outdoor.LightColor.Length() / 10f, $"moonlight {night.LightColor}");
        Assert.True(night.SkyZenithColor.Length() < Outdoor.SkyZenithColor.Length() / 10f, "a dark blue night sky");
        Assert.True(night.Exposure > 2f, "the game's auto exposure opens up at night");
        Assert.Equal(1f, night.Stars);
        Assert.Equal(night.LightDirection, Outdoor.AtHour(24f).LightDirection);

        // As in the game: a moonlit night shows the ground at about a tenth of the noon brightness
        Assert.InRange(Shown(night) / Shown(Outdoor), 0.05f, 0.15f);
    }

    /// <summary>
    /// Audit F427: from 21:00 to 21:13 (and 05:47 to 06:00) the sun below the horizon stayed the key light, shining
    /// upward, and at -2° the light snapped to the moon. Now no light comes from below and the light changes smoothly
    /// minute by minute; its direction only jumps (sun to moon) where both are dark.
    /// </summary>
    [Fact]
    public void NoLightFromBelowAndNoJumpAtDuskOrDawn()
    {
        foreach (var (from, to) in new[] { (20.8f, 21.5f), (5.5f, 6.2f) })
        {
            for (var step = 0; from + (step / 100f) <= to + 1e-4f; step++)
            {
                var h = from + (step / 100f);
                var (now, next) = (Outdoor.AtHour(h), Outdoor.AtHour(h + 0.01f));
                Assert.True(now.LightDirection.Y <= 0f, $"{h:0.00} h: the light {now.LightDirection} comes from below the horizon");
                var (luma, nextLuma) = (Luma(now.LightColor), Luma(next.LightColor));
                Assert.True(MathF.Abs(nextLuma - luma) < 0.02f, $"{h:0.00} h: the light jumps from {now.LightColor} to {next.LightColor}");
                var turn = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(now.LightDirection), Vector3.Normalize(next.LightDirection)), -1f, 1f)) * 180f / MathF.PI;
                Assert.True(turn <= 3f || MathF.Max(luma, nextLuma) < 1e-3f, $"{h:0.00} h: the light turns {turn:0.0}° at {now.LightColor}");
            }
        }
    }

    /// <summary>Light on flat ground as the frame shows it: sun or moon, plus the sky, times the exposure.</summary>
    private static float Shown(RenderSettings s) =>
        (Luma(s.LightColor) * MathF.Max(0f, -Vector3.Normalize(s.LightDirection).Y) + Luma(s.SkyColor)) * s.Exposure;

    private static float Luma(Vector3 c) => (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
}
