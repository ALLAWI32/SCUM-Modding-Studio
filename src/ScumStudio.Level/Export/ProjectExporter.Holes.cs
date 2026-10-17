using System.Numerics;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Export;

public sealed partial class ProjectExporter
{
    /// <summary>
    /// The project's terrain holes (see <see cref="TerrainHoles"/>): every landscape tile under one becomes a changed level
    /// (on top of the tile as this export already wrote it, if it did) whose collision cells under the holes are PhysX
    /// holes, every other byte as cooked. Run against each cook's own tiles, so the server pak carries the same holes.
    /// </summary>
    private void CutTerrainHoles(AssetCatalog catalog, IReadOnlyList<TerrainHole> holes, string staging, List<ExportedLevel> levels, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var boxes = holes.Select(h => h.Bounds).ToList();
        var reached = new bool[holes.Count];
        AssetCatalog? staged = null;
        try
        {
            staged = AssetCatalog.OpenLoose(staging);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing written yet (only holes in the project): every tile comes from the game.
        }

        using var written = staged;
        foreach (var (tile, _) in GroundHeights.LandscapeTiles(catalog).Where(t => boxes.Any(b =>
                     t.Tile.BoundsMin.X <= b.Max.X && b.Min.X <= t.Tile.BoundsMax.X && t.Tile.BoundsMin.Y <= b.Max.Y && b.Min.Y <= t.Tile.BoundsMax.Y)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stagedFile = default(CUE4Parse.FileProvider.Objects.GameFile);
                var already = written?.TryGetPackageFile(tile, out stagedFile) == true;
                var file = already ? stagedFile : catalog.TryGetPackageFile(tile, out var stock) ? stock : null;
                if (file is null)
                {
                    continue;
                }

                var (uasset, uexp, ubulk) = ReadPackageFiles(already ? written! : catalog, file);
                var package = CookedPackage.Parse(uasset, uexp, null, tile);
                var components = TerrainHoleLayers.Components(package);
                var cut = 0;
                var shown = 0;
                for (var i = 0; i < package.Exports.Count; i++)
                {
                    if (package.GetExportClassName(i) != "LandscapeHeightfieldCollisionComponent")
                    {
                        continue;
                    }

                    var name = package.GetObjectName(i + 1);
                    var block = package.ReadProperties(i);
                    if (TerrainHoles.GridOf(package, i, block) is not { } grid)
                    {
                        warnings.Add($"{tile}: {name}: placed in a way the island's landscape never is; no hole cut there.");
                        continue;
                    }

                    var near = holes.Where((h, k) => Touches(grid, boxes[k])).ToList();
                    var quads = TerrainHoles.Quads(grid, near);
                    if (quads.Count == 0)
                    {
                        continue;
                    }

                    var payload = uexp.AsSpan(block.UExpBaseOffset, block.PayloadLength);
                    if (LandscapeCollision.Read(payload, block.EndOffset) is not { } field || field.Rows != grid.SizeQuads + 1)
                    {
                        warnings.Add($"{tile}: {name}: its collision is not laid out as the island's landscape collision; the hole there stays closed.");
                        continue;
                    }

                    cut += TerrainHoles.Cut(payload, field, quads);
                    for (var k = 0; k < holes.Count; k++)
                    {
                        reached[k] |= near.Contains(holes[k]);
                    }

                    // Where the component draws with the hole material, its visibility layer opens too, at the samples the
                    // editor's collision cook turns into exactly these cells; elsewhere the game keeps drawing the ground.
                    var section = ((block.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0, (block.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0);
                    var render = components.FirstOrDefault(c => c.SectionBase == section);
                    string? why = null;
                    if (render is not null && TerrainHoleLayers.VisibilityOf(package, components, render, ubulk?.Length ?? 0, out why) is { } visibility)
                    {
                        shown += TerrainHoleLayers.Open(visibility, quads.Select(q => TerrainHoleLayers.SampleOfQuad(q.X, q.Y)), uexp, ubulk);
                    }
                    else
                    {
                        warnings.Add($"{tile}: {render?.Name ?? name}: collision only ({why ?? "no render component at that place"}); the game still draws the ground over this hole, cover it with your own model.");
                    }
                }

                if (cut == 0 && shown == 0)
                {
                    continue;
                }

                var virtualPath = file.Path.Replace('\\', '/');
                var dot = virtualPath.LastIndexOf('.');
                var target = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target + virtualPath[dot..], uasset);
                File.WriteAllBytes(target + ".uexp", uexp);
                if (ubulk is not null)
                {
                    File.WriteAllBytes(target + ".ubulk", ubulk);
                }

                var previous = levels.FindIndex(l => SameLevel(l.PackagePath, tile));
                if (previous >= 0)
                {
                    var report = levels[previous].Report;
                    levels[previous] = levels[previous] with
                    {
                        Report = report with { TerrainHoleCells = report.TerrainHoleCells + cut, TerrainHoleVisibleSamples = report.TerrainHoleVisibleSamples + shown },
                    };
                }
                else
                {
                    levels.Add(new ExportedLevel(tile, virtualPath, new LevelEditReport
                    {
                        LevelExport = "PersistentLevel", ActorsBefore = 0, ActorsAfter = 0, RemovedActors = [], PatchedTransforms = [], AddedNames = [], Warnings = [],
                        TerrainHoleCells = cut, TerrainHoleVisibleSamples = shown,
                    }));
                }

                _logger.LogInformation("{Level}: {Cells} landscape collision cell(s) cut open, {Samples} visibility sample(s) opened.", tile, cut, shown);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                warnings.Add($"{tile}: the terrain hole could not be cut ({ex.Message}).");
            }
        }

        for (var k = 0; k < holes.Count; k++)
        {
            if (!reached[k])
            {
                warnings.Add($"The {holes[k]} is not over the landscape of {catalog.DisplayName}; nothing was cut.");
            }
        }
    }

    private static bool Touches(TerrainHoles.Grid grid, (Vector2 Min, Vector2 Max) box)
    {
        var corners = new[] { grid.World(0, 0), grid.World(grid.SizeQuads, 0), grid.World(0, grid.SizeQuads), grid.World(grid.SizeQuads, grid.SizeQuads) };
        return corners.Min(c => c.X) <= box.Max.X && box.Min.X <= corners.Max(c => c.X) && corners.Min(c => c.Y) <= box.Max.Y && box.Min.Y <= corners.Max(c => c.Y);
    }
}
