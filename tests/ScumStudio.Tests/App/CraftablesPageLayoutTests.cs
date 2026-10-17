using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's layout and words (owner, 2026-10-12: "every option in its right place … nothing covers another
/// control"): kinds and materials named in the UI language (F138), the editor's options side by side without overlap at
/// every window size, and the "Shows in" picker of the game's crafting menu sections. Screenshots with
/// <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(LanguageCollection.Name)] // switches or reads the UI language: never in parallel with another language test
public sealed class CraftablesPageLayoutTests
{
    private const string Chairs = "CraftingCategory.Placeables.Furniture.Chairs";
    private const string Walls = "CraftingCategory.Placeables.BaseBuilding.Walls";
    private const string Misc = "CraftingCategory.Items.ToolsAndMisc.Misc";

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task KindAndMaterialNamesAreTranslated()
    {
        try
        {
            await WithPageAsync(1600, 900, [new Craftable { Name = "Верстак", Source = "/Game/A/SM_Bench", Mesh = "/Game/A/SM_Bench", Kind = CraftKind.Station, Material = CraftMaterial.Metal }], (window, page) =>
            {
                Assert.Equal("Станция", Loc.T("Craftables.Kind.Station"));
                Assert.Equal(Loc.T("Craftables.Kind.Station"), HeadlessUi.FindNamed<TextBlock>(window, "CraftKindChip")!.Text);
                Assert.Equal(Loc.T("Craftables.Material.Metal"), HeadlessUi.FindNamed<TextBlock>(window, "CraftMaterialChip")!.Text);

                // The pickers show the same names, never the code names.
                var kind = HeadlessUi.FindNamed<ComboBox>(window, "CraftKindBox")!;
                var material = HeadlessUi.FindNamed<ComboBox>(window, "CraftMaterialBox")!;
                Assert.Contains(HeadlessUi.Find<TextBlock>(kind), t => t.Text == Loc.T("Craftables.Kind.Station"));
                Assert.Contains(HeadlessUi.Find<TextBlock>(material), t => t.Text == Loc.T("Craftables.Material.Metal"));
                var view = HeadlessUi.Find<ScumStudio.App.Views.Pages.CraftablesPageView>(window).Single();
                Assert.DoesNotContain(HeadlessUi.Find<TextBlock>(view), t => t.Text is "Station" or "Metal" or "Furniture" or "Power");
                Assert.Equal([Loc.T("Craftables.Kind.Furniture"), Loc.T("Craftables.Kind.Station"), Loc.T("Craftables.Kind.Power")], page.SelectedItem!.KindChoices.Select(k => k.Label));
                Assert.Equal("Мебель", page.SelectedItem.KindChoices[0].Label);
                Assert.Equal(Enum.GetValues<CraftMaterial>().Length, page.MaterialChoices.Select(m => m.Label).Distinct().Count());
                HeadlessUi.SaveScreenshot(window, "craftables-ru");
            }, language: "ru");
        }
        finally
        {
            Loc.Instance.Language = Loc.DefaultLanguage;
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task ThePageNeverOverlaps(int width, int height)
    {
        Craftable[] craftables =
        [
            new() { Name = "Leather Sofa", Source = "/Game/A/SM_Sofa_Leather", Mesh = "/Game/A/SM_Sofa_Leather", Material = CraftMaterial.Fabric },
            new() { Name = "Sun Panel", Source = "/Game/A/SM_Solar", Mesh = "/Game/A/SM_Solar", Kind = CraftKind.Power, Material = CraftMaterial.Metal },
        ];
        await WithPageAsync(width, height, craftables, (window, page) =>
        {
            foreach (var row in page.Items)
            {
                page.SelectedItem = row;
                HeadlessUi.Pump();
                var at = $"{row.Name} at {width}x{height}";

                // The list, the editor and the header's buttons each in a place of their own.
                var list = At(window, HeadlessUi.FindNamed<ListBox>(window, "CraftList")!);
                var editor = At(window, HeadlessUi.FindNamed<ScrollViewer>(window, "CraftEditorScroll")!);
                Assert.True(list.Right <= editor.Left, $"{at}: the list ends at {list.Right:0}, the editor starts at {editor.Left:0}");
                Assert.True(editor.Right <= width && editor.Bottom <= height, $"{at}: the editor {editor} leaves the window");
                Disjoint(at, window, "CraftRecipeTab", "CraftGalleryTab", "CraftUndoButton");

                // Owner 2026-10-17: the editor is six numbered steps, top to bottom, each one in full width of the editor; the
                // rarely needed options are folded away, and Export is a step of its own.
                var steps = new[] { "CraftStepWhat", "CraftStepWhere", "CraftStepIngredients", "CraftStepPicture", "CraftStepFiles", "CraftStepExport" }
                    .Select(n => (Name: n, Rect: At(window, HeadlessUi.FindNamed<Border>(window, n) ?? throw new InvalidOperationException($"no {n}")))).ToList();
                for (var i = 1; i < steps.Count; i++)
                {
                    Assert.True(steps[i - 1].Rect.Bottom <= steps[i].Rect.Top + 0.5, $"{at}: {steps[i - 1].Name} {steps[i - 1].Rect} is not above {steps[i].Name} {steps[i].Rect}");
                    Assert.True(steps[i].Rect.Right <= editor.Right + 0.5, $"{at}: {steps[i].Name} ends at {steps[i].Rect.Right:0}, the editor at {editor.Right:0}");
                }

                Assert.False(HeadlessUi.FindNamed<Expander>(window, "CraftMoreOptions")!.IsExpanded);
                Assert.True(HeadlessUi.FindNamed<Button>(window, "CraftPickFromGame")!.IsEffectivelyVisible);
                var export = HeadlessUi.FindNamed<Button>(window, "CraftExportButton")!;
                Assert.True(export.IsEffectivelyEnabled && export.Command is not null, $"{at}: the Export step has no working button");
                Assert.Equal(Loc.T("Export.Module.Button.Tip"), ToolTip.GetTip(export));
                Assert.Equal(row.WhereText, HeadlessUi.FindNamed<TextBlock>(window, "CraftWhereText")!.Text);

                // The step's options: side by side or on the next line, never on each other, never cut on the right.
                var options = HeadlessUi.FindNamed<WrapPanel>(window, "CraftEditorOptions")!;
                var boxes = options.Children.Where(c => c.IsVisible).Select(c => (Control: c, Rect: At(window, c))).ToList();
                Assert.True(boxes.Count >= 2, $"{at}: {boxes.Count} options shown");
                for (var i = 0; i < boxes.Count; i++)
                {
                    Assert.True(boxes[i].Rect.Right <= editor.Right + 0.5, $"{at}: option {i} ends at {boxes[i].Rect.Right:0}, the editor at {editor.Right:0}");
                    for (var j = i + 1; j < boxes.Count; j++)
                    {
                        Assert.False(boxes[i].Rect.Intersects(boxes[j].Rect), $"{at}: options {i} {boxes[i].Rect} and {j} {boxes[j].Rect} overlap");
                    }
                }

                var showsIn = HeadlessUi.FindNamed<ComboBox>(window, "CraftShowsInBox")!;
                Assert.True(showsIn.IsEffectivelyVisible && showsIn.Bounds.Width >= 220, $"{at}: Shows in is {showsIn.Bounds.Width:0} wide");
                Assert.Equal(row.IsBuilding, HeadlessUi.FindNamed<Border>(window, "CraftMadeInBuilding")!.IsVisible);
                Assert.Equal(!row.IsBuilding, HeadlessUi.FindNamed<Border>(window, "CraftMadeInCrafting")!.IsVisible);
                if (row.IsPower)
                {
                    Disjoint(at, window, "CraftRadiusBox", "CraftOutputBox", "CraftFuelBox");
                }

                HeadlessUi.SaveScreenshot(window, $"craftables-{row.Kind}-{width}x{height}".ToLowerInvariant());
            }
        });
    }

    /// <summary>
    /// Owner 2026-10-17: "unclear where the item will show up". Step 2 says it in one sentence: a furniture craftable is in
    /// the base BUILDING menu under its section, a power object (an item placed afterwards) in the CRAFTING menu; a chosen
    /// section is named, and every step's control has a tooltip.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task StepTwoSaysWhichMenuAndSection()
    {
        Craftable[] craftables =
        [
            new() { Name = "Oak Chair", Source = "/Game/A/SM_Chair", Mesh = "/Game/A/SM_Chair" },
            new() { Name = "Sun Panel", Source = "/Game/A/SM_Solar", Mesh = "/Game/A/SM_Solar", Kind = CraftKind.Power, Material = CraftMaterial.Metal },
        ];
        await WithPageAsync(1600, 900, craftables, (window, page) =>
        {
            var chair = page.Items[0];
            page.SelectedItem = chair;
            HeadlessUi.Pump();
            Assert.Equal(CraftUse.Build, chair.Use);
            Assert.Equal(Loc.F("Craftables.Where.InBuild", chair.SectionName), chair.WhereText);
            Assert.Contains("BUILDING", chair.WhereText, StringComparison.Ordinal);
            Assert.Equal(chair.WhereText, HeadlessUi.FindNamed<TextBlock>(window, "CraftWhereText")!.Text);

            // A section picked in the box is the one the sentence names.
            if (chair.Sections.FirstOrDefault(s => s.Tag is not null) is { } picked)
            {
                chair.SelectedSection = picked;
                HeadlessUi.Pump();
                Assert.Contains(picked.Label, chair.WhereText, StringComparison.Ordinal);
                Assert.Equal(chair.WhereText, HeadlessUi.FindNamed<TextBlock>(window, "CraftWhereText")!.Text);
            }

            var panel = page.Items[1];
            page.SelectedItem = panel;
            HeadlessUi.Pump();
            Assert.Equal(CraftUse.Craft, panel.Use);
            Assert.Contains("CRAFTING", panel.WhereText, StringComparison.Ordinal);
            Assert.False(HeadlessUi.FindNamed<ToggleButton>(window, "CraftUseBuild")!.IsEffectivelyEnabled);
            Assert.True(HeadlessUi.FindNamed<ToggleButton>(window, "CraftUseCraft")!.IsChecked);

            // Every control of the steps explains itself.
            foreach (var name in new[] { "CraftUseBuild", "CraftUseCraft", "CraftUseBoth", "CraftShowsInBox", "CraftKindBox", "CraftMaterialBox", "CraftSuggestButton",
                         "CraftAddIngredientButton", "CraftPictureBox", "CraftAssetNameBox", "CraftFolderBox", "CraftExportButton", "CraftMoreOptions", "CraftPickFromGame" })
            {
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(HeadlessUi.FindNamed<Control>(window, name)!) as string), $"{name} has no tooltip");
            }

            HeadlessUi.SaveScreenshot(window, "craftables-steps-power");

            // The bottom of the editor: the file names, the Export step and the folded options.
            HeadlessUi.FindNamed<ScrollViewer>(window, "CraftEditorScroll")!.ScrollToEnd();
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "craftables-steps-bottom");

            // Step 1's button opens the gallery of the game's objects.
            HeadlessUi.FindNamed<Button>(window, "CraftPickFromGame")!.Command!.Execute(null);
            HeadlessUi.Pump();
            Assert.True(page.IsGalleryOpen);
        });
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheShowsInBoxSaysOnlyTheGamesSectionsExist()
    {
        await WithPageAsync(1600, 900, [new Craftable { Name = "Oak Chair", Source = "/Game/A/SM_Chair", Mesh = "/Game/A/SM_Chair" }], (window, page) =>
        {
            var box = HeadlessUi.FindNamed<ComboBox>(window, "CraftShowsInBox")!;
            var tip = Assert.IsType<string>(ToolTip.GetTip(box));
            Assert.Equal(Loc.T("Craftables.ShowsIn.Tip"), tip);
            Assert.Contains("Only the game's own sections exist", tip, StringComparison.Ordinal);
            Assert.Contains("which a mod cannot add", tip, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(HeadlessUi.FindNamed<Border>(window, "CraftMadeInBuilding")!) as string));

            // A catalog without the game's recipes (this synthetic one) offers the default only, named after the template.
            var row = page.SelectedItem!;
            Assert.True(HeadlessUi.PumpUntil(() => row.Sections.Count == 1 && !row.Sections[0].Label.Contains('…', StringComparison.Ordinal)));
            Assert.Equal(Loc.F("Craftables.ShowsIn.Default", "CR_Chair_Improvised_Wood"), row.SelectedSection!.Label);
            Assert.Null(row.SelectedSection.Tag);
            Assert.Contains(HeadlessUi.Find<TextBlock>(box), t => t.Text == row.SelectedSection.Label);
        });
    }

    /// <summary>
    /// The row's "Shows in" list: the default first (named after the default section), then the game's sections of its
    /// recipe class only; a kind of the other class drops a chosen section, and the choice is saved with the project.
    /// </summary>
    [Fact]
    public void TheSectionsFollowTheKind()
    {
        var sections = CraftCategories.Merge(
        [
            new(Chairs, CraftCategories.Label(Chairs), "Furniture", true, 1, null, ["CR_Chair_Improvised_Wood"]),
            new(Walls, CraftCategories.Label(Walls), "Base building", true, 1, null, [CraftCategories.WallRecipe]),
            new(Misc, CraftCategories.Label(Misc), "Tools and misc", false, 1, null, ["CR_Small_Battery_Charger"]),
        ]);
        var saved = new List<Craftable>();
        var row = new CraftableRowViewModel(new Craftable { Name = "Oak Table", Source = "/Game/A/SM_Table", Mesh = "/Game/A/SM_Table" }, r => saved.Add(r.ToModel()),
            (_, _) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null));
        row.SetSections(sections, null);

        Assert.True(row.IsBuilding);
        Assert.Equal([null, Walls, Chairs], row.Sections.Select(s => s.Tag));
        Assert.Equal("Game default (Furniture › Chairs)", row.Sections[0].Label);
        Assert.Equal("Base building › Walls", row.Sections[1].Label);
        Assert.Null(row.SelectedSection!.Tag);

        row.SelectedSection = row.Sections[1];
        Assert.Equal(Walls, row.Category);
        Assert.Equal(Walls, saved[^1].Category);
        row.SelectedSection = null; // the box sends one while its list is renewed
        Assert.Equal(Walls, row.Category);

        // Power is crafted into the inventory: its sections are the items' ones, and a building section is dropped.
        row.Kind = CraftKind.Power;
        Assert.False(row.IsBuilding);
        Assert.Null(row.Category);
        Assert.Null(saved[^1].Category);
        Assert.Equal([null, Misc], row.Sections.Select(s => s.Tag));
        Assert.Equal("Game default (Tools and misc › Misc)", row.Sections[0].Label);

        // A section the game files do not list (an older game, a typo in the file) stays listed and chosen; export reports it.
        var odd = new CraftableRowViewModel(new Craftable { Name = "Odd", Source = "/Game/A/SM_Odd", Mesh = "/Game/A/SM_Odd", Category = "CraftingCategory.Placeables.Gone" }, _ => { },
            (_, _) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null));
        odd.SetSections(sections, null);
        Assert.Equal("CraftingCategory.Placeables.Gone", odd.SelectedSection!.Tag);
        Assert.Equal("Gone", odd.SelectedSection.Label);
    }

    private static async Task WithPageAsync(int width, int height, IReadOnlyList<Craftable> craftables, Action<Window, CraftablesPageViewModel> check, string? language = null)
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        new CraftablesFile { Items = craftables }.Save(project.DirectoryPath);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, width, height);
        try
        {
            if (language is not null)
            {
                Loc.Instance.Language = language; // after the window: it starts in the saved language
            }

            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            Assert.True(HeadlessUi.PumpUntil(() => page.Items.Count == craftables.Count && page.SelectedItem is not null));
            Assert.True(page.HasCatalog);
            Assert.False(page.IsGalleryOpen);
            HeadlessUi.Pump();
            check(window, page);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static Rect At(Visual window, Visual control) => new(control.TranslatePoint(new Point(0, 0), window)!.Value, control.Bounds.Size);

    private static void Disjoint(string at, Window window, params string[] names)
    {
        var rects = names.Select(n => (Name: n, Rect: At(window, HeadlessUi.FindNamed<Control>(window, n) ?? throw new InvalidOperationException($"no {n}")))).ToList();
        foreach (var (name, rect) in rects)
        {
            Assert.True(rect.Width > 0 && rect.Right <= window.Bounds.Width, $"{at}: {name} {rect}");
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                Assert.False(rects[i].Rect.Intersects(rects[j].Rect), $"{at}: {rects[i].Name} {rects[i].Rect} and {rects[j].Name} {rects[j].Rect} overlap");
            }
        }
    }
}
