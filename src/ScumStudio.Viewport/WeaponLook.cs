using System.Numerics;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport;

/// <summary>
/// How a SCUM gun (<c>M_Weapons_Master</c>) is drawn in the Weapons studio, from its own packed maps instead of the
/// colour texture alone (owner: "weapon textures look broken in 3D"). The master reads three textures: <c>Color</c>
/// (RGB colour, alpha roughness between <c>Roughness Min</c> and <c>Max</c>), <c>Normal</c> and <c>AOMEC</c> (R ambient
/// occlusion, G metalness, B emissive, A curvature). The preview shader has one texture per part whose alpha drives both
/// the roughness and the metal, so the look texture carries the colour with its occlusion and the metal mask in alpha,
/// and the roughness range runs from the bare parts' mean roughness (wood, polymer) to the metal's.
/// </summary>
/// <param name="Image">The look texture: colour × occlusion (sRGB), alpha = metalness.</param>
/// <param name="Roughness">Roughness where the alpha is 0 (x: wood, polymer) and 1 (y: metal).</param>
/// <param name="Metal">How metallic the metal is (<c>MetallicAmount</c>), times the alpha.</param>
public sealed record WeaponLook(TextureImage Image, Vector2 Roughness, float Metal)
{
    /// <summary>How much of the texture is metal, 0..1 (the AKM receiver's mostly, its wood furniture's hardly).</summary>
    public float MetalShare { get; init; }

    /// <summary>The texture parameter of the packed occlusion / metalness / emissive / curvature map.</summary>
    public const string PackedMapParameter = "AOMEC";

    /// <summary>Linear value of each sRGB byte.</summary>
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        var v = i / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    /// <summary>
    /// The look of a gun material: <paramref name="colour"/> (its <c>Color</c> texture, RGBA) darkened by the occlusion of
    /// <paramref name="packed"/> (its <c>AOMEC</c>, any size: sampled at the same UV), alpha = the packed metalness; the
    /// colour's alpha (roughness) is scaled into <paramref name="roughnessMin"/>..<paramref name="roughnessMax"/> and
    /// averaged over the metal and over the rest. <paramref name="flat"/> draws the even finish: the game's flat white
    /// colour (its alpha fully rough) instead of the printed one, the occlusion and metal kept.
    /// </summary>
    public static WeaponLook Bake(TextureImage colour, TextureImage packed, float roughnessMin, float roughnessMax, float metallic, bool flat = false)
    {
        ArgumentNullException.ThrowIfNull(colour);
        ArgumentNullException.ThrowIfNull(packed);
        int w = colour.Width, h = colour.Height;
        var rgba = new byte[w * h * 4];
        double roughMetal = 0, roughBare = 0, metalWeight = 0, bareWeight = 0;
        for (var y = 0; y < h; y++)
        {
            var py = Math.Min(packed.Height - 1, (int)((y + 0.5f) * packed.Height / h));
            for (var x = 0; x < w; x++)
            {
                var px = Math.Min(packed.Width - 1, (int)((x + 0.5f) * packed.Width / w));
                var p = ((py * packed.Width) + px) * 4;
                var c = ((y * w) + x) * 4;
                var ao = packed.Rgba[p] / 255f;
                var metal = packed.Rgba[p + 1];
                for (var k = 0; k < 3; k++)
                {
                    var linear = (flat ? 1f : ToLinear[colour.Rgba[c + k]]) * ao;
                    rgba[c + k] = ToSrgbByte(linear);
                }

                rgba[c + 3] = metal;
                var rough = flat ? 1f : colour.Rgba[c + 3] / 255f;
                var m = metal / 255.0;
                roughMetal += rough * m;
                metalWeight += m;
                roughBare += rough * (1 - m);
                bareWeight += 1 - m;
            }
        }

        float Range(double sum, double weight) => roughnessMin + ((roughnessMax - roughnessMin) * (float)(weight > 0 ? sum / weight : 0.5));
        var image = colour with { Name = colour.Name + (flat ? "#flatlook" : "#look"), Rgba = rgba, PixelFormat = "PF_R8G8B8A8", IsSrgb = true, IsNormalMap = false, CompressedMips = null };
        return new WeaponLook(image, new Vector2(Range(roughBare, bareWeight), Range(roughMetal, metalWeight)), Math.Clamp(metallic, 0f, 1f))
        {
            MetalShare = (float)(metalWeight / Math.Max(1, w * h)),
        };
    }

    private static byte ToSrgbByte(float linear)
    {
        var v = linear <= 0.0031308f ? 12.92f * linear : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }
}
