using System.Numerics;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport;

/// <summary>
/// Car paint as SCUM's <c>M_Car_01</c> shows it: where the colour mask is green the body has paint colour A, where it is
/// also blue colour B (the Kinglet's tan wings on its navy body, the Mariner's beige floats); the diffuse texture only adds
/// its scratches and grime to the paint (its brightness around its own mean), and bare metal, plastic and rubber keep it.
/// The game's icons show it that way: a plane whose body texture is 12 times darker than the Rager's still wears its paint
/// colours, not a black coat. The result's alpha is the paint coverage, so the preview's shine (metal, clear coat) stays
/// on the paint.
/// </summary>
public static class PaintBaker
{
    private const float DetailMin = 0.3f;
    private const float DetailMax = 1.6f;

    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();
    private static readonly byte[] ToSrgb = Enumerable.Range(0, 4096).Select(i => (byte)Math.Clamp(Math.Round(255 * LinearToSrgb(i / 4095f)), 0, 255)).ToArray();

    /// <summary>Paints <paramref name="diffuse"/> (sRGB) with linear colours through <paramref name="mask"/> (any size; sampled nearest).</summary>
    /// <param name="diffuse">The material's diffuse texture.</param>
    /// <param name="mask">The colour mask: green = paint, blue = paint in the second colour.</param>
    /// <param name="colourA">Main colour (linear).</param>
    /// <param name="colourB">Second colour (linear), or null to use the main colour there too.</param>
    public static TextureImage Bake(TextureImage diffuse, TextureImage mask, Vector4 colourA, Vector4? colourB = null)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(mask);
        var (w, h) = (diffuse.Width, diffuse.Height);
        var b = colourB ?? colourA;
        var src = diffuse.Rgba;
        var m = mask.Rgba;
        int MaskIndex(int x, int y) => ((Math.Min(mask.Height - 1, y * mask.Height / h) * mask.Width) + Math.Min(mask.Width - 1, x * mask.Width / w)) * 4;
        float Luminance(int i) => (0.2126f * ToLinear[src[i]]) + (0.7152f * ToLinear[src[i + 1]]) + (0.0722f * ToLinear[src[i + 2]]);

        // The diffuse's mean brightness under the paint: the paint keeps its colour there, scratches go darker, edges lighter.
        double sum = 0, weight = 0;
        for (var y = 0; y < h; y += 4)
        {
            for (var x = 0; x < w; x += 4)
            {
                var mi = MaskIndex(x, y);
                var cover = Math.Max(m[mi + 1], m[mi + 2]) / 255f;
                sum += cover * Luminance(((y * w) + x) * 4);
                weight += cover;
            }
        }

        var mean = weight > 0 ? Math.Max(1e-4f, (float)(sum / weight)) : 1f;
        var dst = new byte[src.Length];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var mi = MaskIndex(x, y);
                var cover = Math.Max(m[mi + 1], m[mi + 2]) / 255f;
                var tone = m[mi + 2] / 255f; // blue: the second colour
                var i = ((y * w) + x) * 4;
                var detail = Math.Clamp(Luminance(i) / mean, DetailMin, DetailMax);
                for (var c = 0; c < 3; c++)
                {
                    var paint = (colourA[c] + (tone * (b[c] - colourA[c]))) * detail;
                    var lit = ToLinear[src[i + c]] + (cover * (paint - ToLinear[src[i + c]]));
                    dst[i + c] = ToSrgb[Math.Clamp((int)(lit * 4095f), 0, 4095)];
                }

                dst[i + 3] = (byte)Math.Round(255 * cover);
            }
        });

        return diffuse with { Rgba = dst, IsSrgb = true };
    }

    private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float v) => v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
}
