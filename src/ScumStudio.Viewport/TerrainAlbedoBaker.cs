using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Landscape;

namespace ScumStudio.Viewport;

/// <summary>How terrain is coloured in a level scene.</summary>
public enum GroundMode
{
    /// <summary>
    /// The game look: per-component baked colour from the cooked paint layers. The layer textures (<c>T_*_D</c>) are
    /// tiled in world space and blended by the weightmaps when they are in the game files (their mean colour, or the
    /// catalog's fallback palette, otherwise), plus rock on steep slopes, wet shore and underwater tint.
    /// </summary>
    Realistic,

    /// <summary>Debug view: a distinct colour per paint layer, blended by weight (see <see cref="TerrainLayerCatalog"/>).</summary>
    Layers,

    /// <summary>Height colour ramp (blue under the sea, green → brown → grey → white with altitude).</summary>
    Height,

    /// <summary>The flat tint used before ground colours existed (no texture).</summary>
    Plain,
}

/// <summary>Settings of <see cref="TerrainAlbedoBaker"/>.</summary>
public sealed record TerrainBakeSettings
{
    /// <summary>
    /// Baked texture size per component when layer textures are tiled: 1024² is 37 cm per texel on SCUM's 150 cm quads
    /// and about 5.3 MB per component on the GPU with mips (340 MB for a 64-component cell).
    /// </summary>
    public const int DefaultTexturedSize = 1024;

    /// <summary>Ground mode to bake (<see cref="GroundMode.Plain"/> bakes nothing).</summary>
    public GroundMode Mode { get; init; } = GroundMode.Realistic;

    /// <summary>Layer look table.</summary>
    public TerrainLayerCatalog Catalog { get; init; } = TerrainLayerCatalog.Default;

    /// <summary>
    /// The layer diffuse textures to tile in world space (<see cref="GroundMode.Realistic"/>; decoded once per load).
    /// Null, or a layer whose texture is not in the set, uses the catalog colour instead.
    /// </summary>
    public TerrainLayerTextures? Textures { get; init; }

    /// <summary>
    /// Edge length of the baked texture per component; 0 (default) = <see cref="DefaultTexturedSize"/> when textures are
    /// tiled, otherwise one texel per height sample (256 for SCUM's 254-quad components).
    /// </summary>
    public int TextureSize { get; init; }

    /// <summary>
    /// Anti-tiling: how much of a second, 7.3× larger repeat of the same texture is mixed into each tiled sample
    /// (0 = none, 1 = only the large repeat). Breaks the visible grid of repeats from the air.
    /// </summary>
    public float MacroVariation { get; init; } = 0.4f;

    /// <summary>Bottom of the <see cref="GroundMode.Height"/> ramp (UE centimetres).</summary>
    public float HeightMinCm { get; init; } = -8000f;

    /// <summary>Top of the <see cref="GroundMode.Height"/> ramp (UE centimetres).</summary>
    public float HeightMaxCm { get; init; } = 30000f;

    /// <summary>Darken the wet shore band and tint ground below sea level (Realistic mode).</summary>
    public bool ShoreTint { get; init; } = true;
}

/// <summary>A baked terrain texture: <c>Size × Size</c> sRGB RGBA8 pixels covering the component (see <see cref="LandscapeExtractor.TextureCoordinate"/>).</summary>
/// <param name="Size">Edge length in texels.</param>
/// <param name="Rgba">Pixels, row-major, first row = the component's first sample row. Alpha is 0 on holes.</param>
public sealed record TerrainAlbedo(int Size, byte[] Rgba);

/// <summary>
/// Bakes per-component ground colour textures from the decoded paint layers (CPU, thread-safe, no GL). The texture lines up
/// with the terrain mesh UVs (<see cref="LandscapeExtractor.TextureCoordinate"/>) and is uploaded with clamp-to-edge
/// wrapping, so neighbouring components meet without seams. With <see cref="TerrainBakeSettings.Textures"/> every texel
/// samples each painted layer's texture at its world position (tiling from the catalog, at the mip that matches the
/// baked texel size) and blends them by weight; without, each layer is its catalog colour.
/// </summary>
public static class TerrainAlbedoBaker
{
    /// <summary>Scale of the anti-tiling macro repeat relative to the layer tiling (1/7.3: 44 m for a 6 m tiling).</summary>
    private const float MacroScale = 1f / 7.3f;

    /// <summary>Bakes one component; null for <see cref="GroundMode.Plain"/>.</summary>
    public static TerrainAlbedo? Bake(LandscapeSurface surface, LandscapeComponentLayers? layers, TerrainBakeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        settings ??= new TerrainBakeSettings();
        if (settings.Mode == GroundMode.Plain)
        {
            return null;
        }

        var n = surface.SampleCount;
        var sizeQuads = surface.ComponentSizeQuads;
        var baseSize = LandscapeExtractor.AlbedoTextureSize(sizeQuads);
        var realistic = settings.Mode == GroundMode.Realistic;
        var textures = realistic ? settings.Textures : null;
        var size = settings.TextureSize > 0 ? settings.TextureSize : textures is { Count: > 0 } ? TerrainBakeSettings.DefaultTexturedSize : baseSize;
        var catalog = settings.Catalog;
        var rules = catalog.Rules;
        if (layers is not null && layers.SampleCount != n)
        {
            layers = null; // a mismatched grid cannot be sampled safely
        }

        // The mesh UV of quad q is (q + 0.5) / baseSize, so texel t of a size² texture sits at quad (t + 0.5) * baseSize / size - 0.5
        // (one texel = one sample at the base size; a quarter of a quad at 1024² for 254-quad components).
        var quadsPerTexel = (float)baseSize / size;
        var texelCm = surface.QuadSizeCm * quadsPerTexel;

        // Resolve every layer's style and colour source once.
        var styles = (layers?.Layers ?? [])
            .Select(l => (Layer: l, Style: catalog.Resolve(l.Name)))
            .Where(e => e.Layer.WeightBlended && e.Style.Rule != TerrainLayerRule.Hidden)
            .Select(e => new LayerEntry(e.Layer, Tap.For(e.Style, settings.Mode == GroundMode.Layers ? e.Style.DebugColor : e.Style.Color, textures, texelCm, settings.MacroVariation),
                realistic && e.Style.Rule == TerrainLayerRule.SlopeHeight))
            .ToArray();
        var autoStyle = catalog.Resolve("Default_Slope_Height");
        var auto = Tap.For(autoStyle, autoStyle.Color, textures, texelCm, settings.MacroVariation);
        var rock = Tap.For(rules.Rock, rules.Rock.Color, textures, texelCm, settings.MacroVariation);
        var sand = Tap.For(rules.Sand, rules.Sand.Color, textures, texelCm, settings.MacroVariation);
        var underwater = Tap.For(rules.Underwater, rules.Underwater.Color, textures, texelCm, settings.MacroVariation);

        // Normal Z per sample once (decoding the packed normal is the slow part); bilinear between samples below.
        var normalZ = new float[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                normalZ[y * n + x] = surface.NormalAt(x, y).Z;
            }
        }

        var m = surface.ToWorld;
        var rgba = new byte[size * size * 4];
        for (var ty = 0; ty < size; ty++)
        {
            var qy = Math.Clamp((ty + 0.5f) * quadsPerTexel - 0.5f, 0f, sizeQuads);
            var y0 = Math.Min((int)qy, sizeQuads - 1);
            var fy = qy - y0;
            for (var tx = 0; tx < size; tx++)
            {
                var qx = Math.Clamp((tx + 0.5f) * quadsPerTexel - 0.5f, 0f, sizeQuads);
                var x0 = Math.Min((int)qx, sizeQuads - 1);
                var fx = qx - x0;
                var z = surface.SampleHeight(qx, qy);
                Vector3 color;
                if (settings.Mode == GroundMode.Height)
                {
                    color = HeightRamp(z, settings.HeightMinCm, settings.HeightMaxCm, rules.SeaLevelCm);
                }
                else
                {
                    var nz = Bilinear(normalZ, n, x0, y0, fx, fy);
                    var wx = m.M41 + qx * m.M11 + qy * m.M21;
                    var wy = m.M42 + qx * m.M12 + qy * m.M22;
                    // The rock/sand/underwater textures are sampled only where a rule needs them (most texels need none).
                    Vector3? rockColor = null;
                    Vector3? sandColor = null;
                    Vector3? underwaterColor = null;
                    var sum = Vector3.Zero;
                    var weightSum = 0f;
                    foreach (var entry in styles)
                    {
                        var w = Bilinear(entry.Layer.Weights, n, x0, y0, fx, fy);
                        if (w <= 0f)
                        {
                            continue;
                        }

                        var c = entry.Tap.Sample(wx, wy);
                        if (entry.Auto)
                        {
                            c = AutoLayerColor(z, nz, c, rules, rockColor ??= rock.Sample(wx, wy), sandColor ??= sand.Sample(wx, wy), underwaterColor ??= underwater.Sample(wx, wy));
                        }

                        sum += w * c;
                        weightSum += w;
                    }

                    if (weightSum > 0f)
                    {
                        color = sum / weightSum;
                    }
                    else if (settings.Mode == GroundMode.Layers)
                    {
                        color = new Vector3(0.02f);
                    }
                    else
                    {
                        // No paint at all: behave like the automatic layer.
                        color = AutoLayerColor(z, nz, auto.Sample(wx, wy), rules, rockColor ??= rock.Sample(wx, wy), sandColor ??= sand.Sample(wx, wy), underwaterColor ??= underwater.Sample(wx, wy));
                    }

                    if (realistic)
                    {
                        var rockWeight = Math.Clamp((rules.RockSlopeStartNz - nz) / MathF.Max(rules.RockSlopeRangeNz, 1e-3f), 0f, 1f);
                        if (rockWeight > 0f)
                        {
                            color = Vector3.Lerp(color, rockColor ?? rock.Sample(wx, wy), rockWeight);
                        }

                        if (settings.ShoreTint)
                        {
                            color = ShoreTint(color, z - rules.SeaLevelCm);
                        }
                    }
                }

                var srgb = TerrainColor.FromLinear(color);
                var o = (ty * size + tx) * 4;
                rgba[o] = srgb.R;
                rgba[o + 1] = srgb.G;
                rgba[o + 2] = srgb.B;
                rgba[o + 3] = layers?.IsHole((int)MathF.Round(qx), (int)MathF.Round(qy)) == true ? (byte)0 : (byte)255;
            }
        }

        return new TerrainAlbedo(size, rgba);
    }

    /// <summary>
    /// Bakes many components in parallel (<paramref name="maxDegreeOfParallelism"/> ≤ 0 uses all cores); the result is
    /// index-aligned with <paramref name="components"/>. <paramref name="elapsed"/> is the wall time.
    /// </summary>
    public static IReadOnlyList<TerrainAlbedo?> BakeAll(
        IReadOnlyList<(LandscapeSurface Surface, LandscapeComponentLayers? Layers)> components,
        TerrainBakeSettings settings,
        out TimeSpan elapsed,
        int maxDegreeOfParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(settings);
        var clock = Stopwatch.StartNew();
        var result = new TerrainAlbedo?[components.Count];
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
        };
        Parallel.For(0, components.Count, options, i => result[i] = Bake(components[i].Surface, components[i].Layers, settings));
        elapsed = clock.Elapsed;
        return result;
    }

    /// <summary>
    /// Colour of the automatic layer (<see cref="TerrainLayerRule.SlopeHeight"/>, linear RGB): underwater rock below
    /// <see cref="TerrainAutoRules.UnderwaterBelowCm"/> (blending into sand up to <see cref="TerrainAutoRules.BeachMinCm"/>),
    /// sand up to <see cref="TerrainAutoRules.BeachMaxCm"/> above sea level, rock on slopes steeper
    /// than <see cref="TerrainAutoRules.SlopeLayerRockStartNz"/>, otherwise <paramref name="flat"/>; transitions are smooth.
    /// </summary>
    public static Vector3 AutoLayerColor(float zCm, float normalZ, Vector3 flat, TerrainAutoRules rules, Vector3 rock, Vector3 sand, Vector3 underwater)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var rel = zCm - rules.SeaLevelCm;
        // Sand band: full inside [BeachMin, BeachMax], fading out over 1 m above it.
        var sandWeight = 1f - SmoothStep(rules.BeachMaxCm, rules.BeachMaxCm + 100f, rel);
        var color = Vector3.Lerp(flat, sand, sandWeight);
        // Underwater rock below UnderwaterBelowCm, blending into the sand up to BeachMinCm.
        var underwaterWeight = 1f - SmoothStep(rules.UnderwaterBelowCm, MathF.Max(rules.BeachMinCm, rules.UnderwaterBelowCm + 1f), rel);
        color = Vector3.Lerp(color, underwater, underwaterWeight);
        var rockWeight = SmoothStep(0f, 0.08f, rules.SlopeLayerRockStartNz - normalZ);
        return Vector3.Lerp(color, rock, rockWeight);
    }

    /// <summary>
    /// Wet shore and underwater tint (linear RGB): ground from 0.5 m below to 0.4 m above sea level is darkened like wet
    /// sand, and ground below the sea gets darker and blue-green with depth (fully at 25 m).
    /// </summary>
    public static Vector3 ShoreTint(Vector3 color, float heightAboveSeaCm)
    {
        if (heightAboveSeaCm < 40f)
        {
            var wet = SmoothStep(40f, 0f, heightAboveSeaCm) * 0.28f;
            color *= 1f - wet;
        }

        if (heightAboveSeaCm < 0f)
        {
            var depth = Math.Clamp(-heightAboveSeaCm / 2500f, 0f, 1f);
            color = Vector3.Lerp(color, new Vector3(0.035f, 0.075f, 0.08f), depth * 0.55f);
        }

        return color;
    }

    /// <summary>Hypsometric ramp (linear RGB) of a height between <paramref name="minCm"/> and <paramref name="maxCm"/>.</summary>
    public static Vector3 HeightRamp(float zCm, float minCm, float maxCm, float seaLevelCm = 0f)
    {
        if (zCm < seaLevelCm)
        {
            var depth = Math.Clamp((seaLevelCm - zCm) / MathF.Max(seaLevelCm - minCm, 1f), 0f, 1f);
            return Vector3.Lerp(Srgb(110, 170, 210), Srgb(20, 50, 110), depth);
        }

        var t = Math.Clamp((zCm - seaLevelCm) / MathF.Max(maxCm - seaLevelCm, 1f), 0f, 1f);
        for (var s = 1; s < RampStops.Length; s++)
        {
            if (t <= RampStops[s])
            {
                return Vector3.Lerp(RampColors[s - 1], RampColors[s], (t - RampStops[s - 1]) / (RampStops[s] - RampStops[s - 1]));
            }
        }

        return RampColors[^1];
    }

    private static readonly float[] RampStops = [0f, 0.04f, 0.25f, 0.55f, 0.8f, 1f];

    private static readonly Vector3[] RampColors =
    [
        Srgb(200, 190, 140), Srgb(110, 160, 80), Srgb(60, 120, 50), Srgb(150, 120, 70), Srgb(140, 130, 120), Srgb(245, 245, 245),
    ];

    private static Vector3 Srgb(byte r, byte g, byte b) => new TerrainColor(r, g, b).ToLinear();

    /// <summary>Bilinear value of a per-sample grid (<paramref name="x0"/>, <paramref name="y0"/> ≤ n − 2) between four samples.</summary>
    private static float Bilinear(float[] grid, int n, int x0, int y0, float fx, float fy)
    {
        var i = y0 * n + x0;
        return (grid[i] * (1f - fx) + grid[i + 1] * fx) * (1f - fy) + (grid[i + n] * (1f - fx) + grid[i + n + 1] * fx) * fy;
    }

    /// <summary>Bilinear weight (0..1) of a per-sample byte grid.</summary>
    private static float Bilinear(byte[] grid, int n, int x0, int y0, float fx, float fy)
    {
        var i = y0 * n + x0;
        return ((grid[i] * (1f - fx) + grid[i + 1] * fx) * (1f - fy) + (grid[i + n] * (1f - fx) + grid[i + n + 1] * fx) * fy) / 255f;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A painted layer to blend: its weights, colour source and whether the slope/height rule applies.</summary>
    private sealed record LayerEntry(LandscapeLayerWeights Layer, Tap Tap, bool Auto);

    /// <summary>
    /// Where a style's colour comes from at a world position: its texture tiled in world space (the mip matching the
    /// baked texel size, mixed with a 7.3× larger repeat against visible tiling) or a flat colour.
    /// </summary>
    private readonly struct Tap
    {
        private readonly Vector3 _flat;
        private readonly LinearMipChain? _chain;
        private readonly float _invTilingCm;
        private readonly int _level;
        private readonly int _macroLevel;
        private readonly float _macro;

        private Tap(Vector3 flat, LinearMipChain? chain, float invTilingCm, int level, int macroLevel, float macro)
        {
            _flat = flat;
            _chain = chain;
            _invTilingCm = invTilingCm;
            _level = level;
            _macroLevel = macroLevel;
            _macro = macro;
        }

        public static Tap For(TerrainLayerStyle style, TerrainColor color, TerrainLayerTextures? textures, float texelCm, float macroVariation)
        {
            var chain = textures?.Find(style.DiffuseTexture);
            if (chain is null || !(style.TilingCm > 0f) || !(texelCm > 0f))
            {
                return new Tap(color.ToLinear(), null, 0f, 0, 0, 0f);
            }

            var texelsPerRepeat = style.TilingCm / texelCm;
            return new Tap(color.ToLinear(), chain, 1f / style.TilingCm, chain.LevelFor(texelsPerRepeat), chain.LevelFor(texelsPerRepeat / MacroScale),
                Math.Clamp(macroVariation, 0f, 1f));
        }

        public Vector3 Sample(float worldX, float worldY)
        {
            if (_chain is null)
            {
                return _flat;
            }

            var u = worldX * _invTilingCm;
            var v = worldY * _invTilingCm;
            var detail = _chain.Sample(u, v, _level);
            if (_macro <= 0f)
            {
                return detail;
            }

            var macro = _chain.Sample(u * MacroScale + 0.37f, v * MacroScale + 0.71f, _macroLevel);
            return Vector3.Lerp(detail, macro, _macro);
        }
    }
}
