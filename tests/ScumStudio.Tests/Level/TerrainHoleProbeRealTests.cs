using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Read-only go/no-go probes for the terrain hole tool on the real game files (<c>SCUM_PAKS</c>, key from this PC's
/// store): the cooked landscape collision layout and where the game's own holes are. They print what they find.
/// </summary>
public sealed class TerrainHoleProbeRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public void ProbeCellA0()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var printedProps = false;
        int components = 0, holesChecked = 0;
        foreach (var name in ScumStudio.Tests.Assets.LandscapeLayerReaderTests.A0Tiles())
        {
            var tile = MapSlice.MapsPath + name;
            var layers = LandscapeLayerReader.ReadPackage(catalog, tile, includeGrass: false);
            var surfaces = LandscapeExtractor.Extract(catalog, tile, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
                .SelectMany(p => p.Components).Select(c => c.Surface!).ToList();
            var package = ModdableAssets.ReadPackage(catalog, tile);
            var holeComponents = layers.Where(l => l.Holes is not null).Select(l => l.Name).ToHashSet();
            if (holeComponents.Count > 0)
            {
                output.WriteLine($"== {name}: DataLayer allocated in {string.Join(", ", holeComponents)}");
                foreach (var l in layers.Where(l => l.Holes is not null))
                {
                    var s = surfaces.Single(x => x.Name == l.Name);
                    var holes = Enumerable.Range(0, l.Holes!.Length).Where(i => l.Holes[i] > 127).ToList();
                    var nonzero = l.Holes.Count(b => b > 0);
                    if (holes.Count > 0)
                    {
                        var pts = holes.Select(i => s.WorldPosition(i % l.SampleCount, i / l.SampleCount)).ToList();
                        output.WriteLine($"   {l.Name}: {holes.Count} hole samples (>127), {nonzero} nonzero; X {pts.Min(p => p.X):0}..{pts.Max(p => p.X):0} Y {pts.Min(p => p.Y):0}..{pts.Max(p => p.Y):0} Z {pts.Min(p => p.Z):0}..{pts.Max(p => p.Z):0}");
                        output.WriteLine($"   qx {holes.Min(i => i % l.SampleCount)}..{holes.Max(i => i % l.SampleCount)} qy {holes.Min(i => i / l.SampleCount)}..{holes.Max(i => i / l.SampleCount)}");
                    }
                    else
                    {
                        output.WriteLine($"   {l.Name}: no hole sample; {nonzero} nonzero");
                    }
                }

                DumpMaterials(catalog, tile, holeComponents, layers.First(l => l.Holes is null).Name, package);
            }

            // Collision components.
            for (var e = 0; e < package.Exports.Count; e++)
            {
                var cls = package.GetExportClassName(e);
                if (cls == "LandscapeComponent")
                {
                    var b = package.ReadProperties(e);
                    var cname = package.GetObjectName(e + 1);
                    if (holeComponents.Contains(cname!) || !printedProps)
                    {
                        output.WriteLine($"   LC {cname}: payload {b.PayloadLength}, tail {b.NativeDataLength}; props: {string.Join(" | ", b.Properties.Select(p => p.Name))}");
                    }
                }

                if (cls != "LandscapeHeightfieldCollisionComponent")
                {
                    continue;
                }

                var block = package.ReadProperties(e);
                var payload = package.GetExportBytes(e);
                var objName = package.GetObjectName(e + 1);
                if (!printedProps)
                {
                    output.WriteLine($"-- {name} {objName} props:");
                    foreach (var p in block.Properties)
                    {
                        output.WriteLine($"   {p}");
                    }

                    output.WriteLine($"   tail {block.NativeDataLength} bytes, head: {Hex(payload.AsSpan(block.EndOffset, Math.Min(140, block.NativeDataLength)))}");
                    printedProps = true;
                }

                var sbx = (block.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0;
                var sby = (block.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0;
                var hf = LandscapeCollision.Read(payload, block.EndOffset);
                if (hf is null)
                {
                    output.WriteLine($"   {name} {objName}: NOT a recognised heightfield; tail {block.NativeDataLength}, head {Hex(payload.AsSpan(block.EndOffset, Math.Min(64, block.NativeDataLength)))}");
                    continue;
                }

                var h = hf.Value;
                var holeCells = 0;
                var mat0 = new int[256];
                var mat1 = new int[256];
                for (var i = 0; i < h.Rows * h.Columns; i++)
                {
                    var m0 = payload[h.SamplesOffset + (i * 4) + 2];
                    var m1 = payload[h.SamplesOffset + (i * 4) + 3];
                    mat0[m0]++;
                    mat1[m1]++;
                    holeCells += (m0 & 0x7F) == 0x7F ? 1 : 0;
                }

                // Which of the 8 square symmetries maps the samples onto the render component's heights?
                var render = surfaces.FirstOrDefault(s => s.SectionBase == (sbx, sby));
                var verdict = render is null ? "no render component with that section base" : Symmetries(h, payload, render);
                Assert.Contains($"s1:{h.Rows * h.Columns}/0", verdict, StringComparison.Ordinal); // rows run back along X, every sample
                Assert.Equal((255, 255, 1, 1), (h.Rows, h.Columns, h.Format, h.Flags));
                components++;
                if (holeCells > 0 && render is not null && layers.FirstOrDefault(l => l.SectionBase == (sbx, sby)) is { Holes: { } vis } holeLayer)
                {
                    var n = h.Rows;
                    var cells = new HashSet<(int, int)>();
                    for (var r = 0; r < n; r++)
                    {
                        for (var c = 0; c < n; c++)
                        {
                            if ((payload[h.SampleAt(r, c) + 2] & 0x7F) == 0x7F)
                            {
                                cells.Add((r, c));
                            }
                        }
                    }

                    var samples = Enumerable.Range(0, vis.Length).Where(i => vis[i] > 127).Select(i => (X: i % n, Y: i / n)).ToList();
                    // The rule the export uses: visibility sample (x, y) <=> PhysX cell (n - 1 - x, y), all of them.
                    Assert.True(cells.SetEquals(samples.Select(s => (n - 1 - s.X, s.Y))), $"{name} {objName}: the game's hole cells are not its visibility samples");
                    foreach (var (label, map) in new (string, Func<int, int, (int, int)>)[]
                             {
                                 ("r=254-x,c=y", (x, y) => (n - 1 - x, y)), ("r=253-x,c=y", (x, y) => (n - 2 - x, y)),
                                 ("r=254-x,c=y-1", (x, y) => (n - 1 - x, y - 1)), ("r=253-x,c=y-1", (x, y) => (n - 2 - x, y - 1)),
                             })
                    {
                        var mapped = samples.Select(s => map(s.X, s.Y)).ToHashSet();
                        output.WriteLine($"      {label}: {mapped.Count(cells.Contains)} of {samples.Count} samples map onto the {cells.Count} hole cells");
                    }

                    output.WriteLine($"      vis samples: {string.Join(" ", samples.Take(40).Select(s => $"{s.X},{s.Y}"))}");
                    output.WriteLine($"      hole cells (r,c): {string.Join(" ", cells.OrderBy(x => x).Take(40).Select(x => $"{x.Item1},{x.Item2}"))}");

                    // PhysX collision-vertex bit (material byte 1, bit 7) of every vertex of a hole cell.
                    int inner = 0, innerSet = 0, rim = 0, rimSet = 0;
                    var near = cells.SelectMany(x => new[] { x, (x.Item1 + 1, x.Item2), (x.Item1, x.Item2 + 1), (x.Item1 + 1, x.Item2 + 1) }).Distinct();
                    foreach (var (r, c) in near)
                    {
                        var around = new[] { (r - 1, c - 1), (r - 1, c), (r, c - 1), (r, c) }.Where(x => x.Item1 >= 0 && x.Item2 >= 0 && x.Item1 < n - 1 && x.Item2 < n - 1).ToList();
                        var holes = around.Count(cells.Contains);
                        var set = (payload[h.SampleAt(r, c) + 3] & 0x80) != 0;
                        if (holes == around.Count)
                        {
                            inner++;
                            innerSet += set ? 1 : 0;
                        }
                        else
                        {
                            rim++;
                            rimSet += set ? 1 : 0;
                        }
                    }

                    Assert.Equal((0, rim), (innerSet, rimSet)); // the collision-vertex rule TerrainHoles.Cut writes
                    holesChecked++;
                    output.WriteLine($"      vertex bits: inside the hole {innerSet}/{inner} set, on its rim {rimSet}/{rim} set; hole cells' material1 low bits: {string.Join(",", cells.Select(x => payload[h.SampleAt(x.Item1, x.Item2) + 3]).GroupBy(b => b).Select(g => $"{g.Key:X2}x{g.Count()}"))}");
                }

                if (holeCells == 0)
                {
                    continue;
                }

                output.WriteLine($"   {name} {objName} base=({sbx},{sby}) v{h.Version} rows={h.Rows} cols={h.Columns} fmt={h.Format} flags={h.Flags} stride={h.SampleStride} samples@{h.SamplesOffset - block.EndOffset} streamLen={h.StreamLength} after={h.StreamOffset + h.StreamLength - (h.SamplesOffset + (4 * h.Rows * h.Columns))} min={h.MinHeight} max={h.MaxHeight} holeCells={holeCells}");
                output.WriteLine($"      mat0: {Histogram(mat0)}  mat1: {Histogram(mat1)}  {verdict}");
            }
        }

        Assert.Equal(64, components); // cell A_0: 16 tiles of 4
        Assert.True(holesChecked >= 4, $"{holesChecked} of the game's holes checked"); // A_0_1c, A_0_1d, A_0_2c, A_0_3b
    }

    [Fact]
    public void ScanIslandForHoles()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var tiles = GroundHeights.LandscapeTiles(catalog);
        int components = 0, layered = 0, holed = 0;
        var odd = new List<string>();
        foreach (var (path, _) in tiles.OrderBy(t => t.Path, StringComparer.Ordinal))
        {
            catalog.TryGetPackageFile(path, out var file);
            var (uasset, uexp, _) = ProjectExporter.ReadPackageFiles(catalog, file!);
            var package = CookedPackage.Parse(uasset, uexp, null, path);
            var found = new List<(int X, int Y, List<(int R, int C)> Cells)>();
            var dataLayer = new List<string>();
            for (var e = 0; e < package.Exports.Count; e++)
            {
                var cls = package.GetExportClassName(e);
                if (cls == "LandscapeComponent")
                {
                    var b = package.ReadProperties(e);
                    if (b.FindAll("LayerInfo").Any(t => t.Value is ObjectValue o && o.Reference.Contains("DataLayer", StringComparison.OrdinalIgnoreCase)))
                    {
                        dataLayer.Add($"{package.GetObjectName(e + 1)}({(b.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0},{(b.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0})");
                    }
                }

                if (cls != "LandscapeHeightfieldCollisionComponent")
                {
                    continue;
                }

                components++;
                var block = package.ReadProperties(e);
                var payload = package.GetExportBytes(e);
                if (LandscapeCollision.Read(payload, block.EndOffset) is not { } h)
                {
                    odd.Add($"{path} {package.GetObjectName(e + 1)}: unreadable, tail {block.NativeDataLength}");
                    continue;
                }

                if (h.Rows != 255 || h.Columns != 255 || h.Format != 1 || h.Version != 1 || h.Flags != 1)
                {
                    odd.Add($"{path} {package.GetObjectName(e + 1)}: v{h.Version} {h.Rows}x{h.Columns} fmt {h.Format} flags {h.Flags}");
                }

                // The quad grid from the cooked properties: 1.5 m quads starting on the world grid (the view's rule, TerrainHole.WorldQuads).
                if (TerrainHoles.GridOf(package, e, block) is not { } grid || grid.AxisX != new Vector2(150f, 0f) || grid.AxisY != new Vector2(0f, 150f)
                    || grid.Origin.X % 150f != 0f || grid.Origin.Y % 150f != 0f)
                {
                    odd.Add($"{path} {package.GetObjectName(e + 1)}: grid {TerrainHoles.GridOf(package, e, block)}");
                }

                var cells = new List<(int, int)>();
                for (var r = 0; r < h.Rows; r++)
                {
                    for (var c = 0; c < h.Columns; c++)
                    {
                        if ((payload[h.SampleAt(r, c) + 2] & 0x7F) == LandscapeCollision.HoleMaterial)
                        {
                            cells.Add((r, c));
                        }
                    }
                }

                if (cells.Count > 0)
                {
                    found.Add(((block.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0, (block.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0, cells));
                }
            }

            layered += dataLayer.Count;
            if (dataLayer.Count > 0 || found.Count > 0)
            {
                output.WriteLine($"{path[(path.LastIndexOf('/') + 1)..]}: DataLayer in {string.Join(" ", dataLayer)}");
            }

            if (found.Count == 0)
            {
                continue;
            }

            var surfaces = LandscapeExtractor.Extract(catalog, path, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
                .SelectMany(p => p.Components).Select(c => c.Surface!).ToList();
            foreach (var (x, y, cells) in found)
            {
                holed++;
                var s = surfaces.Single(s => s.SectionBase == (x, y));
                var pts = cells.Select(cell => s.WorldPosition(s.SampleCount - 1 - cell.R, cell.C)).ToList();
                output.WriteLine($"   holes in ({x},{y}): {cells.Count} cells around X {pts.Average(p => p.X):0} Y {pts.Average(p => p.Y):0} Z {pts.Average(p => p.Z):0} (X {pts.Min(p => p.X):0}..{pts.Max(p => p.X):0}, Y {pts.Min(p => p.Y):0}..{pts.Max(p => p.Y):0})");
            }
        }

        output.WriteLine($"{tiles.Count} tiles, {components} collision components, {holed} with holes, {layered} render components allocate DataLayer; odd: {odd.Count}");
        foreach (var o in odd.Take(20))
        {
            output.WriteLine("   " + o);
        }

        // Every collision component of the island is the one layout TerrainHoles.Cut patches; the game cuts its own holes.
        Assert.Empty(odd);
        Assert.Equal(tiles.Count * 4, components);
        Assert.True(holed > 100, $"{holed} components with the game's holes");
    }

    private void DumpMaterials(AssetCatalog catalog, string tile, HashSet<string> holeComponents, string plain, CookedPackage package)
    {
        var loaded = catalog.LoadPackage(tile);
        foreach (var export in loaded.GetExports())
        {
            if (LandscapeExtractor.IsLandscapeProxyClass(export.ExportType))
            {
                output.WriteLine($"   proxy {export.Name}: {string.Join(" | ", export.Properties.Select(p => $"{p.Name.Text}={Short(p.Tag?.GenericValue)}"))}");
                continue;
            }

            if (export.ExportType != "LandscapeComponent" || (!holeComponents.Contains(export.Name) && export.Name != plain))
            {
                continue;
            }

            foreach (var index in export.GetOrDefault<FPackageIndex[]>("MaterialInstances") ?? [])
            {
                UUnrealMaterial? material = index.Load<UMaterialInstance>();
                var chain = new StringBuilder($"   {export.Name} MI:");
                for (var depth = 0; depth < 8 && material is not null; depth++)
                {
                    chain.Append($" -> {material.GetPathName()} [{material.ExportType}]");
                    if (material is UMaterialInstance instance)
                    {
                        var size = ExportSize(package, material.Name);
                        if (size >= 0)
                        {
                            chain.Append($" (export {size} B)");
                        }

                        if (instance.StaticParameters?.TerrainLayerWeightParameters is { Length: > 0 } parameters)
                        {
                            chain.Append(" layers{" + string.Join(",", parameters.Select(p => $"{p.ParameterInfo?.Name.Text ?? p.Name}@{p.WeightmapIndex}{(p.bWeightBasedBlend ? "" : "*")}")) + "}");
                        }

                        chain.Append(" props{" + string.Join(",", instance.Properties.Select(p => p.Name.Text)) + "}");
                        material = instance.Parent;
                    }
                    else
                    {
                        break;
                    }
                }

                output.WriteLine(chain.ToString());
            }

            foreach (var key in new[] { "MobileMaterialInterfaces", "LODIndexToMaterialIndex", "MaterialIndexToDisabledTessellationMaterial", "OverrideHoleMaterial", "OverrideMaterial" })
            {
                if (export.Properties.FirstOrDefault(p => p.Name.Text == key) is { } prop)
                {
                    output.WriteLine($"   {export.Name}.{key} = {Short(prop.Tag?.GenericValue)}");
                }
            }
        }
    }

    private static int ExportSize(CookedPackage package, string name)
    {
        for (var e = 0; e < package.Exports.Count; e++)
        {
            if (package.GetObjectName(e + 1) == name)
            {
                return package.GetExportRange(e).Length;
            }
        }

        return -1;
    }

    private static string Short(object? value)
    {
        var text = value switch
        {
            null => "null",
            Array a => $"[{a.Length}] " + string.Join(",", a.Cast<object>().Take(4)),
            _ => value.ToString() ?? "?",
        };
        return text.Length > 120 ? text[..120] : text;
    }

    private static string Symmetries(LandscapeCollision.Heightfield h, byte[] payload, LandscapeSurface render)
    {
        var n = render.SampleCount;
        if (h.Rows != n || h.Columns != n)
        {
            return $"size differs from the render grid {n}";
        }

        Matrix4x4.Invert(render.ToWorld, out var toLocal);
        var local = new int[n * n];
        for (var i = 0; i < n * n; i++)
        {
            var w = render.WorldPosition(i % n, i / n);
            local[i] = (int)MathF.Round(Vector3.Transform(w, toLocal).Z * 128f);
        }

        var results = new List<string>();
        for (var sym = 0; sym < 8; sym++)
        {
            var exact = 0;
            var maxDiff = 0;
            for (var r = 0; r < n; r++)
            {
                for (var c = 0; c < n; c++)
                {
                    var (a, b) = (sym & 4) != 0 ? (c, r) : (r, c);
                    if ((sym & 1) != 0)
                    {
                        a = n - 1 - a;
                    }

                    if ((sym & 2) != 0)
                    {
                        b = n - 1 - b;
                    }

                    // collision sample (row r, column c) against render sample (x = a, y = b)
                    var s = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(h.SamplesOffset + (((r * n) + c) * 4)));
                    var d = Math.Abs(s - local[(b * n) + a]);
                    exact += d == 0 ? 1 : 0;
                    maxDiff = Math.Max(maxDiff, d);
                }
            }

            results.Add($"s{sym}:{exact}/{maxDiff}");
        }

        return string.Join(" ", results);
    }

    private static string Histogram(int[] counts) =>
        string.Join(",", Enumerable.Range(0, 256).Where(i => counts[i] > 0).OrderByDescending(i => counts[i]).Take(8).Select(i => $"{i:X2}x{counts[i]}"));

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            if (i % 4 == 3)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
