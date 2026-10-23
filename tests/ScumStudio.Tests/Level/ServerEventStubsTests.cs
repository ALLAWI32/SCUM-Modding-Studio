using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Server;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Modding;

namespace ScumStudio.Tests.Level;

/// <summary>
/// D12 (taishan_song; audit C2): the server pak carried imported mods' client-cooked sound events unchanged and the
/// dedicated server dies on them ("Serial size mismatch: Got 12, Expected N"). Every staged event of the server cook
/// becomes the stock server's 12-byte stub; without a stub the server pak is refused; a client pak with stubs is named.
/// </summary>
public sealed class ServerEventStubsTests : IDisposable
{
    private const string ModEvent = "/Game/Mods/Car/WwiseAudio/Event/Car_Ignition";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-stubs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task EveryStagedEventBecomesTheServerStubOutsideWwiseAudioToo()
    {
        var staging = Path.Combine(_dir, "staging");
        Write(staging, ModEvent, Event(ModEvent, client: true));
        Write(staging, "/Game/WwiseAudio/Event/Car_Horn", Event("/Game/WwiseAudio/Event/Car_Horn", client: true));
        Write(staging, "/Game/WwiseAudio/Event/Already", Event("/Game/WwiseAudio/Event/Already", client: false)); // a stub stays as it is

        var replaced = await ServerEventStubs.ApplyAsync(staging, Stub, "SCUM");

        Assert.Equal(new[] { ModEvent, "/Game/WwiseAudio/Event/Car_Horn" }.Order(), replaced.Order());
        foreach (var path in new[] { ModEvent, "/Game/WwiseAudio/Event/Car_Horn" })
        {
            var package = Read(staging, path);
            Assert.Equal(AkAudioEventStubBuilder.StubExportSize, package.Exports[0].SerialSize);
            Assert.Equal(path[(path.LastIndexOf('/') + 1)..], package.ResolveName(package.Exports[0].ObjectName));
        }

        Assert.All(ServerEventStubs.FindEvents(staging), e => Assert.Equal(AkAudioEventStubBuilder.StubExportSize, e.Size));
    }

    [Fact]
    public async Task WithoutAStubTheServerPakIsRefused()
    {
        var staging = Path.Combine(_dir, "staging");
        Write(staging, ModEvent, Event(ModEvent, client: true));

        var ex = await Assert.ThrowsAsync<ServerEventStubException>(() =>
            ServerEventStubs.ApplyAsync(staging, () => throw new ServerEventStubException("no server here"), "SCUM"));
        Assert.Equal(1, ex.Events);
        Assert.Equal(ModEvent, ex.FirstEvent);
        Assert.Contains("Serial size mismatch", ex.Message, StringComparison.Ordinal);

        // Nothing to replace: no stub is needed (a pak without events is never refused for it).
        Assert.Empty(await ServerEventStubs.ApplyAsync(Path.Combine(_dir, "empty"), () => throw new ServerEventStubException("unused"), "SCUM"));
    }

    [Fact]
    public void AFolderWithoutTheServerCookGivesNoStub()
    {
        var paks = Path.Combine(_dir, "Paks");
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), [0]);
        File.WriteAllBytes(Path.Combine(paks, "pakchunk900-Mod-WindowsServer_P.pak"), [0]); // a mod pak is no server cook

        var ex = Assert.Throws<ServerEventStubException>(() => ServerEventStubs.FromServerPaks(paks, null));
        Assert.Contains("pakchunk0*-WindowsServer.pak", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ServerEventStubException>(() => ServerEventStubs.FromServerPaks(Path.Combine(_dir, "missing"), null));
    }

    [Fact]
    public async Task TheServerExportStubsAnImportedModsEventsAndTheClientExportNamesServerStubs()
    {
        var game = Path.Combine(_dir, "game");
        SyntheticGame.WriteGame(game);
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        var paks = Path.Combine(_dir, "Paks");
        Directory.CreateDirectory(paks);
        await new PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));

        // An imported car mod with one client-cooked event, another taken from a server's mods folder (a 12-byte stub).
        var mod = Path.Combine(_dir, "project", ProjectMods.FolderName, "Car");
        Write(mod, ModEvent, Event(ModEvent, client: true));
        Write(mod, "/Game/Mods/Car/WwiseAudio/Event/Car_Stop", Event("/Game/Mods/Car/WwiseAudio/Event/Car_Stop", client: false));
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { LooseOverlays = [mod] });
        using var project = Project.Create(Path.Combine(_dir, "project", "Car.ssproj"), "Car");
        project.Apply(new CloneAssetOp("item", SyntheticGame.Gen, "/Game/T/BP_Ours",
            [new PackagePair(SyntheticGame.Gen, "/Game/T/BP_Ours"), new PackagePair(SyntheticGame.GenEs, "/Game/T/BP_Ours_ES")]));
        var options = new ExportOptions { OutputDirectory = Path.Combine(_dir, "out"), WritePak = false, Mods = [mod], Validate = false }; // the synthetic game names packages it does not hold

        var server = await new ProjectExporter().ExportAsync(project, catalog, options with { ServerEventStubs = Stub }, ProjectSourceRole.Server);
        Assert.Equal(new[] { ModEvent }, server.StubbedEvents);

        // The package check holds the stub to the game's own copy, never to the imported mod's client event it replaced.
        Assert.DoesNotContain(ModPackageValidator.Validate(server.StagingDirectory, catalog).Problems, p => p.Contains("sound", StringComparison.Ordinal));
        Assert.Equal(AkAudioEventStubBuilder.StubExportSize, Read(server.StagingDirectory, ModEvent).Exports[0].SerialSize);
        Assert.Contains("## Sound events (server)", await File.ReadAllTextAsync(server.ReportPath!));

        // No stub to be had: the server pak is refused, never written with the client's event.
        await Assert.ThrowsAsync<ServerEventStubException>(() => new ProjectExporter().ExportAsync(project, catalog,
            options with { ServerEventStubs = () => throw new ServerEventStubException("no server") }, ProjectSourceRole.Server));

        // The client keeps its event and is told about the server stub it carries.
        var client = await new ProjectExporter().ExportAsync(project, catalog, options, ProjectSourceRole.Client);
        Assert.Empty(client.StubbedEvents);
        Assert.NotEqual(AkAudioEventStubBuilder.StubExportSize, Read(client.StagingDirectory, ModEvent).Exports[0].SerialSize);
        Assert.Contains(client.Warnings, w => w.Contains("server-only sound event stub", StringComparison.Ordinal) && w.Contains("Car_Stop", StringComparison.Ordinal));
        Assert.Contains("on every player's PC", await File.ReadAllTextAsync(client.ReportPath!)); // the Install section: the CLIENT folder
    }

    private static AkAudioEventStubBuilder Stub()
    {
        var bytes = Event(AkAudioEventStubBuilder.DefaultStubGamePath, client: false);
        return new AkAudioEventStubBuilder(bytes.UAsset.ToArray(), bytes.UExp.ToArray());
    }

    /// <summary>An AkAudioEvent package: the server's 12-byte stub (tags' None and the guid flag) or a client event with its data.</summary>
    private static PackageBytes Event(string gamePath, bool client)
    {
        var p = new SyntheticLevelPackage();
        p.Name(gamePath); // a cooked package names its own path
        var export = p.Export(gamePath[(gamePath.LastIndexOf('/') + 1)..], p.ScriptClass("/Script/AkAudio", "AkAudioEvent"), 0, flags: 0x1 | 0x8);
        p.SetPayload(export, client ? p.Properties(native: w => w.Raw(new byte[40])) : p.Properties());
        return p.Build();
    }

    private static void Write(string root, string gamePath, PackageBytes bytes) =>
        bytes.WriteAsync(Path.Combine([root, .. AssetPaths.ToFilePathWithoutExtension(gamePath).Split('/')]), ".uasset").GetAwaiter().GetResult();

    private static CookedPackage Read(string root, string gamePath)
    {
        var stem = Path.Combine([root, .. AssetPaths.ToFilePathWithoutExtension(gamePath).Split('/')]);
        return CookedPackage.Parse(File.ReadAllBytes(stem + ".uasset"), File.ReadAllBytes(stem + ".uexp"), null, gamePath);
    }
}
