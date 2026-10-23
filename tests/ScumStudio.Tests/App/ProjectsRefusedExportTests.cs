using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord, O Piqueno (3.4.0): "the .pak file is not updating". The package check had refused his export; 3.4.0 said so
/// only in a 12-second toast and the Projects page kept showing the earlier export's pak as the last export. A refused
/// export now replaces the Last export card with what happened (no pak, the problems, the report) and its toast stays.
/// </summary>
public sealed class ProjectsRefusedExportTests
{
    [AvaloniaFact]
    public async Task ARefusedExportShowsOnTheProjectsPage()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 1300);
        try
        {
            var game = ctx.Combine("game");
            SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
            await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
            var page = (ProjectsPageViewModel)vm.NavigateTo("projects")!;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "MyMapMod");
            HeadlessUi.Pump();
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
            HeadlessUi.Pump();
            page.ExportFolder = ctx.Combine("out");
            page.ExportServer = false;
            await page.ExportCommand.ExecuteAsync(null);
            var pak = Assert.Single(page.LastExport).PakPath!;
            Assert.True(File.Exists(pak));
            var pakBytes = await File.ReadAllBytesAsync(pak);
            var sig = Path.ChangeExtension(pak, ".sig");
            var sigBytes = File.Exists(sig) ? await File.ReadAllBytesAsync(sig) : null;

            // An imported mod with a package the check cannot read back: the next export is refused.
            ExportRefusalTests.BrokenMod(ctx.Services.Projects.Current!.DirectoryPath);
            ctx.Services.Notifications.DismissAll();
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            HeadlessUi.Find<ItemsControl>(window).First(i => ReferenceEquals(i.ItemsSource, page.ExportRows)).BringIntoView();
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "projects-refused-export");

            // The card says no pak was written and where the problems are, never the earlier pak as this export's.
            Assert.Contains(page.ExportRows, r => r.Value.Contains("NOT written", StringComparison.Ordinal));
            Assert.DoesNotContain(page.ExportRows, r => r.Value == pak);
            Assert.Contains(page.ExportRows, r => r.Value.EndsWith(ModPackageValidator.ListFileName, StringComparison.Ordinal));
            Assert.Contains(page.ExportRows, r => r.Value.EndsWith(ProjectExporter.ReportFileName, StringComparison.Ordinal));
            Assert.Contains(page.ExportWarnings, w => w.Contains("cannot be read back", StringComparison.Ordinal));
            Assert.Empty(page.LastExport);
            Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(pak)), page.LastExportFolder); // Open folder: the mod's folder, where the report is (review 3.5.1)
            var toast = Assert.Single(ctx.Services.Notifications.Toasts, t => t.Severity == ToastSeverity.Error); // one toast, not the operation's too
            Assert.Equal("Mod NOT exported", toast.Title);
            Assert.Contains(ProjectExporter.ReportFileName, toast.Message, StringComparison.Ordinal);
            Assert.True(HeadlessUi.FindNamed<Button>(window, "OpenProblemListButton") is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
            Assert.Equal(pakBytes, await File.ReadAllBytesAsync(pak)); // the earlier pak stays as it is, under its own name (never renamed, moved or deleted)
            Assert.Equal(sigBytes, File.Exists(sig) ? await File.ReadAllBytesAsync(sig) : null);
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(pak)!), f => Path.GetFileName(f).StartsWith(Path.GetFileName(pak) + ".", StringComparison.OrdinalIgnoreCase));

            // The broken mod removed: the next export writes the pak, the card shows it and the refusal's toast goes.
            Directory.Delete(Path.Combine(ctx.Services.Projects.Current!.DirectoryPath, ProjectMods.FolderName), recursive: true);
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            Assert.True(File.Exists(pak));
            Assert.Contains(page.ExportRows, r => r.Value == pak);
            Assert.Null(page.ProblemListPath);
            Assert.False(HeadlessUi.FindNamed<Button>(window, "OpenProblemListButton")!.IsEffectivelyVisible);
            Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Title == "Mod NOT exported");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
