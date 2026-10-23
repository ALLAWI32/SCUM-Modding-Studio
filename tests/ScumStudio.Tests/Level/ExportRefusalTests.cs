using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord, O Piqueno (3.4.0): "the .pak file is not updating". The package check refused his export, and 3.4.0 left the
/// folder looking as if nothing had happened: the pak, the .sig and export-report.md of the earlier export untouched (only a
/// 12-second toast said it), and item-spawners.txt rewritten by the export that wrote no pak. A refused export now rewrites
/// the report as NOT EXPORTED with the reasons and says that the pak in the folder is the EARLIER export (its name and time)
/// and was NOT updated, and keeps the item spawner list of the last export that wrote a pak. The earlier pak and .sig stay
/// byte for byte under their own names (owner, r38: someone exporting straight into a server's mods folder must keep the mod
/// that works). Here the check refuses an imported mod's package it cannot read back.
/// </summary>
public sealed class ExportRefusalTests : IDisposable
{
    private const string Fridge = SyntheticLevels.LevelPath + "|Refrigerator_Added|/Game/ConZ_Files/Items/Kitchen/Refrigerator.Refrigerator_C";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-refusal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task ARefusedExportSaysNotExportedAndLeavesTheEarlierPakAsItIs()
    {
        var game = Path.Combine(_dir, "game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(game);
        var stockSig = Path.Combine(_dir, "stock.sig");
        File.WriteAllBytes(stockSig, [1, 2, 3, 4]);
        var options = new ExportOptions { OutputDirectory = Path.Combine(_dir, "out"), StockSigPath = stockSig };
        var state = new EditState();
        state.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
        var exporter = new ProjectExporter();

        var first = await exporter.ExportAsync(state, "MyMapMod", catalog, options, ProjectSourceRole.Client);
        Assert.True(File.Exists(first.PakPath));
        Assert.True(File.Exists(first.SigPath));
        var role = Path.GetDirectoryName(first.PakPath)!;
        var pakName = Path.GetFileName(first.PakPath)!;
        var pakBytes = await File.ReadAllBytesAsync(first.PakPath!);
        var sigBytes = await File.ReadAllBytesAsync(first.SigPath!);
        var pakTime = File.GetLastWriteTimeUtc(first.PakPath!);
        var sigTime = File.GetLastWriteTimeUtc(first.SigPath!);

        // What the last export that wrote a pak placed (a fridge the project has since removed), in the hidden work folder.
        var spawners = Path.Combine(ProjectExporter.WorkFolder(role), ProjectExporter.ItemSpawnersFileName);
        await File.WriteAllTextAsync(spawners, Fridge + "\n");

        var refused = await Assert.ThrowsAsync<ModValidationException>(() =>
            exporter.ExportAsync(state, "MyMapMod", catalog, options with { Mods = [BrokenMod(Path.Combine(_dir, "project"))] }, ProjectSourceRole.Client));

        // The report says it, first line first, names the problems and the earlier pak (it was NOT updated).
        var reportPath = Path.Combine(role, ProjectExporter.ReportFileName);
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("NOT EXPORTED", report.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("cannot be read back", report, StringComparison.Ordinal);
        Assert.Contains(ModPackageValidator.ListFileName, report, StringComparison.Ordinal);
        Assert.Contains($"({pakName}, written ", report, StringComparison.Ordinal);
        Assert.Contains("is from an EARLIER export and was NOT updated", report, StringComparison.Ordinal);
        Assert.StartsWith("NOT EXPORTED", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reportPath, refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(role, ModPackageValidator.ListFileName)));

        // The earlier pak and .sig stay byte for byte under their own names (an export into a server's mods folder keeps the
        // mod that works): never renamed, moved or deleted.
        Assert.Equal(pakBytes, await File.ReadAllBytesAsync(first.PakPath!));
        Assert.Equal(sigBytes, await File.ReadAllBytesAsync(first.SigPath!));
        Assert.Equal(pakTime, File.GetLastWriteTimeUtc(first.PakPath!));
        Assert.Equal(sigTime, File.GetLastWriteTimeUtc(first.SigPath!));
        Assert.Equal(new[] { first.PakPath! }, Directory.GetFiles(role, "*.pak*"));
        Assert.Equal(Path.Combine(role, ProjectExporter.WorkFolderName), Assert.Single(Directory.GetDirectories(role))); // the paks' folder holds no staging folder

        // The export that wrote no pak leaves the item spawner list of the last one that did.
        Assert.Equal(Fridge + "\n", await File.ReadAllTextAsync(spawners));

        // Fixed (the broken mod gone): the export writes the pak and a normal report again, and names the fridge the last
        // export with a pak had placed.
        var again = await exporter.ExportAsync(state, "MyMapMod", catalog, options, ProjectSourceRole.Client);
        Assert.True(File.Exists(again.PakPath));
        Assert.True(File.Exists(again.SigPath));
        Assert.DoesNotContain("NOT EXPORTED", await File.ReadAllTextAsync(again.ReportPath!), StringComparison.Ordinal);
        Assert.Contains(again.RemovedItemSpawners, s => s.Contains("Refrigerator_Added", StringComparison.Ordinal) && s.Contains("earlier export", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(role, ModPackageValidator.ListFileName)));
    }

    [Fact]
    public void TheRefusalReportNamesTheProblemsAndTheEarlierPakWhichStaysAsItIs()
    {
        var role = Directory.CreateDirectory(Path.Combine(_dir, "Client")).FullName;
        var pak = Path.Combine(role, "pakchunk900-MyMapMod_P.pak");
        var sig = Path.ChangeExtension(pak, ".sig");
        File.WriteAllBytes(pak, [7, 7, 7]);
        File.WriteAllBytes(sig, [5]);
        var earlier = new DateTime(2026, 10, 17, 17, 38, 0, DateTimeKind.Local);
        File.SetLastWriteTime(pak, earlier);
        File.SetLastWriteTime(sig, earlier);
        var at = new DateTime(2026, 10, 17, 18, 26, 0, DateTimeKind.Local);
        var problems = Enumerable.Range(1, 30).Select(i => $"/Game/A: PersistentLevel.Actors[{i}] points at another actor").ToList();
        var list = Path.Combine(role, ModPackageValidator.ListFileName);
        File.WriteAllLines(list, problems);
        var report = Path.Combine(role, ProjectExporter.ReportFileName);
        File.WriteAllText(report, "# MyMapMod — Client export\n- Pak: " + pak);

        var thrown = ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Packages = 2, Problems = problems }, list),
            "MyMapMod", ProjectSourceRole.Client, report, pak, ["No stock .sig found"], now: at);

        var text = File.ReadAllText(report);
        Assert.Equal("# MyMapMod — Client export: NOT EXPORTED", text.Split('\n')[0].TrimEnd('\r'));
        Assert.Contains("Stopped by the package check at 2026-10-17 18:26: 30 problem(s)", text, StringComparison.Ordinal);
        Assert.Contains("pakchunk900-MyMapMod_P.pak was NOT written", text, StringComparison.Ordinal);
        Assert.Contains("All of them: " + list, text, StringComparison.Ordinal);
        Assert.Contains("(pakchunk900-MyMapMod_P.pak, written 2026-10-17 17:38) is from an EARLIER export and was NOT updated", text, StringComparison.Ordinal);
        Assert.Contains("Its .sig (pakchunk900-MyMapMod_P.sig) is the earlier export's too and was NOT updated", text, StringComparison.Ordinal);
        Assert.Contains("the first 25 of 30", text, StringComparison.Ordinal);
        Assert.Contains("Actors[25]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Actors[26]", text, StringComparison.Ordinal);
        Assert.Contains("No stock .sig found", text, StringComparison.Ordinal);

        // The earlier pak and .sig: the same names, bytes and times; nothing else appeared beside them.
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(pak));
        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(sig));
        Assert.Equal(earlier, File.GetLastWriteTime(pak));
        Assert.Equal(earlier, File.GetLastWriteTime(sig));
        Assert.Equal(new[] { pak, sig }, Directory.GetFiles(role, "pakchunk900-MyMapMod_P.*").Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(pak, thrown.PakPath);
        Assert.Equal(report, thrown.ReportPath);
        Assert.Equal(list, thrown.ListPath);
        Assert.StartsWith($"NOT EXPORTED - see {report}\n", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("was not packed", thrown.Message, StringComparison.Ordinal);

        // Refused again: the same earlier pak is named again, still untouched.
        ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["x"] }, list), "MyMapMod", ProjectSourceRole.Client, report, pak, [], now: at);
        Assert.Contains("(pakchunk900-MyMapMod_P.pak, written 2026-10-17 17:38) is from an EARLIER export", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(pak));

        // No earlier pak in the folder: the report says so.
        var none = Path.Combine(role, "pakchunk900-Other_P.pak");
        ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["x"] }, list), "Other", ProjectSourceRole.Client, report, none, [], now: at);
        Assert.Contains("No pakchunk900-Other_P.pak is in this folder", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPakOfTheProjectStaysAndARefusalReportGoesOnceThePakPasses()
    {
        var role = Directory.CreateDirectory(Path.Combine(_dir, "Client")).FullName;
        var paks = new[]
        {
            ProjectExporter.PakFileName("MyMapMod", ProjectExporter.DefaultPakChunkIndex),
            CraftablesExporter.PakFileName("MyMapMod", ProjectExporter.DefaultPakChunkIndex),
            ModuleExport.PakFileName("MyMapMod", ExportModule.Vehicles, ProjectExporter.DefaultPakChunkIndex),
            ModuleExport.RegistryPakFileName("MyMapMod", ProjectExporter.DefaultPakChunkIndex),
        }.Select(n => Path.Combine(role, n)).ToList();
        for (var i = 0; i < paks.Count; i++)
        {
            File.WriteAllBytes(paks[i], [(byte)(i + 1)]);
            File.WriteAllBytes(Path.ChangeExtension(paks[i], ".sig"), [(byte)(i + 11)]);
        }

        // Each pak refused in turn (the project pak, the Craftables pak, a page pak, the Registry pak): every one stays.
        var report = Path.Combine(role, ModuleExport.ReportFileName(ExportModule.Craftables));
        foreach (var pak in paks)
        {
            var thrown = ProjectExporter.WriteRefusal(new ModValidationException(new ValidationResult { Problems = ["y"] }), "MyMapMod", ProjectSourceRole.Client, report, pak, []);
            Assert.Equal(pak, thrown.PakPath);
            Assert.Contains($"({Path.GetFileName(pak)}, written ", File.ReadAllText(report), StringComparison.Ordinal);
        }

        for (var i = 0; i < paks.Count; i++)
        {
            Assert.Equal(new[] { (byte)(i + 1) }, File.ReadAllBytes(paks[i]));
            Assert.Equal(new[] { (byte)(i + 11) }, File.ReadAllBytes(Path.ChangeExtension(paks[i], ".sig")));
        }

        Assert.Equal(paks.Count * 2, Directory.GetFiles(role, "pakchunk*").Length);

        // A pak that passes clears its old refusal report; any other file stays.
        ProjectExporter.ClearRefusal(report);
        Assert.False(File.Exists(report));
        File.WriteAllText(report, "# MyMapMod — Client export\n");
        ProjectExporter.ClearRefusal(report);
        Assert.True(File.Exists(report));
    }

    /// <summary>An imported mod (in <paramref name="project"/>'s mods folder) whose one package is not a package.</summary>
    internal static string BrokenMod(string project)
    {
        var folder = Path.Combine(project, ProjectMods.FolderName, "Broken");
        var content = Path.Combine(folder, "SCUM", "Content", "Broken");
        Directory.CreateDirectory(content);
        File.WriteAllBytes(Path.Combine(content, "SM_Junk.uasset"), [.. Enumerable.Range(0, 256).Select(i => (byte)(i * 7))]);
        return folder;
    }
}
