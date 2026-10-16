using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's element editor without the game (owner, 2026-10-12: "a bed must behave like a normal bed: he can
/// set its health, respawn at it, levels"): the values each level stores with the game's ones and Reset, Use (Both only
/// with an item), a bed source becoming an element, the warning when an exported craftable changes kind, and the respawn
/// tooltip that says where respawn times live. The real game files are in <see cref="CraftPickersRealTests"/>.
/// </summary>
[Collection(LanguageCollection.Name)] // compares texts of the UI language
public sealed class CraftElementEditorTests
{
    private const string Bed = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Bed_Improvised";
    private const string BedUpgrade = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Bed_Improvised_Upgrade";
    private const string Bench = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Base_ImprovisedWorkbench";

    [Fact]
    public void LevelsListTheStoredValuesAndResetRoundTrips()
    {
        var row = Row();
        row.SetFamily(BedFamily(), null);
        var editor = Assert.IsType<ElementEditorViewModel>(row.Element);
        Assert.True(row.HasElement);
        Assert.True(editor.HasLevels);
        Assert.Equal(Loc.F("Craftables.Element.Like", "BP_Bed_Improvised"), editor.Like);

        // Level 1 stores health and "can be damaged"; level 2 only health (the rest is its parent class's).
        var first = editor.Levels[0];
        Assert.Equal(["_healthPoints", "_canBeDamaged"], first.Values.Select(v => v.Key));
        Assert.Equal(["500", "true"], first.Values.Select(v => v.Stock));
        Assert.Equal(Loc.T("Craftables.Element.Health"), first.Values[0].Label);
        Assert.True(first.Values[1].IsFlag);
        var second = editor.Levels[1];
        Assert.Equal(["_healthPoints"], second.Values.Select(v => v.Key));
        Assert.Equal(1000m, second.Values[0].Number);
        Assert.Contains(Loc.T("Craftables.Element.CanBeDamaged"), second.Inherited);
        Assert.DoesNotContain("_isUsedForRespawn", second.Inherited); // the respawn point is shown, not edited

        // Nothing changed: no value is written. An edit is written for its level only; Reset takes it away again.
        Assert.Empty(row.ToModel().Element!.Levels);
        Assert.Equal(Bed, row.ToModel().Element!.Template);
        second.Values[0].Number = 1500;
        var levels = row.ToModel().Element!.Levels;
        Assert.Equal(2, levels.Count);
        Assert.Empty(levels[0]);
        Assert.Equal("1500", levels[1]["_healthPoints"]);
        first.Values[1].Flag = false;
        Assert.Equal("false", row.ToModel().Element!.Levels[0]["_canBeDamaged"]);
        Assert.True(first.Values[1].ResetCommand.CanExecute(null));
        first.Values[1].ResetCommand.Execute(null);
        second.Values[0].ResetCommand.Execute(null);
        Assert.False(second.Values[0].ResetCommand.CanExecute(null));
        Assert.Empty(row.ToModel().Element!.Levels);

        // A saved edit comes back as the edited value next to the game's.
        var again = Row(new ElementSettings { Template = Bed, Levels = [new Dictionary<string, string>(), new Dictionary<string, string> { ["_healthPoints"] = "2000" }] });
        again.SetFamily(BedFamily(), null);
        Assert.Equal(2000m, again.Element!.Levels[1].Values[0].Number);
        Assert.True(again.Element.Levels[1].Values[0].IsEdited);
        Assert.Equal("2000", again.ToModel().Element!.Levels[1]["_healthPoints"]);
    }

    [Fact]
    public void BothNeedsAnItemClass()
    {
        var bed = Row();
        bed.SetFamily(BedFamily(), null);
        Assert.Equal(CraftUse.Build, bed.Use);
        Assert.False(bed.CanBoth);
        Assert.False(bed.CanCraft);
        bed.IsBoth = true;
        Assert.Equal(CraftUse.Build, bed.Use);
        Assert.Equal(CraftUse.Build, bed.ToModel().Use);

        var bench = Row(new ElementSettings { Template = Bench });
        bench.SetFamily(new ElementFamily(Bench, "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Improvised_Workbench", "BaseBuildingItemComponent",
            "/Game/ConZ_Files/Items/Crafting/Improvised_Workbench", [new ElementLevel("CR_Improvised_Workbench", Bench, [Value(Bench, "_healthPoints", TunableKind.Float, "300")])], false), null);
        Assert.True(bench.CanBoth);
        bench.IsBoth = true;
        Assert.Equal(CraftUse.Both, bench.Use);
        Assert.Equal(CraftUse.Both, bench.ToModel().Use);

        // An item of the game goes to the bag; its Use is its kind's own (not stored).
        var item = new CraftableRowViewModel(new Craftable { Name = "Rifle", Source = "/Game/A/Weapon_AK47", Mesh = "/Game/A/Weapon_AK47", Kind = CraftKind.Item }, _ => { }, NoPicture);
        Assert.Equal(CraftUse.Craft, item.Use);
        Assert.False(item.CanBuild);
        Assert.Null(item.ToModel().Use);
    }

    [Fact]
    public void ABedSourceBecomesAnElement()
    {
        var furniture = new Craftable { Name = "Bed Improvised", Source = Bed, Mesh = "/Game/A/SM_Bed", Kind = RecipeRules.GuessKind(Bed) };
        Assert.Equal(CraftKind.Furniture, furniture.Kind); // what the page gave a bed before: a chair showing a bed

        var element = CraftablesPageViewModel.AsElement(furniture, BedFamily());
        Assert.Equal(CraftKind.Element, element.Kind);
        Assert.Equal(Bed, element.Element!.Template);
        Assert.Equal(CraftUse.Build, element.Use);
        Assert.Same(furniture, CraftablesPageViewModel.AsElement(furniture, null));

        // The row offers the element kind; an existing Furniture craftable is never switched by itself.
        var row = new CraftableRowViewModel(element, _ => { }, NoPicture);
        Assert.Contains(CraftKind.Element, row.Kinds);
        var old = new CraftableRowViewModel(furniture, _ => { }, NoPicture);
        Assert.DoesNotContain(CraftKind.Element, old.Kinds);
        old.SetFamily(BedFamily(), null);
        Assert.Contains(CraftKind.Element, old.Kinds);
        Assert.Equal(CraftKind.Furniture, old.Kind);
    }

    [Fact]
    public void ChangingTheKindOfAnExportedCraftableWarns()
    {
        var saved = new CraftableRowViewModel(new Craftable { Name = "Bed", Source = Bed, Mesh = Bed, Kind = CraftKind.Element, Element = new ElementSettings { Template = Bed } }, _ => { }, NoPicture) { MayBeExported = true };
        Assert.Null(saved.KindWarning);
        saved.Kind = CraftKind.Furniture;
        Assert.Equal(Loc.T("Craftables.KindChange.Warning"), saved.KindWarning);

        var added = Row();
        added.Kind = CraftKind.Furniture;
        Assert.Null(added.KindWarning); // added in this session: nothing of it was exported yet
    }

    [Fact]
    public void TheRespawnTooltipSaysWhereRespawnTimesLive()
    {
        foreach (var language in Loc.Languages.Select(l => l.Code))
        {
            var tip = Loc.Table(language)["Craftables.Element.Respawn.Tip"];
            Assert.Contains("ServerSettings.ini", tip, StringComparison.Ordinal);
            Assert.Contains("RespawnSettings", tip, StringComparison.Ordinal);
        }

        var row = Row();
        row.SetFamily(BedFamily(), null);
        Assert.True(row.Element!.IsRespawnPoint);
    }

    /// <summary>The page shows the element editor, the ingredient rows with their pictures and badges, and the picker over the recipe.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ThePageShowsTheEditorAndThePicker()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        Craftable bed = new()
        {
            Name = "Bed", Source = Bed, Mesh = "/Game/A/SM_Bed", Kind = CraftKind.Element, Element = new ElementSettings { Template = Bed }, Use = CraftUse.Build,
            Ingredients = [new("CI_Group_Rags", 4, false, "/Game/ConZ_Files/Items/Crafting/Resources/Rag", true), new("CI_Plank", 6), new("CI_Group_Blade", 1, true)],
        };
        new CraftablesFile { Items = [bed] }.Save(project.DirectoryPath);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            Assert.True(HeadlessUi.PumpUntil(() => page.SelectedItem is not null && !page.IsGalleryOpen));
            var row = page.SelectedItem!;

            // The synthetic game has no bed: the page says why; the game's family fills the editor.
            Assert.True(HeadlessUi.PumpUntil(() => row.ElementNote is not null, TimeSpan.FromSeconds(30)));
            row.SetFamily(BedFamily(), null);
            HeadlessUi.Pump();
            Assert.True(HeadlessUi.FindNamed<Border>(window, "CraftElementRespawn")!.IsEffectivelyVisible);
            Assert.Equal(2, HeadlessUi.FindNamed<ItemsControl>(window, "CraftElementValues")!.ItemCount);
            Assert.False(HeadlessUi.FindNamed<ToggleButton>(window, "CraftUseBoth")!.IsEffectivelyEnabled);
            Assert.True(HeadlessUi.FindNamed<ToggleButton>(window, "CraftUseBuild")!.IsChecked);

            // Each ingredient as a picture, its name and badge; no tag is typed any more.
            var ingredients = HeadlessUi.FindNamed<ItemsControl>(window, "CraftIngredients")!;
            Assert.Equal(3, ingredients.ItemCount);
            Assert.Empty(HeadlessUi.Find<AutoCompleteBox>(ingredients));
            Assert.Contains(HeadlessUi.Find<TextBlock>(ingredients), t => t.Text == Loc.T("Craftables.Ingredient.Badge.Group"));
            Assert.Contains(HeadlessUi.Find<TextBlock>(ingredients), t => t.Text == "Rag");

            // Add ingredient opens the picker over the recipe; Cancel leaves the recipe as it was.
            var picking = page.PickIngredientCommand.ExecuteAsync(null);
            Assert.True(HeadlessUi.PumpUntil(() => page.Picker.LoadCompletion.IsCompleted && HeadlessUi.FindNamed<Control>(window, "CraftIngredientPicker")!.IsEffectivelyVisible, TimeSpan.FromSeconds(60)));
            Assert.False(HeadlessUi.FindNamed<Button>(window, "CraftPickerOk")!.IsEffectivelyEnabled);
            Assert.Contains(page.Picker.Roots, r => r.Title == Loc.T("Craftables.Picker.Groups"));
            HeadlessUi.SaveScreenshot(window, "craftables-ingredient-picker-synthetic");
            page.Picker.CancelCommand.Execute(null);
            await picking;
            Assert.False(HeadlessUi.FindNamed<Control>(window, "CraftIngredientPicker")!.IsVisible);
            Assert.Equal(3, page.File.Items.Single().Ingredients.Count);
            Assert.Equal(bed.Ingredients, page.File.Items.Single().Ingredients);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static ElementFamily BedFamily() => new(
        Bed,
        "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Bed_Improvised",
        "BaseBuildingComponent",
        null,
        [
            new ElementLevel("CR_Bed_Improvised", Bed, [Value(Bed, "_healthPoints", TunableKind.Float, "500"), Value(Bed, "_canBeDamaged", TunableKind.Bool, "true"), Value(Bed, "_isUsedForRespawn", TunableKind.Bool, "true")]),
            new ElementLevel("CR_Upgrade_Bed_Improvised", BedUpgrade, [Value(BedUpgrade, "_healthPoints", TunableKind.Float, "1000")]),
        ],
        true);

    private static Tunable Value(string element, string key, TunableKind kind, string value) =>
        new($"Default__{element[(element.LastIndexOf('/') + 1)..]}_C", key, kind, value) { Name = key };

    private static CraftableRowViewModel Row(ElementSettings? settings = null) =>
        new(new Craftable { Name = "Bed", Source = Bed, Mesh = "/Game/A/SM_Bed", Kind = CraftKind.Element, Element = settings ?? new ElementSettings { Template = Bed }, Use = CraftUse.Build }, _ => { }, NoPicture);

    private static Task<Bitmap?> NoPicture(Craftable craftable, CancellationToken cancellationToken) => Task.FromResult<Bitmap?>(null);
}
