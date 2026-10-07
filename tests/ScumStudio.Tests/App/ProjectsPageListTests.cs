using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: after an update he pressed "Open project" and could not find his project among the files. The Projects page
/// lists every project with one-click Open, the top bar's project label leads there, and the last project reopens.
/// </summary>
[Collection(LanguageCollection.Name)] // reads English texts and switches the UI language
public sealed class ProjectsPageListTests
{
    [AvaloniaFact]
    public async Task YourProjectsListsEveryProjectOpensOneAndReopensItAfterARestart()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var folder = ctx.Combine("projects");
        var outpostPath = Path.Combine(folder, "Outpost_list_test.ssproj");
        var portPath = Path.Combine(folder, "Port_list_test.ssproj");
        using (var outpost = Project.Create(outpostPath, "Outpost_list_test"))
        {
            outpost.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Crate_1")));
            outpost.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Rock_12")));
            outpost.Undo(); // an undone edit still counts, as in the open project's "Edits" row
        }

        using (var port = Project.Create(portPath, "Port_list_test"))
        {
            port.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Port, "Barrel_3")));
        }

        Directory.CreateDirectory(Path.Combine(folder, "Empty_list_test.ssproj")); // no project.json: not listed

        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            // The top bar's project label opens the Projects page.
            vm.NavigateTo("settings");
            HeadlessUi.FindNamed<Button>(window, "ProjectButton")!.Command!.Execute(null);
            var page = Assert.IsType<ProjectsPageViewModel>(vm.CurrentPage);

            page.NewProjectFolder = folder;
            page.OnNavigatedTo();
            HeadlessUi.Pump();
            var outpostCard = Assert.Single(page.Projects, c => c.Name == "Outpost_list_test");
            var portCard = Assert.Single(page.Projects, c => c.Name == "Port_list_test");
            Assert.DoesNotContain(page.Projects, c => c.Name == "Empty_list_test");
            Assert.Equal(2, outpostCard.Edits);
            Assert.Equal(1, portCard.Edits);
            Assert.False(outpostCard.IsCurrent || portCard.IsCurrent);

            var cards = HeadlessUi.FindNamed<ItemsControl>(window, "ProjectCards")!;
            Assert.True(cards.IsEffectivelyVisible);
            var texts = HeadlessUi.Find<TextBlock>(cards).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Outpost_list_test", texts);
            Assert.Contains("Port_list_test", texts);
            Assert.Contains(texts, t => t.StartsWith("Edits: 2 ", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.StartsWith("Edits: 1 ", StringComparison.Ordinal));

            // One click opens it; the card is marked and the top bar shows its name.
            await portCard.OpenCommand.ExecuteAsync(portCard.Path);
            HeadlessUi.Pump();
            Assert.Equal(portPath, ctx.Services.Projects.DirectoryPath);
            Assert.True(Assert.Single(page.Projects, c => c.Name == "Port_list_test").IsCurrent);
            Assert.False(Assert.Single(page.Projects, c => c.Name == "Outpost_list_test").IsCurrent);
            Assert.Equal("Port_list_test", HeadlessUi.FindNamed<TextBlock>(window, "ProjectName")!.Text);
            HeadlessUi.SaveScreenshot(window, "page-projects-list");

            // Restart (an update): the last project opens again.
            ctx.Services.Projects.Close();
            using (var again = new MainWindowViewModel(ctx.Services))
            {
                await again.InitializeAsync();
                HeadlessUi.Pump();
                Assert.Equal(portPath, ctx.Services.Projects.DirectoryPath);
            }

            // The Settings switch turns that off.
            vm.NavigateTo("settings");
            HeadlessUi.Pump();
            var reopen = HeadlessUi.FindNamed<CheckBox>(window, "ReopenLastProject")!;
            Assert.True(reopen.IsChecked);
            reopen.IsChecked = false;
            Assert.False(ctx.Services.UiState.Current.ReopenLastProject);
            ctx.Services.Projects.Close();
            using (var third = new MainWindowViewModel(ctx.Services))
            {
                await third.InitializeAsync();
                HeadlessUi.Pump();
                Assert.False(ctx.Services.Projects.HasProject);
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Owner (0.2.7): the page looked scattered (a dead "missing" card among his projects, cards of other widths than
    /// the boxes below) and said "No project" although his last project should have reopened: its journal held a batch
    /// that 0.2.7 had half applied, and the replay refused it. Now the project reopens, the gone entry is a "Not found"
    /// line with Remove, and the page is one aligned flow that fits at 1280 and 1600 px in every language.
    /// </summary>
    [AvaloniaFact]
    public async Task TheLastProjectReopensAndThePageIsOneAlignedFlowInEveryLanguage()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var mapPath = Path.Combine(ctx.Services.ProjectsFolder, "MyMapMod.ssproj");
        var portPath = Path.Combine(ctx.Services.ProjectsFolder, "Port_layout_test.ssproj");
        var gonePath = ctx.Combine("Desktop", "MyMapMod.ssproj");
        using (var map = Project.Create(mapPath, "MyMapMod"))
        {
            map.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Crate_1")));
        }

        // As in the owner's journal: a batch that deleted an actor, then one of its parts.
        var boathouse = new ActorRef(EditOpTests.Outpost, "BP_River_Boathouse_5");
        ProjectTests.AppendUnchecked(mapPath, 2, new BatchOp("Deleted 2 objects",
            [new DeleteActorOp(boathouse), new DeleteInstanceOp(new InstanceRef(EditOpTests.Outpost, boathouse.Actor, "HierarchicalInstancedStaticMesh5", 1))]));
        using (Project.Create(portPath, "Port_layout_test"))
        {
        }

        ctx.Services.UpdateSettings(s => s.WithRecentProject(gonePath).WithRecentProject(portPath).WithRecentProject(mapPath));
        ctx.Services.UiState.Update(u => u with { SetupCompleted = true });

        // Start: the last project is open, and a toast says so.
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            await vm.InitializeAsync();
            HeadlessUi.Pump();
            Assert.Equal(mapPath, ctx.Services.Projects.DirectoryPath);
            Assert.True(ctx.Services.Projects.Current!.State.IsDeleted(boathouse));
            Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Reopened MyMapMod");

            // Two cards, the open one marked; the gone one is a "Not found" line, not a card.
            var page = Assert.IsType<ProjectsPageViewModel>(vm.NavigateTo("projects"));
            HeadlessUi.Pump();
            var view = window.GetVisualDescendants().OfType<ProjectsPageView>().Single();
            Assert.Equal(["MyMapMod", "Port_layout_test"], page.Projects.Select(c => c.Name).Order());
            Assert.True(Assert.Single(page.Projects, c => c.Name == "MyMapMod").IsCurrent);
            Assert.All(page.Projects, c => Assert.False(c.ShowFolder));
            var cards = HeadlessUi.FindNamed<ItemsControl>(view, "ProjectCards")!;
            Assert.Single(HeadlessUi.Find<Border>(cards), b => b.Classes.Contains("current"));
            Assert.DoesNotContain(HeadlessUi.Find<TextBlock>(cards), t => t.Text?.Contains(gonePath, StringComparison.Ordinal) == true);
            var missing = HeadlessUi.FindNamed<ItemsControl>(view, "MissingProjects")!;
            Assert.True(missing.IsEffectivelyVisible);
            Assert.Contains(HeadlessUi.Find<TextBlock>(missing), t => t.Text == "Not found: " + gonePath);
            Assert.True(HeadlessUi.FindNamed<Border>(view, "ExportCard")!.IsEffectivelyVisible);
            Assert.False(HeadlessUi.FindNamed<TextBlock>(view, "ExportNeedsProject")!.IsEffectivelyVisible);
            CheckLayout(window, view, "page-projects");

            // Remove takes it off the list (nothing on disk changes).
            var remove = HeadlessUi.Find<Button>(missing).Single();
            remove.Command!.Execute(remove.CommandParameter);
            HeadlessUi.Pump();
            Assert.Empty(page.MissingProjects);
            Assert.False(missing.IsEffectivelyVisible);
            Assert.DoesNotContain(gonePath, ctx.Services.Settings.Load().RecentProjects);
            Assert.Equal(2, page.Projects.Count);

            // No project open: one friendly way back, and the export is a single line.
            page.CloseCommand.Execute(null);
            HeadlessUi.Pump();
            var reopen = HeadlessUi.FindNamed<Button>(view, "OpenRecentButton")!;
            Assert.True(reopen.IsEffectivelyVisible);
            Assert.Contains(HeadlessUi.Find<TextBlock>(reopen), t => t.Text == "Open MyMapMod");
            Assert.False(HeadlessUi.FindNamed<Border>(view, "ExportCard")!.IsEffectivelyVisible);
            Assert.True(HeadlessUi.FindNamed<TextBlock>(view, "ExportNeedsProject")!.IsEffectivelyVisible);
            CheckLayout(window, view, "page-projects-empty");
            reopen.Command!.Execute(reopen.CommandParameter);
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.DirectoryPath == mapPath));
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// At 1280x800 and 1600x900 in every language: no horizontal scrolling, no text cut or running out of its card, and
    /// the project cards as wide as (and lined up with) the Current project / New project boxes. English screenshots.
    /// </summary>
    private static void CheckLayout(Window window, ProjectsPageView view, string screenshot)
    {
        foreach (var language in Loc.Languages)
        {
            Loc.Instance.Language = language.Code;
            foreach (var (width, height) in new[] { (1280, 800), (1600, 900) })
            {
                window.Width = width;
                window.Height = height;
                HeadlessUi.Pump();
                var where = $"{language.Code} at {width}";
                if (language.Code == "en")
                {
                    HeadlessUi.SaveScreenshot(window, $"{screenshot}-{width}");
                }

                var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"{where}: content {scroller.Extent.Width:0} > {scroller.Viewport.Width:0}");
                var content = HeadlessUi.FindNamed<StackPanel>(view, "PageContent")!;
                Assert.True(Math.Abs(content.Bounds.Width - 1100) < 1.5, $"{where}: the page is {content.Bounds.Width:0} wide, not 1100");

                var current = HeadlessUi.FindNamed<Border>(view, "CurrentCard")!;
                var columns = new[] { 0, content.Bounds.Width - current.Bounds.Width };
                Assert.Equal(columns[0], current.TranslatePoint(default, content)!.Value.X, 1.5);
                foreach (var card in HeadlessUi.Find<Border>(HeadlessUi.FindNamed<ItemsControl>(view, "ProjectCards")!).Where(b => b.Classes.Contains("card")))
                {
                    Assert.Equal(current.Bounds.Width, card.Bounds.Width, 1.5);
                    var x = card.TranslatePoint(default, content)!.Value.X;
                    Assert.True(columns.Any(c => Math.Abs(x - c) < 1.5), $"{where}: a project card starts at {x:0}, not at {columns[0]:0} or {columns[1]:0}");
                }

                foreach (var text in content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    var box = text.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
                    var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), (Visual?)box ?? content)!.Value.X;
                    var limit = box is null ? content.Bounds.Width : box.Bounds.Width - box.Padding.Right;
                    Assert.True(right <= limit + 0.5, $"{where}: '{text.Text}' ends at {right:0}, its box at {limit:0}");
                    if (text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None)
                    {
                        var natural = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight, LetterSpacing = text.LetterSpacing };
                        natural.Measure(Size.Infinity);
                        Assert.True(text.Bounds.Width + 1 >= natural.DesiredSize.Width, $"{where}: '{text.Text}' is cut ({text.Bounds.Width:0} < {natural.DesiredSize.Width:0})");
                    }
                }
            }
        }

        Loc.Instance.Language = Loc.DefaultLanguage;
        window.Width = 1280;
        window.Height = 800;
        HeadlessUi.Pump();
    }
}
