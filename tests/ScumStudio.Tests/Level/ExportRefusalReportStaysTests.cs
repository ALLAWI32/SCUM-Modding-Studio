using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Review 3.5.1 (regression lens): a refused Craftables pak writes <c>export-report-craftables.md</c> saying NOT EXPORTED
/// (<see cref="ProjectExporter.WriteRefusal"/>); the report is cleared only when a Craftables pak passes the check
/// (<see cref="ProjectExporter.ClearRefusal"/>). The other way out of a refusal, removing every craftable (or unticking the
/// box), deletes the Craftables pak and returns before that clearing, so the folder keeps a NOT EXPORTED report for a pak
/// that no longer exists and that the user never meant to export again.
/// </summary>
public sealed class ExportRefusalReportStaysTests
{
    [Fact]
    public async Task RemovingTheCraftablesAfterARefusalLeavesNoRefusalReport()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(game, new AssetCatalogOptions());

        var role = Directory.CreateDirectory(ProjectExporter.ExportFolder(temp.Combine("out"), "MyMapMod", pages: false, ProjectSourceRole.Client)).FullName; // the mod's own folder (3.5.1 layout)
        var pak = Path.Combine(role, CraftablesExporter.PakFileName("MyMapMod", ProjectExporter.DefaultPakChunkIndex));
        File.WriteAllBytes(pak, [1]);
        var report = Path.Combine(role, ModuleExport.ReportFileName(ExportModule.Craftables));
        ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["y"] }), "MyMapMod", ProjectSourceRole.Client, report, pak, []);
        Assert.EndsWith(ProjectExporter.NotExported, File.ReadLines(report).First().TrimEnd('\r'));

        // The user removes every craftable (or unticks the box) and exports again: the pak goes, as before ...
        var result = await CraftablesExporter.ExportAsync(new CraftablesFile(), "MyMapMod", catalog, new ExportOptions { OutputDirectory = temp.Combine("out") }, ProjectSourceRole.Client);
        Assert.Null(result);
        Assert.False(File.Exists(pak));

        // ... and nothing in the folder may still say that this export's Craftables pak was refused.
        Assert.False(File.Exists(report), "the NOT EXPORTED report of the earlier refusal stays beside no pak");

        // The Craftables page's own pak the same way; a report that is no refusal stays.
        var pagePak = Path.Combine(role, ModuleExport.PakFileName("MyMapMod", ExportModule.Craftables, ProjectExporter.DefaultPakChunkIndex));
        File.WriteAllBytes(pagePak, [1]);
        ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["y"] }), "MyMapMod-Craftables", ProjectSourceRole.Client, report, pagePak, []);
        Assert.Equal([pagePak], CraftablesExporter.RemoveStaleModule(role, "MyMapMod", ProjectExporter.DefaultPakChunkIndex));
        Assert.False(File.Exists(report));
        File.WriteAllText(report, "# MyMapModCraftables — Client export\n");
        CraftablesExporter.RemoveStale(role, "MyMapMod", ProjectExporter.DefaultPakChunkIndex);
        Assert.True(File.Exists(report));
    }

    /// <summary>
    /// The same for the Registry pak of page exports: once no page pak is left to add a record, the Registry pak goes and so
    /// does an earlier refusal's NOT EXPORTED <c>export-report-registry.md</c> (<see cref="ModuleExport.WriteRegistryPakAsync"/>
    /// returned before clearing it).
    /// </summary>
    [Fact]
    public async Task NoPagePakLeftAfterARegistryRefusalLeavesNoRefusalReport()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(game, new AssetCatalogOptions());

        var role = Directory.CreateDirectory(ProjectExporter.ExportFolder(temp.Combine("out"), "MyMapMod", pages: true, ProjectSourceRole.Client)).FullName; // the pages folder (3.5.1 layout)
        var pak = Path.Combine(role, ModuleExport.RegistryPakFileName("MyMapMod", ProjectExporter.DefaultPakChunkIndex));
        File.WriteAllBytes(pak, [1]);
        var report = Path.Combine(role, ModuleExport.RegistryReportFileName);
        ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["y"] }), "MyMapMod-Registry", ProjectSourceRole.Client, report, pak, []);
        Assert.EndsWith(ProjectExporter.NotExported, File.ReadLines(report).First().TrimEnd('\r'));

        Assert.Null(await ModuleExport.WriteRegistryPakAsync("MyMapMod", catalog, new ExportOptions { OutputDirectory = temp.Combine("out") }, ProjectSourceRole.Client));
        Assert.False(File.Exists(pak));
        Assert.False(File.Exists(report), "the NOT EXPORTED report of the earlier Registry refusal stays beside no pak");
    }
}
