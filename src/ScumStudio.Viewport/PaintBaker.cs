using System.Numerics;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport;

/// <summary>
/// Car paint as SCUM's <c>M_Car_01</c> lays it on: the diffuse texture, times paint colour A where the colour mask's green
/// channel is set and colour B where its red channel is (two-tone bodies); bare metal, plastic and rubber keep the diffuse.
/// The result's alpha is the paint coverage, so the preview's shine (metal, clear coat) stays on the paint.
/// </summary>
public static class PaintBaker
{
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();
    private static readonly byte[] ToSrgb = Enumerable.Range(0, 4096).Select(i => (byte)Math.Clamp(Math.Round(255 * LinearToSrgb(i / 4095f)), 0, 255)).ToArray();

    /// <summary>Paints <paramref name="diffuse"/> (sRGB) with linear colours through <paramref name="mask"/> (any size; sampled nearest).</summary>
    public static TextureImage Bake(TextureImage diffuse, TextureImage mask, Vector4 colourA, Vector4? colourB = null)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(mask);
        var (w, h) = (diffuse.Width, diffuse.Height);
        var b = colourB ?? Vector4.One;
        var src = diffuse.Rgba;
        var m = mask.Rgba;
        var dst = new byte[src.Length];
        Parallel.For(0, h, y =>
        {
            var my = Math.Min(mask.Height - 1, y * mask.Height / h);
            for (var x = 0; x < w; x++)
            {
                var mx = Math.Min(mask.Width - 1, x * mask.Width / w);
                var mi = ((my * mask.Width) + mx) * 4;
                var ga = m[mi + 1] / 255f; // colour A
                var rb = colourB is null ? 0f : m[mi] / 255f; // colour B
                var i = ((y * w) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var paint = (1f + (ga * (colourA[c] - 1f))) * (1f + (rb * (b[c] - 1f)));
                    dst[i + c] = ToSrgb[Math.Clamp((int)(ToLinear[src[i + c]] * paint * 4095f), 0, 4095)];
                }

                dst[i + 3] = (byte)Math.Round(255 * Math.Min(1f, ga + rb));
            }
        });

        return diffuse with { Rgba = dst, IsSrgb = true };
    }

    private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float v) => v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
}
