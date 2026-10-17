using System.Buffers.Binary;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The terrain hole tool on the real game files (<c>SCUM_PAKS</c>, key from this PC's store): a 4 x 4 m hole cut into
/// Landscape_A_0_1, exported and read back.
/// </summary>
public sealed class TerrainHoleRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_A_0_1";

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public async Task AFourMetreHoleOpensNineCellsAndNothingElse()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var stock = ModdableAssets.ReadPackage(catalog, Tile);
        var surfaces = LandscapeExtractor.Extract(catalog, Tile, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
            .SelectMany(p => p.Components).Select(c => c.Surface!).ToList();

        // The quad grid read from the cooked properties alone lies exactly on the render components' samples.
        var grids = new Dictionary<int, TerrainHoles.Grid>();
        for (var e = 0; e < stock.Exports.Count; e++)
        {
            if (stock.GetExportClassName(e) != "LandscapeHeightfieldCollisionComponent")
            {
                continue;
            }

            var props = stock.ReadProperties(e);
            var grid = TerrainHoles.GridOf(stock, e, props) ?? throw new InvalidOperationException($"export {e}: no grid");
            var section = ((props.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0, (props.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0);
            var surface = surfaces.Single(s => s.SectionBase == section);
            foreach (var (x, y) in new[] { (0, 0), (254, 0), (0, 254), (254, 254), (127, 200) })
            {
                var want = surface.WorldPosition(x, y);
                var got = grid.World(x, y);
                Assert.True(MathF.Abs(want.X - got.X) < 0.5f && MathF.Abs(want.Y - got.Y) < 0.5f, $"export {e} ({x},{y}): {got} vs {want}");
            }

            grids[e] = grid;
        }

        Assert.Equal(4, grids.Count);
        var (target, grid0) = grids.First();
        var centre = grid0.World(100.5f, 60.5f); // a quad centre: a 4 m square takes 3 x 3 quads
        var hole = new TerrainHole(Tile, TerrainHoleShape.Square, centre.X, centre.Y, 400f);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Holes.ssproj"), "Holes");
        project.Apply(new TerrainHoleOp(hole));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels, l => l.PackagePath == Tile);
        Assert.Equal(9, level.Report.TerrainHoleCells);

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var after = ModdableAssets.ReadPackage(written, Tile);
        Assert.Equal(stock.UAsset, after.UAsset); // the header, sizes and offsets as cooked
        Assert.Equal(stock.UExp.Length, after.UExp.Length);

        // Exactly the nine cells open; every changed byte is a material byte of one of them or a vertex bit of a corner.
        var block = stock.ReadProperties(target);
        var field = LandscapeCollision.Read(stock.GetExportBytes(target), block.EndOffset)!.Value;
        var cells = new HashSet<(int, int)>();
        for (var qx = 99; qx <= 101; qx++)
        {
            for (var qy = 59; qy <= 61; qy++)
            {
                cells.Add(TerrainHoles.CellOf(field, qx, qy));
            }
        }

        var allowed = new HashSet<int>();
        foreach (var (r, c) in cells)
        {
            allowed.UnionWith([field.SampleAt(r, c) + 2, field.SampleAt(r, c) + 3, field.SampleAt(r + 1, c) + 3, field.SampleAt(r, c + 1) + 3, field.SampleAt(r + 1, c + 1) + 3]);
        }

        var changed = 0;
        for (var b = 0; b < stock.UExp.Length; b++)
        {
            if (stock.UExp[b] != after.UExp[b])
            {
                changed++;
                Assert.Contains(b - block.UExpBaseOffset, allowed);
            }
        }

        var now = after.GetExportBytes(target);
        var was = stock.GetExportBytes(target);
        for (var r = 0; r < field.Rows; r++)
        {
            for (var c = 0; c < field.Columns; c++)
            {
                var at = field.SampleAt(r, c);
                Assert.Equal(BinaryPrimitives.ReadInt16LittleEndian(was.AsSpan(at)), BinaryPrimitives.ReadInt16LittleEndian(now.AsSpan(at)));
                if (r < field.Rows - 1 && c < field.Columns - 1)
                {
                    Assert.Equal(cells.Contains((r, c)), TerrainHoles.IsHole(now, field, r, c));
                    Assert.False(TerrainHoles.IsHole(was, field, r, c));
                }
            }
        }

        // The inner vertex (all four cells open) lost its collision-vertex bit, the rim keeps or gains one.
        var (cr, cc) = TerrainHoles.CellOf(field, 100, 60);
        Assert.Equal(0, now[field.SampleAt(cr, cc) + 3] & 0x80);
        Assert.Equal(0x80, now[field.SampleAt(cr - 1, cc - 1) + 3] & 0x80);

        // The game's reader (CUE4Parse) loads the written tile, every export, and the bulk file is the stock one.
        var loaded = written.LoadPackage(Tile);
        Assert.Equal(stock.Exports.Count, loaded.GetExports().Count());
        Assert.True(written.TryGetPackageFile(Tile, out var file));
        Assert.Equal(ProjectExporter.ReadPackageFiles(catalog, catalog.TryGetPackageFile(Tile, out var f0) ? f0 : null!).UBulk, ProjectExporter.ReadPackageFiles(written, file).UBulk);
        output.WriteLine($"hole at ({centre.X:0}, {centre.Y:0}): {level.Report.TerrainHoleCells} cells, {changed} bytes changed in {stock.UExp.Length}");

        // Undo: a second hole cut and undone leaves the tile exactly as with the first alone; undoing the first leaves nothing to export.
        var bytes = File.ReadAllBytes(Path.ChangeExtension(Path.Combine([result.StagingDirectory, .. file.Path.Replace('\\', '/').Split('/')]), ".uexp"));
        project.Apply(new TerrainHoleOp(hole with { Shape = TerrainHoleShape.Circle, X = centre.X + 3000f }));
        project.Undo();
        var again = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out2"), WritePak = false });
        Assert.Equal(bytes, File.ReadAllBytes(Path.ChangeExtension(Path.Combine([again.StagingDirectory, .. file.Path.Replace('\\', '/').Split('/')]), ".uexp")));
        project.Undo();
        Assert.True(project.State.IsEmpty);
    }
}
