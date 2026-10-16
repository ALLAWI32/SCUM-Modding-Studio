using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The export's client/server pairing (D12, D13, D19: Qraox, taishan_song) and Setup's game folder (D10, Markus6789): an
/// old server pak never stays beside a new client pak, a Craftables pak built for one side only is named, the install
/// rule is said plainly, and the dedicated server's Paks folder is refused as the game's. The Map pastes lights and effects
/// anywhere and says when a volume cannot go into another level (D15, seviscache).
/// </summary>
[Collection(LanguageCollection.Name)] // reads localized text
public sealed class ExportSafetyAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-parity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void AnExportWithoutAServerPakRemovesTheOldServerPaks()
    {
        var server = Path.Combine(_dir, ProjectExporter.ServerFolderName);
        Directory.CreateDirectory(server);
        string[] old = ["pakchunk900-Mod_P.pak", "pakchunk900-Mod_P.sig", "pakchunk901-ModCraftables_P.pak", "pakchunk901-ModCraftables_P.sig"];
        foreach (var file in old)
        {
            File.WriteAllBytes(Path.Combine(server, file), [0]);
        }

        var client = Result(ProjectSourceRole.Client, "Mod");

        // A server pak of this export keeps its files; the Craftables one it did not build goes.
        var kept = ModExportService.RemoveStaleServerPaks(_dir, "Mod", 900, [client, Result(ProjectSourceRole.Server, "Mod")]);
        Assert.Equal(new[] { "pakchunk901-ModCraftables_P.pak", "pakchunk901-ModCraftables_P.sig" }, kept.Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(server, old[0])));

        var removed = ModExportService.RemoveStaleServerPaks(_dir, "Mod", 900, [client]);
        Assert.Equal(new[] { "pakchunk900-Mod_P.pak", "pakchunk900-Mod_P.sig" }, removed.Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFiles(server));
    }

    [Fact]
    public void ACraftablesPakForOneSideOnlyIsNamedAndTheInstallRuleIsSaid()
    {
        var clientMain = Result(ProjectSourceRole.Client, "Mod");
        var clientCraft = Result(ProjectSourceRole.Client, "Mod" + CraftablesExporter.Suffix, 901);
        var serverMain = Result(ProjectSourceRole.Server, "Mod");
        var serverCraft = Result(ProjectSourceRole.Server, "Mod" + CraftablesExporter.Suffix, 901);

        var problem = Assert.Single(ModExportService.ParityProblems([clientMain, clientCraft, serverMain]));
        Assert.Contains("pakchunk901-ModCraftables_P.pak", problem, StringComparison.Ordinal);
        Assert.Single(ModExportService.ParityProblems([clientMain, serverMain, serverCraft]));
        Assert.Empty(ModExportService.ParityProblems([clientMain, clientCraft, serverMain, serverCraft]));

        var note = ModExportService.InstallNote([clientMain, clientCraft, serverMain, serverCraft]);
        Assert.Contains("pakchunk900-Mod_P.pak + pakchunk901-ModCraftables_P.pak", note, StringComparison.Ordinal);
        Assert.Contains(@"Paks\~mods", note, StringComparison.Ordinal);
        Assert.NotEqual(ModExportService.InstallNote([clientMain]), note);
    }

    [Fact]
    public void SetupRefusesTheDedicatedServersPaksAsTheGameFolder()
    {
        using var ctx = AppTestContext.Create();
        var common = Path.Combine(ctx.Combine("steamapps"), "common");
        var serverPaks = Path.Combine(common, "SCUM Server", "SCUM", "Content", "Paks");
        Directory.CreateDirectory(serverPaks);
        File.WriteAllBytes(Path.Combine(serverPaks, "pakchunk0-WindowsServer.pak"), [0]);
        var gamePaks = Path.Combine(common, "SCUM", "SCUM", "Content", "Paks");
        Directory.CreateDirectory(gamePaks);
        File.WriteAllBytes(Path.Combine(gamePaks, "pakchunk0-WindowsNoEditor.pak"), [0]);

        var closed = new List<bool>();
        var setup = new SetupViewModel(ctx.Services, closed.Add) { GamePaksFolder = serverPaks };
        Assert.False(setup.Save());
        setup.SaveAndCloseCommand.Execute(null);
        Assert.Empty(closed); // the dialog stays open
        Assert.False(setup.TestSucceeded);
        Assert.Contains(gamePaks, setup.TestResult, StringComparison.OrdinalIgnoreCase); // it names the game's folder
        Assert.Null(ctx.Services.Settings.Load().GamePaksFolder); // nothing was saved

        Assert.Null(SetupViewModel.GameFolderProblem(gamePaks));
        setup.GamePaksFolder = gamePaks;
        Assert.True(setup.Save());
    }

    [Fact]
    public void TheMapPastesLightsAnywhereButAVolumeOnlyInItsOwnLevel()
    {
        const string one = "/Game/ConZ_Files/Maps/The_Island/A_0_One";
        const string two = "/Game/ConZ_Files/Maps/The_Island/A_0_Two";
        var b = new FakeLevelBuilder(one);
        var volume = b.Actor("PP_1", "/Script/Engine.PostProcessVolume");
        b.Root(volume, b.Component(volume, "BrushComponent0", "BrushComponent"));
        var lamp = b.Actor("Lamp_1", "/Script/Engine.PointLight");
        b.Root(lamp, b.Component(lamp, "LightComponent0", "PointLightComponent"));
        var reader = new FakeLevelReader().Add(b.Build()).Add(new FakeLevelBuilder(two).Build());
        var source = LevelDocument.Load(reader, one);
        var target = LevelDocument.Load(reader, two);

        // D15 (seviscache): a light is pasted anywhere; a volume's brush shape cannot go into another level yet.
        var fog = new ActorItemViewModel(source, source.FindActor("PP_1")!, 1);
        Assert.Contains("BrushComponent0", MapPageViewModel.CopyRefusedText(fog, target), StringComparison.Ordinal);
        Assert.Null(MapPageViewModel.CopyRefusedText(fog, source));
        Assert.Null(MapPageViewModel.CopyRefusedText(new ActorItemViewModel(source, source.FindActor("Lamp_1")!, 2), target));
    }

    private ExportResult Result(ProjectSourceRole role, string modName, int chunk = 900) => new()
    {
        Role = role,
        ModName = modName,
        StagingDirectory = Path.Combine(_dir, "staging"),
        PakPath = Path.Combine(_dir, ProjectExporter.RoleFolder(role), ProjectExporter.PakFileName(modName, chunk)),
        Levels = [],
        Warnings = [],
    };
}
