using System.Numerics;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// How SCUM's car master <c>M_Car_01</c> reads a <c>Color Mask</c> texel: paint covers <c>max(green, blue)</c> of it and blue
/// picks colour B (<c>lerp(Base Color A, Base Color B, blue)</c>); red and alpha play no part. The stock masks rest on it:
/// the Kinglet's (T_Plane_01_Body_Mask_A) and the WolfsWagen's (T_WWBody_2_Color_UV2) are DXT1 with red 0 and alpha 255 on
/// every texel, green on 53 % and 65 % of them and blue (the Kinglet's tan) on 38 %, 3 % of it outside the green; the flat
/// white dummy paints everything, the flat black ones (police light bar, wheels, interiors) nothing.
/// </summary>
public static class PaintRule
{
    /// <summary>How much paint covers the texel at <paramref name="i"/> of an RGBA mask, 0..1.</summary>
    public static float Coverage(byte[] mask, int i) => Math.Max(mask[i + 1], mask[i + 2]) / 255f;

    /// <summary>How far the paint there is colour B rather than colour A, 0..1.</summary>
    public static float SelectB(byte[] mask, int i) => mask[i + 2] / 255f;

    /// <summary>The paint colour at the texel (linear).</summary>
    public static Vector3 Paint(byte[] mask, int i, Vector3 colourA, Vector3 colourB) => Vector3.Lerp(colourA, colourB, SelectB(mask, i));

    /// <summary>What the game shows at a texel (linear): the diffuse, painted as far as the mask covers it.</summary>
    public static Vector3 Shown(Vector3 diffuse, byte[] mask, int i, Vector3 colourA, Vector3 colourB) =>
        Vector3.Lerp(diffuse, Paint(mask, i, colourA, colourB), Coverage(mask, i));

    /// <summary>Takes the paint off a texel by <paramref name="alpha"/> (a decal on top): coverage becomes <c>c·(1 − alpha)</c>.</summary>
    public static void Uncover(byte[] mask, int i, float alpha)
    {
        var keep = 1f - Math.Clamp(alpha, 0f, 1f);
        mask[i + 1] = (byte)MathF.Round(mask[i + 1] * keep);
        mask[i + 2] = (byte)MathF.Round(mask[i + 2] * keep);
    }

    /// <summary>
    /// The diffuse to write under a decal of colour <paramref name="decal"/> and opacity <paramref name="alpha"/> on a texel the
    /// paint covered by <paramref name="coverage"/> (all linear), so that with the mask <see cref="Uncover">uncovered</see> the game
    /// shows <c>lerp(lerp(diffuse, paint, c), decal, alpha)</c> whatever colour the player paints.
    /// </summary>
    public static Vector3 DiffuseUnder(Vector3 diffuse, float coverage, Vector3 decal, float alpha)
    {
        var bare = 1f - coverage + (coverage * alpha);
        return bare <= 1e-6f ? diffuse : (((1f - alpha) * (1f - coverage) * diffuse) + (alpha * decal)) / bare;
    }
}
