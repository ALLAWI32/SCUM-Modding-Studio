using ScumStudio.App.Services;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (3.5.0): two cabins exported from the Craftables page and then the whole project left his output folder with four
/// staging folders, loose notes and the same craftables twice ("it makes me dizzy; put it in a folder for me, one mod, just
/// the pak"). Every export now lands in the mod's own folder with only what is copied into the game.
/// </summary>
public sealed class ExportLayoutTests
{
    [Fact]
    public void TheModsFolderIsNamedAfterItAndThePagePaksGetTheirOwn()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath("out"), "My_Mod"), ProjectExporter.ModFolder("out", "My Mod", pages: false));
        Assert.Equal(Path.Combine(Path.GetFullPath("out"), "My_Mod - pages"), ProjectExporter.ModFolder("out", "My Mod", pages: true));
        Assert.Equal(Path.Combine(Path.GetFullPath("out"), "My_Mod", "Server"), ProjectExporter.ExportFolder("out", "My Mod", pages: false, ProjectSourceRole.Server));
    }

    [Fact]
    public async Task AnExportLandsInTheModsOwnFolderWithOnlyWhatIsInstalled()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Tidy");
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));

        // What an export before 3.5.1 left loose in the output folder: staging, notes, this mod's pak, and another mod's pak.
        var output = ctx.Combine("out");
        var old = Directory.CreateDirectory(Path.Combine(output, ProjectExporter.ClientFolderName)).FullName;
        Directory.CreateDirectory(Path.Combine(old, ProjectExporter.StagingFolderName, "SCUM"));
        File.WriteAllText(Path.Combine(old, ProjectExporter.StagingFolderName, "SCUM", "x.txt"), "staged");
        Directory.CreateDirectory(Path.Combine(old, "staging-craftables"));
        File.WriteAllText(Path.Combine(old, ProjectExporter.PakFileName("Tidy", ProjectExporter.DefaultPakChunkIndex)), "old");
        File.WriteAllText(Path.Combine(old, ProjectExporter.PakFileName("Other", ProjectExporter.DefaultPakChunkIndex)), "someone else's");
        File.WriteAllText(Path.Combine(old, ProjectExporter.ReportFileName), "old report");
        File.WriteAllText(Path.Combine(old, ProjectExporter.ItemSpawnersFileName), $"{SyntheticLevels.LevelPath}|Fridge_Added|/Game/Items/Fridge.Fridge_C\n");

        var results = await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, output, null, false), ProgressSink.Null);
        var result = Assert.Single(results);

        // The mod's own folder: Client inside, with the pak, its signature when there is one, and the report; nothing else shows.
        var client = ProjectExporter.ExportFolder(output, "Tidy", pages: false, ProjectSourceRole.Client);
        Assert.Equal(Path.Combine(client, ProjectExporter.PakFileName("Tidy", ProjectExporter.DefaultPakChunkIndex)), result.PakPath);
        Assert.Equal(Path.Combine(client, ProjectExporter.ReportFileName), result.ReportPath);
        var shown = Directory.GetFileSystemEntries(client).Select(Path.GetFileName).ToList();
        Assert.All(shown, name => Assert.True(name == ProjectExporter.WorkFolderName || Path.GetExtension(name) is ".pak" or ".sig" or ".md", name));
        var work = new DirectoryInfo(Path.Combine(client, ProjectExporter.WorkFolderName));
        Assert.True(work.Attributes.HasFlag(FileAttributes.Hidden), "the work folder is hidden");
        Assert.Equal(Path.Combine(work.FullName, ProjectExporter.StagingFolderName), result.StagingDirectory);
        Assert.True(File.Exists(Path.Combine(work.FullName, ProjectExporter.ItemSpawnersFileName)), "the notes moved into the work folder");

        // The report says what to copy first.
        var report = File.ReadAllText(result.ReportPath!);
        Assert.True(report.IndexOf("## Install", StringComparison.Ordinal) < report.IndexOf("## Details", StringComparison.Ordinal), report);
        Assert.Contains(Path.GetFileName(result.PakPath!), report, StringComparison.Ordinal);

        // The earlier export's placed spawner is still known, from the notes carried over.
        var gone = Assert.Single(result.RemovedItemSpawners);
        Assert.Contains("Fridge_Added", gone, StringComparison.Ordinal);
        Assert.Contains("earlier export", gone, StringComparison.Ordinal);

        // The old layout: this mod's files and every staging folder are gone, the other mod's pak and the report beside it stay.
        Assert.False(Directory.Exists(Path.Combine(old, ProjectExporter.StagingFolderName)));
        Assert.False(Directory.Exists(Path.Combine(old, "staging-craftables")));
        Assert.False(File.Exists(Path.Combine(old, ProjectExporter.PakFileName("Tidy", ProjectExporter.DefaultPakChunkIndex))));
        Assert.False(File.Exists(Path.Combine(old, ProjectExporter.ItemSpawnersFileName)));
        Assert.True(File.Exists(Path.Combine(old, ProjectExporter.PakFileName("Other", ProjectExporter.DefaultPakChunkIndex))));
        Assert.True(File.Exists(Path.Combine(old, ProjectExporter.ReportFileName)));

        // Exported again: the same folder, replaced, still tidy; the old folder loses nothing more.
        var again = Assert.Single(await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, output, null, false), ProgressSink.Null));
        Assert.Equal(result.PakPath, again.PakPath);
        Assert.Empty(again.RemovedItemSpawners);
        Assert.Equal(shown.Order(StringComparer.Ordinal), Directory.GetFileSystemEntries(client).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(old, ProjectExporter.PakFileName("Other", ProjectExporter.DefaultPakChunkIndex))));
    }

    /// <summary>
    /// Review 3.5.1: the tidy-up ran last and unguarded, so an old pak the running server holds open (an output folder that is
    /// the server's ~mods) turned a successful export into a failure; and the project's pak deleted the old Craftables pak
    /// before the Craftables pak of the same export was built (refused, no Craftables pak was left anywhere).
    /// </summary>
    [Fact]
    public void AnOldPakHeldOpenStaysAndTheOldCraftablesPakWaitsForItsOwnExport()
    {
        var output = Path.Combine(Path.GetTempPath(), "scumstudio-layout-" + Guid.NewGuid().ToString("N"));
        var old = Directory.CreateDirectory(Path.Combine(output, ProjectExporter.ClientFolderName)).FullName;
        try
        {
            var pak = Path.Combine(old, ProjectExporter.PakFileName("Mod", 900));
            var sig = pak[..^4] + ".sig";
            var craftables = Path.Combine(old, CraftablesExporter.PakFileName("Mod", 900));
            File.WriteAllText(pak, "pak");
            File.WriteAllText(sig, "sig");
            File.WriteAllText(craftables, "craftables");
            Directory.CreateDirectory(Path.Combine(old, ProjectExporter.StagingFolderName));
            using (new FileStream(pak, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var removed = ProjectExporter.RemoveOldLayout(output, "Mod", 900, ProjectSourceRole.Client, keepCraftables: true);
                Assert.Equal([Path.Combine(old, ProjectExporter.StagingFolderName), sig], removed);
                Assert.True(File.Exists(pak), "held open: it stays");
                Assert.True(File.Exists(craftables), "the Craftables pak of the same export replaces it");
            }

            var later = ProjectExporter.RemoveOldLayout(output, "Mod", 900, ProjectSourceRole.Client);
            Assert.Contains(pak, later);
            Assert.Contains(craftables, later);
            Assert.False(Directory.Exists(old));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// Review 3.5.1: a staging-only run (CLI --no-pak) deleted the old layout's pak and .sig although it wrote no pak, and the
    /// old notes were moved before the package check (a refused export changed the old folder).
    /// </summary>
    [Fact]
    public async Task AStagingOnlyRunKeepsTheOldPakAndTheOldNotes()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var catalog = global::ScumStudio.Assets.Catalog.AssetCatalog.OpenLoose(game);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Tidy");
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
        var output = ctx.Combine("out");
        var old = Directory.CreateDirectory(Path.Combine(output, ProjectExporter.ClientFolderName)).FullName;
        var pak = Path.Combine(old, ProjectExporter.PakFileName("Tidy", ProjectExporter.DefaultPakChunkIndex));
        File.WriteAllText(pak, "old");
        File.WriteAllText(pak[..^4] + ".sig", "old");
        var notes = Path.Combine(old, ProjectExporter.ItemSpawnersFileName);
        File.WriteAllText(notes, $"{SyntheticLevels.LevelPath}|Fridge_Added|/Game/Items/Fridge.Fridge_C\n");

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = output, WritePak = false });
        Assert.Null(result.PakPath);
        Assert.True(File.Exists(pak) && File.Exists(pak[..^4] + ".sig"), "the only pak the user has stays");
        Assert.True(File.Exists(notes), "the old notes are copied, not moved");
        Assert.True(File.Exists(Path.Combine(ProjectExporter.ExportFolder(output, "Tidy", pages: false, ProjectSourceRole.Client), ProjectExporter.WorkFolderName, ProjectExporter.ItemSpawnersFileName)));
    }

    /// <summary>
    /// Review 3.5.1: a page exported without its server cook removes that page's stale server pak; with one page pak left the
    /// server's Registry pak (and its refusal report) went only when no page pak was left, although one page pak carries its
    /// own records and the report told the user to copy every pak in the folder.
    /// </summary>
    [Fact]
    public async Task TheServersRegistryPakGoesWhenOnlyOnePagePakIsLeft()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Tidy");
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
        var output = ctx.Combine("out");
        var server = Directory.CreateDirectory(ProjectExporter.ExportFolder(output, "Tidy", pages: true, ProjectSourceRole.Server)).FullName;
        var chunk = ProjectExporter.DefaultPakChunkIndex;
        var paks = new[] { ModuleExport.PakFileName("Tidy", ExportModule.Map, chunk), ModuleExport.PakFileName("Tidy", ExportModule.Vehicles, chunk), ModuleExport.RegistryPakFileName("Tidy", chunk) };
        foreach (var name in paks)
        {
            File.WriteAllText(Path.Combine(server, name), "old");
            File.WriteAllText(Path.Combine(server, name[..^4] + ".sig"), "old");
        }

        File.WriteAllText(Path.Combine(server, ModuleExport.RegistryReportFileName), "# Tidy-Registry — Server export: NOT EXPORTED\n");

        await ModExportService.ExportModuleAsync(ctx.Services, project, ExportModule.Map, output, includeServer: false, ProgressSink.Null);
        Assert.Equal(
            [paks[1], paks[1][..^4] + ".sig"],
            Directory.GetFiles(server).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEmptyOldFolderGoesOnceOnlyThisModsFilesWereInIt()
    {
        var output = Path.Combine(Path.GetTempPath(), "scumstudio-layout-" + Guid.NewGuid().ToString("N"));
        var old = Directory.CreateDirectory(Path.Combine(output, ProjectExporter.ServerFolderName)).FullName;
        try
        {
            File.WriteAllText(Path.Combine(old, ProjectExporter.PakFileName("Mod", 900)), "pak");
            File.WriteAllText(Path.Combine(old, ProjectExporter.PakFileName("Mod", 900)[..^4] + ".sig"), "sig");
            File.WriteAllText(Path.Combine(old, "export-report-vehicles.md"), "report");
            File.WriteAllText(Path.Combine(old, ModPackageValidator.ListFileName), "check");
            Directory.CreateDirectory(Path.Combine(old, "staging-vehicles-mod"));

            var removed = ProjectExporter.RemoveOldLayout(output, "Mod", 900, ProjectSourceRole.Server);
            Assert.Equal(6, removed.Count); // staging, pak, sig, report, check list, the folder itself
            Assert.False(Directory.Exists(old));
            Assert.Empty(ProjectExporter.RemoveOldLayout(output, "Mod", 900, ProjectSourceRole.Server));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
