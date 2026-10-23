using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Pak;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Real game files: Check my install over a real export copied into a temporary <c>~mods</c> (Discord 2026-10-18, hexter_98
/// and youngpiegus; r39 diagnosis E2). Two page paks with clones (Items, Weapons) installed without the Registry pak, then
/// with it; another registry-carrying pak loading first; the whole project's pak beside the pages; an export newer than the
/// installed copy; and, with a dedicated server's paks, the client pak on the server. The probe reads the real paks' indexes
/// (the game's own pakchunk0 only with the key).
/// </summary>
[Collection(LanguageCollection.Name)] // words the findings in English
public sealed class InstallCheckRealTests(ITestOutputHelper output)
{
    private const string Coins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Copper_Coins";
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Name = "Install Check";

    [PaksFact]
    public async Task ARealExportInATempModsFolderIsCheckedAsTheGameMountsIt()
    {
        var key = AesKeyText.FromEnvironmentOrStore()!;
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = key, PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames(Name)) });
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("InstallCheck.ssproj"), Name);
        Clone(project, "item", Coins, CloneFamilyPlanner.PlanItem(catalog, Coins, "Install_Coins"));
        Clone(project, "weapon", Ak47, CloneFamilyPlanner.PlanItem(catalog, Ak47, "Weapon_AK47_Install"));

        // The Items and the Weapons page exported as the pages' Export buttons do, each followed by the Registry pak.
        var exports = temp.Combine("exports");
        var options = new ExportOptions { OutputDirectory = exports, ProjectDirectory = project.DirectoryPath, LeaveOutEconomy = true };
        var exporter = new ProjectExporter();
        var pages = new List<ExportResult>();
        foreach (var module in new[] { ExportModule.Items, ExportModule.Weapons })
        {
            var own = options with { Module = module, Elsewhere = ModuleExport.Elsewhere(project.State, module, Name, options.PakChunkIndex) };
            pages.Add(await exporter.ExportAsync(ModuleExport.StateOf(project.State, module), Name, catalog, own, ProjectSourceRole.Client));
            await ModuleExport.WriteRegistryPakAsync(Name, catalog, own, ProjectSourceRole.Client, state: project.State);
        }

        var pagesFolder = ProjectExporter.ExportFolder(exports, Name, pages: true, ProjectSourceRole.Client);
        var registry = Path.Combine(pagesFolder, ModuleExport.RegistryPakFileName(Name, options.PakChunkIndex));
        Assert.True(File.Exists(registry));
        Assert.All(pages, p => Assert.True(p.Validation is { Passed: true } && File.Exists(p.SigPath), p.ModName));

        // The probe on real paks: ours carry the registry; the game's pakchunk0 does too, but only the key opens its index.
        Assert.True(InstallCheck.HasRegistry(pages[0].PakPath!, null));
        Assert.True(InstallCheck.HasRegistry(registry, null));
        var stock = Path.Combine(RealPaks.Folder!, "pakchunk0-WindowsNoEditor.pak");
        Assert.True(InstallCheck.HasRegistry(stock, key));
        Assert.Null(InstallCheck.HasRegistry(stock, null));

        // hexter_98's install: the two page paks (and .sig) in ~mods, no Registry pak.
        var game = Directory.CreateDirectory(temp.Combine("SCUM", "Content", "Paks", "~mods")).Parent!.FullName;
        var mods = Path.Combine(game, "~mods");
        foreach (var page in pages)
        {
            Copy(page.PakPath!, mods);
            Copy(page.SigPath!, mods);
        }

        var input = new InstallCheckInput { ModNames = OwnPaks.ProjectNames(Name), GamePaks = game, ExportFolder = exports, HasRegistry = p => InstallCheck.HasRegistry(p, key) };
        var findings = Check(input);
        var missingRegistry = Find(findings, InstallIssue.MissingRegistry);
        Assert.Equal(pages.Select(p => Path.GetFileName(p.PakPath)).Order(StringComparer.OrdinalIgnoreCase), missingRegistry.Files);
        Assert.DoesNotContain(findings, f => f.Issue == InstallIssue.Missing); // the Registry pak is named once, by MissingRegistry
        Assert.Equal(2, Find(findings, InstallIssue.Installed).Files.Count);
        Assert.DoesNotContain(findings, f => f.Issue is InstallIssue.RegistryAbove or InstallIssue.Stale or InstallIssue.BothModes);

        // The Registry pak copied too: as it should be.
        Copy(registry, mods);
        Copy(Path.ChangeExtension(registry, ".sig"), mods);
        findings = Check(input);
        Assert.DoesNotContain(findings, f => f.Side == InstallSide.Game && f.Severity is InstallSeverity.Problem or InstallSeverity.Warning);
        Assert.Equal(3, Find(findings, InstallIssue.Installed).Files.Count);

        // Another mod whose pak loads first and brings its own registry (here: a copy of a real page pak under its name).
        File.Copy(pages[0].PakPath!, Path.Combine(mods, "pakchunk99-OtherServerMod_P.pak"));
        findings = Check(input);
        Assert.Equal(["pakchunk99-OtherServerMod_P.pak"], Find(findings, InstallIssue.RegistryAbove).Files);
        File.Delete(Path.Combine(mods, "pakchunk99-OtherServerMod_P.pak"));

        // The whole project exported and installed beside the pages; then the Items page exported again after a change.
        var whole = await exporter.ExportAsync(project, catalog, options, ProjectSourceRole.Client);
        Assert.True(whole.Validation is { Passed: true });

        // Review r40 (probe S1): the whole project's pak just written while the pages are installed and current. The toast's
        // line said "The game's ~mods has this export's paks"; with the paks the export wrote it names the pak to copy and the
        // page paks to delete.
        var afterExport = Check(input with { Written = [whole.PakPath!] });
        Assert.Equal([Path.GetFileName(whole.PakPath)!], Find(afterExport, InstallIssue.NotCopied).Files);
        Assert.Equal(3, Find(afterExport, InstallIssue.OtherMode).Files.Count);
        Assert.StartsWith("Not in the game's ~mods yet: copy every file of " + Path.GetDirectoryName(whole.PakPath) + " there. Then delete this project's other paks there",
            InstallCheckService.ToastLine(afterExport), StringComparison.Ordinal);

        // The pages' Client folder copied into a subfolder of ~mods as well: the game mounts it too, the check says so.
        foreach (var file in Directory.GetFiles(pagesFolder))
        {
            Directory.CreateDirectory(Path.Combine(mods, "pages"));
            Copy(file, Path.Combine(mods, "pages"));
        }

        Assert.Equal(3, Find(Check(input), InstallIssue.InSubfolder).Files.Count);
        Directory.Delete(Path.Combine(mods, "pages"), recursive: true);
        Copy(whole.PakPath!, mods);
        Clone(project, "item", Coins, CloneFamilyPlanner.PlanItem(catalog, Coins, "Install_Coins_Two"));
        var again = await exporter.ExportAsync(ModuleExport.StateOf(project.State, ExportModule.Items), Name, catalog,
            options with { Module = ExportModule.Items, Elsewhere = ModuleExport.Elsewhere(project.State, ExportModule.Items, Name, options.PakChunkIndex) }, ProjectSourceRole.Client);
        findings = Check(input);
        Assert.Equal(4, Find(findings, InstallIssue.BothModes).Files.Count);
        Assert.Equal([Path.GetFileName(again.PakPath)!], Find(findings, InstallIssue.Stale).Files);
        Assert.Equal("Check my install found 1 thing(s) to fix in the mods folders (Projects → Check my install).", InstallCheckService.ToastLine(findings));

        // A loose copy in Paks itself.
        Copy(whole.PakPath!, game);
        Assert.Equal([Path.GetFileName(whole.PakPath)!], Find(Check(input), InstallIssue.LooseInPaks).Files);
    }

    /// <summary>
    /// youngpiegus: the client page pak copied to a dedicated server (without its .sig) instead of the server's own pak. Needs
    /// the server's paks (<c>SCUM_SERVER_PAKS</c>) to build the server cook the check compares with.
    /// </summary>
    [PaksFact]
    public async Task TheClientPakOnTheServerIsNamedAsTheWrongCook()
    {
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is not { Length: > 0 } serverPaks || !Directory.Exists(serverPaks))
        {
            output.WriteLine("SCUM_SERVER_PAKS not set: the server cook is not built.");
            return;
        }

        var key = AesKeyText.FromEnvironmentOrStore()!;
        var names = OwnPaks.ProjectNames(Name);
        using var client = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = key, PakFileFilter = OwnPaks.Filter(names) });
        using var server = AssetCatalog.OpenPaks(serverPaks, new AssetCatalogOptions { AesKey = key, PakFileFilter = OwnPaks.Filter(names) });
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("InstallCheck.ssproj"), Name);
        Clone(project, "item", Coins, CloneFamilyPlanner.PlanItem(client, Coins, "Install_Coins"));
        var exports = temp.Combine("exports");
        var options = new ExportOptions { OutputDirectory = exports, ProjectDirectory = project.DirectoryPath, LeaveOutEconomy = true, Module = ExportModule.Items };
        options = options with { Elsewhere = ModuleExport.Elsewhere(project.State, ExportModule.Items, Name, options.PakChunkIndex) };
        var exporter = new ProjectExporter();
        var clientPak = await exporter.ExportAsync(ModuleExport.StateOf(project.State, ExportModule.Items), Name, client, options, ProjectSourceRole.Client);
        var serverPak = await exporter.ExportAsync(ModuleExport.StateOf(project.State, ExportModule.Items), Name, server, options with { ClientCatalog = client, ServerEventStubs = () => ServerEventStubs.FromServerPaks(serverPaks, key) }, ProjectSourceRole.Server);
        Assert.NotEqual(new FileInfo(clientPak.PakPath!).Length, new FileInfo(serverPak.PakPath!).Length);

        var serverFolder = Directory.CreateDirectory(temp.Combine("SCUMServer", "SCUM", "Content", "Paks", "~mods")).Parent!.FullName;
        Copy(clientPak.PakPath!, Path.Combine(serverFolder, "~mods"));
        var input = new InstallCheckInput { ModNames = names, ServerPaks = serverFolder, ExportFolder = exports, HasRegistry = p => InstallCheck.HasRegistry(p, key) };
        var findings = Check(input);
        Assert.Equal([Path.GetFileName(clientPak.PakPath)!], Find(findings, InstallIssue.WrongCook, InstallSide.Server).Files);
        Assert.Equal([Path.GetFileName(clientPak.PakPath)!], Find(findings, InstallIssue.NoSig, InstallSide.Server).Files);

        // The server's own pak with its .sig: as it should be.
        Copy(serverPak.PakPath!, Path.Combine(serverFolder, "~mods"));
        Copy(serverPak.SigPath!, Path.Combine(serverFolder, "~mods"));
        findings = Check(input);
        Assert.DoesNotContain(findings, f => f.Side == InstallSide.Server && f.Severity is InstallSeverity.Problem or InstallSeverity.Warning);
        Assert.Equal([Path.GetFileName(serverPak.PakPath)!], Find(findings, InstallIssue.Installed, InstallSide.Server).Files);
    }

    private IReadOnlyList<InstallFinding> Check(InstallCheckInput input)
    {
        var findings = InstallCheck.Check(input);
        foreach (var finding in findings)
        {
            output.WriteLine($"{finding.Severity}: {InstallCheckService.Describe(finding, input)}");
        }

        output.WriteLine(string.Empty);
        return findings;
    }

    private static InstallFinding Find(IReadOnlyList<InstallFinding> findings, InstallIssue issue, InstallSide side = InstallSide.Game) =>
        Assert.Single(findings, f => f.Side == side && f.Issue == issue);

    private static void Copy(string file, string folder) => File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);

    private static void Clone(Project project, string kind, string template, ClonePlan plan) =>
        project.Apply(new CloneAssetOp(kind, template, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList()));
}
