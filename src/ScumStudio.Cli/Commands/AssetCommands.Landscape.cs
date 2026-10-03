using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Level.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio asset landscape-layers &lt;source&gt; &lt;tile&gt;...</c>: decodes the paint layers of landscape tiles
/// (<see cref="LandscapeLayerReader"/>) and prints a per-component table (layer coverage, blend flag, grass types) plus the
/// layer area shares over all components; <c>--png</c> writes every layer's weights as 8-bit greyscale images.
/// </summary>
internal sealed partial class AssetCommands
{
    private static Command BuildLandscapeLayers()
    {
        var source = SourceArgument();
        var tiles = new Argument<string[]>("tile", "Landscape tiles (e.g. Landscape_A_0_4d), package paths, or a cell name (A_0 = every landscape tile of the cell).")
        {
            Arity = ArgumentArity.OneOrMore,
        };
        var aes = AesOption();
        var grass = new Option<bool>("--grass", "Also list every component's grass types with their mean density.");
        var png = new Option<DirectoryInfo?>("--png", "Write each component's layer weights as greyscale PNGs into this folder.");
        var command = new Command("landscape-layers", "Print the paint layers (weights, blend flags, grass) of landscape tiles per component, and the layer areas.")
        {
            source, tiles, aes, grass, png,
        };
        command.SetHandler(async (InvocationContext ctx) =>
        {
            var logger = CliHost.CreateLogger<AssetCommands>();
            var parse = ctx.ParseResult;
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await RunGuardedAsync(logger, async () =>
            {
                using var catalog = OpenCatalog(parse.GetValueForArgument(source), parse.GetValueForOption(aes), logger);
                var packages = ResolveLandscapeTiles(catalog, parse.GetValueForArgument(tiles), logger);
                if (packages is null)
                {
                    return 2;
                }

                var pngDir = parse.GetValueForOption(png);
                if (pngDir is not null)
                {
                    Directory.CreateDirectory(pngDir.FullName);
                }

                var all = new List<LandscapeComponentLayers>();
                var clock = Stopwatch.StartNew();
                var catalogLayers = TerrainLayerCatalog.Default;
                foreach (var package in packages)
                {
                    ct.ThrowIfCancellationRequested();
                    var components = LandscapeLayerReader.ReadPackage(catalog, package, includeGrass: true);
                    Console.Out.WriteLine($"== {package}: {components.Count} component(s)");
                    foreach (var c in components)
                    {
                        all.Add(c);
                        var layers = string.Join(" ", c.Layers.Select(l => Inv($"{l.Name}{(l.WeightBlended ? string.Empty : "*")}={l.Coverage * 100:0.0}%")));
                        Console.Out.WriteLine(Inv($"  {c.Name} base=({c.SectionBase.X},{c.SectionBase.Y}) allocations={c.AllocationCount} grass={c.Grass.Count}{(c.Holes is null ? string.Empty : " holes")}"));
                        Console.Out.WriteLine($"    layers: {layers}");
                        if (parse.GetValueForOption(grass))
                        {
                            foreach (var g in c.Grass)
                            {
                                Console.Out.WriteLine(Inv($"    grass: {g.GrassTypeName} mean={g.MeanDensity * 100:0.0}% samples={g.Density.Length}"));
                            }
                        }

                        foreach (var warning in c.Warnings)
                        {
                            logger.LogWarning("{Warning}", warning);
                        }

                        if (pngDir is not null)
                        {
                            var tile = package[(package.LastIndexOf('/') + 1)..];
                            foreach (var layer in c.Layers)
                            {
                                await SaveGreyPngAsync(layer.Weights, c.SampleCount, Path.Combine(pngDir.FullName, $"{tile}_{c.Name}_{layer.Name}.png"), ct).ConfigureAwait(false);
                            }
                        }

                        foreach (var layer in c.Layers)
                        {
                            catalogLayers.Resolve(layer.Name);
                        }
                    }
                }

                Console.Out.WriteLine(Inv($"layer areas over {all.Count} component(s) ({clock.Elapsed.TotalMilliseconds:0} ms; * = not weight-blended):"));
                foreach (var area in LandscapeLayerReader.Summarize(all))
                {
                    Console.Out.WriteLine(Inv($"  {area.Name + (area.WeightBlended ? string.Empty : "*"),-40} {area.AreaShare * 100,6:0.00}%  components={area.Components}"));
                }

                if (catalogLayers.UnknownLayers.Count > 0)
                {
                    Console.Out.WriteLine($"layers missing from terrain-layers.json: {string.Join(", ", catalogLayers.UnknownLayers)}");
                }

                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>Package paths of the named landscape tiles (cells expand to their landscape tiles); null after logging an error.</summary>
    private static List<string>? ResolveLandscapeTiles(AssetCatalog catalog, IEnumerable<string> names, ILogger logger)
    {
        var index = WorldIndex.FromCatalog(catalog);
        var result = new List<string>();
        foreach (var name in names)
        {
            if (MapCell.TryParse(name, out var cell))
            {
                var cellTiles = index.InCell(cell).Where(p => p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (cellTiles.Count > 0)
                {
                    result.AddRange(cellTiles);
                    continue;
                }
            }

            var packagePath = index.Find(name)?.PackagePath ?? (name.Contains('/') ? name : null);
            if (packagePath is null || !catalog.PackageExists(packagePath))
            {
                logger.LogError("Landscape tile not found: {Name} (use 'scumstudio level list' to see the available levels).", name);
                return null;
            }

            result.Add(packagePath);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task SaveGreyPngAsync(byte[] values, int size, string path, CancellationToken cancellationToken)
    {
        using var image = Image.LoadPixelData<L8>(values, size, values.Length / size);
        await image.SaveAsPngAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
