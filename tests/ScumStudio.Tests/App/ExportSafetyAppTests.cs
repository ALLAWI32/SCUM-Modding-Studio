using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The export's client/server pairing (D12, D13, D19: Qraox, taishan_song) and Setup's game folder (D10, Markus6789): an
/// old server pak never stays beside a new client pak (unless the package check refused the new server pak: a refused
/// export never deletes a pak), a Craftables pak built for one side only is named, the install
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
        var server = ProjectExporter.ExportFolder(_dir, "Mod", pages: false, ProjectSourceRole.Server);
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

    /// <summary>
    /// 3.5.1 verify (owner, r38: a refused export never renames, moves or deletes a pak): the client pak passed and the package
    /// check refused the server pak. The Server folder's NOT EXPORTED report names the earlier server pak, so it and every other
    /// server pak stay, byte for byte under their own names.
    /// </summary>
    [Fact]
    public void ARefusedServerPakKeepsEveryEarlierServerPak()
    {
        var server = ProjectExporter.ExportFolder(_dir, "Mod", pages: false, ProjectSourceRole.Server); // the mod's own Server folder (3.5.1 layout)
        Directory.CreateDirectory(server);
        string[] old = ["pakchunk900-Mod_P.pak", "pakchunk900-Mod_P.sig", "pakchunk901-ModCraftables_P.pak", "pakchunk901-ModCraftables_P.sig"];
        for (var i = 0; i < old.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(server, old[i]), [(byte)(i + 1), 9]);
        }

        var refused = new ModValidationException(new ValidationResult { Problems = ["/Game/A: an import the server does not have"] });
        Assert.Empty(ModExportService.RemoveStaleServerPaks(_dir, "Mod", 900, [Result(ProjectSourceRole.Client, "Mod")], refused));
        Assert.Equal(old.Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(server).Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase));
        for (var i = 0; i < old.Length; i++)
        {
            Assert.Equal(new byte[] { (byte)(i + 1), 9 }, File.ReadAllBytes(Path.Combine(server, old[i])));
        }

        // The same folder without a refusal: the old server paks go (so the folder above is the one the service looks at).
        Assert.Equal(4, ModExportService.RemoveStaleServerPaks(_dir, "Mod", 900, [Result(ProjectSourceRole.Client, "Mod")]).Count);
        Assert.Empty(Directory.EnumerateFiles(server));
    }

    /// <summary>
    /// Review 3.5.1: the Craftables pak an earlier one-click export copied into the server's mods folder goes when this
    /// export's server cook built none for a reason other than the package check (as in 3.5.0: an old server Craftables pak
    /// beside the new client one, D12, D13), and stays when the package check refused a server pak (a refused export never
    /// deletes a pak, owner r38). 950678b kept it whenever a client Craftables pak was built.
    /// </summary>
    [Fact]
    public void TheServerModsCraftablesCopyGoesUnlessTheServerPakWasRefused()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_dir, "server_mods")).FullName;
        string[] copy = ["pakchunk901-ModCraftables_P.pak", "pakchunk901-ModCraftables_P.sig"];
        void Write()
        {
            foreach (var file in copy)
            {
                File.WriteAllBytes(Path.Combine(mods, file), [7]);
            }
        }

        var clientMain = Result(ProjectSourceRole.Client, "Mod");
        var clientCraft = Result(ProjectSourceRole.Client, "Mod" + CraftablesExporter.Suffix, 901);
        var serverMain = Result(ProjectSourceRole.Server, "Mod");
        var serverCraft = Result(ProjectSourceRole.Server, "Mod" + CraftablesExporter.Suffix, 901);
        var refused = new ModValidationException(new ValidationResult { Problems = ["/Game/A: an import the server does not have"] });

        // The server's Craftables cook failed (an unreadable file, no refusal): the old copy no longer matches.
        Write();
        Assert.Equal(copy.Order(StringComparer.Ordinal), ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, new ModExportResults([clientMain, clientCraft, serverMain]))
            .Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));

        // Refused: it stays, byte for byte. So it does beside this export's own server Craftables pak and without any server cook.
        Write();
        Assert.Empty(ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, new ModExportResults([clientMain, clientCraft, serverMain], refused)));
        Assert.Empty(ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, new ModExportResults([clientMain, clientCraft, serverMain, serverCraft])));
        Assert.Empty(ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, new ModExportResults([clientMain, clientCraft])));
        Assert.All(copy, f => Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(mods, f))));

        // A server cook without craftables, or nothing left to build at all: it goes.
        Assert.Equal(2, ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, new ModExportResults([clientMain, serverMain])).Count);
        Write();
        Assert.Equal(2, ModExportService.RemoveStaleServerModsCraftables(mods, "Mod", 900, ModExportResults.None).Count);
        Assert.Empty(ModExportService.RemoveStaleServerModsCraftables(null, "Mod", 900, ModExportResults.None));
    }

    /// <summary>
    /// Review 3.5.0: one click copies the whole project's paks and, on a page, its page pak and the Registry pak into the same
    /// server mods folder; the Registry pak loaded above the whole project's and its list of assets left out their records.
    /// Copying one way of installing removes the other's paks of the same project there, never another project's.
    /// </summary>
    [Fact]
    public void CopyingOneInstallModeToTheServerRemovesTheOther()
    {
        Directory.CreateDirectory(_dir);
        string[] whole = ["pakchunk900-Mod_P.pak", "pakchunk900-Mod_P.sig", "pakchunk901-ModCraftables_P.pak", "pakchunk901-ModCraftables_P.sig"];
        string[] pages = ["pakchunk912-Mod-Vehicles_P.pak", "pakchunk912-Mod-Vehicles_P.sig", "pakchunk919-Mod-Registry_P.pak", "pakchunk919-Mod-Registry_P.sig"];
        string[] other = ["pakchunk900-Other_P.pak", "pakchunk912-Other-Vehicles_P.pak"];
        void Write()
        {
            foreach (var file in whole.Concat(pages).Concat(other))
            {
                File.WriteAllBytes(Path.Combine(_dir, file), [0]);
            }
        }

        Write();
        Assert.Equal(whole.Order(), ModExportService.RemoveOtherInstallMode(_dir, "Mod", 900, pagesInstalled: true).Select(Path.GetFileName).Order());
        Assert.Equal(pages.Concat(other).Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase));

        Write();
        Assert.Equal(pages.Order(), ModExportService.RemoveOtherInstallMode(_dir, "Mod", 900, pagesInstalled: false).Select(Path.GetFileName).Order());
        Assert.Equal(whole.Concat(other).Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(ModExportService.RemoveOtherInstallMode(Path.Combine(_dir, "missing"), "Mod", 900, pagesInstalled: true));
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

    /// <summary>
    /// Review 3.5.1: the package check refused the server pak (the client's passed, the Server folder is set). The install rule
    /// (Projects page, Map page) and a page's export line say that pak was NOT written, never to set the Server folder, and a
    /// client Craftables pak without its server one is the refusal's to say, not a missing Server folder.
    /// </summary>
    [Fact]
    public void ARefusedServerPakIsNotSaidAsAClientOnlyExport()
    {
        var clientMain = Result(ProjectSourceRole.Client, "Mod");
        var clientCraft = Result(ProjectSourceRole.Client, "Mod" + CraftablesExporter.Suffix, 901);
        var serverPak = Path.Combine(_dir, ProjectExporter.ServerFolderName, ProjectExporter.PakFileName("Mod", 900));
        var refused = new ModValidationException(new ValidationResult { Problems = ["/Game/A: an import the server does not have"] }, null, serverPak, null);

        var note = ModExportService.InstallNote([clientMain, clientCraft], refused);
        Assert.Contains("pakchunk900-Mod_P.pak + pakchunk901-ModCraftables_P.pak", note, StringComparison.Ordinal);
        Assert.Contains("pakchunk900-Mod_P.pak was NOT written", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Server folder", note, StringComparison.Ordinal);
        Assert.Contains("Server folder", ModExportService.InstallNote([clientMain, clientCraft]), StringComparison.Ordinal); // no server cook at all

        Assert.Empty(ModExportService.ParityProblems([clientMain, clientCraft], refused));
        Assert.Single(ModExportService.ParityProblems([clientMain, clientCraft]));

        var line = ModuleExportViewModel.Describe([clientMain], [], refused);
        Assert.Contains("pakchunk900-Mod_P.pak was NOT written", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T("Export.Module.NoServer"), line, StringComparison.Ordinal);
        Assert.Contains(Loc.T("Export.Module.NoServer"), ModuleExportViewModel.Describe([clientMain], []), StringComparison.Ordinal);
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
        PakPath = Path.Combine(ProjectExporter.ExportFolder(_dir, modName, pages: false, role), ProjectExporter.PakFileName(modName, chunk)),
        Levels = [],
        Warnings = [],
    };
}
