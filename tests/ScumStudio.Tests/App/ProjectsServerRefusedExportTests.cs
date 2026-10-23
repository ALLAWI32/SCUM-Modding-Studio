using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Modding;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.App;

/// <summary>
/// Review 3.5.1 (regression lens): the package check refuses only the SERVER pak (the client's passed). The earlier server pak
/// now stays in the Server folder (owner, r38) and its report says NOT EXPORTED; the Projects page must then say so as it
/// does for a refused client pak (CHANGELOG, Unreleased: "A refused export no longer looks like nothing happened"), or the
/// Server folder holds an old pak under this export's client pak with only a 12-second warning toast having said why.
/// </summary>
public sealed class ProjectsServerRefusedExportTests
{
    private const string Boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder";

    [AvaloniaFact]
    public async Task AServerOnlyRefusalShowsOnTheProjectsPageLikeAClientOne()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 1300);
        try
        {
            // The client's game files (loose, as the other Projects tests open them): SM_Boulder holds an object of its name.
            var game = ctx.Combine("game");
            SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
            SyntheticGame.Write(game, Boulder, SyntheticGame.Package(Boulder));

            // The dedicated server's cook (paks, as the export resolves it): the same level, but its SM_Boulder package holds
            // no object SM_Boulder, so a placed SM_Boulder fails the server's package check only ("has no object").
            var serverContent = ctx.Combine("server-content");
            SyntheticLevels.WriteContent(serverContent, withBlueprintPackage: true);
            SyntheticGame.Write(serverContent, Boulder, SyntheticGame.Package(Boulder + "X"));
            var serverPaks = Directory.CreateDirectory(ctx.Combine("ServerPaks")).FullName;
            await new PakWriter().WriteFromDirectoryAsync(serverContent, Path.Combine(serverPaks, "pakchunk0-WindowsServer.pak"));
            ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = serverPaks });

            await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
            var page = (ProjectsPageViewModel)vm.NavigateTo("projects")!;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "MyMapMod");
            HeadlessUi.Pump();
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
            HeadlessUi.Pump();
            page.ExportFolder = ctx.Combine("out");
            page.ExportServer = true;
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            var serverPak = Assert.Single(page.LastExport, r => r.Role == ProjectSourceRole.Server).PakPath!;
            Assert.True(File.Exists(serverPak));
            var serverBytes = await File.ReadAllBytesAsync(serverPak);

            // The placed mesh: the client's pak passes, the server's is refused.
            ctx.Services.Projects.Apply(new AddStaticMeshActorOp(SyntheticLevels.LevelPath, "SM_Boulder_Added", Boulder + ".SM_Boulder", TransformValue.At(-900, 250, 0)));
            HeadlessUi.Pump();
            ctx.Services.Notifications.DismissAll();
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();

            // The folder is right by design: the earlier server pak stays as it is and the Server report says NOT EXPORTED.
            Assert.Contains(page.LastExport, r => r.Role == ProjectSourceRole.Client);
            Assert.DoesNotContain(page.LastExport, r => r.Role == ProjectSourceRole.Server);
            Assert.Equal(serverBytes, await File.ReadAllBytesAsync(serverPak));
            var report = Path.Combine(Path.GetDirectoryName(serverPak)!, ProjectExporter.ReportFileName);
            Assert.EndsWith(ProjectExporter.NotExported, File.ReadLines(report).First().TrimEnd('\r'));

            // The page must say so too (as for a refused client pak): a row saying the server pak was NOT written, and an
            // error toast that stays. At 950678b the Last export card showed only the client's rows and the only word was a
            // 12-second warning toast, while the old server pak sat under the new client pak.
            Assert.Contains(page.ExportRows, r => r.Value.Contains("NOT written", StringComparison.Ordinal));
            Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Severity == ToastSeverity.Error);

            // Fix: the toast names the server pak and its report and stays until it is closed; the card lists the server's
            // problems, its report and the problem list (the Open package-check.txt button), and never tells to set the Server
            // folder (it is set) as a client-only export would.
            var toast = Assert.Single(ctx.Services.Notifications.Toasts, t => t.Severity == ToastSeverity.Error);
            Assert.Equal(Loc.T("Export.ServerRefused"), toast.Title);
            Assert.Contains(Path.GetFileName(serverPak), toast.Message, StringComparison.Ordinal);
            Assert.Contains(report, toast.Message, StringComparison.Ordinal);
            Assert.Contains(page.ExportRows, r => r.Value == report);
            var list = Path.Combine(Path.GetDirectoryName(serverPak)!, ModPackageValidator.ListFileName);
            Assert.Contains(page.ExportRows, r => r.Value == list);
            Assert.Equal(list, page.ProblemListPath);
            Assert.Contains(page.ExportWarnings, w => w.StartsWith("Server: ", StringComparison.Ordinal) && w.Contains("SM_Boulder", StringComparison.Ordinal));
            var install = Assert.Single(page.ExportRows, r => r.Name == Loc.T("Projects.Row.Install")).Value;
            Assert.Contains(Path.GetFileName(serverPak) + " was NOT written", install, StringComparison.Ordinal);
            Assert.DoesNotContain("Server folder", install, StringComparison.Ordinal);
            Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Message.Contains("Server folder and export again", StringComparison.Ordinal));
            Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Title == Loc.T("Export.ServerSkipped"));

            // The server's problem gone (the placed mesh removed): the next export writes both paks and the toast goes.
            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();
            await page.ExportCommand.ExecuteAsync(null);
            HeadlessUi.Pump();
            Assert.Contains(page.LastExport, r => r.Role == ProjectSourceRole.Server);
            Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Title == Loc.T("Export.ServerRefused"));
            Assert.Null(page.ProblemListPath);
            Assert.DoesNotContain(page.ExportRows, r => r.Value.Contains("NOT written", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
