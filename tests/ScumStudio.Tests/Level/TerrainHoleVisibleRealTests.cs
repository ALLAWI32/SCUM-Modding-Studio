using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// A 4 x 4 m hole cut where the game can draw it (a "green" component, one that already has the visibility layer), on the
/// real game files (<c>SCUM_PAKS</c>, key from this PC's store): collision and visibility open exactly there, every mip
/// follows, every other byte stays as cooked, and undo gives the tiles back byte for byte.
/// </summary>
public sealed class TerrainHoleVisibleRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_A_0_1c";

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public async Task AFourMetreHoleInAGreenComponentOpensCollisionAndVisibilityAndNothingElse()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        Assert.True(catalog.TryGetPackageFile(Tile, out var stockFile));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, stockFile);
        var stock = CookedPackage.Parse(uasset, uexp, ubulk, Tile);
        var components = TerrainHoleLayers.Components(stock);
        var places = TerrainHoleLayers.Places(Tile, stock, ubulk!.Length);
        Assert.Equal(4, places.Count);
        var green = places.First(p => p.Visible);
        var render = components.Single(c => c.Name == green.Component);
        var target = TerrainHoleLayers.VisibilityOf(stock, components, render, ubulk.Length, out _)!;
        var vb = LandscapeWeightmap.ByteOfChannel(target.Channel);
        var width = target.Texture.Width;
        byte Vis(byte[] mip0, int x, int y)
        {
            var (tx, ty) = LandscapeWeightmap.TexelsOf(x, y, render.SubsectionQuads, render.Subsections).First();
            return mip0[(((ty * width) + tx) * 4) + vb];
        }

        // The collision component at the same place and its grid.
        var collision = Enumerable.Range(0, stock.Exports.Count).Single(e => stock.GetExportClassName(e) == "LandscapeHeightfieldCollisionComponent"
            && (stock.ReadProperties(e).Find("SectionBaseX")?.Value as IntValue)?.Value == render.SectionBase.X
            && (stock.ReadProperties(e).Find("SectionBaseY")?.Value as IntValue)?.Value == render.SectionBase.Y);
        var block = stock.ReadProperties(collision);
        var grid = TerrainHoles.GridOf(stock, collision, block)!.Value;
        var field = LandscapeCollision.Read(stock.GetExportBytes(collision), block.EndOffset)!.Value;

        // A quad whose surroundings have no visibility and no hole in the stock tile.
        var stockMips = target.Texture.Mips.Select(m => LandscapeWeightmap.Bytes(m, uexp, ubulk).ToArray()).ToList();
        var was = stock.GetExportBytes(collision);
        var (qx, qy) = (from y in Enumerable.Range(40, 170)
                        from x in Enumerable.Range(40, 170)
                        where Enumerable.Range(-6, 13).All(dy => Enumerable.Range(-6, 13).All(dx => Vis(stockMips[0], x + dx, y + dy) == 0
                            && !TerrainHoles.IsHole(was, field, TerrainHoles.CellOf(field, x + dx, y + dy).Row, TerrainHoles.CellOf(field, x + dx, y + dy).Column)))
                        select (x, y)).First();
        var centre = grid.World(qx + 0.5f, qy + 0.5f);
        var hole = new TerrainHole(Tile, TerrainHoleShape.Square, centre.X, centre.Y, 400f);
        var quads = TerrainHoles.Quads(grid, [hole]);
        Assert.Equal(9, quads.Count); // 4 m on the 1.5 m grid: 3 x 3 quads around (qx, qy)
        Assert.All(quads, q => Assert.True(Math.Abs(q.X - qx) <= 1 && Math.Abs(q.Y - qy) <= 1));
        output.WriteLine($"{green.Component} of {Tile}: hole at quad ({qx}, {qy}), world ({centre.X:0}, {centre.Y:0}); visibility in weightmap export {target.Texture.Export} channel {target.Channel}");

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Holes.ssproj"), "Holes");
        project.Apply(new TerrainHoleOp(hole));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels, l => l.PackagePath == Tile);
        Assert.Equal(9, level.Report.TerrainHoleCells);
        Assert.Equal(9, level.Report.TerrainHoleVisibleSamples);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("collision only", StringComparison.Ordinal));

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        Assert.True(written.TryGetPackageFile(Tile, out var writtenFile));
        var (wAsset, wExp, wBulk) = ProjectExporter.ReadPackageFiles(written, writtenFile);
        Assert.Equal(uasset, wAsset); // the header, sizes and offsets as cooked
        Assert.Equal(uexp.Length, wExp.Length);
        Assert.Equal(ubulk.Length, wBulk!.Length);
        var after = CookedPackage.Parse(wAsset, wExp, wBulk, Tile);

        // Collision: exactly the nine cells (their material bytes and corner vertex bits).
        var cells = quads.Select(q => TerrainHoles.CellOf(field, q.X, q.Y)).ToHashSet();
        var allowedUExp = new HashSet<int>();
        foreach (var (r, c) in cells)
        {
            foreach (var at in new[] { field.SampleAt(r, c) + 2, field.SampleAt(r, c) + 3, field.SampleAt(r + 1, c) + 3, field.SampleAt(r, c + 1) + 3, field.SampleAt(r + 1, c + 1) + 3 })
            {
                allowedUExp.Add(block.UExpBaseOffset + at);
            }
        }

        var now = after.GetExportBytes(collision);
        for (var r = 0; r < field.Rows - 1; r++)
        {
            for (var c = 0; c < field.Columns - 1; c++)
            {
                Assert.Equal(cells.Contains((r, c)) || TerrainHoles.IsHole(was, field, r, c), TerrainHoles.IsHole(now, field, r, c));
            }
        }

        // Visibility, mip 0: 255 at exactly the samples the collision cook turns into those cells, the rest as cooked.
        var samples = quads.Select(q => TerrainHoleLayers.SampleOfQuad(q.X, q.Y)).ToHashSet();
        var writtenMips = target.Texture.Mips.Select(m => LandscapeWeightmap.Bytes(m, wExp, wBulk).ToArray()).ToList();
        for (var y = 0; y <= render.SizeQuads; y++)
        {
            for (var x = 0; x <= render.SizeQuads; x++)
            {
                foreach (var (tx, ty) in LandscapeWeightmap.TexelsOf(x, y, render.SubsectionQuads, render.Subsections))
                {
                    var i = (((ty * width) + tx) * 4) + vb;
                    Assert.Equal(samples.Contains((x, y)) ? (byte)255 : stockMips[0][i], writtenMips[0][i]);
                }
            }
        }

        // Every smaller mip: the stock byte where the mip rule gives the same from the stock and the opened mip 0, the
        // rule's value where it changes; other channels untouched. The hole reaches down the chain.
        var fromStock = stockMips.Select(m => (byte[])m.Clone()).ToList();
        LandscapeWeightmap.BuildMips(fromStock, width, render.SubsectionQuads, render.Subsections, vb);
        var fromOpened = writtenMips.Select(m => (byte[])m.Clone()).ToList();
        LandscapeWeightmap.BuildMips(fromOpened, width, render.SubsectionQuads, render.Subsections, vb);
        var changedPerMip = new int[stockMips.Count];
        for (var m = 0; m < stockMips.Count; m++)
        {
            for (var i = 0; i < stockMips[m].Length; i++)
            {
                byte expected;
                if (i % 4 != vb)
                {
                    expected = stockMips[m][i];
                }
                else if (m == 0)
                {
                    expected = writtenMips[0][i]; // checked sample by sample above
                }
                else
                {
                    expected = fromOpened[m][i] != fromStock[m][i] ? fromOpened[m][i] : stockMips[m][i];
                }

                Assert.Equal(expected, writtenMips[m][i]);
                changedPerMip[m] += writtenMips[m][i] != stockMips[m][i] ? 1 : 0;
            }
        }

        Assert.True(changedPerMip.Take(4).All(n => n > 0), $"changed texels per mip: {string.Join(" ", changedPerMip)}");
        output.WriteLine($"visibility texels changed per mip: {string.Join(" ", changedPerMip)}");

        // Every other byte of the .uexp and the .ubulk as cooked.
        var allowedBulk = new HashSet<int>();
        foreach (var mip in target.Texture.Mips)
        {
            for (var i = vb; i < mip.Length; i += 4)
            {
                (mip.InUBulk ? allowedBulk : allowedUExp).Add(mip.Offset + i);
            }
        }

        var (changedUExp, changedBulk) = (0, 0);
        for (var b = 0; b < uexp.Length; b++)
        {
            if (uexp[b] != wExp[b])
            {
                changedUExp++;
                Assert.Contains(b, allowedUExp);
            }
        }

        for (var b = 0; b < ubulk.Length; b++)
        {
            if (ubulk[b] != wBulk[b])
            {
                changedBulk++;
                Assert.Contains(b, allowedBulk);
            }
        }

        output.WriteLine($"{changedUExp} bytes changed in the .uexp ({uexp.Length}), {changedBulk} in the .ubulk ({ubulk.Length})");

        // The game's reader (CUE4Parse) loads the written tile and its weightmap; the visibility it decodes holes exactly
        // where the written collision has hole cells, the rule the game's own 173 holes follow.
        var loaded = written.LoadPackage(Tile);
        Assert.Equal(stock.Exports.Count, loaded.GetExports().Count());
        var layers = LandscapeLayerReader.ReadPackage(written, Tile, includeGrass: false).Single(l => l.Name == render.Name);
        var n = field.Rows;
        var visCells = Enumerable.Range(0, layers.Holes!.Length).Where(i => layers.Holes[i] > 127).Select(i => (n - 1 - (i % n), i / n)).ToHashSet();
        var holeCells = new HashSet<(int, int)>();
        for (var r = 0; r < n - 1; r++)
        {
            for (var c = 0; c < n - 1; c++)
            {
                if (TerrainHoles.IsHole(now, field, r, c))
                {
                    holeCells.Add((r, c));
                }
            }
        }

        Assert.True(holeCells.SetEquals(visCells), $"{holeCells.Count} hole cells, {visCells.Count} visibility holes");
        Assert.Subset(holeCells, cells);

        // Undo: a second hole cut and undone leaves both files exactly as with the first alone; undoing the first leaves
        // nothing to export (the game's own tile is used).
        var stem = Path.Combine([result.StagingDirectory, .. writtenFile.Path.Replace('\\', '/')[..writtenFile.Path.LastIndexOf('.')].Split('/')]);
        var firstExp = File.ReadAllBytes(stem + ".uexp");
        var firstBulk = File.ReadAllBytes(stem + ".ubulk");
        var second = grid.World(qx + 4.5f, qy + 0.5f);
        project.Apply(new TerrainHoleOp(hole with { Shape = TerrainHoleShape.Circle, X = second.X, Y = second.Y }));
        project.Undo();
        var again = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out2"), WritePak = false });
        var stem2 = Path.Combine([again.StagingDirectory, .. writtenFile.Path.Replace('\\', '/')[..writtenFile.Path.LastIndexOf('.')].Split('/')]);
        Assert.Equal(firstExp, File.ReadAllBytes(stem2 + ".uexp"));
        Assert.Equal(firstBulk, File.ReadAllBytes(stem2 + ".ubulk"));
        project.Undo();
        Assert.True(project.State.IsEmpty);
    }
}
