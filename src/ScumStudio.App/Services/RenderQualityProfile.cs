using ScumStudio.Core.Settings;

namespace ScumStudio.App.Services;

/// <summary>What a <see cref="RenderQuality"/> preset sets for the Map's 3D view.</summary>
/// <param name="StreamRadiusCm">Whole-island mode loads every level whose bounds come this close to the camera.</param>
/// <param name="ObjectDistanceCm">Objects farther than this are not drawn (0 = as far as the game draws them).</param>
/// <param name="LodBias">Below 1 switches meshes to coarser LODs sooner.</param>
/// <param name="CullPixels">Objects smaller than this many pixels on screen are skipped.</param>
/// <param name="TextureSize">Largest mesh texture edge in pixels (colour and normal maps).</param>
public sealed record RenderQualityProfile(float StreamRadiusCm, float ObjectDistanceCm, float LodBias, float CullPixels, int TextureSize)
{
    /// <summary>The profile of <paramref name="quality"/>.</summary>
    public static RenderQualityProfile For(RenderQuality quality) => quality switch
    {
        RenderQuality.Performance => new(20_000f, 30_000f, 0.6f, 6f, 256),
        RenderQuality.High => new(60_000f, 150_000f, 1f, 3f, 1024),
        // 1024: at 2048 the ~2,200 textures and normal maps around the owner's B_4 outpost took 24-32 GB to read and prepare
        // and 16 GB of RAM, and the app froze (owner, 2026-10-09). ponytail: one size for every texture; 2048 only near the
        // camera needs a near/far split in LevelScenePreparer (sharper copies already replace the cached ones).
        RenderQuality.Ultra => new(100_000f, 0f, 1.25f, 2f, 1024),
        _ => new(30_000f, 60_000f, 0.8f, 4f, 512),
    };
}
