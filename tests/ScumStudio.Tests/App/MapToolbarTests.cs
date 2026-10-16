using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.App.Views.Pages;
using ScumStudio.Core.Abstractions;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Map header: no Drone mode (owner: "flying is simply how the 3D view works"), every item switchable from the gear menu
/// and from Settings › Map toolbar (owner: "show only what I want"), kept in ui-state.json, and the quality names translated.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class MapToolbarTests
{
    // What the removed button and HUD said in the ten languages (en/fr/tr/vi, de, es/sh, ru, ar, zh).
    private static readonly string[] DroneWords = ["Drone", "Drohne", "Dron", "Дрон", "ДРОН", "درون", "无人机"];

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheHeaderHasNoDroneButtonAndMouseLookStillWorks()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, _) = await OpenMapAsync(ctx);
        try
        {
            Assert.Null(HeadlessUi.FindNamed<Button>(window, "DroneButton"));
            var page = window.GetVisualDescendants().OfType<MapPageView>().Single();
            foreach (var language in Loc.Languages)
            {
                Assert.DoesNotContain(Loc.Table(language.Code).Keys, k => k.StartsWith("Map.Drone", StringComparison.Ordinal));
                Loc.Instance.Language = language.Code;
                HeadlessUi.Pump();
                var texts = HeadlessUi.Find<TextBlock>(page).Select(t => t.Text ?? string.Empty).ToList();
                Assert.DoesNotContain(texts, t => DroneWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
            }

            // Tab (here: the viewport's toggle) keeps looking around without holding the right button; the HUD says so.
            Loc.Instance.Language = "ar";
            var viewport = HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!;
            viewport.ToggleDrone();
            HeadlessUi.Pump();
            var hud = HeadlessUi.Find<TextBlock>(page).Single(t => t.Text == Loc.T("Map.MouseLookHud"));
            Assert.True(hud.IsEffectivelyVisible);
            var keys = HeadlessUi.Find<TextBlock>(page).Single(t => t.Text == Loc.T("Map.MouseLookHudKeys"));
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, keys.FlowDirection); // whole strings: the Arabic reads right to left
            viewport.ToggleDrone();
            HeadlessUi.Pump();
            Assert.False(hud.IsEffectivelyVisible);
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task QualityNamesAreTranslated()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, _) = await OpenMapAsync(ctx);
        try
        {
            foreach (var code in new[] { "ru", "ar" })
            {
                Loc.Instance.Language = code;
                foreach (var (page, boxName) in new[] { ("map", "QualityBox"), ("settings", "QualityPicker") })
                {
                    vm.NavigateTo(page);
                    HeadlessUi.Pump();
                    var box = HeadlessUi.FindNamed<ComboBox>(window, boxName)!;
                    var selected = (ScumStudio.Core.Settings.RenderQuality)box.SelectedItem!;
                    var shown = box.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                    Assert.Contains(Loc.T("Map.Quality." + selected), shown);
                    Assert.DoesNotContain(selected.ToString(), shown);

                    box.IsDropDownOpen = true;
                    HeadlessUi.Pump();
                    foreach (var quality in Enum.GetValues<ScumStudio.Core.Settings.RenderQuality>())
                    {
                        var name = Loc.T("Map.Quality." + quality);
                        Assert.NotEqual(quality.ToString(), name);
                        var item = box.ContainerFromIndex((int)quality)!;
                        Assert.Contains(name, item.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                    }

                    box.IsDropDownOpen = false;
                    HeadlessUi.Pump();
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

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task HidingAnItemInSettingsHidesItInTheHeaderAndIsKept()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, map) = await OpenMapAsync(ctx);
        try
        {
            var snap = HeadlessUi.FindNamed<ToggleButton>(window, "SnapButton")!;
            Assert.True(snap.IsVisible);
            var settings = (SettingsPageViewModel)vm.NavigateTo("settings")!;
            HeadlessUi.Pump();
            var settingsSwitch = HeadlessUi.Find<CheckBox>(HeadlessUi.FindNamed<ItemsControl>(window, "SettingsToolbarSwitches")!)
                .Single(c => c.DataContext == settings.Toolbar.Snap);
            settingsSwitch.IsChecked = false; // the user unticks Snap in Settings › Map toolbar
            HeadlessUi.Pump();
            Assert.False(settings.Toolbar.Snap.IsShown);

            vm.NavigateTo("map");
            HeadlessUi.Pump();
            Assert.False(map.Toolbar.Snap.IsShown);
            Assert.False(snap.IsVisible);
            Assert.False(GearSwitch(window, "Snap").IsChecked);
            Assert.Contains("Snap", new UiStateStore(ctx.Services.DataDirectory, NullLogger.Instance).Current.HiddenMapToolbarItems);

            // Back on from the gear menu: the header and the Settings page follow.
            GearSwitch(window, "Snap").IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(snap.IsVisible);
            Assert.True(settings.Toolbar.Snap.IsShown);
            Assert.DoesNotContain("Snap", new UiStateStore(ctx.Services.DataDirectory, NullLogger.Instance).Current.HiddenMapToolbarItems);

            // The switches follow a language switch (names and what they do).
            Loc.Instance.Language = "de";
            HeadlessUi.Pump();
            Assert.Equal(Loc.T("Map.Snap"), GearSwitch(window, "Snap").Content);
            Assert.Equal(Loc.F("Map.Toolbar.Item.Tip", Loc.T("Map.Snap")), ToolTip.GetTip(GearSwitch(window, "Snap")));
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
            window.Close();
            vm.Dispose();
        }
    }

    [Fact]
    public void OldUiStateFilesGetTheDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("scumstudio-uistate").FullName;
        try
        {
            // A ui-state.json written before the switches existed: Pop out starts hidden, everything else shown.
            File.WriteAllText(Path.Combine(directory, UiStateStore.FileName), """{ "accent": "#4FB3A2", "pickParts": true, "timeOfDay": 9 }""");
            var store = new UiStateStore(directory, NullLogger.Instance);
            Assert.Equal(["PopOut"], store.Current.HiddenMapToolbarItems);
            using (var toolbar = new MapToolbar(store))
            {
                Assert.False(toolbar.PopOut.IsShown);
                Assert.All(toolbar.Items.Where(i => i.Key != "PopOut"), i => Assert.True(i.IsShown, i.Key));
            }

            // A key this build does not know (a newer build's item) is kept hidden for that build; Show all keeps it.
            store.Update(u => u with { HiddenMapToolbarItems = ["Snap", "SomeFutureItem"] });
            using (var toolbar = new MapToolbar(store))
            {
                Assert.False(toolbar.Snap.IsShown);
                toolbar.ShowAll();
                Assert.True(toolbar.Snap.IsShown);
                Assert.Equal(["SomeFutureItem"], new UiStateStore(directory, NullLogger.Instance).Current.HiddenMapToolbarItems);
                toolbar.ResetDefaults();
                Assert.False(toolbar.PopOut.IsShown);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task EveryToolbarItemHasAHeaderControlASwitchAndATooltip()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Toolbar");
        var (window, vm, map) = await OpenMapAsync(ctx, 1920, 1080);
        try
        {
            // Every conditional item applies: a selection, a copy, a shape, the island streaming.
            map.SelectedActor = map.AllActors.First(a => a.Name == "BP_Lamp_C_1");
            map.CopySelectedCommand.Execute(null);
            map.HasShape = true;
            map.IsWorldMode = true;
            map.WorldStatus = Loc.F("Map.World.Around", 41);
            map.Toolbar.ShowAll();
            HeadlessUi.Pump();
            Assert.Equal(HeaderControls.Keys.Order(), map.Toolbar.Items.Select(i => i.Key).Order());
            var english = Loc.Table("en");
            foreach (var (key, titleKey) in MapToolbar.Layout)
            {
                Assert.True(english.ContainsKey(titleKey), titleKey);
                var item = map.Toolbar[key];
                Assert.Contains(item.Title, item.Tip);
                var control = HeadlessUi.FindNamed<Control>(window, HeaderControls[key]);
                Assert.True(control is { IsEffectivelyVisible: true }, $"{key}: {HeaderControls[key]} shows");
                var tipless = new[] { control! }.Concat(control!.GetLogicalDescendants().OfType<Control>()) // not template parts
                    .Where(c => c is Button or ComboBox or TextBox or Slider or CheckBox)
                    .Where(c => c.IsEffectivelyVisible && ToolTip.GetTip(c) is not string { Length: > 0 })
                    .Select(c => c.Name ?? c.GetType().Name).ToList();
                Assert.True(tipless.Count == 0, $"{key}: no tooltip on {string.Join(", ", tipless)}");

                item.IsShown = false;
                HeadlessUi.Pump();
                Assert.False(control.IsVisible, $"{key} switched off still shows");
                Assert.False(GearSwitch(window, key).IsChecked, key);
                item.IsShown = true;
                HeadlessUi.Pump();
                Assert.True(control.IsEffectivelyVisible, $"{key} switched on again does not show");
            }

            // A separator shows only between two groups that both show something.
            var separators = new[] { "ViewSeparator", "AddSeparator" }.Select(n => HeadlessUi.FindNamed<Border>(window, n)!).ToList();
            Assert.All(separators, s => Assert.True(s.IsVisible));
            foreach (var item in map.Toolbar.Items)
            {
                item.IsShown = item.Key is "Quality" or "AddObject";
            }

            HeadlessUi.Pump();
            Assert.All(separators, s => Assert.False(s.IsVisible)); // the middle group (Spawns … Prefabs) shows nothing
            map.Toolbar.Snap.IsShown = true;
            HeadlessUi.Pump();
            Assert.All(separators, s => Assert.True(s.IsVisible));
            map.Toolbar.ShowAll();
            HeadlessUi.Pump();

            // The settings menu: one switch per item, each says what it does; the menu itself has its buttons' tips.
            var gear = HeadlessUi.FindNamed<Button>(window, "ToolbarButton")!;
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(gear) as string));
            var switches = GearSwitches(window);
            Assert.Equal(map.Toolbar.Items.Count, switches.Count);
            Assert.All(switches, s => Assert.Equal(((MapToolbarItem)s.DataContext!).Tip, ToolTip.GetTip(s)));
            gear.Flyout!.Hide();
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheLoadModeSlotsShowTheTemplatesOnceTheyAreMerged()
    {
        // The load-mode pieces live in their own dictionary: empty slots without it, filled as soon as the keys exist.
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, map) = await OpenMapAsync(ctx);
        var resources = Avalonia.Application.Current!.Resources;
        try
        {
            var item = HeadlessUi.FindNamed<ContentControl>(window, "LoadModeItem")!;
            var choice = HeadlessUi.FindNamed<ContentControl>(window, "LoadChoiceSlot")!;
            if (Avalonia.Application.Current.TryFindResource("MapLoadModeItemTemplate", out _))
            {
                // load-mode's dictionary is merged in App.axaml: the slots start with its templates.
                Assert.NotNull(item.ContentTemplate);
                Assert.NotNull(choice.ContentTemplate);
            }
            else
            {
                Assert.Null(item.ContentTemplate);
                Assert.Null(choice.ContentTemplate);
                Assert.Empty(HeadlessUi.Find<TextBlock>(item));
            }

            resources["MapLoadModeItemTemplate"] = new FuncDataTemplate<MapPageViewModel>((_, _) => new TextBlock { Text = "load mode" });
            resources["MapLoadChoiceTemplate"] = new FuncDataTemplate<MapPageViewModel>((_, _) => new TextBlock { Text = "load choice" });
            HeadlessUi.Pump();
            Assert.Equal("load mode", Assert.Single(HeadlessUi.Find<TextBlock>(item)).Text);
            Assert.NotNull(choice.ContentTemplate);
            map.Toolbar.LoadMode.IsShown = false;
            HeadlessUi.Pump();
            Assert.False(item.IsVisible);
        }
        finally
        {
            resources.Remove("MapLoadModeItemTemplate");
            resources.Remove("MapLoadChoiceTemplate");
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The header control of each toolbar switch (a group, a chip or the project controls for some).</summary>
    private static readonly Dictionary<string, string> HeaderControls = new()
    {
        ["Maximize"] = "MaximizeButton", ["PopOut"] = "PopOutButton", ["Quality"] = "QualityBox", ["Time"] = "TimeOfDayGroup",
        ["Spawns"] = "SpawnsButton", ["Legend"] = "SpawnLegendButton", ["Parts"] = "PartsButton", ["Brush"] = "BrushButton",
        ["Axes"] = "AxesBox", ["Ground"] = "DropButton", ["Snap"] = "SnapButton", ["FrameAll"] = "FrameAllButton",
        ["FrameSelection"] = "FrameSelectionButton", ["Shape"] = "ShapeButton", ["Landscape"] = "LandscapeButton",
        ["Replace"] = "ReplaceButton", ["Extend"] = "ExtendButton", ["LastObject"] = "LastObjectButton", ["GoTo"] = "GoToButton", ["Prefabs"] = "PrefabsButton",
        ["Paste"] = "PasteButton", ["AddObject"] = "AddObjectButton", ["LoadMode"] = "LoadModeItem", ["Project"] = "ProjectControls",
        ["Export"] = "ExportModButton", ["LevelChip"] = "LevelChip", ["WorldChip"] = "WorldChip", ["Stats"] = "FrameStats",
        ["View"] = "ViewPresetButton", ["Wireframe"] = "WireframeButton", ["Isolate"] = "IsolateButton",
    };

    /// <summary>The gear menu's switches (the menu opens if it is closed).</summary>
    private static List<CheckBox> GearSwitches(Window window)
    {
        var gear = HeadlessUi.FindNamed<Button>(window, "ToolbarButton")!;
        var panel = (Control)((Flyout)gear.Flyout!).Content!;
        if (!gear.Flyout.IsOpen)
        {
            gear.Flyout.ShowAt(gear);
            HeadlessUi.Pump();
        }

        return HeadlessUi.Find<CheckBox>(panel).ToList();
    }

    private static CheckBox GearSwitch(Window window, string key) =>
        GearSwitches(window).Single(c => ((MapToolbarItem)c.DataContext!).Key == key);

    private static async Task<(MainWindow Window, MainWindowViewModel ViewModel, MapPageViewModel Map)> OpenMapAsync(AppTestContext ctx, double width = 1600, double height = 900)
    {
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, width, height);
        var map = (MapPageViewModel)vm.NavigateTo("map")!;
        await map.LoadCompletion;
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        HeadlessUi.Pump();
        return (window, vm, map);
    }
}
