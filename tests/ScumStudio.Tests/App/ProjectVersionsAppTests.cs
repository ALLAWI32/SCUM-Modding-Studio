using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Level;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// The app side of the r40 "projects" package (Discord: salvador.garcia, pheidian, hexter_98, youngpiegus): the Projects
/// page's Duplicate project, snapshots with Restore and Open as new project, Check my install and its line after an export,
/// and the Map's Reset a sector to stock. No game files.
/// </summary>
[Collection(LanguageCollection.Name)] // reads English texts
public sealed class ProjectVersionsAppTests
{
    private static readonly ActorRef House = new(Outpost, "House_1");
    private static readonly ActorRef Barrel = new(Port, "Barrel_3");
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/B_1_Farm";

    [Fact]
    public async Task TheProjectsPageSavesASnapshotRestoresItAndDuplicatesTheProject()
    {
        using var ctx = AppTestContext.Create();
        var original = await ctx.Services.Projects.CreateAsync(ctx.Services.ProjectsFolder, "Outpost versions");
        var folder = original.DirectoryPath;
        ctx.Services.Projects.Apply(new DeleteActorOp(House));
        using var page = new ProjectsPageViewModel(ctx.Services);
        page.OnNavigatedTo();
        Assert.Equal("Outpost versions copy", page.DuplicateName);
        Assert.False(page.HasSnapshots);

        // Save a snapshot: listed with its name, time and edits; the project goes on.
        page.SnapshotName = "one delete";
        await page.SaveSnapshotCommand.ExecuteAsync(null);
        var row = Assert.Single(page.Snapshots);
        Assert.Equal("one delete", row.Name);
        Assert.Equal(1, row.Snapshot.Edits);
        Assert.Contains("Edits: 1", row.Details, StringComparison.Ordinal);
        Assert.Equal(string.Empty, page.SnapshotName);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Snapshot saved");
        ctx.Services.Projects.Apply(new DeleteActorOp(Barrel));

        // Restore asks first; Cancel keeps everything.
        row.RestoreCommand.Execute(null);
        Assert.True(row.IsConfirming);
        Assert.Contains("one delete", row.ConfirmText, StringComparison.Ordinal);
        row.CancelRestoreCommand.Execute(null);
        Assert.False(row.IsConfirming);
        Assert.True(ctx.Services.Projects.Current!.State.IsDeleted(Barrel));

        // Restore: the project is back as in the snapshot, and what it was is a snapshot of its own.
        row.RestoreCommand.Execute(null);
        await row.ConfirmRestoreCommand.ExecuteAsync(null);
        var restored = ctx.Services.Projects.Current!;
        Assert.Equal(folder, restored.DirectoryPath);
        Assert.True(restored.State.IsDeleted(House));
        Assert.False(restored.State.IsDeleted(Barrel));
        Assert.Equal(2, page.Snapshots.Count);
        var before = Assert.Single(page.Snapshots, s => s.Name == "before restoring one delete");
        Assert.Equal(2, before.Snapshot.Edits);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Snapshot restored");

        // Duplicate project: the copy opens under its own name; the project it came from stays.
        await page.DuplicateCommand.ExecuteAsync(null);
        var copy = ctx.Services.Projects.Current!;
        Assert.Equal("Outpost versions copy", copy.Manifest.Name);
        Assert.Equal(Path.Combine(ctx.Services.ProjectsFolder, "Outpost versions copy.ssproj"), copy.DirectoryPath);
        Assert.True(copy.State.IsDeleted(House));
        Assert.False(page.HasSnapshots); // a copy starts without snapshots
        Assert.Equal("Outpost versions copy copy", page.DuplicateName); // the next copy is a copy of the copy
        Assert.Contains(page.Projects, c => c.Name == "Outpost versions");
        Assert.Contains(page.Projects, c => c.Name == "Outpost versions copy" && c.IsCurrent);

        // Open as new project from a snapshot of the first project.
        var snapshot = ProjectCopies.Snapshots(folder).Single(s => s.Name == "before restoring one delete");
        await ctx.Services.Projects.OpenAsync(folder);
        page.OnNavigatedTo();
        await page.Snapshots.Single(s => s.Snapshot.Path == snapshot.Path).OpenAsNewCommand.ExecuteAsync(null);
        var fromSnapshot = ctx.Services.Projects.Current!;
        Assert.Equal("Outpost versions before restoring one delete", fromSnapshot.Manifest.Name);
        Assert.True(fromSnapshot.State.IsDeleted(Barrel));
    }

    [Fact]
    public async Task CheckMyInstallWordsWhatItFindsAndTheExportToastGetsOneLine()
    {
        using var ctx = AppTestContext.Create();
        var game = Directory.CreateDirectory(ctx.Combine("SCUM", "Content", "Paks", "~mods")).Parent!.FullName;
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = game });
        var project = await ctx.Services.Projects.CreateAsync(ctx.Services.ProjectsFolder, "Checked");
        using var page = new ProjectsPageViewModel(ctx.Services) { ExportFolder = ctx.Combine("exports") };
        page.OnNavigatedTo();

        await page.CheckInstallCommand.ExecuteAsync(null);
        var texts = page.InstallRows.Select(r => r.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("Game: None of this project's paks is in " + Path.Combine(game, "~mods"), StringComparison.Ordinal)
                                    && t.Contains("there is no export of it yet", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Server: No server folder is set", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("-fileopenlog", StringComparison.Ordinal));
        Assert.Contains(page.InstallRows, r => r.Badge == "Check" && r.IsWarning);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Check my install");

        // An export written, not copied yet: the toast's line says where to copy from; copied, it says so; a foreign pak
        // with its own registry loading first is something to fix.
        var client = ProjectExporter.ExportFolder(ctx.Combine("exports"), "Checked", pages: false, ScumStudio.Level.Projects.ProjectSourceRole.Client);
        var pak = await WritePak(Path.Combine(client, "pakchunk900-Checked_P.pak"), registry: true);
        Assert.Equal("Not in the game's ~mods yet: copy every file of " + client + " there.", await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")));
        File.Copy(pak, Path.Combine(game, "~mods", Path.GetFileName(pak)));
        Assert.Equal("The game's ~mods has this export's paks.", await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")));
        await WritePak(pak, registry: true, size: 99);
        File.SetLastWriteTimeUtc(pak, DateTime.UtcNow.AddMinutes(1));
        Assert.StartsWith("The game's ~mods still has the paks of an earlier export", await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")), StringComparison.Ordinal);
        File.Copy(pak, Path.Combine(game, "~mods", Path.GetFileName(pak)), overwrite: true);
        await WritePak(Path.Combine(game, "~mods", "pakchunk99-Other_P.pak"), registry: true);

        // A project without new items, vehicles or recipes: the other mod's list hides nothing of it, a note (review r40).
        Assert.Equal("The game's ~mods has this export's paks.", await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")));
        await page.CheckInstallCommand.ExecuteAsync(null);
        Assert.Contains(page.InstallRows, r => r.Badge == "Note" && r.Text.StartsWith("Game: These paks load before this mod's", StringComparison.Ordinal)
                                               && r.Text.Contains("hides nothing of it", StringComparison.Ordinal));

        // With a craftable (a new asset), it is hidden while the other list loads first: something to fix.
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName),
            new CraftablesFile { Items = [new Craftable { Name = "Bench", Source = "/Game/ConZ_Files/Bench", Mesh = "/Game/ConZ_Files/SM_Bench" }] }.ToJson());
        Assert.Equal("Check my install found 1 thing(s) to fix in the mods folders (Projects → Check my install).",
            await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")));
        await page.CheckInstallCommand.ExecuteAsync(null);
        Assert.Contains(page.InstallRows, r => r.IsProblem && r.Badge == "Fix" && r.Text.StartsWith("Game: These paks load before this mod's", StringComparison.Ordinal)
                                               && r.Text.Contains("pakchunk99-Other_P.pak", StringComparison.Ordinal));

        // No game folder: no line (the export's own toast stands).
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = null });
        Assert.Null(await InstallCheckService.ToastLineAsync(ctx.Services, project, ctx.Combine("exports")));
    }

    /// <summary>
    /// Review r40, finding 1 (probe S1): page paks installed and current, then Map → Export mod writes the whole project's pak.
    /// The toast said "The game's ~mods has this export's paks"; with the paks the export wrote it says where to copy them from
    /// and to delete the other install mode's paks. Both directions.
    /// </summary>
    [Fact]
    public async Task TheExportToastNamesTheExportJustWrittenWhenTheOtherInstallModeIsInModsBothWays()
    {
        using var ctx = AppTestContext.Create();
        var mods = Directory.CreateDirectory(ctx.Combine("SCUM", "Content", "Paks", "~mods")).FullName;
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = Path.GetDirectoryName(mods) });
        var project = await ctx.Services.Projects.CreateAsync(ctx.Services.ProjectsFolder, "Modes");
        var exports = ctx.Combine("exports");
        var pagesClient = ProjectExporter.ExportFolder(exports, "Modes", pages: true, ProjectSourceRole.Client);
        var wholeClient = ProjectExporter.ExportFolder(exports, "Modes", pages: false, ProjectSourceRole.Client);
        var items = await WritePak(Path.Combine(pagesClient, "pakchunk914-Modes-Items_P.pak"), registry: true);
        var registry = await WritePak(Path.Combine(pagesClient, "pakchunk919-Modes-Registry_P.pak"), registry: true, size: 20);
        void Copy(params string[] paks) => Array.ForEach(paks, p => File.Copy(p, Path.Combine(mods, Path.GetFileName(p)), overwrite: true));
        Copy(items, registry);
        var whole = await WritePak(Path.Combine(wholeClient, "pakchunk900-Modes_P.pak"), registry: true, size: 30);

        Assert.Equal("Not in the game's ~mods yet: copy every file of " + wholeClient + " there. Then delete this project's other paks there "
                     + "(pakchunk914-Modes-Items_P.pak, pakchunk919-Modes-Registry_P.pak): the whole project's pak and page paks must not be installed together.",
            await InstallCheckService.ToastLineAsync(ctx.Services, project, exports, written: [whole]));
        Copy(whole);
        Assert.Equal("Check my install found 1 thing(s) to fix in the mods folders (Projects → Check my install).", // both modes now
            await InstallCheckService.ToastLineAsync(ctx.Services, project, exports, written: [whole]));

        // The other way round: the whole project's pak is installed, then a page is exported.
        File.Delete(Path.Combine(mods, Path.GetFileName(items)));
        File.Delete(Path.Combine(mods, Path.GetFileName(registry)));
        Assert.Equal("Not in the game's ~mods yet: copy every file of " + pagesClient + " there. Then delete this project's other paks there "
                     + "(pakchunk900-Modes_P.pak): the whole project's pak and page paks must not be installed together.",
            await InstallCheckService.ToastLineAsync(ctx.Services, project, exports, written: [items, registry]));
        Copy(items, registry);
        File.Delete(Path.Combine(mods, Path.GetFileName(whole)));
        Assert.Equal("The game's ~mods has this export's paks.", await InstallCheckService.ToastLineAsync(ctx.Services, project, exports, written: [items, registry]));
    }

    [Fact]
    public async Task TheMapsResetASectorTakesTheSectorBackInOneHistoryRow()
    {
        using var ctx = AppTestContext.Create();
        using var map = new MapPageViewModel(ctx.Services);
        map.RefreshSectorReset();
        Assert.Equal("Open a project first.", map.ResetNote);
        Assert.False(map.ResetSectorCommand.CanExecute(null));

        await ctx.Services.Projects.CreateAsync(ctx.Services.ProjectsFolder, "Reset");
        ctx.Services.Projects.Apply(new DeleteActorOp(House));
        ctx.Services.Projects.Apply(new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Somewhere));
        ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(Farm, "Silo_1")));
        Assert.Equal(25, map.ResetCells.Count);

        map.PrepareSectorReset();
        map.ResetCell = new MapCell('B', 0);
        Assert.Empty(map.ResetLines);
        Assert.Equal("This project changed nothing in B_0.", map.ResetNote);
        Assert.False(map.ResetSectorCommand.CanExecute(null));

        map.ResetCell = new MapCell('A', 0);
        Assert.Equal(["1 deleted object(s) come back", "1 added object(s) are removed (placed, copied, pasted, painted, prefabs, traders, new spawn places)"], map.ResetLines);
        Assert.Equal("Reset A_0 to stock", map.ResetButtonText);
        Assert.True(map.ResetSectorCommand.CanExecute(null));
        var rows = ctx.Services.Projects.History.Count;
        map.ResetSectorCommand.Execute(null);

        var state = ctx.Services.Projects.Current!.State;
        Assert.Equal(rows + 1, ctx.Services.Projects.History.Count);
        Assert.StartsWith("Reset sector A_0 to stock", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        Assert.False(state.IsDeleted(House));
        Assert.False(state.IsAdded(new ActorRef(Port, "SM_Rock_Added")));
        Assert.True(state.IsDeleted(new ActorRef(Farm, "Silo_1")));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Sector A_0 reset to stock");
        Assert.False(map.ResetSectorCommand.CanExecute(null)); // nothing left there

        // Undo puts the sector's changes back, as one step.
        map.UndoCommand.Execute(null);
        Assert.True(state.IsDeleted(House));
        Assert.True(state.IsAdded(new ActorRef(Port, "SM_Rock_Added")));
    }

    /// <summary>
    /// The Projects page with snapshots, a confirmation and a checked install, at 1280 px in every language: every text stays
    /// inside its card, nothing is cut, no horizontal scrolling. English screenshot.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheCopiesSnapshotsAndInstallCardsFitInEveryLanguage()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = Directory.CreateDirectory(ctx.Combine("SCUM", "Content", "Paks", "~mods")).Parent!.FullName;
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = game });
        ctx.Services.UiState.Update(u => u with { SetupCompleted = true });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            await ctx.Services.Projects.CreateAsync(ctx.Services.ProjectsFolder, "Layout versions");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.HasProject));
            ctx.Services.Projects.Apply(new DeleteActorOp(House));
            var page = Assert.IsType<ProjectsPageViewModel>(vm.NavigateTo("projects"));
            page.SnapshotName = "before the long bridge over the river near the outpost";
            await page.SaveSnapshotCommand.ExecuteAsync(null);
            await page.SaveSnapshotCommand.ExecuteAsync(null);
            await page.CheckInstallCommand.ExecuteAsync(null);
            page.Snapshots[0].RestoreCommand.Execute(null);
            HeadlessUi.Pump();
            var view = window.GetVisualDescendants().OfType<ScumStudio.App.Views.Pages.ProjectsPageView>().Single();
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Border>(view, "VersionsCard")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.Border>(view, "InstallCard")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<Avalonia.Controls.ItemsControl>(view, "InstallRowsList")!.IsEffectivelyVisible);
            foreach (var language in Loc.Languages)
            {
                Loc.Instance.Language = language.Code;
                page.OnLanguageChanged();
                page.Snapshots[0].RestoreCommand.Execute(null);
                await page.CheckInstallCommand.ExecuteAsync(null);
                HeadlessUi.Pump();
                if (language.Code == "en")
                {
                    HeadlessUi.FindNamed<Avalonia.Controls.Border>(view, "VersionsCard")!.BringIntoView();
                    HeadlessUi.Pump();
                    HeadlessUi.SaveScreenshot(window, "page-projects-versions");
                    HeadlessUi.FindNamed<Avalonia.Controls.Border>(view, "InstallCard")!.BringIntoView();
                    HeadlessUi.Pump();
                    HeadlessUi.SaveScreenshot(window, "page-projects-install");
                }

                var scroller = view.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First();
                Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"{language.Code}: content {scroller.Extent.Width:0} > {scroller.Viewport.Width:0}");
                foreach (var cardName in new[] { "VersionsCard", "InstallCard" })
                {
                    var card = HeadlessUi.FindNamed<Avalonia.Controls.Border>(view, cardName)!;
                    foreach (var text in card.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                    {
                        var right = text.TranslatePoint(new Avalonia.Point(text.Bounds.Width, 0), card)!.Value.X;
                        Assert.True(right <= card.Bounds.Width - card.Padding.Right + 0.5, $"{language.Code}: '{text.Text}' ends at {right:0}, its card at {card.Bounds.Width - card.Padding.Right:0}");
                        if (text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && text.TextTrimming == Avalonia.Media.TextTrimming.None)
                        {
                            var natural = new Avalonia.Controls.TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight };
                            natural.Measure(Avalonia.Size.Infinity);
                            Assert.True(text.Bounds.Width + 1 >= natural.DesiredSize.Width, $"{language.Code}: '{text.Text}' is cut ({text.Bounds.Width:0} < {natural.DesiredSize.Width:0})");
                        }
                    }
                }
            }
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The History panel's Reset a sector to stock button opens its panel, which plans the picked sector (synthetic level of A_0).</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheHistoryPanelsResetButtonOpensThePanelForASector()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            var button = HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "ResetSectorButton")!;
            Assert.False(button.IsEffectivelyVisible); // no project: nothing to reset
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Reset view");
            await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
            HeadlessUi.Pump();
            Assert.True(button.IsEffectivelyVisible);

            button.Flyout!.ShowAt(button);
            HeadlessUi.Pump();
            map.ResetCell = new MapCell('A', 0);
            HeadlessUi.Pump();
            Assert.Equal(["1 deleted object(s) come back"], map.ResetLines);
            Assert.True(map.ResetSectorCommand.CanExecute(null));
            HeadlessUi.SaveScreenshot(window, "map-reset-sector");
            map.ResetSectorCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.False(ctx.Services.Projects.Current!.State.IsDeleted(new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1")));
            Assert.Equal("This project changed nothing in A_0.", map.ResetNote);
            button.Flyout.Hide();
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Fact]
    public void EveryFindingHasItsWords()
    {
        var input = new InstallCheckInput { ModNames = ["Words"], GamePaks = @"C:\Game\Paks", ServerPaks = @"D:\Server\Paks" };
        foreach (var issue in Enum.GetValues<InstallIssue>())
        {
            foreach (var side in Enum.GetValues<InstallSide>())
            {
                foreach (var folder in new[] { null, @"C:\Exports\Words\Client" })
                {
                    var text = InstallCheckService.Describe(new InstallFinding(side, issue, InstallSeverity.Problem, ["a.pak", "b.pak"], folder), input);
                    Assert.False(string.IsNullOrWhiteSpace(text));
                    Assert.DoesNotContain("{", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("Install.", text, StringComparison.Ordinal); // a key shown instead of its text
                }
            }
        }

        Assert.All(Enum.GetValues<InstallSeverity>(), s => Assert.DoesNotContain("Install.", InstallCheckService.Badge(s), StringComparison.Ordinal));
        Assert.Equal(@"Server: Mod paks directly in D:\Server\Paks: a.pak. The game refuses mod paks there: move them into the ~mods folder inside it.",
            InstallCheckService.Describe(new InstallFinding(InstallSide.Server, InstallIssue.LooseInPaks, InstallSeverity.Problem, ["a.pak"], @"D:\Server\Paks"), input));
        Assert.Equal(Loc.T("Install.Note.FileOpenLog"), InstallCheckService.Describe(new InstallFinding(InstallSide.Server, InstallIssue.NoteFileOpenLog, InstallSeverity.Info, []), input));
        Assert.Contains("hides nothing of it", InstallCheckService.Describe(new InstallFinding(InstallSide.Game, InstallIssue.RegistryAbove, InstallSeverity.Info, ["a.pak"], @"C:\Game\Paks\~mods"), input), StringComparison.Ordinal);
        Assert.Equal(@"Game: This export's paks are not in C:\Game\Paks\~mods yet (or not the same files there): a.pak. Copy every file of C:\Exports\Words\Client there.",
            InstallCheckService.Describe(new InstallFinding(InstallSide.Game, InstallIssue.NotCopied, InstallSeverity.Problem, ["a.pak"], @"C:\Exports\Words\Client"), input));
        Assert.Equal(@"Game: Paks of this mod in a subfolder of C:\Game\Paks\~mods: Words\Client\a.pak. The game loads paks from subfolders too, so every copy loads, and an old copy can hide the new one. Keep one copy of each pak, directly in C:\Game\Paks\~mods, and delete the others.",
            InstallCheckService.Describe(new InstallFinding(InstallSide.Game, InstallIssue.InSubfolder, InstallSeverity.Warning, [@"Words\Client\a.pak"], @"C:\Game\Paks\~mods"), input));
    }

    private static async Task<string> WritePak(string path, bool registry, int size = 10)
    {
        var staging = Path.Combine(Path.GetTempPath(), "scumstudio-versions-" + Guid.NewGuid().ToString("N")[..10]);
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
            return path;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
