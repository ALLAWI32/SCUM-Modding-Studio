using ScumStudio.Level.World;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// <see cref="PackageDependencyWalker"/>: the reference closure of level packages, used to compute the exact file list
/// a map cell needs (extraction) and what a loose slice still lacks. Real-data tests run on the map slice.
/// </summary>
public sealed class PackageDependencyWalkerTests(ITestOutputHelper output)
{
    [Fact]
    public void FoldersAreGroupedAndCounted()
    {
        Assert.Equal("/Game/A/B/", DependencyReport.FolderOf("/Game/A/B/C"));
        Assert.Equal("/", DependencyReport.FolderOf("/Game"));
        var groups = DependencyReport.GroupByFolder(["/Game/X/a", "/Game/Y/b", "/Game/X/c", "/game/x/d"]);
        Assert.Equal(2, groups.Count);
        Assert.Equal(("/Game/X/", 3), (groups[0].Key, groups[0].Value));
        Assert.Equal(("/Game/Y/", 1), (groups[1].Key, groups[1].Value));
    }

    [MapSliceFact]
    public void DirectImportsOfTheSaloonLevelArePresentInTheSlice()
    {
        using var catalog = MapSlice.Open();
        var walker = new PackageDependencyWalker(catalog);
        var level = MapSlice.MapsPath + "A_0_Outpost_Ext_Saloon";

        var direct = walker.ReadReferencedPackages(level);
        Assert.NotEmpty(direct);
        Assert.All(direct, p => Assert.StartsWith("/", p));
        Assert.DoesNotContain(direct, p => p.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase));

        var report = walker.Walk([level], maxDepth: 1);
        Assert.Equal([level], report.Roots);
        Assert.Equal(1, report.Depth);
        Assert.Contains(level, report.Present);
        Assert.True(report.Present.Count > 1, "the saloon level imports at least its Blueprint class package");
        Assert.All(report.Files, f => Assert.StartsWith("SCUM/Content/", f));
        Assert.All(report.Present, p => Assert.True(catalog.PackageExists(p), p));
        Assert.All(report.Missing, p => Assert.False(catalog.PackageExists(p), p));
        Assert.All(report.Missing, p => Assert.StartsWith("/Game/", p));
        Assert.Empty(report.Present.Intersect(report.Missing, StringComparer.OrdinalIgnoreCase));

        // Every present package header is accompanied by its .uexp in the file list.
        var files = new HashSet<string>(report.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var header in report.Files.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(header[..header.LastIndexOf('.')] + ".uexp", files);
        }

        output.WriteLine($"saloon level: {direct.Count} direct package imports; depth 1 closure: {report.Present.Count} present, {report.Missing.Count} missing");
    }

    [MapSliceFact]
    public void WholeCellClosureReportsWhatTheSliceLacks()
    {
        using var catalog = MapSlice.Open();
        var index = WorldIndex.FromCatalog(catalog);
        var roots = index.Sublevels.Where(p => p.Cell == MapCell.Parse("A_0")).Select(p => p.PackagePath).ToList();
        Assert.True(roots.Count >= 20, $"expected the A_0 sublevels in the slice, found {roots.Count}");

        var report = new PackageDependencyWalker(catalog).Walk(roots);
        Assert.Equal(roots.Count, report.Roots.Count);
        Assert.True(report.Present.Count > roots.Count);
        Assert.True(report.Files.Count >= report.Present.Count * 2, "header + uexp per present package");
        Assert.NotEmpty(report.Missing); // the slice holds only the Outpost buildings, not every prop/material it references
        Assert.Empty(report.Warnings);

        // Landscape tiles pull the shared layer infos and their built data.
        Assert.Contains(report.Present.Concat(report.Missing), p => p.Contains("_sharedassets/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Present, p => p.EndsWith("_BuiltData", StringComparison.OrdinalIgnoreCase));

        var byFolder = report.MissingByFolder();
        Assert.Equal(report.Missing.Count, byFolder.Sum(p => p.Value));
        output.WriteLine($"A_0: {report.Roots.Count} levels → {report.Present.Count} present packages ({report.Files.Count} files), {report.Missing.Count} missing in {byFolder.Count} folders, {report.Unresolved.Count} unresolved, depth {report.Depth}");
        foreach (var (folder, count) in byFolder.Take(10))
        {
            output.WriteLine($"  {count,5}  {folder}");
        }
    }
}
