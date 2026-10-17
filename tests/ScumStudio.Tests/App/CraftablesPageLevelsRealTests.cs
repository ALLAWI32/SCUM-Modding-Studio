using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's "Game recipes" panel on the real game files (<c>SCUM_PAKS</c>): the search finds the workbench
/// recipe, Add lists its slots with the game's amounts and its skill, an edit is saved and undone. Pictures to
/// <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class CraftablesPageLevelsRealTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AGameRecipeIsFoundAddedEditedAndUndone()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            page.IsStockOpen = true;
            page.StockQuery = "Improvised_Workbench";
            Assert.True(HeadlessUi.PumpUntil(() => page.StockResults.Any(r => r.PackagePath == CraftablesPlanner.StationRecipe), TimeSpan.FromMinutes(3)), "the search found no workbench recipe");
            Assert.All(page.StockResults, r => Assert.StartsWith("CR_", r.Name, StringComparison.Ordinal));

            await page.AddStockRecipeCommand.ExecuteAsync(page.StockResults.First(r => r.PackagePath == CraftablesPlanner.StationRecipe));
            var row = Assert.Single(page.RecipeOverrides);
            Assert.Equal("Levels of: Engineering", row.SkillText);
            Assert.Null(row.Problem);
            Assert.Contains(row.Slots, s => s.Tags == "CI_Plank" && s.GameText.StartsWith("Game: ", StringComparison.Ordinal));
            HeadlessUi.SaveScreenshot(window, "craftables-game-recipes");

            var plank = row.Slots.First(s => s.Tags == "CI_Plank");
            var game = plank.Levels.ToModel();
            plank.Levels.Medium = 3;
            var saved = Assert.Single(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides);
            Assert.Equal(game with { Medium = 3 }, saved.Slots.First(s => s.Tags == "CI_Plank").Amount);

            page.UndoCommand.Execute(null);
            Assert.Equal(game, Assert.Single(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides).Slots.First(s => s.Tags == "CI_Plank").Amount);
            page.UndoCommand.Execute(null);
            Assert.Empty(CraftablesFile.Load(project.DirectoryPath).RecipeOverrides);

            // A craftable's ingredient per level, as the editor shows it.
            page.IsStockOpen = false;
            await page.AddAsync("/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01");
            var ingredient = Assert.Single(page.Items).Ingredients[0];
            ingredient.PerLevel = true;
            ingredient.Levels.AboveAdvanced = 1;
            Assert.Equal(1, Assert.Single(CraftablesFile.Load(project.DirectoryPath).Items).Ingredients[0].Levels?.AboveAdvanced);
            HeadlessUi.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            HeadlessUi.SaveScreenshot(window, "craftables-per-level");
        }
        finally
        {
            window.Close();
        }
    }
}
