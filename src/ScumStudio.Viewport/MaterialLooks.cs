using System.Numerics;
using ScumStudio.Assets.Materials;

namespace ScumStudio.Viewport;

/// <summary>
/// How the 3D previews draw an inspected material: its tint, alpha clip and roughness range, by the Map's rules
/// (<c>LevelScenePreparer.FindMaterialTextures</c>), so an asset looks in its preview as it does placed on the island.
/// </summary>
internal static class MaterialLooks
{
    /// <summary>Mid grey for a surface without a texture or a colour of its own.</summary>
    internal static Vector4 Untextured => new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>River and lake water (linear RGBA, translucent): the Map's sea colour.</summary>
    internal static Vector4 Water => new(0.035f, 0.12f, 0.17f, 0.75f);

    /// <summary>
    /// The look of <paramref name="material"/> (null when it could not be inspected) at <paramref name="materialPath"/>,
    /// drawn with its base-colour texture when <paramref name="textured"/>. A textured opaque material takes only the
    /// masters' colour scale; a translucent one (glass) keeps its colour at alpha 0.4 and water takes the sea's colour,
    /// which the renderer draws see-through; a masked one (leaves) is cut out at its clip, without a roughness highlight.
    /// </summary>
    internal static (Vector4? Tint, float Clip, Vector2 Roughness) Of(MaterialInfo? material, string materialPath, bool textured)
    {
        if (material is null)
        {
            return (Untextured, 0f, Vector2.Zero);
        }

        var clip = material.OpacityMaskClip ?? 0f;
        var tint = material.SurfaceColor ?? Untextured;
        if (material.IsTranslucent)
        {
            tint = IsWater(materialPath) ? Water : tint with { W = 0.4f };
        }
        else if (textured)
        {
            tint = material.BaseColorScale is { } scale ? new Vector4(scale, 1f) : Vector4.One;
        }

        var roughness = textured && clip == 0f && !material.IsTranslucent && material.RoughnessRange is { } r ? r : Vector2.Zero;
        return (tint, clip, roughness);
    }

    private static bool IsWater(string materialPath)
    {
        var name = materialPath[(materialPath.LastIndexOf('/') + 1)..];
        return name.Contains("Water", StringComparison.OrdinalIgnoreCase) || name.Contains("River", StringComparison.OrdinalIgnoreCase);
    }
}
