using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Mcp.Studio;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.App;
using ScumStudio.Tests.Assets;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The studio's own exported paks, copied into the game's Paks folder as the Build page says, are not game files: read as
/// stock they made every earlier craftable "already exist in the game files" and left it out of the next export (P0 #1).
/// </summary>
public sealed class OwnPaksTests
{
    private const string Foo = "/Game/ConZ_Files/Recipes/Placeables/CR_SS_Foo";

    [Fact]
    public void TheStudiosOwnPaksAreNotGameSources()
    {
        string[] mods = ["My Mod"];
        Assert.False(OwnPaks.IsGameSource(ProjectExporter.PakFileName("My Mod", 900), mods));
        Assert.False(OwnPaks.IsGameSource(CraftablesExporter.PakFileName("My Mod", 900), mods));
        Assert.False(OwnPaks.IsGameSource(ProjectExporter.PakFileName("My Mod", 950), mods));
        Assert.False(OwnPaks.IsGameSource(CraftablesExporter.PakFileName("My Mod", 950), mods));
        Assert.False(OwnPaks.IsGameSource("pakchunk901-OtherCraftables_P.pak", mods));
        Assert.False(OwnPaks.IsGameSource(ProjectExporter.PakFileName("My Mod", 50), mods)); // any chunk, by name

        Assert.True(OwnPaks.IsGameSource("pakchunk0-WindowsNoEditor.pak", mods));
        Assert.True(OwnPaks.IsGameSource("pakchunk99-BMW_P.pak", mods));
        Assert.True(OwnPaks.IsGameSource("pakchunk50-Other_P.pak", mods));
        Assert.True(OwnPaks.IsGameSource(ProjectExporter.PakFileName("My Mod", 50), []));
    }

    [Fact]
    public async Task AnInstalledCraftablesPakIsNotReadAsTheGames()
    {
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock");
        await SyntheticPaks.WriteAsync(paks, CraftablesExporter.PakFileName("My Mod", 900), Foo);

        using (var all = AssetCatalog.OpenPaks(paks))
        {
            Assert.True(all.PackageExists(Foo)); // what every export read as "the game files" until now
        }

        var skipped = new List<string>();
        using var game = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { PakFileFilter = OwnPaks.Filter(["My Mod"], skipped) });
        Assert.False(game.PackageExists(Foo));
        Assert.True(game.PackageExists("/Game/A/Stock"));
        Assert.Equal([CraftablesExporter.PakFileName("My Mod", 900)], skipped);
    }

    [Fact]
    public async Task TheWorkspaceLeavesOutTheOpenProjectsPaks()
    {
        using var ctx = AppTestContext.Create();
        var paks = ctx.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock");
        await SyntheticPaks.WriteAsync(paks, CraftablesExporter.PakFileName("My Mod", 900), Foo);
        await SyntheticPaks.WriteAsync(paks, ProjectExporter.PakFileName("My Mod", 50), "/Game/A/Edited");
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });

        var workspace = ctx.Services.Workspace;
        await workspace.ConnectAsync(ProgressSink.Null);
        Assert.False(workspace.Catalog!.PackageExists(Foo));
        Assert.Equal([CraftablesExporter.PakFileName("My Mod", 900)], workspace.SkippedOwnPaks);
        Assert.Contains(CraftablesExporter.PakFileName("My Mod", 900), workspace.StatusText, StringComparison.Ordinal);
        Assert.True(workspace.Catalog.PackageExists("/Game/A/Edited")); // chunk 50: only the open project's name tells

        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "My Mod");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (workspace.SkippedOwnPaks.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(["My Mod"], workspace.OwnModNames);
        Assert.Contains(ProjectExporter.PakFileName("My Mod", 50), workspace.SkippedOwnPaks);
        Assert.False(workspace.Catalog!.PackageExists("/Game/A/Edited"));
    }

    [Fact]
    public async Task OneNameListDecidesWhenASourceIsOpenedAgain()
    {
        Assert.Equal(["Custom", "My Mod"], OwnPaks.ProjectNames("My Mod", "Custom"));
        Assert.Equal(["My Mod"], OwnPaks.ProjectNames("My Mod", " "));
        Assert.Single(OwnPaks.ProjectNames("My Mod", "my mod")); // pak names compare without case

        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock");
        await SyntheticPaks.WriteAsync(paks, CraftablesExporter.PakFileName("Custom", 50), Foo);
        Assert.True(OwnPaks.ChangesSkips(paks, OwnPaks.ProjectNames("My Mod"), OwnPaks.ProjectNames("My Mod", "Custom")));
        Assert.False(OwnPaks.ChangesSkips(paks, OwnPaks.ProjectNames("My Mod"), OwnPaks.ProjectNames("Other")));
        Assert.True(OwnPaks.ChangesSkips(Path.Combine(paks, CraftablesExporter.PakFileName("Custom", 50)), [], ["Custom"]));
    }

    [Fact]
    public async Task TheMcpSourceLeavesOutTheProjectsPaksOnceTheProjectOpens()
    {
        // Game files opened before the project: its pak at chunk 50 is known by the project's name only.
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock");
        await SyntheticPaks.WriteAsync(paks, ProjectExporter.PakFileName("My Mod", 50), "/Game/A/Edited");
        using var host = new HeadlessStudioHost();
        await host.OpenSourceAsync(paks, CancellationToken.None);
        Assert.True(host.Catalog!.PackageExists("/Game/A/Edited"));

        await host.CreateProjectAsync(temp.Combine("projects"), "My Mod", CancellationToken.None);
        Assert.False(host.Catalog!.PackageExists("/Game/A/Edited"));
        Assert.True(host.Catalog.PackageExists("/Game/A/Stock"));
    }

    [Fact]
    public async Task AnExportUnderItsOwnNameLeavesOutTheSamePaksForTheClientAsForTheServer()
    {
        // Exported earlier as "Custom" at chunk 50 and copied into Paks: the client's game files, opened with the project
        // name only, read that pak; the server's, opened with the export's name, did not, so the two cooks disagreed.
        using var ctx = AppTestContext.Create();
        var paks = ctx.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock");
        await SyntheticPaks.WriteAsync(paks, CraftablesExporter.PakFileName("Custom", 50), Foo);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var workspace = ctx.Services.Workspace;
        await workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "My Mod");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (workspace.OwnModNames.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(workspace.Catalog!.PackageExists(Foo));

        new CraftablesFile().Save(project.DirectoryPath); // an emptied list: the export runs and builds nothing
        var request = new ModExportRequest(project, ctx.Combine("out"), "Custom", false, 50);
        Assert.Empty(await ModExportService.ExportAsync(ctx.Services, request, ProgressSink.Null));

        var server = new List<string>();
        using (AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames("My Mod", "Custom"), server) }))
        {
            Assert.Equal([CraftablesExporter.PakFileName("Custom", 50)], server);
        }

        Assert.Equal(server, workspace.SkippedOwnPaks);
        Assert.False(workspace.Catalog!.PackageExists(Foo));
    }
}
