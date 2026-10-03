using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Settings;

namespace ScumStudio.Assets.Landscape;

/// <summary>An 8-bit sRGB colour.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct TerrainColor(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c> (the <c>#</c> is optional).</summary>
    public static TerrainColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"'{text}' is not a #RRGGBB colour.");
        }

        return new TerrainColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Linear RGB (0..1) of this sRGB colour.</summary>
    public Vector3 ToLinear() => new(SrgbToLinear(R), SrgbToLinear(G), SrgbToLinear(B));

    /// <summary>The sRGB colour of a linear RGB value (clamped to 0..1).</summary>
    public static TerrainColor FromLinear(Vector3 linear) => new(LinearToSrgb(linear.X), LinearToSrgb(linear.Y), LinearToSrgb(linear.Z));

    /// <summary>sRGB byte → linear.</summary>
    public static float SrgbToLinear(byte value)
    {
        var c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Linear → sRGB byte (rounded, clamped).</summary>
    public static byte LinearToSrgb(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
    }

    /// <inheritdoc />
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>How a terrain layer is coloured.</summary>
public enum TerrainLayerRule
{
    /// <summary>Its own colour/texture, blended by weight.</summary>
    None,

    /// <summary>
    /// An automatic layer (SCUM's <c>Default_Slope_Height</c>): rock on steep slopes, sand near sea level, underwater rock
    /// below it, otherwise the layer's own colour.
    /// </summary>
    SlopeHeight,

    /// <summary>Never drawn (foliage masks such as <c>EraseFoliage</c>, the <c>DataLayer</c> hole layer).</summary>
    Hidden,
}

/// <summary>Where a layer's colour comes from.</summary>
public enum TerrainColorSource
{
    /// <summary>The fallback colour of the catalog.</summary>
    Fallback,

    /// <summary>The mean colour of the layer's diffuse texture, found in the game files.</summary>
    Texture,

    /// <summary>A colour derived from the layer name (layer missing from the catalog).</summary>
    Hashed,
}

/// <summary>The look of one terrain paint layer.</summary>
/// <param name="Name">Layer name.</param>
/// <param name="DiffuseTexture">Object path of the layer's diffuse (<c>T_*_D</c>) texture, or null.</param>
/// <param name="NhrTexture">Object path of the layer's normal/height/roughness (<c>T_*_NHR</c>) texture, or null.</param>
/// <param name="TilingCm">World size of one texture repeat in centimetres.</param>
/// <param name="Color">sRGB colour used for the baked ground (fallback or texture mean, see <paramref name="ColorSource"/>).</param>
/// <param name="DebugColor">Distinct sRGB colour for the Layers view.</param>
/// <param name="Rule">Colouring rule.</param>
/// <param name="ColorSource">Where <paramref name="Color"/> comes from.</param>
public sealed record TerrainLayerStyle(
    string Name,
    string? DiffuseTexture,
    string? NhrTexture,
    float TilingCm,
    TerrainColor Color,
    TerrainColor DebugColor,
    TerrainLayerRule Rule,
    TerrainColorSource ColorSource)
{
    /// <summary>False for layers that are not in the catalog (they get a hashed colour).</summary>
    public bool IsKnown => ColorSource != TerrainColorSource.Hashed;
}

/// <summary>Slope/height rules shared by the automatic layer and the global rock blend.</summary>
/// <param name="SeaLevelCm">World Z of the sea surface (UE centimetres).</param>
/// <param name="RockSlopeStartNz">Normal Z below which rock starts to blend in over every layer (0.82 ≈ 35°).</param>
/// <param name="RockSlopeRangeNz">Normal Z range over which rock goes from 0 to 100 %.</param>
/// <param name="SlopeLayerRockStartNz">Normal Z below which the automatic layer turns to rock (0.86 ≈ 31°).</param>
/// <param name="BeachMinCm">Lowest height (relative to sea level) of the automatic layer's sand band.</param>
/// <param name="BeachMaxCm">Highest height (relative to sea level) of the sand band.</param>
/// <param name="UnderwaterBelowCm">Height (relative to sea level) below which the automatic layer is underwater rock.</param>
/// <param name="Rock">Rock style.</param>
/// <param name="Sand">Sand style.</param>
/// <param name="Underwater">Underwater rock style.</param>
public sealed record TerrainAutoRules(
    float SeaLevelCm,
    float RockSlopeStartNz,
    float RockSlopeRangeNz,
    float SlopeLayerRockStartNz,
    float BeachMinCm,
    float BeachMaxCm,
    float UnderwaterBelowCm,
    TerrainLayerStyle Rock,
    TerrainLayerStyle Sand,
    TerrainLayerStyle Underwater);

/// <summary>The layer diffuse textures found in the game files (see <see cref="TerrainLayerCatalog.LoadTextures"/>), decoded once.</summary>
/// <param name="Images">Decoded textures by the catalog's texture path.</param>
/// <param name="Missing">Texture paths named by the catalog that are not in the game files (sorted).</param>
/// <param name="Warnings">Textures that exist but could not be decoded (path and reason).</param>
public sealed record TerrainLayerTextureSet(IReadOnlyDictionary<string, TextureImage> Images, IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings)
{
    /// <summary>An empty set (no game files).</summary>
    public static TerrainLayerTextureSet Empty { get; } = new(new Dictionary<string, TextureImage>(), [], []);
}

/// <summary>
/// Maps landscape paint layer names to their look: diffuse/NHR texture paths, tiling, a fallback colour and a rule.
/// <para>Cooked tiles only store layer weights; which texture a layer uses lives in the stripped landscape material, so the
/// table is a heuristic shipped as an embedded <c>terrain-layers.json</c>, which users can override with a file of the same
/// shape at <see cref="UserOverridePath"/>. When a layer's diffuse texture is in the game files (<see cref="LoadTextures"/>),
/// <see cref="WithTextureColors(TerrainLayerTextureSet)"/> replaces its fallback colour by the texture's mean colour. Unknown layers get a stable
/// colour hashed from the name and are listed in <see cref="UnknownLayers"/>.</para>
/// </summary>
public sealed class TerrainLayerCatalog
{
    /// <summary>Logical name of the embedded default table.</summary>
    public const string ResourceName = "ScumStudio.Assets.Landscape.terrain-layers.json";

    /// <summary>File name of the user override in the studio home folder.</summary>
    public const string OverrideFileName = "terrain-layers.json";

    private static readonly Lazy<TerrainLayerCatalog> DefaultCatalog = new(() => Parse(ReadEmbeddedJson()));

    private readonly Dictionary<string, TerrainLayerStyle> _layers;
    private readonly List<(string Prefix, TerrainLayerStyle Style)> _wildcards;
    private readonly ConcurrentDictionary<string, byte> _unknown = new(StringComparer.OrdinalIgnoreCase);

    private TerrainLayerCatalog(Dictionary<string, TerrainLayerStyle> layers, List<(string, TerrainLayerStyle)> wildcards, TerrainAutoRules rules,
        string textureRoot, float defaultTilingCm, IReadOnlyList<string> warnings)
    {
        _layers = layers;
        _wildcards = wildcards;
        Rules = rules;
        TextureRoot = textureRoot;
        DefaultTilingCm = defaultTilingCm;
        Warnings = warnings;
    }

    /// <summary>The embedded default table.</summary>
    public static TerrainLayerCatalog Default => DefaultCatalog.Value;

    /// <summary>Path of the user override file (<c>&lt;StudioHome&gt;/terrain-layers.json</c>).</summary>
    public static string UserOverridePath => Path.Combine(StudioHome.GetDirectory(), OverrideFileName);

    /// <summary>Slope/height rules and the rock/sand/underwater styles.</summary>
    public TerrainAutoRules Rules { get; }

    /// <summary>Folder that relative texture names are resolved against.</summary>
    public string TextureRoot { get; }

    /// <summary>Tiling used when an entry has none.</summary>
    public float DefaultTilingCm { get; }

    /// <summary>Known layers by name.</summary>
    public IReadOnlyDictionary<string, TerrainLayerStyle> Layers => _layers;

    /// <summary>Problems found while loading overrides or texture colours.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Layer names resolved so far that are not in the catalog (sorted).</summary>
    public IReadOnlyList<string> UnknownLayers => _unknown.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The embedded <c>terrain-layers.json</c> text.</summary>
    public static string ReadEmbeddedJson()
    {
        using var stream = typeof(TerrainLayerCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The default table with the user override applied when the file exists (<paramref name="overridePath"/>, default
    /// <see cref="UserOverridePath"/>). A broken override is ignored and reported in <see cref="Warnings"/>.
    /// </summary>
    public static TerrainLayerCatalog LoadDefault(string? overridePath = null)
    {
        string path;
        try
        {
            path = overridePath ?? UserOverridePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Default;
        }

        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            return Default.WithOverrides(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
        {
            return Default.CloneWith(warnings: [$"{path}: {ex.Message} (override ignored)"]);
        }
    }

    /// <summary>Parses a complete table.</summary>
    public static TerrainLayerCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var empty = new TerrainLayerCatalog(new Dictionary<string, TerrainLayerStyle>(StringComparer.OrdinalIgnoreCase), [],
            new TerrainAutoRules(0f, 0.82f, 0.12f, 0.86f, -100f, 200f, -150f,
                Fallback("Rock", new TerrainColor(116, 110, 102)), Fallback("Sand", new TerrainColor(188, 170, 130)), Fallback("Underwater", new TerrainColor(108, 100, 80))),
            "/Game/ConZ_Files/Landscape/LandscapeTextures/", 600f, []);
        return empty.WithOverrides(json);

        static TerrainLayerStyle Fallback(string name, TerrainColor color) =>
            new(name, null, null, 600f, color, color, TerrainLayerRule.None, TerrainColorSource.Fallback);
    }

    /// <summary>
    /// Returns a copy with the entries of <paramref name="json"/> applied: layers are matched by name and only the fields
    /// present are replaced (new names are added); <c>autoRules</c>, <c>textureRoot</c> and <c>defaultTilingCm</c> likewise.
    /// A name ending in <c>*</c> matches every layer starting with the text before it.
    /// </summary>
    public TerrainLayerCatalog WithOverrides(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The terrain layer table must be a JSON object.");
        }

        var textureRoot = root.TryGetProperty("textureRoot", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString()! : TextureRoot;
        if (!textureRoot.EndsWith('/'))
        {
            textureRoot += "/";
        }

        var tiling = root.TryGetProperty("defaultTilingCm", out var dt) && dt.TryGetSingle(out var dtv) && dtv > 0 ? dtv : DefaultTilingCm;
        var layers = new Dictionary<string, TerrainLayerStyle>(_layers, StringComparer.OrdinalIgnoreCase);
        var wildcards = new List<(string Prefix, TerrainLayerStyle Style)>(_wildcards);
        if (root.TryGetProperty("layers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { Length: > 0 } name)
                {
                    throw new InvalidDataException("Every layer entry needs a \"name\".");
                }

                if (name.EndsWith('*'))
                {
                    var prefix = name[..^1];
                    var index = wildcards.FindIndex(w => string.Equals(w.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
                    var basis = index >= 0 ? wildcards[index].Style : null;
                    var style = ReadStyle(entry, name, basis, textureRoot, tiling);
                    if (index >= 0)
                    {
                        wildcards[index] = (prefix, style);
                    }
                    else
                    {
                        wildcards.Add((prefix, style));
                    }
                }
                else
                {
                    layers[name] = ReadStyle(entry, name, layers.GetValueOrDefault(name), textureRoot, tiling);
                }
            }
        }

        var rules = Rules;
        if (root.TryGetProperty("autoRules", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            rules = new TerrainAutoRules(
                Float(r, "seaLevelCm", rules.SeaLevelCm),
                Float(r, "rockSlopeStartNz", rules.RockSlopeStartNz),
                Float(r, "rockSlopeRangeNz", rules.RockSlopeRangeNz),
                Float(r, "slopeLayerRockStartNz", rules.SlopeLayerRockStartNz),
                Float(r, "beachMinCm", rules.BeachMinCm),
                Float(r, "beachMaxCm", rules.BeachMaxCm),
                Float(r, "underwaterBelowCm", rules.UnderwaterBelowCm),
                r.TryGetProperty("rock", out var rock) ? ReadStyle(rock, "Rock", rules.Rock, textureRoot, tiling) : rules.Rock,
                r.TryGetProperty("sand", out var sand) ? ReadStyle(sand, "Sand", rules.Sand, textureRoot, tiling) : rules.Sand,
                r.TryGetProperty("underwater", out var under) ? ReadStyle(under, "Underwater", rules.Underwater, textureRoot, tiling) : rules.Underwater);
        }

        return new TerrainLayerCatalog(layers, wildcards, rules, textureRoot, tiling, Warnings);

        static float Float(JsonElement e, string name, float fallback) =>
            e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : fallback;
    }

    /// <summary>Returns a copy with different slope/height rules (e.g. another sea level).</summary>
    public TerrainLayerCatalog WithRules(TerrainAutoRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new TerrainLayerCatalog(new Dictionary<string, TerrainLayerStyle>(_layers, StringComparer.OrdinalIgnoreCase), [.. _wildcards], rules, TextureRoot,
            DefaultTilingCm, Warnings);
    }

    /// <summary>
    /// The style of layer <paramref name="name"/>: an exact entry, else the longest matching wildcard, else a hashed
    /// neutral colour (the name is then reported in <see cref="UnknownLayers"/>). Thread-safe.
    /// </summary>
    public TerrainLayerStyle Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_layers.TryGetValue(name, out var style))
        {
            return style;
        }

        var best = _wildcards.Where(w => name.StartsWith(w.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(w => w.Prefix.Length).FirstOrDefault();
        if (best.Style is { } wildcard)
        {
            return wildcard with { Name = name };
        }

        _unknown.TryAdd(name, 0);
        return new TerrainLayerStyle(name, null, null, DefaultTilingCm, HashedColor(name, vivid: false), HashedColor(name, vivid: true), TerrainLayerRule.None,
            TerrainColorSource.Hashed);
    }

    /// <summary>
    /// A stable colour derived from <paramref name="name"/> (FNV-1a of the lower-case name → hue): muted earth-like for
    /// the ground, saturated for the Layers view.
    /// </summary>
    public static TerrainColor HashedColor(string name, bool vivid)
    {
        ArgumentNullException.ThrowIfNull(name);
        var hash = 2166136261u;
        foreach (var c in name.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        var hue = (hash % 360u) / 360f;
        var (s, v) = vivid ? (0.75f, 0.9f) : (0.25f, 0.45f);
        return FromHsv(hue, s, v);
    }

    /// <summary>
    /// Returns a copy whose layers (and rock/sand/underwater styles) use the mean colour of their diffuse texture when that
    /// texture is in <paramref name="catalog"/>; <paramref name="missingTextures"/> lists the texture paths that were not
    /// found. Textures are decoded at a small mip (<paramref name="maxSize"/>). To also tile the textures, keep the
    /// decoded set from <see cref="LoadTextures"/> and pass it to <see cref="WithTextureColors(TerrainLayerTextureSet)"/>.
    /// </summary>
    public TerrainLayerCatalog WithTextureColors(AssetCatalog catalog, out IReadOnlyList<string> missingTextures, int maxSize = 64)
    {
        var textures = LoadTextures(catalog, maxSize);
        missingTextures = textures.Missing;
        return WithTextureColors(textures);
    }

    /// <summary>Returns a copy whose layers (and rock/sand/underwater styles) use the mean colour of their diffuse texture when it is in <paramref name="textures"/>.</summary>
    public TerrainLayerCatalog WithTextureColors(TerrainLayerTextureSet textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        var means = textures.Images.ToDictionary(kv => kv.Key, kv => MeanColor(kv.Value), StringComparer.OrdinalIgnoreCase);
        TerrainLayerStyle Apply(TerrainLayerStyle style) =>
            style.DiffuseTexture is { } path && style.Rule != TerrainLayerRule.Hidden && means.TryGetValue(path, out var mean)
                ? style with { Color = mean, ColorSource = TerrainColorSource.Texture }
                : style;

        var layers = _layers.ToDictionary(kv => kv.Key, kv => Apply(kv.Value), StringComparer.OrdinalIgnoreCase);
        var wildcards = _wildcards.Select(w => (w.Prefix, Apply(w.Style))).ToList();
        var rules = Rules with { Rock = Apply(Rules.Rock), Sand = Apply(Rules.Sand), Underwater = Apply(Rules.Underwater) };
        return new TerrainLayerCatalog(layers, wildcards, rules, TextureRoot, DefaultTilingCm, [.. Warnings, .. textures.Warnings]);
    }

    /// <summary>The distinct diffuse texture paths of every drawn layer, wildcard and rock/sand/underwater style (sorted).</summary>
    public IReadOnlyList<string> DiffuseTexturePaths =>
        _layers.Values.Concat(_wildcards.Select(w => w.Style)).Append(Rules.Rock).Append(Rules.Sand).Append(Rules.Underwater)
            .Where(s => s.Rule != TerrainLayerRule.Hidden && s.DiffuseTexture is not null)
            .Select(s => s.DiffuseTexture!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Decodes every texture of <see cref="DiffuseTexturePaths"/> that is in <paramref name="catalog"/>, once each, at the
    /// mip of at most <paramref name="maxSize"/> pixels; textures that are not in the game files are listed in
    /// <see cref="TerrainLayerTextureSet.Missing"/>, ones that fail to decode in <see cref="TerrainLayerTextureSet.Warnings"/>.
    /// </summary>
    public TerrainLayerTextureSet LoadTextures(AssetCatalog catalog, int maxSize = 256)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var images = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var warnings = new List<string>();
        foreach (var path in DiffuseTexturePaths)
        {
            var packagePath = path.Contains('.') ? path[..path.IndexOf('.')] : path;
            if (!catalog.PackageExists(packagePath))
            {
                missing.Add(path);
                continue;
            }

            try
            {
                var objectPath = path.Contains('.') ? path : $"{path}.{path[(path.LastIndexOf('/') + 1)..]}";
                images[path] = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(objectPath), maxSize);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                warnings.Add($"{path}: {ex.Message}");
            }
        }

        return new TerrainLayerTextureSet(images, missing, warnings);
    }

    /// <summary>Mean colour of a decoded texture, averaged in linear space (alpha ignored).</summary>
    public static TerrainColor MeanColor(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.Width * image.Height;
        if (pixels == 0)
        {
            return default;
        }

        var sum = Vector3.Zero;
        for (var i = 0; i < pixels; i++)
        {
            var o = i * 4;
            sum += image.IsSrgb
                ? new Vector3(TerrainColor.SrgbToLinear(image.Rgba[o]), TerrainColor.SrgbToLinear(image.Rgba[o + 1]), TerrainColor.SrgbToLinear(image.Rgba[o + 2]))
                : new Vector3(image.Rgba[o], image.Rgba[o + 1], image.Rgba[o + 2]) / 255f;
        }

        var mean = sum / pixels;
        return image.IsSrgb
            ? TerrainColor.FromLinear(mean)
            : new TerrainColor((byte)MathF.Round(mean.X * 255f), (byte)MathF.Round(mean.Y * 255f), (byte)MathF.Round(mean.Z * 255f));
    }

    private TerrainLayerCatalog CloneWith(IReadOnlyList<string> warnings) =>
        new(new Dictionary<string, TerrainLayerStyle>(_layers, StringComparer.OrdinalIgnoreCase), [.. _wildcards], Rules, TextureRoot, DefaultTilingCm, [.. Warnings, .. warnings]);

    private static TerrainLayerStyle ReadStyle(JsonElement e, string name, TerrainLayerStyle? basis, string textureRoot, float defaultTiling)
    {
        string? Texture(string property, string? fallback)
        {
            if (!e.TryGetProperty(property, out var v))
            {
                return fallback;
            }

            if (v.ValueKind == JsonValueKind.Null || v.GetString() is not { Length: > 0 } text)
            {
                return null;
            }

            return text.StartsWith('/') ? text : textureRoot + text;
        }

        var color = e.TryGetProperty("color", out var c) && c.GetString() is { } cs ? TerrainColor.Parse(cs) : basis?.Color ?? HashedColor(name, vivid: false);
        var debug = e.TryGetProperty("debugColor", out var d) && d.GetString() is { } ds ? TerrainColor.Parse(ds) : basis?.DebugColor ?? HashedColor(name, vivid: true);
        var rule = basis?.Rule ?? TerrainLayerRule.None;
        if (e.TryGetProperty("rule", out var r) && r.GetString() is { } rs)
        {
            rule = rs.ToLowerInvariant() switch
            {
                "none" or "" => TerrainLayerRule.None,
                "slopeheight" or "slope_height" or "auto" => TerrainLayerRule.SlopeHeight,
                "hidden" => TerrainLayerRule.Hidden,
                _ => throw new InvalidDataException($"Layer {name}: unknown rule '{rs}' (none, slopeHeight, hidden)."),
            };
        }

        var tiling = e.TryGetProperty("tilingCm", out var t) && t.TryGetSingle(out var tv) && tv > 0 ? tv : basis?.TilingCm ?? defaultTiling;
        return new TerrainLayerStyle(name, Texture("diffuse", basis?.DiffuseTexture), Texture("nhr", basis?.NhrTexture), tiling, color, debug, rule,
            TerrainColorSource.Fallback);
    }

    private static TerrainColor FromHsv(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f) % 6;
        var f = h * 6f - MathF.Floor(h * 6f);
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new TerrainColor((byte)MathF.Round(r * 255f), (byte)MathF.Round(g * 255f), (byte)MathF.Round(b * 255f));
    }
}
