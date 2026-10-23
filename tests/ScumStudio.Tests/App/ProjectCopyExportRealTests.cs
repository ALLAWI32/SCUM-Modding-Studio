using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Opt-in check of the 3.5.1 export layout on a real project (the release check exports a COPY of a player's project; never
/// the project itself): <c>SCUMSTUDIO_PROJECT_COPY</c> names that copy, <c>SCUMSTUDIO_EXPORT_OUT</c> an empty folder that keeps
/// what is written (default: a temporary one), with <c>SCUM_PAKS</c> and <c>SCUM_SERVER_PAKS</c>. The app's whole export with
/// the server pak, then the Vehicles and the Craftables page's own export into the same folder, twice: the output folder holds
/// only <c>&lt;Mod&gt;</c> and <c>&lt;Mod&gt; - pages</c>, each role folder only paks, their .sig, the reports (and the economy
/// file) beside the hidden work folder, every report starts with what to copy, a server report says <c>-fileopenlog</c>, a
/// pre-3.5.1 export's loose files of this mod are tidied away, and the second export leaves the same files.
/// </summary>
public sealed class ProjectCopyExportRealTests(ITestOutputHelper output)
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AProjectsWholeAndPageExportsLandInTheModsOwnFolders()
    {
        if (RealPaks.Folder is not { } paks
            || Environment.GetEnvironmentVariable("SCUMSTUDIO_PROJECT_COPY") is not { Length: > 0 } copy || !Directory.Exists(copy)
            || Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is not { Length: > 0 } server || !Directory.Exists(server))
        {
            return; // needs the game's and a dedicated server's files and a copy of a project
        }

        using var ctx = AppTestContext.Create(inline: false);
        var outDir = Environment.GetEnvironmentVariable("SCUMSTUDIO_EXPORT_OUT") is { Length: > 0 } kept ? kept : ctx.Combine("out");
        Directory.CreateDirectory(outDir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ServerPaksFolder = server });
        var toasts = new List<string>();
        ctx.Services.Notifications.Toasts.CollectionChanged += (_, e) =>
            toasts.AddRange((e.NewItems ?? Array.Empty<object>()).OfType<ToastViewModel>().Select(t => $"{(t.IsError ? "error" : t.IsWarning ? "warning" : "info")} {t.Title}: {t.Message}"));
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.OpenAsync(copy);
        var mod = ProjectExporter.SanitizeModName(project.Manifest.Name);

        // What an export before 3.5.1 left loose in <out>\Client: this mod's pak, its .sig and a staging folder.
        var oldClient = Path.Combine(outDir, ProjectExporter.ClientFolderName);
        Directory.CreateDirectory(Path.Combine(oldClient, ProjectExporter.StagingFolderName));
        await File.WriteAllTextAsync(Path.Combine(oldClient, ProjectExporter.StagingFolderName, "old.txt"), "old");
        var oldPak = Path.Combine(oldClient, ProjectExporter.PakFileName(mod, ProjectExporter.DefaultPakChunkIndex));
        await File.WriteAllTextAsync(oldPak, "old");
        await File.WriteAllTextAsync(ScumStudio.Pak.Writing.SigCopier.GetSigPath(oldPak), "old");

        var rounds = new List<IReadOnlyList<string>>();
        for (var round = 1; round <= 2; round++)
        {
            var written = new List<ExportResult>();
            written.AddRange(await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, outDir, null, IncludeServer: true), ProgressSink.Null));
            foreach (var module in new[] { ExportModule.Vehicles, ExportModule.Craftables })
            {
                Assert.Null(ModExportService.ModuleBlocker(ctx.Services, project, module));
                written.AddRange(await ModExportService.ExportModuleAsync(ctx.Services, project, module, outDir, includeServer: true, ProgressSink.Null));
            }

            foreach (var r in written)
            {
                output.WriteLine($"round {round}: {r.Role} {Path.GetFileName(r.PakPath)}: {r.Validation?.Summary ?? "not checked"}; {r.Warnings.Count} warning(s)");
                Assert.True(r.Validation is { Passed: true } && File.Exists(r.PakPath) && File.Exists(r.SigPath),
                    $"{r.Role} {Path.GetFileName(r.PakPath)}: {string.Join('\n', r.Validation?.Problems ?? ["not checked"])}");
            }

            var listing = Listing(outDir);
            rounds.Add(listing.Select(l => l.Split("  ")[0]).ToList());
            output.WriteLine($"--- listing after round {round} ---");
            listing.ForEach(output.WriteLine);
            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_EXPORT_OUT") is { Length: > 0 })
            {
                await File.WriteAllLinesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outDir))!, $"listing-round{round}.txt"), listing);
            }

            // Only the mod's two folders under the output folder: the old loose files are gone.
            Assert.Equal(new[] { mod, mod + ProjectExporter.PagesFolderSuffix }.Order(StringComparer.OrdinalIgnoreCase),
                Directory.GetFileSystemEntries(outDir).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
            foreach (var pages in new[] { false, true })
            {
                foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
                {
                    var folder = ProjectExporter.ExportFolder(outDir, mod, pages, role);
                    Assert.All(Directory.GetFileSystemEntries(folder), entry => Assert.True(
                        Path.GetExtension(entry) is ".pak" or ".sig"
                        || Path.GetFileName(entry) is var n && (n.StartsWith("export-report", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)
                            || n == "EconomyOverride.json" || n == ProjectExporter.WorkFolderName), entry));
                    Assert.True(new DirectoryInfo(Path.Combine(folder, ProjectExporter.WorkFolderName)).Attributes.HasFlag(FileAttributes.Hidden), folder);
                    Assert.False(File.Exists(Path.Combine(folder, ModPackageValidator.ListFileName)), "package-check.txt only on a refusal");
                    foreach (var report in Directory.GetFiles(folder, "export-report*.md"))
                    {
                        var text = await File.ReadAllTextAsync(report);
                        var first = text.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("## ", StringComparison.Ordinal));
                        Assert.True(first == "## Install", $"{report}: first section {first}");
                        if (role == ProjectSourceRole.Server)
                        {
                            Assert.Contains("-fileopenlog", text, StringComparison.Ordinal);
                        }
                    }
                }
            }

            // Two page paks: the Registry pak comes with them.
            var pagesClient = ProjectExporter.ExportFolder(outDir, mod, pages: true, ProjectSourceRole.Client);
            Assert.Equal(new[]
                {
                    ModuleExport.PakFileName(mod, ExportModule.Vehicles, ProjectExporter.DefaultPakChunkIndex),
                    ModuleExport.PakFileName(mod, ExportModule.Craftables, ProjectExporter.DefaultPakChunkIndex),
                    ModuleExport.RegistryPakFileName(mod, ProjectExporter.DefaultPakChunkIndex),
                }.Order(StringComparer.OrdinalIgnoreCase),
                Directory.GetFiles(pagesClient, "*.pak").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var toast in toasts)
        {
            output.WriteLine($"toast: {toast}");
        }

        // The second export left the same files: nothing stale, nothing new.
        Assert.Equal(rounds[0], rounds[1]);
    }

    /// <summary>Every file and folder under <paramref name="root"/> (the hidden ones too), with sizes and the hidden mark.</summary>
    private static List<string> Listing(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(e => Path.GetRelativePath(root, e.FullName) + (e is DirectoryInfo ? "\\" : string.Empty)
                         + (e is FileInfo f ? "  " + f.Length : string.Empty)
                         + (e.Attributes.HasFlag(FileAttributes.Hidden) ? "  [hidden]" : string.Empty))
            .ToList();
}
