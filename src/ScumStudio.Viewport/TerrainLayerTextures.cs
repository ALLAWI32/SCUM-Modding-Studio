using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport;

/// <summary>
/// The layer diffuse textures (<c>T_*_D</c>) a <see cref="TerrainLayerCatalog"/> names, decoded once per load into
/// linear-RGB mip chains so <see cref="TerrainAlbedoBaker"/> can tile them in world space without aliasing: the bake
/// samples the mip whose texel density matches the baked texture. Immutable and thread-safe.
/// </summary>
public sealed class TerrainLayerTextures
{
    private readonly Dictionary<string, LinearMipChain> _chains;

    /// <summary>Builds the mip chains of <paramref name="images"/> (keyed by the catalog's texture path).</summary>
    public TerrainLayerTextures(IReadOnlyDictionary<string, TextureImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        Images = images;
        _chains = new Dictionary<string, LinearMipChain>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, image) in images)
        {
            _chains[path] = new LinearMipChain(image);
        }
    }

    /// <summary>The decoded images by texture path.</summary>
    public IReadOnlyDictionary<string, TextureImage> Images { get; }

    /// <summary>Number of textures.</summary>
    public int Count => _chains.Count;

    /// <summary>The mip chain of the texture at <paramref name="path"/>, or null when it was not loaded.</summary>
    public LinearMipChain? Find(string? path) => path is not null && _chains.TryGetValue(path, out var chain) ? chain : null;
}

/// <summary>A texture as linear-RGB mips (box-filtered down to 1×1) with bilinear, wrapping sampling.</summary>
public sealed class LinearMipChain
{
    private static readonly float[] SrgbLut = Enumerable.Range(0, 256).Select(v => TerrainColor.SrgbToLinear((byte)v)).ToArray();

    private readonly Vector3[][] _levels;
    private readonly int[] _widths;
    private readonly int[] _heights;

    /// <summary>Converts <paramref name="image"/> to linear RGB and builds its mips.</summary>
    public LinearMipChain(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var w = image.Width;
        var h = image.Height;
        var level0 = new Vector3[w * h];
        for (var i = 0; i < level0.Length; i++)
        {
            var o = i * 4;
            level0[i] = image.IsSrgb
                ? new Vector3(SrgbLut[image.Rgba[o]], SrgbLut[image.Rgba[o + 1]], SrgbLut[image.Rgba[o + 2]])
                : new Vector3(image.Rgba[o], image.Rgba[o + 1], image.Rgba[o + 2]) / 255f;
        }

        var levels = new List<Vector3[]> { level0 };
        var widths = new List<int> { w };
        var heights = new List<int> { h };
        while (w > 1 || h > 1)
        {
            var nw = Math.Max(1, w / 2);
            var nh = Math.Max(1, h / 2);
            var src = levels[^1];
            var dst = new Vector3[nw * nh];
            for (var y = 0; y < nh; y++)
            {
                var y0 = Math.Min(y * 2, h - 1);
                var y1 = Math.Min(y * 2 + 1, h - 1);
                for (var x = 0; x < nw; x++)
                {
                    var x0 = Math.Min(x * 2, w - 1);
                    var x1 = Math.Min(x * 2 + 1, w - 1);
                    dst[y * nw + x] = (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]) * 0.25f;
                }
            }

            levels.Add(dst);
            widths.Add(nw);
            heights.Add(nh);
            w = nw;
            h = nh;
        }

        _levels = [.. levels];
        _widths = [.. widths];
        _heights = [.. heights];
    }

    /// <summary>Number of mip levels (level 0 = the decoded image).</summary>
    public int LevelCount => _levels.Length;

    /// <summary>Mean colour (the 1×1 mip, linear RGB).</summary>
    public Vector3 Mean => _levels[^1][0];

    /// <summary>
    /// The mip whose texel density matches <paramref name="texelsPerRepeat"/> output texels per texture repeat (the
    /// nearest level in log2, so a repeat that covers 16 output texels samples the 16-texel mip).
    /// </summary>
    public int LevelFor(float texelsPerRepeat)
    {
        if (!(texelsPerRepeat > 0f))
        {
            return _levels.Length - 1;
        }

        var lod = MathF.Log2(_widths[0] / texelsPerRepeat);
        return Math.Clamp((int)MathF.Round(lod), 0, _levels.Length - 1);
    }

    /// <summary>Bilinear sample at (<paramref name="u"/>, <paramref name="v"/>) in texture repeats (wrapping) of mip <paramref name="level"/>.</summary>
    public Vector3 Sample(float u, float v, int level)
    {
        level = Math.Clamp(level, 0, _levels.Length - 1);
        var w = _widths[level];
        var h = _heights[level];
        var px = _levels[level];
        var x = u * w - 0.5f;
        var y = v * h - 0.5f;
        var xf = MathF.Floor(x);
        var yf = MathF.Floor(y);
        var fx = x - xf;
        var fy = y - yf;
        var x0 = Wrap((int)xf, w);
        var x1 = Wrap((int)xf + 1, w);
        var y0 = Wrap((int)yf, h);
        var y1 = Wrap((int)yf + 1, h);
        var top = Vector3.Lerp(px[y0 * w + x0], px[y0 * w + x1], fx);
        var bottom = Vector3.Lerp(px[y1 * w + x0], px[y1 * w + x1], fx);
        return Vector3.Lerp(top, bottom, fy);
    }

    private static int Wrap(int i, int n)
    {
        var m = i % n;
        return m < 0 ? m + n : m;
    }
}
