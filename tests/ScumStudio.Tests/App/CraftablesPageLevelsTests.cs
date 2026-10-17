using ScumStudio.App.ViewModels;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's amounts per skill level (CwHedgehog): an ingredient switched to Per level is saved with its five
/// amounts and one Undo step per change; a game recipe's override is edited, undone, reset and removed.
/// </summary>
public sealed class CraftablesPageLevelsTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Workbench = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Improvised_Workbench";

    [Fact]
    public async Task AnIngredientPerLevelIsSavedAndUndone()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""
            { "Items": [ { "Name": "Chair", "Token": "SS_Chair", "Source": "{{Table}}", "Mesh": "{{Table}}", "Ingredients": [ { "Tag": "CI_Plank", "Amount": 9 } ] } ] }
            """);

        using var page = new CraftablesPageViewModel(ctx.Services);
        var ingredient = Assert.Single(page.Items[0].Ingredients);
        Assert.False(ingredient.PerLevel);
        Assert.Equal("By skill level: 9/9/7/6/5", ingredient.AutoLevelsText);
        Assert.False(page.CanUndo);

        ingredient.PerLevel = true; // starts from what the game was asked so far: one step
        Assert.Equal(new LevelAmounts(9, 9, 7, 6, 5), Saved().Levels);
        ingredient.Levels.Basic = 4;
        ingredient.Levels.NoSkill = 12;
        var saved = Saved();
        Assert.Equal((12, new LevelAmounts(12, 4, 7, 6, 5)), (saved.Amount, saved.Levels));
        Assert.Equal(12, ingredient.Amount); // the no-skill amount is the row's amount

        page.UndoCommand.Execute(null);
        Assert.Equal(new LevelAmounts(9, 4, 7, 6, 5), Saved().Levels);
        page.UndoCommand.Execute(null);
        page.UndoCommand.Execute(null);
        Assert.Null(Saved().Levels); // back to the game's ratio
        Assert.False(page.CanUndo);

        CraftIngredient Saved() => Assert.Single(Assert.Single(CraftablesFile.Load(project.DirectoryPath).Items).Ingredients);
    }

    [Fact]
    public async Task AGameRecipeOverrideIsEditedUndoneAndRemoved()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""
            { "Items": [ { "Name": "Chair", "Token": "SS_Chair", "Source": "{{Table}}", "Mesh": "{{Table}}" } ], "RecipeOverrides": [ { "Recipe": "{{Workbench}}", "Slots": [
              { "Tags": "CI_Plank", "Amount": { "NoSkill": 14, "Basic": 12, "Medium": 11, "Advanced": 10, "AboveAdvanced": 8 } },
              { "Tags": "CI_Bolts", "Amount": { "NoSkill": 14, "Basic": 12, "Medium": 11, "Advanced": 10, "AboveAdvanced": 8 } } ] } ] }
            """);

        using var page = new CraftablesPageViewModel(ctx.Services);
        var row = Assert.Single(page.RecipeOverrides);
        Assert.Equal("CR_Improvised_Workbench", row.Name);
        Assert.Equal(["CI_Plank", "CI_Bolts"], row.Slots.Select(s => s.Tags));
        Assert.False(page.HasNoRecipeOverrides);

        page.IsStockOpen = true;
        row.Slots[1].Levels.AboveAdvanced = 2;
        Assert.Equal(new LevelAmounts(14, 12, 11, 10, 2), Saved()[1].Amount);
        Assert.Equal(new LevelAmounts(14, 12, 11, 10, 8), Saved()[0].Amount);
        Assert.Equal("Chair", Assert.Single(CraftablesFile.Load(project.DirectoryPath).Items).Name); // the craftable is kept beside it

        page.UndoCommand.Execute(null);
        Assert.Equal(new LevelAmounts(14, 12, 11, 10, 8), Saved()[1].Amount);
        Assert.True(page.IsStockOpen); // Undo keeps the panel the edit was made in

        page.SelectedItem = null;
        page.SelectedItem = page.Items[0];
        Assert.False(page.IsStockOpen); // a craftable picked in the list is shown

        page.RecipeOverrides[0].RemoveCommand.Execute(null);
        Assert.Empty(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides);
        Assert.True(page.HasNoRecipeOverrides);
        page.UndoCommand.Execute(null);
        Assert.Equal(Workbench, Assert.Single(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides).Recipe);

        IReadOnlyList<StockSlotAmounts> Saved() => Assert.Single(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides).Slots;
    }
}
