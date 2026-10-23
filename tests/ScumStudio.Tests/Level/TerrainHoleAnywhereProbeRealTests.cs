using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// pheidian: "it's the green big cells only that can be used? Is there a reason for that and not every cell?" and "Being
/// able to delete part of landscape with the hole tool - no matter where". A green landscape component (one the game can
/// draw a hole in) against an amber one of the same tile, on the real game files (<c>SCUM_PAKS</c>): the evidence that a
/// pak cannot give an amber component the game's hole layer, kept as asserts.
/// </summary>
public sealed class TerrainHoleAnywhereProbeRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_A_0_1c";

    [PaksFact]
    public void AGreenComponentDiffersFromAnAmberOneByItsCompiledMaterialNotJustItsLayer()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        Assert.True(catalog.TryGetPackageFile(Tile, out var file));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var package = CookedPackage.Parse(uasset, uexp, ubulk, Tile);
        var components = TerrainHoleLayers.Components(package);
        var places = TerrainHoleLayers.Places(Tile, package, ubulk?.Length ?? 0);
        var green = components.Single(c => places.Single(p => p.Component == c.Name).Visible);
        var amber = components.First(c => !places.Single(p => p.Component == c.Name).Visible);

        // The proxy names the landscape's two materials: the hole material only draws where a component's own material is built on it.
        var proxy = Enumerable.Range(0, package.Exports.Count).First(e => package.GetExportClassName(e) == "LandscapeStreamingProxy");
        var proxyBlock = package.ReadProperties(proxy);
        var landscapeMaterial = Path(package, proxyBlock.Find("LandscapeMaterial")?.Value);
        var holeMaterial = Path(package, proxyBlock.Find("LandscapeHoleMaterial")?.Value);
        output.WriteLine($"proxy: LandscapeMaterial {landscapeMaterial}; LandscapeHoleMaterial {holeMaterial}");

        var looks = new Dictionary<string, (string Parent, string Layers, int CompiledBytes, int Allocations)>();
        foreach (var (kind, component) in new[] { ("green", green), ("amber", amber) })
        {
            var block = package.ReadProperties(component.Export);
            var mic = (block.Find("MaterialInstances")?.Value as ArrayValue)?.Items.OfType<ObjectValue>().First(o => o.Index > 0)!;
            var micBlock = package.ReadProperties(mic.Index - 1);
            var combination = (ObjectValue)micBlock.Find("Parent")!.Value;
            var comboBlock = package.ReadProperties(combination.Index - 1);
            var parent = Path(package, comboBlock.Find("Parent")?.Value);
            var layers = string.Join(", ", ((comboBlock.Find("StaticParameters")?.Value as StructValue)?.Properties
                .FirstOrDefault(p => p.Name == "TerrainLayerWeightParameters")?.Value as ArrayValue)?.Items.OfType<StructValue>()
                .Select(i => $"{((i.Properties.First(p => p.Name == "ParameterInfo").Value as StructValue)!.Properties.First(p => p.Name == "Name").Value as NameValue)!.Value}@{(i.Properties.FirstOrDefault(p => p.Name == "WeightmapIndex")?.Value as IntValue)?.Value}") ?? []);
            output.WriteLine($"{kind}: {component.Name} at section {component.SectionBase}: layers {string.Join(", ", component.Allocations.Select(a => $"{a.Layer}@{a.Texture}.{"RGBA"[a.Channel]}"))}; {component.Weightmaps.Count} weightmap(s)");
            output.WriteLine($"   material {package.GetObjectName(mic.Index)} -> combination {package.GetObjectName(combination.Index)} ({comboBlock.NativeDataLength:N0} bytes of compiled shader data after its properties) -> {parent}");
            output.WriteLine($"   combination's static layer switches: {layers}");
            looks[kind] = (parent, layers, comboBlock.NativeDataLength, component.Allocations.Count);
        }

        // The two parents and what they are: the hole parent is masked (its pixels are cut where the visibility layer is 1).
        var inspector = new MaterialInspector(catalog);
        var masked = new Dictionary<string, float?>();
        foreach (var path in looks.Values.Select(l => l.Parent).Distinct())
        {
            var info = inspector.Inspect(path);
            masked[path] = info.OpacityMaskClip;
            output.WriteLine($"{path}: {(info.OpacityMaskClip is { } clip ? $"masked at {clip:0.####}" : info.IsTranslucent ? "translucent" : "opaque")}; parents {string.Join(" -> ", info.ParentChain)}");
        }

        Assert.NotNull(green.Visibility);
        Assert.Null(amber.Visibility);
        Assert.EndsWith("_Hole", looks["green"].Parent, StringComparison.Ordinal);
        Assert.DoesNotContain("_Hole", looks["amber"].Parent, StringComparison.Ordinal);
        Assert.NotNull(masked[looks["green"].Parent]); // the hole parent cuts pixels (masked)
        Assert.Null(masked[looks["amber"].Parent]);    // the other is opaque: no pixel of it is ever cut
        Assert.Contains("VISIBILITY", looks["green"].Layers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VISIBILITY", looks["amber"].Layers, StringComparison.OrdinalIgnoreCase);

        // Each combination carries its own compiled shaders for exactly its layer switches: a new layer set has none.
        Assert.True(looks["green"].CompiledBytes > 0 && looks["amber"].CompiledBytes > 0);
    }

    private static string Path(CookedPackage package, PropertyValue? value) =>
        value is ObjectValue { Index: not 0 } o ? package.GetFullPath(o.Index) : "(none)";
}
