using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// r37 (owner, 3.4.0: "the light-tower generator trailer, a white trailer, white fences inside a hangar are pure white on
/// the Map but textured in the game"): no material of the game is drawn plain white on the Map while it has a colour
/// texture. The Map draws a material without a decodable colour texture in its surface colour (white for most masters);
/// the trailer's T_Light_Generator_01b_D is a streaming virtual texture (no regular mips), which could not be decoded.
/// Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class WhiteMaterialsRealTests(ITestOutputHelper output)
{
    [Fact]
    public void NoMaterialWithAColourTextureIsDrawnPlainWhite()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var materials = AssetDumper.Packages
            .Where(p => p.ClassName is "MaterialInstanceConstant" or "Material")
            .Select(p => p.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var decodes = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var undecodable = new ConcurrentBag<string>();
        var unpicked = new ConcurrentBag<string>();
        var mispicked = new ConcurrentBag<string>();
        var weatherPicked = new ConcurrentBag<string>();
        var inspected = 0;
        var textured = 0;
        var virtualTextures = 0;
        Parallel.ForEach(materials, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
        {
            MaterialInfo info;
            try
            {
                info = new MaterialInspector(catalog).Inspect(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }

            Interlocked.Increment(ref inspected);
            if (info.BaseColorTexture is { } colour)
            {
                var error = decodes.GetOrAdd(colour, t => Decode(catalog, t, ref virtualTextures));
                if (error is null)
                {
                    Interlocked.Increment(ref textured);
                }
                else if (DrawnWhite(info))
                {
                    undecodable.Add($"{path}: {colour[(colour.LastIndexOf('/') + 1)..]} ({error})");
                }

                // A mask or utility map taken for the colour while a colour map of its own is right there (the storage
                // shelves' 'UtilityTexture' over their 'B_DR'): drawn near white or grey instead of painted.
                if (!MaterialInspector.LooksLikeDiffuse(colour) && info.Textures.FirstOrDefault(t => IsColourMap(t) && t.Source != info.ParentChain.LastOrDefault()?.Split('.')[^1]) is { } own
                    && !colour.Equals(own.TexturePath, StringComparison.OrdinalIgnoreCase))
                {
                    mispicked.Add($"{path}: picked '{info.Textures.FirstOrDefault(t => t.TexturePath == colour)?.Name}' = {colour[(colour.LastIndexOf('/') + 1)..]} beside '{own.Name}' = {own.TexturePath[(own.TexturePath.LastIndexOf('/') + 1)..]}");
                }

                // r39: the landscape's ice, snow or rain layer taken for the colour while the material's own folder (or its
                // base material's) holds a colour map: the railway drew T_Ice_D instead of its T_Tracks_01_D.
                if (IsWeatherLayer(colour) && OwnColourMap(path, info) is { } ownMap)
                {
                    weatherPicked.Add($"{path}: {colour[(colour.LastIndexOf('/') + 1)..]} beside {ownMap[(ownMap.LastIndexOf('/') + 1)..]}");
                }

                return;
            }

            // No colour texture picked, drawn in a white surface colour, while a texture of it is named as a colour map.
            if (DrawnWhite(info) && info.Textures.FirstOrDefault(IsColourMap) is { } missed && decodes.GetOrAdd(missed.TexturePath, t => Decode(catalog, t, ref virtualTextures)) is null)
            {
                unpicked.Add($"{path}: '{missed.Name}' = {missed.TexturePath[(missed.TexturePath.LastIndexOf('/') + 1)..]} [{string.Join("; ", info.Textures.Select(t => t.Name))}]");
            }
        });

        output.WriteLine($"r37: {inspected} of {materials.Count} materials inspected, {textured} textured ({virtualTextures} virtual textures decoded); drawn plain white: {undecodable.Count} with a colour texture that does not decode, {unpicked.Count} with a colour map not picked; {mispicked.Count} draw another map beside their own colour map; r39: {weatherPicked.Count} draw a weather layer beside their own colour map");
        foreach (var line in undecodable.Order().Take(30).Concat(unpicked.Order().Take(30)).Concat(weatherPicked.Order().Take(30)).Concat(mispicked.Order().Take(60)))
        {
            output.WriteLine(line);
        }

        Assert.True(inspected > 1000, $"only {inspected} materials inspected");
        Assert.True(virtualTextures > 100, $"only {virtualTextures} virtual textures decoded");
        Assert.Empty(undecodable);
        Assert.Empty(weatherPicked);
        Assert.True(unpicked.Count <= 10, $"{unpicked.Count} materials draw white beside a colour map");
        Assert.DoesNotContain(mispicked, l => l.Contains("picked 'Utility", StringComparison.Ordinal) || l.Contains("Emblem", StringComparison.Ordinal));
    }

    /// <summary>
    /// The light-tower trailer's colour texture is a streaming virtual texture: its 256 px mip is put together from its tiles
    /// and shows the trailer's paint (not one flat colour).
    /// </summary>
    [Fact]
    public void TheLightTowerTrailersVirtualTextureDecodes()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return;
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var texture = catalog.LoadObject<UTexture2D>("/Game/ConZ_Files/Models/Objects/Outdoor/Construction_Site_Props/Textures/T_Light_Generator_01b_D");
        var image = TextureDecoder.Decode(texture, maxSize: 256);
        var gpu = TextureDecoder.DecodeForGpu(texture, maxSize: 256);

        Assert.Equal((256, 256, 2048), (image.Width, image.Height, image.SourceWidth));
        Assert.Equal(256 * 256 * 4, image.Rgba.Length);
        var lumas = Enumerable.Range(0, 256 * 256).Select(i => (image.Rgba[i * 4] + image.Rgba[(i * 4) + 1] + image.Rgba[(i * 4) + 2]) / 3).ToList();
        output.WriteLine($"luma min {lumas.Min()} max {lumas.Max()} mean {lumas.Average():0.0}; GPU mips {gpu.CompressedMips?.Count}");
        Assert.True(lumas.Max() - lumas.Min() > 60, "the trailer's texture is one flat colour");
        Assert.True(lumas.Average() is > 20 and < 235, $"mean {lumas.Average():0.0}");
        Assert.Equal(64 * 64 * 16, gpu.CompressedMips![0].Length); // DXT5: 64x64 blocks of 16 bytes
        Assert.Equal(2, gpu.CompressedMips.Count); // 256 and 128: the mips at least one tile wide
    }

    /// <summary>
    /// r39 (owner: "the rails are white"): the railway's track bed (RailRoad_01, the Railroad slot of SM_RailBridge_Arch) is
    /// drawn with its own sleepers and ballast, not with the landscape's ice layer that M_Railroad lists first.
    /// </summary>
    [PaksFact]
    public void TheRailwayIsDrawnWithItsTrackTexture()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var inspector = new MaterialInspector(catalog);
        foreach (var material in new[] { "/Game/ConZ_Files/Models/Rails/RailRoad_01/Mi_railRoad_01_PX", "/Game/ConZ_Files/Models/Rails/RailRoad_01/M_Railroad" })
        {
            var info = inspector.Inspect(material);
            output.WriteLine($"{material}: {info.BaseColorTexture}");
            Assert.Contains(info.ReferencedTextures, t => t.EndsWith("/Landscape/Textures/T_Ice_D.T_Ice_D", StringComparison.OrdinalIgnoreCase));
            Assert.EndsWith("/RailRoad_01/T_Tracks_01_D.T_Tracks_01_D", info.BaseColorTexture, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A landscape weather layer: under <c>/Landscape/Textures/</c> with Ice, Snow or Rain in its name (not "Terrain").</summary>
    private static bool IsWeatherLayer(string texturePath)
    {
        var name = texturePath[(texturePath.LastIndexOf('/') + 1)..];
        return texturePath.Contains("/Landscape/Textures/", StringComparison.OrdinalIgnoreCase)
               && (name.Contains("Ice", StringComparison.Ordinal) || name.Contains("Snow", StringComparison.Ordinal) || name.Contains("Rain", StringComparison.Ordinal));
    }

    /// <summary>A colour map (named <c>_D</c> …) in the folder of the material at <paramref name="packagePath"/> or of its base material, or null.</summary>
    private static string? OwnColourMap(string packagePath, MaterialInfo info)
    {
        var folders = new[] { packagePath, info.ParentChain.LastOrDefault() ?? packagePath }.Select(p => p[..Math.Max(p.LastIndexOf('/'), 0)] + "/").Distinct().ToList();
        return info.ReferencedTextures.Concat(info.Textures.Select(t => t.TexturePath))
            .FirstOrDefault(t => MaterialInspector.LooksLikeDiffuse(t) && !t.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
                                 && folders.Any(f => t.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? Decode(AssetCatalog catalog, string texturePath, ref int virtualTextures)
    {
        try
        {
            var texture = catalog.LoadObject<UTexture2D>(texturePath);
            TextureDecoder.Decode(texture, maxSize: 64);
            if (texture.PlatformData.Mips.Length == 0)
            {
                Interlocked.Increment(ref virtualTextures);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Opaque and drawn in a near-white surface colour when it has no texture (what the Map does then).</summary>
    private static bool DrawnWhite(MaterialInfo info) =>
        !info.IsTranslucent && info.SurfaceColor is { X: >= 0.85f, Y: >= 0.85f, Z: >= 0.85f };

    /// <summary>A texture parameter that holds a real colour map (named <c>_D</c>, <c>_BaseColor</c> …, no dummy, no layer painted on top).</summary>
    private static bool IsColourMap(TextureParameter t) =>
        t.TexturePath.Length > 0 && !t.TexturePath.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase)
        && !t.TexturePath.Contains("Dummy", StringComparison.OrdinalIgnoreCase) && MaterialInspector.LooksLikeDiffuse(t.TexturePath)
        && !t.Name.Contains("Snow", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Moss", StringComparison.OrdinalIgnoreCase)
        && !t.Name.Contains("Dirt", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Aging", StringComparison.OrdinalIgnoreCase)
        && !t.Name.Contains("Puddle", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Overlay", StringComparison.OrdinalIgnoreCase)
        && !t.Name.Contains("Blend", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Detail", StringComparison.OrdinalIgnoreCase);
}
