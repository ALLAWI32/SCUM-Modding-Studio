using System.Security.Cryptography;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Real game files (Discord report, O Piqueno 2026-10-17: "the .pak file is not updating; the modification timestamps show
/// the pak was not exported"). In 3.4.0 the package check refused his second export (one farm object duplicated, then one
/// deleted as well in the same level), and the folder kept the first export's pak, .sig and export-report.md as they were
/// while item-spawners.txt and package-check.txt were new. 3.5.0 exports that edit (3bc6138); a refusal (here forced by an
/// imported mod's broken package) now says NOT EXPORTED in the report and names the earlier pak, which stays byte for byte under its own name.
/// </summary>
public sealed class ExportRefusalFilesRealTests(ITestOutputHelper output)
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    [Fact]
    public async Task DeleteAndCopyInOneLevelExportsAndARefusalSaysSo()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-refusal-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "MyMapMod");
            var farm = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Farm);
            var objects = farm.Actors.Where(a => a.Kind == ActorKind.StaticMeshActor && a.Root is not null).Take(2).ToList();

            // 17:38 in his folder: one object duplicated 5 m away.
            var copied = objects[1].Root!.Relative;
            project.Apply(new DuplicateActorOp(new ActorRef(Farm, objects[1].Name), objects[1].Name + "_Copy",
                copied with { Location = copied.Location + new FVector(500, 0, 0) }));
            var outDir = Path.Combine(dir, "out");
            var options = new ExportOptions { OutputDirectory = outDir };
            var exporter = new ProjectExporter();
            var first = await exporter.ExportAsync(project, catalog, options);
            Assert.True(first.Validation is { Passed: true }, string.Join('\n', first.Validation?.Problems ?? []));
            Assert.True(File.Exists(first.PakPath));
            var role = ProjectExporter.ExportFolder(outDir, "MyMapMod", pages: false, ProjectSourceRole.Client);
            Assert.Equal(role, Path.GetDirectoryName(first.PakPath));
            var pak = Path.GetFileName(first.PakPath)!;
            var sig = Path.GetFileName(first.SigPath)!;
            var spawners = Path.Combine(ProjectExporter.WorkFolder(role), ProjectExporter.ItemSpawnersFileName); // the exporter's notes: hidden work folder
            Print("After the first export (passed):", Snapshot(role));
            await Task.Delay(TimeSpan.FromSeconds(2)); // so the exports' times differ on any file system

            // 18:26 in his folder: one object deleted too, in the same level. Refused by 3.4.0, exported now.
            project.Apply(new DeleteActorOp(new ActorRef(Farm, objects[0].Name)));
            var second = await exporter.ExportAsync(project, catalog, options);
            Assert.True(second.Validation is { Passed: true }, string.Join('\n', second.Validation?.Problems ?? []));
            Assert.Contains(objects[0].Name, second.Levels.Single(l => string.Equals(l.PackagePath, Farm, StringComparison.OrdinalIgnoreCase)).Report.RemovedActors);
            var passed = Snapshot(role);
            var pakHash = Hash(Path.Combine(role, pak));
            var sigHash = Hash(Path.Combine(role, sig));
            var spawnersPassed = File.GetLastWriteTime(spawners);
            Print($"After the second export ({second.Validation!.Summary}):", passed);
            Assert.DoesNotContain(ModPackageValidator.ListFileName, passed.Keys);
            Assert.Equal(new[] { ProjectExporter.WorkFolderName, ProjectExporter.ReportFileName, pak, sig }.Order(StringComparer.OrdinalIgnoreCase), passed.Keys); // the paks, the report and the hidden work folder: nothing else
            await Task.Delay(TimeSpan.FromSeconds(2));

            // The same edit with an imported mod whose package cannot be read back: refused, and the folder says so.
            var refused = await Assert.ThrowsAsync<ModValidationException>(() =>
                exporter.ExportAsync(project, catalog, options with { Mods = [ExportRefusalTests.BrokenMod(project.DirectoryPath)] }));
            var after = Snapshot(role);
            Print("After the refused export:", after);
            output.WriteLine(refused.Message.Split('\n')[0]);

            var report = await File.ReadAllTextAsync(Path.Combine(role, ProjectExporter.ReportFileName));
            output.WriteLine(report);
            Assert.Equal($"# MyMapMod — Client export: {ProjectExporter.NotExported}", report.Split('\n')[0].TrimEnd('\r'));
            Assert.Contains("cannot be read back", report, StringComparison.Ordinal);
            Assert.True(after[ProjectExporter.ReportFileName] > passed[ProjectExporter.ReportFileName], "the report is rewritten");
            Assert.True(after[ModPackageValidator.ListFileName] > passed[ProjectExporter.ReportFileName], "package-check.txt is written");
            Assert.Equal(passed[pak], after[pak]); // the second export's pak stays as it is, under its own name (never renamed, moved or deleted)
            Assert.Equal(passed[sig], after[sig]);
            Assert.Equal(pakHash, Hash(Path.Combine(role, pak)));
            Assert.Equal(sigHash, Hash(Path.Combine(role, sig)));
            Assert.DoesNotContain(after.Keys, k => k.StartsWith(pak + ".", StringComparison.OrdinalIgnoreCase) || k.StartsWith(sig + ".", StringComparison.OrdinalIgnoreCase));
            Assert.Contains($"({pak}, written ", report, StringComparison.Ordinal);
            Assert.Contains("is from an EARLIER export and was NOT updated", report, StringComparison.Ordinal);
            Assert.Equal(spawnersPassed, File.GetLastWriteTime(spawners)); // written only with a pak
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static byte[] Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    private static SortedDictionary<string, DateTime> Snapshot(string folder)
    {
        var entries = new SortedDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            entries[entry.Name] = entry.LastWriteTime;
        }

        return entries;
    }

    private void Print(string title, SortedDictionary<string, DateTime> entries)
    {
        output.WriteLine(title);
        foreach (var (name, time) in entries)
        {
            output.WriteLine($"  {name,-48} {time:yyyy-MM-dd HH:mm:ss.fff}");
        }

        output.WriteLine(string.Empty);
    }
}
