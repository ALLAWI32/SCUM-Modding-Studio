using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Tests.App;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// D06 (seviscache, defolt_0999): shelves in B_2 Spickovina and rocks around Prvo Selo drawn purple-blue (a normal map) on
/// the Map. Prepares those levels as the Map does and looks at the pixels of the image each section draws as its colour
/// (with the 3.2.0 pick: 15 of 1,023 sections in B_2 Spickovina and 18 of 2,191 in Prvo Selo drew the snow layer's
/// T_SnowTexture, a normal map). Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class NormalMapOnTheMapRealTests(ITestOutputHelper output)
{
    [Fact]
    public void SpickovinaSectionsDrawNoNormalMapAsColour() => Check("Spickovina");

    [Fact]
    public void PrvoSeloSectionsDrawNoNormalMapAsColour() => Check("Prvo");

    private void Check(string levelName)
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var levels = catalog.PackageFiles
            .Where(p => p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) && p.Contains("/Maps/The_Island/", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileNameWithoutExtension(p).Contains(levelName, StringComparison.OrdinalIgnoreCase))
            .Select(p => SpawnPartsRealTests.Maps + p[(p.IndexOf("/Maps/The_Island/", StringComparison.OrdinalIgnoreCase) + "/Maps/The_Island/".Length)..^".umap".Length])
            .ToList();
        output.WriteLine($"{levels.Count} levels: {string.Join(", ", levels.Select(l => l[(l.LastIndexOf('/') + 1)..]))}");
        Assert.NotEmpty(levels);

        var reader = new Cue4ParseLevelReader(catalog);
        var documents = levels.Select(l => LevelDocument.Load(reader, l)).ToList();
        var scene = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256 });
        output.WriteLine($"{scene.Meshes.Count} meshes, {scene.Textures.Count} textures");

        var wrong = new List<string>();
        var sections = 0;
        foreach (var asset in scene.Meshes.Values)
        {
            foreach (var material in asset.Lods[0].Sections.Select(s => s.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                sections++;
                var own = asset.MaterialTextures.TryGetValue(material, out var colour);
                if (!own && asset.MaterialTints.ContainsKey(material))
                {
                    continue; // its own colour, no texture
                }

                colour ??= asset.TexturePath;
                if (colour is null || !scene.Textures.TryGetValue(colour, out var image))
                {
                    continue;
                }

                var (r, g, b) = Mean(image);
                var named = colour[(colour.LastIndexOf('.') + 1)..];
                if (LooksLikeNormalMap(image) || image.IsNormalMap || image.PixelFormat == "PF_BC5" || !string.Equals(image.Name, named, StringComparison.OrdinalIgnoreCase))
                {
                    wrong.Add($"{asset.MeshPath} | {material} | {(own ? "own" : "fallback")} {colour} | image {image.Name} {image.PixelFormat} normal={image.IsNormalMap} mean=({r:0.00},{g:0.00},{b:0.00})");
                }
            }
        }

        output.WriteLine($"{sections} sections, {wrong.Count} draw a normal map (or another image) as colour");
        foreach (var line in wrong.Take(60))
        {
            output.WriteLine(line);
        }

        Assert.Empty(wrong);
    }

    /// <summary>
    /// True when <paramref name="image"/> looks like a tangent-space normal map by its pixels (on average X and Y near 0.5, Z
    /// near 1: the purple-blue of D06), whatever its compression flag says.
    /// </summary>
    internal static bool LooksLikeNormalMap(TextureImage image)
    {
        var (r, g, b) = Mean(image);
        return b > 0.65f && b - r > 0.2f && b - g > 0.2f && r is > 0.3f and < 0.7f && g is > 0.3f and < 0.7f;
    }

    /// <summary>Mean raw R, G, B (0-1, as stored) of a small mip of <paramref name="image"/>.</summary>
    internal static (float R, float G, float B) Mean(TextureImage image)
    {
        var (w, h, rgba) = (image.Width, image.Height, image.Rgba);
        if (image.CompressedMips is { Count: > 0 } mips)
        {
            var k = 0;
            while (k + 1 < mips.Count && Math.Max(w, h) > 64)
            {
                k++;
                (w, h) = (Math.Max(1, w / 2), Math.Max(1, h / 2));
            }

            rgba = TextureDecoder.DecodeRaw(mips[k], Enum.Parse<EPixelFormat>(image.PixelFormat), w, h, image.IsNormalMap);
        }

        double r = 0, g = 0, b = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (r, g, b) = (r + rgba[i], g + rgba[i + 1], b + rgba[i + 2]);
        }

        var n = Math.Max(1, rgba.Length / 4) * 255.0;
        return ((float)(r / n), (float)(g / n), (float)(b / n));
    }
}
