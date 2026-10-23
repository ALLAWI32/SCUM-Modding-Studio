using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord 2026-10-18 (hexter_98: "clone item dont work... cant spawn it with command"; youngpiegus: "works in single-player,
/// but it doesn't work for someone else or on a server"); r39 diagnosis E2. Check my install reads the game's and the server's
/// <c>~mods</c> and names what stands in the way: a missing Registry pak, both install modes together, paks older than the
/// export, an older chunk of the same mod, other mods' registries loading first, paks loose in <c>Paks</c>, the client pak on
/// the server, a server pak without its .sig. Small paks written here with and without <c>SCUM/AssetRegistry.bin</c>.
/// </summary>
public sealed class InstallCheckTests
{
    private const string Mod = "My Mod"; // sanitized: My_Mod

    [Fact]
    public void TheEnginesReadOrderPutsPatchPaksFirstAndThenTheHigherName()
    {
        Assert.Equal(103, InstallCheck.ReadOrder("pakchunk900-My_Mod_P.pak"));
        Assert.Equal(103, InstallCheck.ReadOrder("pakchunk99-Some_Mod_P.pak")); // "Mod" is no number
        Assert.Equal(303, InstallCheck.ReadOrder("pakchunk1-Patch_2_P.pak"));
        Assert.Equal(3, InstallCheck.ReadOrder("pakchunk0-WindowsNoEditor.pak"));
        Assert.Equal(3, InstallCheck.ReadOrder("zzz-NoPatch.pak"));

        // The owner's game logs: 919 (Registry) mounted before 915 before 912; pakchunk99-… before all of them.
        Assert.True(InstallCheck.LoadsBefore("pakchunk919-MyMapMod-Registry_P.pak", "pakchunk915-MyMapMod-Craftables_P.pak"));
        Assert.True(InstallCheck.LoadsBefore("pakchunk99-Other_P.pak", "pakchunk919-MyMapMod-Registry_P.pak"));
        Assert.False(InstallCheck.LoadsBefore("pakchunk1-Below_P.pak", "pakchunk900-My_Mod_P.pak"));
        Assert.False(InstallCheck.LoadsBefore("zzz-NoPatch.pak", "pakchunk900-My_Mod_P.pak")); // not a patch pak: lower order
        Assert.True(InstallCheck.LoadsBefore("pakchunk1-Patch_2_P.pak", "pakchunk999-My_Mod_P.pak"));
        Assert.True(InstallCheck.IsStockPak("pakchunk0_s12-WindowsNoEditor.pak"));
        Assert.True(InstallCheck.IsStockPak("pakchunk0_s38optional-WindowsServer.pak"));
        Assert.False(InstallCheck.IsStockPak("pakchunk900-My_Mod_P.pak"));
        Assert.Contains("pakchunk919-My_Mod-Registry_P.pak", InstallCheck.OwnPakNames([Mod], 900));
        Assert.Contains("pakchunk901-My_ModCraftables_P.pak", InstallCheck.OwnPakNames([Mod], 900));
        Assert.Contains("pakchunk914-My_Mod-Items_P.pak", InstallCheck.OwnPakNames([Mod], 900));
    }

    [Fact]
    public async Task ACleanInstallOnTheGameAndTheServerIsReportedAsInstalled()
    {
        using var temp = new LevelTempDirectory();
        var (game, server, exports) = Folders(temp);
        var client = await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 1000, sig: true);
        var serverPak = await Pak(ExportFile(exports, false, "Server", "pakchunk900-My_Mod_P.pak"), registry: true, size: 2000, sig: true);
        Install(client, Path.Combine(game, "~mods"));
        Install(serverPak, Path.Combine(server, "~mods"));

        var findings = InstallCheck.Check(Input(game, server, exports));

        Assert.DoesNotContain(findings, f => f.Severity is InstallSeverity.Problem or InstallSeverity.Warning);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Installed).Files);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Server, InstallIssue.Installed).Files);
        Assert.Contains(findings, f => f.Issue == InstallIssue.NoteFileOpenLog);
        Assert.Contains(findings, f => f.Issue == InstallIssue.NoteFriends);
    }

    [Fact]
    public async Task EveryProblemOfTheReportsIsNamedOnce()
    {
        using var temp = new LevelTempDirectory();
        var (game, server, exports) = Folders(temp);
        var mods = Path.Combine(game, "~mods");
        var serverMods = Path.Combine(server, "~mods");

        // The export: the whole project (client and server) and three page paks with the Registry pak.
        var whole = await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 1000, sig: true);
        await Pak(ExportFile(exports, false, "Server", "pakchunk900-My_Mod_P.pak"), registry: true, size: 2000, sig: true);
        var vehicles = await Pak(ExportFile(exports, true, "Client", "pakchunk912-My_Mod-Vehicles_P.pak"), registry: true, size: 1100, sig: true);
        var items = await Pak(ExportFile(exports, true, "Client", "pakchunk914-My_Mod-Items_P.pak"), registry: true, size: 1200, sig: true);
        await Pak(ExportFile(exports, true, "Client", "pakchunk915-My_Mod-Craftables_P.pak"), registry: true, size: 1300, sig: true);
        await Pak(ExportFile(exports, true, "Client", "pakchunk919-My_Mod-Registry_P.pak"), registry: true, size: 1400, sig: true);

        // hexter_98: the Items and Craftables… here Vehicles and Items page paks without the Registry pak, and the whole
        // project's pak beside them, an older copy of it.
        Install(vehicles, mods);
        Install(items, mods);
        Install(whole, mods);
        await Pak(whole, registry: true, size: 1050, sig: true); // exported again since
        File.SetLastWriteTimeUtc(whole, DateTime.UtcNow.AddMinutes(5));

        // An older chunk of the same mod, other mods above and below ours (with and without a registry), one not readable.
        await Pak(Path.Combine(mods, "pakchunk950-My_Mod_P.pak"), registry: true, size: 900);
        await Pak(Path.Combine(mods, "pakchunk99-Other_P.pak"), registry: true, size: 800);
        await Pak(Path.Combine(mods, "pakchunk1-Patch_2_P.pak"), registry: true, size: 700);
        await Pak(Path.Combine(mods, "pakchunk999-MapOnly_P.pak"), registry: false, size: 600);
        await Pak(Path.Combine(mods, "pakchunk1-Below_P.pak"), registry: true, size: 500);
        await Pak(Path.Combine(mods, "zzz-NoPatch.pak"), registry: true, size: 400);
        await File.WriteAllBytesAsync(Path.Combine(mods, "pakchunk98-Broken_P.pak"), new byte[300]);

        // A mod pak loose in Paks (the game refuses it there); the game's own paks are left alone.
        File.Copy(whole, Path.Combine(game, "pakchunk900-My_Mod_P.pak"));
        await File.WriteAllBytesAsync(Path.Combine(game, "pakchunk0-WindowsNoEditor.pak"), [1]);

        // youngpiegus: the client pak copied to the server, without its .sig.
        Install(whole, serverMods, withSig: false);

        var findings = InstallCheck.Check(Input(game, server, exports));

        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.LooseInPaks).Files);
        Assert.Equal(["pakchunk912-My_Mod-Vehicles_P.pak", "pakchunk914-My_Mod-Items_P.pak"], Find(findings, InstallSide.Game, InstallIssue.MissingRegistry).Files);
        Assert.Equal(3, Find(findings, InstallSide.Game, InstallIssue.BothModes).Files.Count);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Stale).Files);
        var missing = Find(findings, InstallSide.Game, InstallIssue.Missing);
        Assert.Equal(["pakchunk915-My_Mod-Craftables_P.pak"], missing.Files); // the Registry pak is named by MissingRegistry
        Assert.Equal(ProjectExporter.ExportFolder(exports, Mod, pages: true, ProjectSourceRole.Client), missing.Folder);
        Assert.Equal(["pakchunk950-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.OldName).Files);
        Assert.Equal(["pakchunk1-Patch_2_P.pak", "pakchunk950-My_Mod_P.pak", "pakchunk99-Other_P.pak"], Find(findings, InstallSide.Game, InstallIssue.RegistryAbove).Files.Order(StringComparer.Ordinal));
        Assert.Equal(["pakchunk98-Broken_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Unreadable).Files);
        Assert.Equal(["pakchunk1-Below_P.pak", "zzz-NoPatch.pak"], Find(findings, InstallSide.Game, InstallIssue.RegistryBelow).Files.Order(StringComparer.Ordinal));
        Assert.Equal(["pakchunk912-My_Mod-Vehicles_P.pak", "pakchunk914-My_Mod-Items_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Installed).Files);
        Assert.DoesNotContain(findings, f => f.Files.Contains("pakchunk999-MapOnly_P.pak")); // no registry: hides nothing

        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Server, InstallIssue.WrongCook).Files);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Server, InstallIssue.NoSig).Files);
        Assert.All(findings.GroupBy(f => (f.Side, f.Issue)), g => Assert.Single(g));

        // The Registry pak copied: that one is gone (the pages' folder still lacks the Craftables page).
        Install(ExportFile(exports, true, "Client", "pakchunk919-My_Mod-Registry_P.pak"), mods);
        findings = InstallCheck.Check(Input(game, server, exports));
        Assert.DoesNotContain(findings, f => f.Issue == InstallIssue.MissingRegistry);
        Assert.Equal(["pakchunk915-My_Mod-Craftables_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Missing).Files);
    }

    [Fact]
    public async Task NothingInstalledNamesTheExportToCopyAndWhatWasNotChecked()
    {
        using var temp = new LevelTempDirectory();
        var (game, _, exports) = Folders(temp);
        var pages = await Pak(ExportFile(exports, true, "Client", "pakchunk912-My_Mod-Vehicles_P.pak"), registry: true, size: 100);
        File.SetLastWriteTimeUtc(pages, DateTime.UtcNow.AddMinutes(1));
        await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 100);

        var findings = InstallCheck.Check(Input(game, null, exports));
        var notInstalled = Find(findings, InstallSide.Game, InstallIssue.NotInstalled);
        Assert.Equal(InstallSeverity.Warning, notInstalled.Severity);
        Assert.Equal(ProjectExporter.ExportFolder(exports, Mod, pages: true, ProjectSourceRole.Client), notInstalled.Folder); // the newer export
        Assert.Equal(["pakchunk912-My_Mod-Vehicles_P.pak"], notInstalled.Files);
        Assert.Equal(InstallSeverity.Info, Find(findings, InstallSide.Server, InstallIssue.NoFolder).Severity);

        // No export at all, and a game folder that is gone.
        var none = InstallCheck.Check(Input(game, null, temp.Combine("no-exports")));
        Assert.Null(Find(none, InstallSide.Game, InstallIssue.NotInstalled).Folder);
        var gone = InstallCheck.Check(Input(temp.Combine("gone"), null, exports));
        Assert.Equal(temp.Combine("gone"), Find(gone, InstallSide.Game, InstallIssue.NoFolder).Folder);
    }

    /// <summary>
    /// Review r40, finding 1: after Map → Export mod with the page paks installed (and the other way round), the check only
    /// saw "installed and current" paks and the toast said the export was in ~mods. With the paks the export wrote, the game's
    /// side names those that are not there (or not the same size) and the other install mode's paks.
    /// </summary>
    [Fact]
    public async Task TheExportJustWrittenIsNamedWhenItIsNotInModsWhateverElseIsInstalled()
    {
        using var temp = new LevelTempDirectory();
        var (game, _, exports) = Folders(temp);
        var mods = Path.Combine(game, "~mods");
        var vehicles = await Pak(ExportFile(exports, true, "Client", "pakchunk912-My_Mod-Vehicles_P.pak"), registry: true, size: 1100);
        var items = await Pak(ExportFile(exports, true, "Client", "pakchunk914-My_Mod-Items_P.pak"), registry: true, size: 1200);
        var registry = await Pak(ExportFile(exports, true, "Client", "pakchunk919-My_Mod-Registry_P.pak"), registry: true, size: 1400);
        Install(vehicles, mods);
        Install(items, mods);
        Install(registry, mods);

        // The page paks are installed and current; then Map → Export mod writes the whole project's pak, not copied yet.
        var whole = await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 1000);
        var findings = InstallCheck.Check(Input(game, null, exports, written: [whole]));
        var notCopied = Find(findings, InstallSide.Game, InstallIssue.NotCopied);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], notCopied.Files);
        Assert.Equal(ProjectExporter.ExportFolder(exports, Mod, pages: false, ProjectSourceRole.Client), notCopied.Folder);
        Assert.Equal(["pakchunk912-My_Mod-Vehicles_P.pak", "pakchunk914-My_Mod-Items_P.pak", "pakchunk919-My_Mod-Registry_P.pak"],
            Find(findings, InstallSide.Game, InstallIssue.OtherMode).Files);
        Assert.DoesNotContain(findings, f => f.Issue == InstallIssue.BothModes);

        // Without the written paks (the Projects page's button) the page paks are what is installed, and they are current.
        Assert.DoesNotContain(InstallCheck.Check(Input(game, null, exports)), f => f.Issue is InstallIssue.NotCopied or InstallIssue.OtherMode);

        // Copied: now both modes are installed, which is named as such.
        Install(whole, mods);
        findings = InstallCheck.Check(Input(game, null, exports, written: [whole]));
        Assert.DoesNotContain(findings, f => f.Issue is InstallIssue.NotCopied or InstallIssue.OtherMode);
        Assert.Equal(4, Find(findings, InstallSide.Game, InstallIssue.BothModes).Files.Count);

        // The other way round: only the whole project's pak is installed, then a page export writes the Items page again.
        foreach (var pak in new[] { vehicles, items, registry })
        {
            File.Delete(Path.Combine(mods, Path.GetFileName(pak)));
        }

        await Pak(items, registry: true, size: 1250);
        await Pak(registry, registry: true, size: 1450);
        findings = InstallCheck.Check(Input(game, null, exports, written: [items, registry]));
        notCopied = Find(findings, InstallSide.Game, InstallIssue.NotCopied);
        Assert.Equal(["pakchunk914-My_Mod-Items_P.pak", "pakchunk919-My_Mod-Registry_P.pak"], notCopied.Files);
        Assert.Equal(ProjectExporter.ExportFolder(exports, Mod, pages: true, ProjectSourceRole.Client), notCopied.Folder);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.OtherMode).Files);

        // A written pak in ~mods under its name but another size is not this export's either.
        File.Delete(Path.Combine(mods, "pakchunk900-My_Mod_P.pak"));
        Install(vehicles, mods);
        Install(items, mods);
        Install(registry, mods);
        await Pak(items, registry: true, size: 1300);
        findings = InstallCheck.Check(Input(game, null, exports, written: [items, registry]));
        Assert.Equal(["pakchunk914-My_Mod-Items_P.pak"], Find(findings, InstallSide.Game, InstallIssue.NotCopied).Files);
        Assert.DoesNotContain(findings, f => f.Issue == InstallIssue.OtherMode);

        // Nothing of the project installed: the folder to copy is the one the export wrote, not the newest other export.
        foreach (var pak in Directory.GetFiles(mods))
        {
            File.Delete(pak);
        }

        File.SetLastWriteTimeUtc(items, DateTime.UtcNow.AddMinutes(10)); // the pages' export looks newer
        var notInstalled = Find(InstallCheck.Check(Input(game, null, exports, written: [whole])), InstallSide.Game, InstallIssue.NotInstalled);
        Assert.Equal(ProjectExporter.ExportFolder(exports, Mod, pages: false, ProjectSourceRole.Client), notInstalled.Folder);
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], notInstalled.Files);
    }

    /// <summary>
    /// Review r40, finding 3: the engine finds paks by walking <c>Content\Paks</c> recursively, so a <c>Client</c> folder copied
    /// into <c>~mods</c> loads. The check reads the subfolders too, names this mod's paks found there, and orders paks by their
    /// whole path (a subfolder sorts by its name, with <c>/</c> between folders).
    /// </summary>
    [Fact]
    public async Task PaksInSubfoldersOfModsAreReadNamedAndOrderedByTheirPath()
    {
        using var temp = new LevelTempDirectory();
        var (game, _, exports) = Folders(temp);
        var mods = Path.Combine(game, "~mods");
        var whole = await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 1000);

        // The whole Client folder copied into ~mods: installed (the game loads it), and named as being in a subfolder.
        var nested = Path.Combine("My_Mod", "Client", "pakchunk900-My_Mod_P.pak");
        Install(whole, Path.Combine(mods, "My_Mod", "Client"));
        var findings = InstallCheck.Check(Input(game, null, exports));
        Assert.DoesNotContain(findings, f => f.Issue == InstallIssue.NotInstalled);
        Assert.Equal([nested], Find(findings, InstallSide.Game, InstallIssue.Installed).Files);
        Assert.Equal([nested], Find(findings, InstallSide.Game, InstallIssue.InSubfolder).Files);
        Assert.Equal(mods, Find(findings, InstallSide.Game, InstallIssue.InSubfolder).Folder);

        // A current copy at the top and an older one left in a subfolder, an older chunk of the mod in another, and other mods'
        // paks with their own registry: "zzz\…" mounts before "pakchunk900-…", "aaa\…" after it.
        Directory.Delete(Path.Combine(mods, "My_Mod"), recursive: true);
        Install(whole, mods);
        await Pak(Path.Combine(mods, "old", "pakchunk900-My_Mod_P.pak"), registry: true, size: 900);
        await Pak(Path.Combine(mods, "older", "pakchunk950-My_Mod_P.pak"), registry: false, size: 800);
        await Pak(Path.Combine(mods, "zzz", "pakchunk1-Other_P.pak"), registry: true, size: 700);
        await Pak(Path.Combine(mods, "aaa", "pakchunk1-Low_P.pak"), registry: true, size: 600);
        findings = InstallCheck.Check(Input(game, null, exports));
        Assert.Equal(["pakchunk900-My_Mod_P.pak"], Find(findings, InstallSide.Game, InstallIssue.Installed).Files);
        Assert.Equal([Path.Combine("old", "pakchunk900-My_Mod_P.pak")], Find(findings, InstallSide.Game, InstallIssue.Stale).Files);
        Assert.Equal([Path.Combine("old", "pakchunk900-My_Mod_P.pak"), Path.Combine("older", "pakchunk950-My_Mod_P.pak")],
            Find(findings, InstallSide.Game, InstallIssue.InSubfolder).Files.Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine("older", "pakchunk950-My_Mod_P.pak")], Find(findings, InstallSide.Game, InstallIssue.OldName).Files);
        Assert.Equal([Path.Combine("zzz", "pakchunk1-Other_P.pak")], Find(findings, InstallSide.Game, InstallIssue.RegistryAbove).Files);
        Assert.Equal([Path.Combine("aaa", "pakchunk1-Low_P.pak")], Find(findings, InstallSide.Game, InstallIssue.RegistryBelow).Files);

        // The order of whole paths: "/" sorts below digits ("abc/…" after "abc0…"), where "\" would sort above them.
        Assert.True(InstallCheck.LoadsBefore(Path.Combine(mods, "zzz", "a_P.pak"), Path.Combine(mods, "pakchunk999-b_P.pak")));
        Assert.False(InstallCheck.LoadsBefore(Path.Combine(mods, "abc", "x_P.pak"), Path.Combine(mods, "abc0_P.pak")));
        Assert.True(InstallCheck.LoadsBefore(Path.Combine(mods, "abc0_P.pak"), Path.Combine(mods, "abc", "x_P.pak")));
    }

    /// <summary>
    /// Review r40, finding 4: a project without new items, vehicles or recipes has nothing another mod's asset list could hide;
    /// the list loading first is a note then, and a pak that cannot be looked into is not named.
    /// </summary>
    [Fact]
    public async Task AnotherModsListLoadingFirstIsANoteForAProjectWithoutNewAssets()
    {
        using var temp = new LevelTempDirectory();
        var (game, _, exports) = Folders(temp);
        var mods = Path.Combine(game, "~mods");
        Install(await Pak(ExportFile(exports, false, "Client", "pakchunk900-My_Mod_P.pak"), registry: true, size: 1000), mods);
        await Pak(Path.Combine(mods, "pakchunk99-Other_P.pak"), registry: true, size: 800);
        await File.WriteAllBytesAsync(Path.Combine(mods, "pakchunk98-Broken_P.pak"), new byte[300]);

        var mapOnly = InstallCheck.Check(Input(game, null, exports, hasNewAssets: false));
        Assert.Equal(InstallSeverity.Info, Find(mapOnly, InstallSide.Game, InstallIssue.RegistryAbove).Severity);
        Assert.DoesNotContain(mapOnly, f => f.Issue == InstallIssue.Unreadable);
        Assert.DoesNotContain(mapOnly, f => f.Severity is InstallSeverity.Problem or InstallSeverity.Warning);

        foreach (var hasNew in new bool?[] { true, null })
        {
            var withClones = InstallCheck.Check(Input(game, null, exports, hasNewAssets: hasNew));
            Assert.Equal(InstallSeverity.Problem, Find(withClones, InstallSide.Game, InstallIssue.RegistryAbove).Severity);
            Assert.Equal(["pakchunk98-Broken_P.pak"], Find(withClones, InstallSide.Game, InstallIssue.Unreadable).Files);
        }
    }

    private static InstallFinding Find(IReadOnlyList<InstallFinding> findings, InstallSide side, InstallIssue issue) =>
        Assert.Single(findings, f => f.Side == side && f.Issue == issue);

    private static InstallCheckInput Input(string game, string? server, string exports, IReadOnlyList<string>? written = null, bool? hasNewAssets = null) =>
        new() { ModNames = OwnPaks.ProjectNames(Mod), GamePaks = game, ServerPaks = server, ExportFolder = exports, Written = written ?? [], HasNewAssets = hasNewAssets };

    private static (string Game, string Server, string Exports) Folders(LevelTempDirectory temp)
    {
        var game = Directory.CreateDirectory(temp.Combine("SCUM", "SCUM", "Content", "Paks")).FullName;
        var server = Directory.CreateDirectory(temp.Combine("SCUMServer", "SCUM", "Content", "Paks")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "~mods"));
        Directory.CreateDirectory(Path.Combine(server, "~mods"));
        return (game, server, temp.Combine("exports"));
    }

    private static string ExportFile(string exports, bool pages, string role, string pak) =>
        Path.Combine(ProjectExporter.ExportFolder(exports, Mod, pages, role == "Server" ? ProjectSourceRole.Server : ProjectSourceRole.Client), pak);

    /// <summary>Writes a small pak (its size grows with <paramref name="size"/>), with or without an asset registry, and a .sig.</summary>
    private static async Task<string> Pak(string path, bool registry, int size, bool sig = false)
    {
        var staging = Path.Combine(Path.GetTempPath(), "scumstudio-installcheck-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "SCUM", "Content", "Mods"));
            await File.WriteAllBytesAsync(Path.Combine(staging, "SCUM", "Content", "Mods", "Thing.uasset"), new byte[size]);
            if (registry)
            {
                await File.WriteAllBytesAsync(Path.Combine(staging, "SCUM", "AssetRegistry.bin"), new byte[64]);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await new PakWriter().WriteFromDirectoryAsync(staging, path);
            if (sig)
            {
                await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".sig"), new byte[536]);
            }

            return path;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Copies a pak (and its .sig) into a mods folder as a user does: the file keeps its time.</summary>
    private static void Install(string pak, string mods, bool withSig = true)
    {
        Directory.CreateDirectory(mods);
        File.Copy(pak, Path.Combine(mods, Path.GetFileName(pak)), overwrite: true);
        var sig = Path.ChangeExtension(pak, ".sig");
        if (withSig && File.Exists(sig))
        {
            File.Copy(sig, Path.Combine(mods, Path.GetFileName(sig)), overwrite: true);
        }
    }
}
