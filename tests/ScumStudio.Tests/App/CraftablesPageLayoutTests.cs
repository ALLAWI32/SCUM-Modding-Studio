using Avalonia;
using Avalonia.Controls;
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

                // The editor's options: side by side or on the next line, never on each other, never cut on the right.
                var options = HeadlessUi.FindNamed<WrapPanel>(window, "CraftEditorOptions")!;
                var boxes = options.Children.Where(c => c.IsVisible).Select(c => (Control: c, Rect: At(window, c))).ToList();
                Assert.True(boxes.Count >= 4, $"{at}: {boxes.Count} options shown");
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
