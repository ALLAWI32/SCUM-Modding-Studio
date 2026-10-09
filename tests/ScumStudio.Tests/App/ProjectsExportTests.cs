using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Inspection;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

public sealed class ProjectsExportTests
{
    [Fact]
    public async Task ExportCardBuildsThePakFromTheOpenProject()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var page = new ProjectsPageViewModel(ctx.Services);
        Assert.False(page.CanExport);
        Assert.False(page.ExportCommand.CanExecute(null));
        Assert.Contains("Open a project", page.ExportHint, StringComparison.Ordinal);

        page.NewProjectName = "Outpost cleanup";
        page.NewProjectFolder = ctx.Combine("projects");
        await page.CreateCommand.ExecuteAsync(null);
        Assert.True(page.Session.HasProject);
        Assert.Equal("Outpost cleanup", page.ExportModName);
        Assert.False(page.CanExport);
        Assert.Contains("Nothing to export", page.ExportHint, StringComparison.Ordinal);

        page.Session.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
        Assert.False(page.CanExport);
        Assert.Contains("Connect the game paks", page.ExportHint, StringComparison.Ordinal);

        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        Assert.True(page.CanExport);
        Assert.True(page.ExportCommand.CanExecute(null));
        Assert.Contains("pakchunk900-Outpost_cleanup_P.pak", page.ExportHint, StringComparison.Ordinal);

        var output = ctx.Combine("out");
        page.ExportFolder = output;
        page.ExportServer = false;
        await page.ExportCommand.ExecuteAsync(null);

        var result = Assert.Single(page.LastExport);
        Assert.Equal(Path.Combine(output, "Client", "pakchunk900-Outpost_cleanup_P.pak"), result.PakPath);
        Assert.True(File.Exists(result.PakPath));
        Assert.Equal(2, PakInspector.ReadIndex(result.PakPath!).Entries.Count);
        Assert.Equal(new[] { "StaticMeshActor_1" }, Assert.Single(result.Levels).Report.RemovedActors);
        Assert.True(page.HasExportResult);
        Assert.Contains(page.ExportRows, r => r.Name == "Client pak" && r.Value == result.PakPath);
        Assert.True(page.HasExportWarnings); // loose game files carry no stock .sig
        Assert.Contains(page.ExportWarnings, w => w.Contains(".sig", StringComparison.Ordinal));
        Assert.Equal(output, ctx.Services.Settings.Load().ClientModsOutputFolder);
        Assert.Equal(output, page.LastExportFolder);
        Assert.True(page.OpenExportFolderCommand.CanExecute(null));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title.Contains("Export finished", StringComparison.Ordinal));

        // The server pak needs the server Paks folder from the settings: without it the operation fails as a toast,
        // after the client pak was written again.
        page.ExportServer = true;
        var before = File.GetLastWriteTimeUtc(result.PakPath!);
        await Task.Delay(20);
        await page.ExportCommand.ExecuteAsync(null);
        Assert.True(File.GetLastWriteTimeUtc(result.PakPath!) >= before);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => (t.Message ?? string.Empty).Contains("server Paks folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuickExportWritesTheClientPakAndKnowsAServerCook()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Quick");
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));

        var (results, installed) = await ModExportService.QuickExportAsync(ctx.Services, project, ctx.Combine("exports"), ProgressSink.Null);
        var client = Assert.Single(results);
        Assert.Equal(Path.Combine(ctx.Combine("exports"), "Client", "pakchunk900-Quick_P.pak"), client.PakPath);
        Assert.True(File.Exists(client.PakPath));
        Assert.Empty(installed); // no server cook configured, nothing copied

        var mods = Directory.CreateDirectory(ctx.Combine("server_mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, "pakchunk99-SUV_01_P.pak"), [1]);
        Assert.False(ModExportService.IsServerPaksFolder(mods)); // a mods folder is not the server cook
        var cook = Directory.CreateDirectory(ctx.Combine("server_paks")).FullName;
        File.WriteAllBytes(Path.Combine(cook, "pakchunk0-WindowsServer.pak"), [1]);
        Assert.True(ModExportService.IsServerPaksFolder(cook));
    }

    /// <summary>
    /// Review of craft-1: a craftables-only project whose craftables were all removed can still be exported, and that export
    /// removes the old Craftables pak; unticking the Craftables box in such a project removes it too instead of crashing.
    /// </summary>
    [Fact]
    public async Task AnEmptiedCraftablesProjectRemovesTheOldCraftablesPak()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafts");
        var output = ctx.Combine("out");
        var client = Directory.CreateDirectory(Path.Combine(output, "Client")).FullName;
        var old = Path.Combine(client, CraftablesExporter.PakFileName("Crafts", ProjectExporter.DefaultPakChunkIndex));
        Assert.NotNull(ModExportService.Blocker(ctx.Services, project)); // a new project has nothing to export

        // Unticked with craftables: nothing is built, the old pak goes, no crash.
        var file = new CraftablesFile { Items = [new Craftable { Name = "Oak Table", Source = "/Game/A/SM_T", Mesh = "/Game/A/SM_T" }] };
        file.Save(project.DirectoryPath);
        File.WriteAllText(old, "old");
        var request = new ModExportRequest(project, output, null, false) { IncludeCraftables = false };
        Assert.Null(ModExportService.Blocker(ctx.Services, project));
        Assert.Empty(await ModExportService.ExportAsync(ctx.Services, request, ProgressSink.Null));
        Assert.False(File.Exists(old));

        // Every craftable removed (the page saves the emptied list): still exportable, and the export removes the pak.
        new CraftablesFile().Save(project.DirectoryPath);
        File.WriteAllText(old, "old");
        Assert.Null(ModExportService.Blocker(ctx.Services, project));
        Assert.Empty(await ModExportService.ExportAsync(ctx.Services, request with { IncludeCraftables = true }, ProgressSink.Null));
        Assert.False(File.Exists(old));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => (t.Message ?? string.Empty).Contains(Path.GetFileName(old), StringComparison.Ordinal));
    }
}
