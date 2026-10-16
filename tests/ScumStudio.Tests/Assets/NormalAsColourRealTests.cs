using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// D06 (seviscache, defolt_0999: shelves in B_2 Spickovina and rocks around Prvo Selo drawn purple-blue on the Map): no
/// material of the game picks a normal map as its colour texture, by its <c>TC_Normalmap</c> flag or by its pixels. The flag
/// alone missed the cause: the masters' snow layer 'SnowTexture' (T_SnowTexture, cooked TC_Default BC7) was picked by 471
/// materials (cliffs, shelves, fences, road sides). Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class NormalAsColourRealTests(ITestOutputHelper output)
{
    [Fact]
    public void NoMaterialPicksANormalMapAsItsColour()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var materials = AssetDumper.Packages
            .Where(p => p.ClassName is "MaterialInstanceConstant" or "Material")
            .Select(p => p.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var normalMaps = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var wrong = new System.Collections.Concurrent.ConcurrentBag<string>();
        var inspected = 0;
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
            if (info.BaseColorTexture is { } colour && normalMaps.GetOrAdd(colour, t => IsNormalMap(catalog, t)))
            {
                wrong.Add($"{path}: {colour} [{string.Join("; ", info.Textures.Select(t => t.Name + "=" + t.TexturePath[(t.TexturePath.LastIndexOf('/') + 1)..]))}]");
            }
        });

        output.WriteLine($"D06: {inspected} of {materials.Count} materials inspected, {wrong.Count} pick a normal map as colour");
        foreach (var line in wrong.Order().Take(40))
        {
            output.WriteLine(line);
        }

        Assert.True(inspected > 1000, $"only {inspected} materials inspected");
        Assert.Empty(wrong);
    }

    private static bool IsNormalMap(AssetCatalog catalog, string texture)
    {
        try
        {
            // By its pixels too: the snow layer's T_SnowTexture is a normal map cooked as TC_Default BC7 (no TC_Normalmap flag).
            return catalog.LoadObject<UTexture2D>(texture) is { } t
                   && (t.IsNormalMap || ScumStudio.Tests.Viewport.NormalMapOnTheMapRealTests.LooksLikeNormalMap(ScumStudio.Assets.Textures.TextureDecoder.Decode(t, maxSize: 64)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
