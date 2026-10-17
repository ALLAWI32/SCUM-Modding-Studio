using System.Globalization;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Viewport;

/// <summary>
/// How the Map draws a gravel road or dirt runway piece (<see cref="MaterialInfo.OnlyIntoLandscape"/>), which the game draws
/// only into the landscape texture: its trail colour over the ground, see-through by the piece's own coverage. The texture is
/// the trail's colour map with the coverage in its alpha, made from its mask (<c>DirtMask</c>: wheel tracks bright, sides
/// black) through the material's amount and contrast, so the piece fades into the ground at its sides instead of ending in
/// a dark jagged rim where the ground cuts through it.
/// </summary>
internal static class TrailLook
{
    /// <summary>Marks a texture key that is a trail's colour map with its coverage (not a texture of the game files).</summary>
    private const string Marker = "#coverage|";

    /// <summary>
    /// Alpha of the material colour: below one, so the piece is drawn in the see-through pass (blended over the ground, no
    /// depth writes, no shadow, as the game draws it into the ground and casts none).
    /// </summary>
    internal const float Alpha = 0.99f;

    /// <summary>The texture key of <paramref name="material"/>'s trail look with colour map <paramref name="colourMap"/>.</summary>
    public static string Key(string colourMap, MaterialInfo material) => string.Create(CultureInfo.InvariantCulture,
        $"{Marker}{material.CoverageTexture}|{material.CoverageAmount:0.###}|{material.CoverageContrast:0.###}|{colourMap}");

    /// <summary>True for a key made by <see cref="Key"/>.</summary>
    public static bool IsKey(string key) => key.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// The trail colour: the material's <c>Color_Overlay</c> laid over its grey colour map as UE's overlay blend does on a
    /// mid grey (twice the colour), mid brown without one.
    /// </summary>
    public static Vector4 Tint(MaterialInfo material)
    {
        var overlay = material.TintColor is { } c ? new Vector3(c.X, c.Y, c.Z) : new Vector3(0.42f, 0.338f, 0.252f);
        return new Vector4(Vector3.Min(overlay * 2f, Vector3.One), Alpha);
    }

    /// <summary>The coverage (0..1) of a mask value <paramref name="mask"/> (0..1) through the material's amount and contrast.</summary>
    public static float Coverage(float mask, float amount, float contrast) => amount * Math.Clamp(((mask - 0.5f) * contrast) + 0.5f, 0f, 1f);

    /// <summary>The colour map of <paramref name="key"/> decoded at <paramref name="maxSize"/>, its alpha the coverage (any thread).</summary>
    public static TextureImage Compose(AssetCatalog catalog, string key, int maxSize)
    {
        // Mask, amount, contrast, then the colour map last, so the key ends in the colour map's object path as a texture's does.
        var parts = key[Marker.Length..].Split('|', 4);
        var colour = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(parts[3]), maxSize);
        var amount = float.Parse(parts[1], CultureInfo.InvariantCulture);
        var contrast = float.Parse(parts[2], CultureInfo.InvariantCulture);
        var mask = parts[0].Length > 0 ? TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(parts[0]), maxSize) : null;
        var rgba = colour.Rgba.ToArray();
        for (var y = 0; y < colour.Height; y++)
        {
            for (var x = 0; x < colour.Width; x++)
            {
                var m = mask is null ? 1f : mask.Rgba[((((y * mask.Height) / colour.Height) * mask.Width) + ((x * mask.Width) / colour.Width)) * 4] / 255f;
                rgba[(((y * colour.Width) + x) * 4) + 3] = (byte)MathF.Round(Coverage(m, amount, contrast) * 255f);
            }
        }

        return colour with { Rgba = rgba, CompressedMips = null };
    }
}
