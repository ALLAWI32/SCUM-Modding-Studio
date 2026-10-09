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

    /// <summary>Light on flat ground as the frame shows it: sun or moon, plus the sky, times the exposure.</summary>
    private static float Shown(RenderSettings s) =>
        (Luma(s.LightColor) * MathF.Max(0f, -Vector3.Normalize(s.LightDirection).Y) + Luma(s.SkyColor)) * s.Exposure;

    private static float Luma(Vector3 c) => (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
}
