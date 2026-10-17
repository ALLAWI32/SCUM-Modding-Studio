using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Read-only probes of the landscape weightmaps and hole materials on the real game files (<c>SCUM_PAKS</c>, key from this
/// PC's store): the evidence behind <see cref="TerrainHoleLayers"/> and <see cref="LandscapeWeightmap"/>, kept as asserts.
/// </summary>
public sealed class TerrainHoleVisibilityProbeRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public void IslandWeightmapsAndHoleMaterials()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var tiles = GroundHeights.LandscapeTiles(catalog).OrderBy(t => t.Path, StringComparer.Ordinal).ToList();
        int components = 0, withVisibility = 0, usable = 0, texturesChecked = 0, maxDiff = 0, green = 0;
        var exactPerMip = new int[16];
        var switchable = new List<string>();
        var why = new Dictionary<string, int>();
        var mipMismatch = new long[16];
        var visMismatch = new long[16];
        var borderCopiesDiffer = 0L;

        // (3) coverage: per tile, the hole combination materials (static layer parameters) and each component's own.
        int plain = 0, sameTileMatch = 0, sameTileFree = 0, elsewhereMatch = 0;
        var holeSignatures = new HashSet<string>(StringComparer.Ordinal);
        var plainSignatures = new List<(string Tile, string Signature, string Key, bool FreeChannel)>();
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, _) in tiles)
        {
            catalog.TryGetPackageFile(path, out var file);
            var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file!);
            var package = CookedPackage.Parse(uasset, uexp, ubulk, path);
            var all = TerrainHoleLayers.Components(package);
            components += all.Count;
            var places = TerrainHoleLayers.Places(path, package, ubulk?.Length ?? 0);
            green += places.Count(p => p.Visible);
            var combos = Combinations(package);
            foreach (var c in combos.Values)
            {
                parents[c.Parent] = parents.GetValueOrDefault(c.Parent) + 1;
                if (c.IsHole)
                {
                    holeSignatures.Add(c.Signature);
                }
            }

            foreach (var component in all)
            {
                var target = TerrainHoleLayers.VisibilityOf(package, all, component, ubulk?.Length ?? 0, out var reason);
                if (component.Visibility is not null)
                {
                    withVisibility++;
                    if (target is null)
                    {
                        why[reason!] = why.GetValueOrDefault(reason!) + 1;
                    }
                    else
                    {
                        usable++;
                        // Every mip of every channel against the editor's mip rule rebuilt from mip 0.
                        var stock = target.Texture.Mips.Select(m => LandscapeWeightmap.Bytes(m, uexp, ubulk).ToArray()).ToList();
                        var rebuilt = stock.Select(m => (byte[])m.Clone()).ToList();
                        for (var ch = 0; ch < 4; ch++)
                        {
                            LandscapeWeightmap.BuildMips(rebuilt, target.Texture.Width, component.SubsectionQuads, component.Subsections, ch);
                        }

                        for (var m = 1; m < stock.Count; m++)
                        {
                            var same = true;
                            for (var i = 0; i < stock[m].Length; i++)
                            {
                                if (stock[m][i] != rebuilt[m][i])
                                {
                                    same = false;
                                    mipMismatch[m]++;
                                    visMismatch[m] += i % 4 == LandscapeWeightmap.ByteOfChannel(target.Channel) ? 1 : 0;
                                    maxDiff = Math.Max(maxDiff, Math.Abs(stock[m][i] - rebuilt[m][i]));
                                }
                            }

                            exactPerMip[m] += same ? 1 : 0;
                        }

                        texturesChecked++;

                        // The two copies of a sample on the subsection edge hold the same visibility.
                        var vb = LandscapeWeightmap.ByteOfChannel(target.Channel);
                        for (var y = 0; y <= component.SizeQuads; y++)
                        {
                            for (var x = 0; x <= component.SizeQuads; x++)
                            {
                                var copies = LandscapeWeightmap.TexelsOf(x, y, component.SubsectionQuads, component.Subsections)
                                    .Select(t => stock[0][(((t.Y * target.Texture.Width) + t.X) * 4) + vb]).Distinct().Count();
                                borderCopiesDiffer += copies > 1 ? 1 : 0;
                            }
                        }
                    }

                    continue;
                }

                // A component without the layer: is there a hole material with the same layers (name and weightmap) plus visibility?
                plain++;
                var signature = string.Join(";", component.Allocations.Select(a => $"{a.Layer}@{a.Texture}").Order(StringComparer.Ordinal));
                var own = MaterialOf(package, component, combos);
                var match = combos.Values.Where(c => c.IsHole && c.Signature == signature).ToList();
                if (match.Count > 0)
                {
                    sameTileMatch++;
                    var free = match.Any(c => c.VisibilityTexture < component.Weightmaps.Count && component.Allocations.Count(a => a.Texture == c.VisibilityTexture) < 4);
                    sameTileFree += free ? 1 : 0;
                    var place = places.FirstOrDefault(p => p.Component == component.Name);
                    switchable.Add($"{path[(path.LastIndexOf('/') + 1)..]} {component.Name} ({signature}; free channel: {free}) around X {(place is null ? 0 : (place.Min.X + place.Max.X) / 2):0} Y {(place is null ? 0 : (place.Min.Y + place.Max.Y) / 2):0}");
                }

                plainSignatures.Add((path, signature, own?.Signature ?? "?", false));
            }
        }

        foreach (var p in plainSignatures)
        {
            elsewhereMatch += holeSignatures.Contains(p.Signature) ? 1 : 0;
        }

        output.WriteLine($"{tiles.Count} tiles, {components} landscape components, {withVisibility} with the visibility layer, {usable} with a usable visibility channel");
        foreach (var (reason, count) in why)
        {
            output.WriteLine($"   not usable: {reason}: {count}");
        }

        output.WriteLine($"mips of {texturesChecked} visibility weightmaps rebuilt from mip 0 (all channels): exact per mip {string.Join(" ", exactPerMip.Skip(1).Take(8))}; largest difference {maxDiff}");
        output.WriteLine($"   mismatching bytes per mip: {string.Join(" ", mipMismatch.Skip(1).Take(8))}; in the visibility channel: {string.Join(" ", visMismatch.Skip(1).Take(8))}");
        output.WriteLine($"green places: {green}");
        foreach (var line in switchable)
        {
            output.WriteLine("   same-tile hole material: " + line);
        }
        output.WriteLine($"   subsection-edge samples whose two copies differ: {borderCopiesDiffer}");
        output.WriteLine($"material parents: {string.Join(", ", parents.Select(kv => $"{kv.Key} x{kv.Value}"))}");
        output.WriteLine($"coverage: {plain} components without the layer; {sameTileMatch} have a hole material with the same layers in their own tile ({sameTileFree} with a free channel on its visibility weightmap); {elsewhereMatch} have one somewhere on the island; {holeSignatures.Count} distinct hole layer sets");

        Assert.Equal(tiles.Count * 4, components);
        Assert.True(usable >= 170, $"{usable} usable visibility channels");
        Assert.Equal(usable, green);
        Assert.Equal(0L, borderCopiesDiffer);

        // The mip rule: the visibility channel of every mip of all 172 exactly as the game's; the other channels from mip 4 down, the larger mips within 1.
        Assert.All(Enumerable.Range(4, 5), m => Assert.Equal(texturesChecked, exactPerMip[m]));
        Assert.All(visMismatch, n => Assert.Equal(0L, n));
        Assert.True(maxDiff <= 1, $"largest difference {maxDiff}");
    }

    /// <summary>The landscape combination materials of a tile by export index.</summary>
    private static Dictionary<int, Combination> Combinations(CookedPackage package)
    {
        var result = new Dictionary<int, Combination>();
        for (var e = 0; e < package.Exports.Count; e++)
        {
            if (package.GetExportClassName(e) != "LandscapeMaterialInstanceConstant")
            {
                continue;
            }

            var block = package.ReadProperties(e);
            if (block.Find("StaticParameters")?.Value is not StructValue statics)
            {
                continue;
            }

            var parent = block.Find("Parent")?.Value is ObjectValue { Index: not 0 } p ? package.GetObjectName(p.Index) ?? "?" : "?";
            var layers = new List<(string Name, int Index)>();
            foreach (var item in (statics.Properties.FirstOrDefault(t => t.Name == "TerrainLayerWeightParameters")?.Value as ArrayValue)?.Items.OfType<StructValue>() ?? [])
            {
                var info = item.Properties.FirstOrDefault(t => t.Name == "ParameterInfo")?.Value as StructValue;
                var name = (info?.Properties.FirstOrDefault(t => t.Name == "Name")?.Value as NameValue)?.Value ?? "?";
                layers.Add((name, (item.Properties.FirstOrDefault(t => t.Name == "WeightmapIndex")?.Value as IntValue)?.Value ?? -1));
            }

            var visibility = layers.FirstOrDefault(l => l.Name.Contains("VISIBILITY", StringComparison.Ordinal));
            result[e] = new Combination(parent, parent.EndsWith("_Hole", StringComparison.Ordinal),
                string.Join(";", layers.Where(l => !l.Name.Contains("VISIBILITY", StringComparison.Ordinal)).Select(l => $"{l.Name}@{l.Index}").Order(StringComparer.Ordinal)),
                visibility.Name is null ? -1 : visibility.Index);
        }

        return result;
    }

    private static Combination? MaterialOf(CookedPackage package, TerrainHoleLayers.Component component, Dictionary<int, Combination> combos)
    {
        var block = package.ReadProperties(component.Export);
        if ((block.Find("MaterialInstances")?.Value as ArrayValue)?.Items.FirstOrDefault() is not ObjectValue { Index: > 0 } mic)
        {
            return null;
        }

        var parent = package.ReadProperties(mic.Index - 1).Find("Parent")?.Value as ObjectValue;
        return parent is { Index: > 0 } && combos.TryGetValue(parent.Index - 1, out var c) ? c : null;
    }

    private sealed record Combination(string Parent, bool IsHole, string Signature, int VisibilityTexture);
}
