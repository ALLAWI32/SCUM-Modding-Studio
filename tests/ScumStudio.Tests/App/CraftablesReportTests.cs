using ScumStudio.App.Services;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Modding;

namespace ScumStudio.Tests.App;

/// <summary>
/// Review 3.5.1 (layout): a Craftables pak export wrote no report, so the owner's main case (a project of two cabins and no
/// map edits, exported whole or from the Craftables page) left a folder of paks without the "## Install" note, the Projects
/// page's Report row empty, and an earlier refusal's NOT EXPORTED report beside the pak that was just written. Synthetic game
/// files (<see cref="IngredientFixture"/>), so these run without the paks.
/// </summary>
public sealed class CraftablesReportTests : IDisposable
{
    private readonly IngredientFixture _game = new();

    public void Dispose() => _game.Dispose();

    private static CraftablesFile Kit() => new()
    {
        Items = [new Craftable { Name = "Rifle Kit", Source = IngredientFixture.M16, Mesh = IngredientFixture.M16, Kind = CraftKind.Item, Ingredients = [new CraftIngredient("CI_Lead_Plate", 2)] }],
    };

    [Fact]
    public async Task ACraftablesOnlyProjectExportedWholeWritesTheFoldersReportOverAnEarlierRefusal()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Workspace.OpenLooseAsync(_game.Root, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "MyMod");
        Kit().Save(project.DirectoryPath);

        // What the folder held: the project's pak of an export when the project still had map edits, and a later refusal's report.
        var output = ctx.Combine("out");
        var client = ProjectExporter.ExportFolder(output, "MyMod", pages: false, ProjectSourceRole.Client);
        Directory.CreateDirectory(client);
        var earlier = Path.Combine(client, ProjectExporter.PakFileName("MyMod", ProjectExporter.DefaultPakChunkIndex));
        File.WriteAllText(earlier, "an earlier export's pak");
        File.WriteAllText(Path.Combine(client, ProjectExporter.ReportFileName), "# MyMod — Client export: NOT EXPORTED\n");

        var result = Assert.Single(await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, output, null, false), ProgressSink.Null));
        Assert.Equal(Path.Combine(client, CraftablesExporter.PakFileName("MyMod", ProjectExporter.DefaultPakChunkIndex)), result.PakPath);
        Assert.Equal(Path.Combine(client, ProjectExporter.ReportFileName), result.ReportPath);
        var report = File.ReadAllText(result.ReportPath!);
        Assert.DoesNotContain(ProjectExporter.NotExported, report.Split('\n')[0], StringComparison.Ordinal);
        Assert.StartsWith("# MyModCraftables", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("## Install", StringComparison.Ordinal) is >= 0 and var install && install < report.IndexOf("## Details", StringComparison.Ordinal), report);
        Assert.Contains(Path.GetFileName(result.PakPath!), report, StringComparison.Ordinal);

        // The earlier project pak is named as not this export's (it is never deleted by a craftables-only export).
        Assert.True(File.Exists(earlier));
        Assert.Contains(result.Warnings, w => w.Contains(Path.GetFileName(earlier), StringComparison.Ordinal) && w.Contains("EARLIER export", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(earlier), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCraftablesPagesPakHasItsOwnReport()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Workspace.OpenLooseAsync(_game.Root, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "MyMod");
        Kit().Save(project.DirectoryPath);
        var output = ctx.Combine("out");

        var result = Assert.Single(await ModExportService.ExportModuleAsync(ctx.Services, project, ExportModule.Craftables, output, includeServer: false, ProgressSink.Null));
        var pages = ProjectExporter.ExportFolder(output, "MyMod", pages: true, ProjectSourceRole.Client);
        Assert.Equal(Path.Combine(pages, ModuleExport.ReportFileName(ExportModule.Craftables)), result.ReportPath);
        var report = File.ReadAllText(result.ReportPath!);
        Assert.Contains("## Install", report, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(result.PakPath!), report, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectExporter.NotExported, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BesideTheProjectsPakItsSectionIsAddedToThatReport()
    {
        // The project's pak of the same export wrote export-report.md (its install note covers the Craftables pak too).
        var output = Path.Combine(_game.Root, "out");
        var client = ProjectExporter.ExportFolder(output, "MyMod", pages: false, ProjectSourceRole.Client);
        Directory.CreateDirectory(client);
        var main = Path.Combine(client, ProjectExporter.ReportFileName);
        File.WriteAllText(main, "# MyMod — Client export\n\n## Install\n\n- Files: pakchunk900-MyMod_P.pak\n");

        var result = await CraftablesExporter.ExportAsync(Kit(), "MyMod", _game.Catalog, new ExportOptions { OutputDirectory = output }, ProjectSourceRole.Client, alone: false);
        Assert.Equal(main, result!.ReportPath);
        var report = File.ReadAllText(main);
        Assert.StartsWith("# MyMod — Client export\n", report, StringComparison.Ordinal);
        Assert.Contains("## Craftables pak", report, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(result.PakPath!), report, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(client), f => Path.GetFileName(f) == ModuleExport.ReportFileName(ExportModule.Craftables));
    }
}
