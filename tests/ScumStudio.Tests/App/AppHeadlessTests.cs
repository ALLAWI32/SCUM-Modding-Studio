using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ScumStudio.App;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.App.Views.Pages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Headless UI tests: the real theme, views and view locator on Avalonia's headless platform (Skia rendering).
/// Set <c>SCUMSTUDIO_SCREENSHOTS=&lt;folder&gt;</c> to also save a PNG of every page.
/// </summary>
public sealed class AppHeadlessTests
{
    private static readonly Dictionary<string, Type> ExpectedViews = new()
    {
        ["map"] = typeof(MapPageView),
        ["vehicles"] = typeof(ModulePageView),
        ["weapons"] = typeof(ModulePageView),
        ["spawns"] = typeof(SpawnsPageView),
        ["economy"] = typeof(EconomyPageView),
        ["craftables"] = typeof(CraftablesPageView),
        ["assets"] = typeof(AssetsPageView),
        ["projects"] = typeof(ProjectsPageView),
        ["settings"] = typeof(SettingsPageView),
    };

    [AvaloniaFact]
    public void AppStartsWithTheDarkThemeAndTheNavigationRail()
    {
        Assert.NotNull(Avalonia.Application.Current);
        Assert.IsType<ScumStudio.App.App>(Avalonia.Application.Current);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);

        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var nav = HeadlessUi.FindNamed<ListBox>(window, "NavList");
            Assert.NotNull(nav);
            Assert.Equal(9, nav!.ItemCount);
            var texts = HeadlessUi.Find<TextBlock>(window).Select(t => t.Text).ToHashSet();
            foreach (var title in new[] { "Map", "Vehicles", "Weapons", "Spawns", "Economy", "Craftables", "Assets", "Projects", "Settings" })
            {
                Assert.Contains(title, texts);
            }

            // Top bar: project name, global search and the three status pills.
            Assert.Equal("No project", HeadlessUi.FindNamed<TextBlock>(window, "ProjectName")?.Text);
            Assert.NotNull(HeadlessUi.FindNamed<TextBox>(window, "GlobalSearch"));
            Assert.Contains("Game paks", texts);
            Assert.Contains("Server", texts);
            Assert.Contains("Key", texts);
            Assert.Contains("not set", texts);

            // Status bar + collapsible log panel.
            Assert.Equal("Ready", HeadlessUi.FindNamed<TextBlock>(window, "StatusText")?.Text);
            var logPanel = HeadlessUi.FindNamed<Border>(window, "LogPanel")!;
            Assert.False(logPanel.IsVisible);
            HeadlessUi.FindNamed<Avalonia.Controls.Primitives.ToggleButton>(window, "LogToggle")!.IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(vm.IsLogOpen);
            Assert.True(logPanel.IsVisible);
            window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.Control);
            HeadlessUi.Pump();
            Assert.False(vm.IsLogOpen);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void NavigatingToEachPageCreatesItsView()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var host = HeadlessUi.FindNamed<PageDeck>(window, "PageHost")!;
            foreach (var key in MainWindowViewModel.PageKeys)
            {
                vm.NavigateTo(key);
                HeadlessUi.Pump();
                var view = host.CurrentView;
                Assert.NotNull(view);
                Assert.IsType(ExpectedViews[key], view);
                Assert.Same(vm.CurrentPage, view!.DataContext);
                Assert.Equal(ExpectedViews[key], ViewLocator.ViewTypeFor(vm.CurrentPage!.GetType()));
                Assert.Contains(HeadlessUi.Find<TextBlock>(view), t => t.Text == vm.CurrentPage!.Title);
                HeadlessUi.SaveScreenshot(window, "page-" + key);
            }

            // Clicking a rail entry navigates too.
            var nav = HeadlessUi.FindNamed<ListBox>(window, "NavList")!;
            nav.SelectedIndex = MainWindowViewModel.PageKeys.ToList().IndexOf("assets");
            HeadlessUi.Pump();
            Assert.IsType<AssetsPageView>(host.CurrentView);
            Assert.Contains(HeadlessUi.Find<TextBlock>(window), t => t.Text == "No assets to show yet");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void GoingBackToAPageKeepsItsView()
    {
        // Owner report: going to Assets and back put the map camera somewhere else, under the ground (a new view).
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var host = HeadlessUi.FindNamed<PageDeck>(window, "PageHost")!;
            vm.NavigateTo("map");
            HeadlessUi.Pump();
            var map = host.CurrentView;
            vm.NavigateTo("assets");
            HeadlessUi.Pump();
            Assert.True(map!.IsVisible is false && map.Parent is not null); // hidden, never detached (its OpenGL view lives on)
            vm.NavigateTo("map");
            HeadlessUi.Pump();
            Assert.IsType<MapPageView>(map);
            Assert.Same(map, host.CurrentView);
            Assert.True(map.IsVisible);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void SetupDialogMasksTheKeyAndRejectsAMalformedOne()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            vm.OpenSetup();
            HeadlessUi.Pump();
            var overlay = HeadlessUi.FindNamed<Panel>(window, "SetupOverlay")!;
            Assert.True(overlay.IsVisible);
            var setupView = Assert.Single(HeadlessUi.Find<SetupView>(window));
            var keyBox = HeadlessUi.FindNamed<TextBox>(setupView, "KeyBox")!;
            Assert.NotEqual('\0', keyBox.PasswordChar);
            Assert.False(keyBox.RevealPassword);

            keyBox.Focus();
            window.KeyTextInput("123-not-hex");
            HeadlessUi.Pump();
            Assert.Equal("123-not-hex", vm.Setup!.KeyInput);
            HeadlessUi.SaveScreenshot(window, "setup");

            HeadlessUi.FindNamed<Button>(setupView, "SaveButton")!.Command!.Execute(null);
            HeadlessUi.Pump();
            Assert.True(vm.IsSetupOpen);
            Assert.True(HeadlessUi.FindNamed<TextBlock>(setupView, "KeyErrorText")!.IsEffectivelyVisible);
            Assert.False(ctx.Services.Keys.HasKey);
            HeadlessUi.SaveScreenshot(window, "setup-invalid-key");

            vm.Setup.CancelCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.False(overlay.IsVisible);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ErrorsShowAsToasts()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            ctx.Services.Notifications.Error("Could not open the paks", "The folder does not exist.");
            ctx.Services.Notifications.Success("Texture exported", "T_WW_Body_D.png");
            HeadlessUi.Pump();
            var host = HeadlessUi.FindNamed<ItemsControl>(window, "ToastHost")!;
            Assert.Equal(2, host.ItemCount);
            Assert.Contains(HeadlessUi.Find<TextBlock>(host), t => t.Text == "Could not open the paks");
            HeadlessUi.SaveScreenshot(window, "toasts");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MapPageShowsTheWorldTreeAndTheJournalHistory()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            map.LoadWorld(WorldIndex.Build(WorldIndexTests.SampleFiles));
            map.Nodes.First(n => n.Title == "Cell A_0").IsExpanded = true;

            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Outpost cleanup");
            HeadlessUi.Pump();
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Crate_1")));
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Outpost, "Rock_12")));
            ctx.Services.Projects.Apply(new DeleteActorOp(new ActorRef(EditOpTests.Port, "Barrel_3")));
            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();

            var tree = HeadlessUi.FindNamed<TreeView>(window, "WorldTree")!;
            Assert.True(tree.IsEffectivelyVisible);
            Assert.True(tree.ItemCount > 3);
            var history = HeadlessUi.FindNamed<ListBox>(window, "HistoryList")!;
            Assert.Equal(3, history.ItemCount);
            Assert.True(HeadlessUi.FindNamed<Button>(window, "UndoButton")!.IsEffectivelyEnabled);
            Assert.True(HeadlessUi.FindNamed<Button>(window, "RedoButton")!.IsEffectivelyEnabled);
            Assert.Equal(project.Manifest.Name, HeadlessUi.FindNamed<TextBlock>(window, "ProjectName")!.Text);

            // Undo/redo shortcuts reach the journal.
            window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
            HeadlessUi.Pump();
            Assert.False(ctx.Services.Projects.CanRedo);
            Assert.False(HeadlessUi.FindNamed<Button>(window, "RedoButton")!.IsEffectivelyEnabled);

            map.SelectedNode = map.Nodes.First(n => n.Title == "Cell A_0").Children[0].Children[0];
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "page-map-world");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
