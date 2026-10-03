using ScumStudio.Core.Settings;

namespace ScumStudio.App.Services;

/// <summary>What a <see cref="RenderQuality"/> preset sets for the Map's 3D view.</summary>
/// <param name="StreamRadiusCm">Whole-island mode loads every level whose bounds come this close to the camera.</param>
/// <param name="ObjectDistanceCm">Objects farther than this are not drawn (0 = as far as the game draws them).</param>
/// <param name="LodBias">Below 1 switches meshes to coarser LODs sooner.</param>
/// <param name="CullPixels">Objects smaller than this many pixels on screen are skipped.</param>
/// <param name="TextureSize">Largest mesh texture edge in pixels.</param>
public sealed record RenderQualityProfile(float StreamRadiusCm, float ObjectDistanceCm, float LodBias, float CullPixels, int TextureSize)
{
    /// <summary>The profile of <paramref name="quality"/>.</summary>
    public static RenderQualityProfile For(RenderQuality quality) => quality switch
    {
        RenderQuality.Performance => new(20_000f, 30_000f, 0.6f, 6f, 256),
        RenderQuality.High => new(60_000f, 150_000f, 1f, 3f, 1024),
        RenderQuality.Ultra => new(100_000f, 0f, 1.25f, 2f, 1024),
        _ => new(30_000f, 60_000f, 0.8f, 4f, 512),
    };
}
