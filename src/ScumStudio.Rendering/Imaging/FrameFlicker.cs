namespace ScumStudio.Rendering.Imaging;

/// <summary>
/// How much a sequence of frames flickers (owner: trees "shimmer" while the camera moves): over every three frames in a
/// row, a pixel that gets brighter and then darker again (or darker then brighter) counts the smaller of the two swings.
/// A slow, steady pan changes most pixels one way over three frames and counts little; leaves popping in and out of
/// pixels count every time. The result is the mean count per pixel and frame, in luminance (0..1).
/// </summary>
public static class FrameFlicker
{
    /// <summary>
    /// The flicker of <paramref name="frames"/> (RGBA8, <paramref name="width"/> x <paramref name="height"/>, same size) inside
    /// the rectangle <paramref name="x0"/>..<paramref name="x1"/> x <paramref name="y0"/>..<paramref name="y1"/> (end
    /// exclusive; the whole frame when left out). Fewer than three frames: 0.
    /// </summary>
    public static double Measure(IReadOnlyList<byte[]> frames, int width, int height, int x0 = 0, int y0 = 0, int x1 = -1, int y1 = -1)
    {
        ArgumentNullException.ThrowIfNull(frames);
        (x1, y1) = (x1 < 0 ? width : Math.Min(x1, width), y1 < 0 ? height : Math.Min(y1, height));
        (x0, y0) = (Math.Max(0, x0), Math.Max(0, y0));
        if (frames.Count < 3 || x1 <= x0 || y1 <= y0)
        {
            return 0;
        }

        var sum = 0.0;
        for (var t = 1; t < frames.Count - 1; t++)
        {
            var (a, b, c) = (frames[t - 1], frames[t], frames[t + 1]);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var o = ((y * width) + x) * 4;
                    var (la, lb, lc) = (Luminance(a, o), Luminance(b, o), Luminance(c, o));
                    var (d1, d2) = (lb - la, lc - lb);
                    if (d1 * d2 < 0)
                    {
                        sum += Math.Min(Math.Abs(d1), Math.Abs(d2));
                    }
                }
            }
        }

        return sum / ((double)(frames.Count - 2) * (x1 - x0) * (y1 - y0));
    }

    /// <summary>Rec. 709 luminance of the RGBA8 pixel at byte offset <paramref name="o"/> as stored (0..1).</summary>
    public static double Luminance(byte[] rgba, int o) =>
        ((0.2126 * rgba[o]) + (0.7152 * rgba[o + 1]) + (0.0722 * rgba[o + 2])) / 255.0;
}
