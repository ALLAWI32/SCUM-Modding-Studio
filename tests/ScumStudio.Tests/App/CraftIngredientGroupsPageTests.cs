using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page without the game (r40, Discord igor8802 2026-10-20/21: "copy and paste the ingredients", "make it possible
/// to rearrange places", "create, change, and view groups of ingredients"; redriver6155: "maybe let us create new groups?"):
/// ingredients move up and down, are copied to another craftable, and the project's groups are made, used, changed and deleted;
/// every change is saved at once and one Undo step. The real game files are in <see cref="CraftIngredientTagsRealTests"/>.
/// </summary>
[Collection(LanguageCollection.Name)] // compares texts of the UI language
public sealed class CraftIngredientGroupsPageTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Scissors = "/Game/ConZ_Files/Items/Equipment/Active_Items/Scissors_Plastic";
    private const string Knife = "/Game/ConZ_Files/Items/Weapons/1H_Kitchen_Knife";
    private const string Rag = "/Game/ConZ_Files/Items/Crafting/Resources/Rag";

    [Fact]
    public async Task IngredientsMoveUpAndDownInOneUndoStepEach()
    {
        using var ctx = AppTestContext.Create();
        var (page, project) = await PageAsync(ctx, """{ "Tag": "CI_Plank", "Amount": 9 }, { "Tag": "CI_Bolts", "Amount": 2 }, { "Tag": "CI_Group_Blade", "Amount": 1, "IsTool": true }""");
        using var _ = page;
        var rows = page.SelectedItem!.Ingredients.ToList();
        Assert.Equal([(false, true), (true, true), (true, false)], rows.Select(r => (r.CanMoveUp, r.CanMoveDown)));

        // The first one down: the same rows (their badges and pictures stay), saved in the new order, the places follow.
        page.MoveIngredientDownCommand.Execute(rows[0]);
        Assert.Equal(["CI_Bolts", "CI_Plank", "CI_Group_Blade"], Saved(project).Select(i => i.Tag));
        Assert.Same(rows[0], page.SelectedItem.Ingredients[1]);
        Assert.Equal([(false, true), (true, true), (true, false)], page.SelectedItem.Ingredients.Select(r => (r.CanMoveUp, r.CanMoveDown)));

        // The tool to the top; nothing moves past the ends, and that is no Undo step.
        page.MoveIngredientUpCommand.Execute(rows[2]);
        page.MoveIngredientUpCommand.Execute(rows[2]);
        page.MoveIngredientUpCommand.Execute(rows[2]);
        var moved = Saved(project);
        Assert.Equal(["CI_Group_Blade", "CI_Bolts", "CI_Plank"], moved.Select(i => i.Tag));
        Assert.True(moved[0].IsTool);

        page.UndoCommand.Execute(null);
        Assert.Equal(["CI_Bolts", "CI_Group_Blade", "CI_Plank"], Saved(project).Select(i => i.Tag));
        page.UndoCommand.Execute(null);
        page.UndoCommand.Execute(null);
        Assert.Equal(["CI_Plank", "CI_Bolts", "CI_Group_Blade"], Saved(project).Select(i => i.Tag));
        Assert.False(page.CanUndo);
    }

    [Fact]
    public async Task IngredientsAreCopiedToAnotherCraftable()
    {
        using var ctx = AppTestContext.Create();
        var (page, project) = await PageAsync(ctx,
            $$"""{ "Tag": "CI_Plank", "Amount": 9, "Levels": { "NoSkill": 9, "Basic": 8, "Medium": 7, "Advanced": 6, "AboveAdvanced": 5 } }, { "Tag": "", "Amount": 1, "IsTool": true, "Item": "{{Scissors}}", "AddTag": true }""",
            """, { "Name": "Stool", "Token": "SS_Stool", "Source": "/Game/A/SM_Stool", "Mesh": "/Game/A/SM_Stool", "Ingredients": [ { "Tag": "CI_Stone", "Amount": 3 } ] }""");
        using var _ = page;
        Assert.False(page.PasteIngredientsCommand.CanExecute(null)); // nothing copied yet

        page.CopyIngredientsCommand.Execute(null);
        Assert.True(page.HasCopied);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.F("Craftables.Ingredients.Copied", 2, "Chair"));
        var chair = Saved(project);

        page.SelectedItem = page.Items[1];
        Assert.True(page.PasteIngredientsCommand.CanExecute(null));
        page.PasteIngredientsCommand.Execute(null);
        var file = CraftablesFile.Load(project.DirectoryPath);
        Assert.Equal(chair, file.Items[1].Ingredients); // amounts per level, the tool and the new tag as copied
        Assert.Equal(chair, file.Items[0].Ingredients); // the source keeps its own

        page.UndoCommand.Execute(null);
        Assert.Equal([new CraftIngredient("CI_Stone", 3)], CraftablesFile.Load(project.DirectoryPath).Items[1].Ingredients);
    }

    [Fact]
    public async Task AProjectGroupIsMadeUsedChangedAndDeletedWithUndo()
    {
        using var ctx = AppTestContext.Create();
        var (page, project) = await PageAsync(ctx, """{ "Tag": "CI_Plank", "Amount": 9 }""");
        using var _ = page;
        await LoadPickerAsync(page.Picker);

        // No name: refused. Then "Cutting Kit" from two items picked at once.
        await page.NewGroupCommand.ExecuteAsync(null);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.T("Craftables.Groups.NameFirst"));
        page.NewGroupName = "Cutting Kit";
        var made = page.NewGroupCommand.ExecuteAsync(null);
        Assert.True(page.Picker.IsOpen && page.Picker.IsMulti);
        Assert.DoesNotContain(page.Picker.Roots, r => r.Title == Loc.T("Craftables.Picker.Groups")); // a group holds items, not groups
        Assert.False(page.Picker.OkCommand.CanExecute(null));
        page.Picker.SelectCommand.Execute(Tile(page.Picker, Scissors));
        page.Picker.SelectCommand.Execute(Tile(page.Picker, Knife));
        Assert.Equal(Loc.F("Craftables.Picker.Picked", 2), page.Picker.PickedText);
        page.Picker.OkCommand.Execute(null);
        await made;

        var group = Assert.Single(page.Groups);
        Assert.Equal(("Cutting Kit", "CI_Group_Cutting_Kit"), (group.Name, group.Tag));
        Assert.Equal([Scissors, Knife], group.Members);
        Assert.Equal(Loc.F("Craftables.Group.Members", "Scissors_Plastic, 1H_Kitchen_Knife"), group.MembersText);
        Assert.Equal(new CraftGroup("Cutting Kit", "CI_Group_Cutting_Kit", [Scissors, Knife]), Assert.Single(CraftablesFile.Load(project.DirectoryPath).Groups));
        Assert.Equal(string.Empty, page.NewGroupName);
        Assert.True(page.Picker.GroupOf(group.Tag) is { IsProject: true, Members.Count: 2 });

        // The same name again is refused.
        page.NewGroupName = "cutting kit";
        await page.NewGroupCommand.ExecuteAsync(null);
        Assert.Single(page.Groups);

        // Picked as an ingredient like the game's groups; the row shows its name, the badge and its items.
        var picked = page.PickIngredientCommand.ExecuteAsync(null);
        Assert.False(page.Picker.IsMulti);
        page.Picker.SelectedCategory = page.Picker.Roots.Single(r => r.Title == Loc.T("Craftables.Picker.Groups"));
        var tile = page.Picker.Tiles.First(t => t.Group == group.Tag);
        Assert.True(tile.IsProjectGroup);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.ProjectGroup"), tile.GroupBadge);
        page.Picker.SelectCommand.Execute(tile);
        page.Picker.Amount = 2;
        page.Picker.OkCommand.Execute(null);
        await picked;
        Assert.Equal(new CraftIngredient("CI_Group_Cutting_Kit", 2), Saved(project)[1]);
        var row = page.SelectedItem!.Ingredients[1];
        Assert.Equal(("Cutting Kit", "CI_Group_Cutting_Kit"), (row.Title, row.Detail));
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.ProjectGroup"), row.BadgeText);
        Assert.True(row.HasMembers);

        // In use: not deleted, and the warning names the craftable.
        page.DeleteGroupCommand.Execute(group);
        Assert.Single(page.Groups);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.F("Craftables.Groups.InUse", "Cutting Kit", "Chair"));

        // Its items changed: the picker opens with them picked; a click takes the scissors out again.
        var edited = page.EditGroupCommand.ExecuteAsync(group);
        Assert.Equal([Scissors, Knife], page.Picker.Picked);
        var scissors = Tile(page.Picker, Scissors);
        Assert.True(scissors.IsSelected);
        page.Picker.SelectCommand.Execute(scissors);
        Assert.False(scissors.IsSelected);
        page.Picker.SelectCommand.Execute(Tile(page.Picker, Rag));
        page.Picker.OkCommand.Execute(null);
        await edited;
        Assert.Equal([Knife, Rag], Assert.Single(CraftablesFile.Load(project.DirectoryPath).Groups).Members);
        Assert.Equal("CI_Group_Cutting_Kit", page.Groups[0].Tag); // the tag stays

        // Cancel changes nothing.
        var cancelled = page.EditGroupCommand.ExecuteAsync(page.Groups[0]);
        page.Picker.CancelCommand.Execute(null);
        await cancelled;
        Assert.Equal([Knife, Rag], page.Groups[0].Members);

        // Out of the recipe, it is deleted; each step comes back with Undo.
        page.SelectedItem.Ingredients[1].RemoveCommand.Execute(null);
        page.DeleteGroupCommand.Execute(page.Groups[0]);
        Assert.Empty(page.Groups);
        Assert.Empty(CraftablesFile.Load(project.DirectoryPath).Groups);
        page.UndoCommand.Execute(null);
        Assert.Equal([Knife, Rag], Assert.Single(page.Groups).Members);
        page.UndoCommand.Execute(null);
        Assert.Equal(2, Saved(project).Count);
        page.UndoCommand.Execute(null);
        Assert.Equal([Scissors, Knife], Assert.Single(page.Groups).Members);
        page.UndoCommand.Execute(null);
        page.UndoCommand.Execute(null);
        Assert.Empty(page.Groups);
        Assert.DoesNotContain("Groups", File.ReadAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName)), StringComparison.Ordinal); // a file without groups stays as before
    }

    private static async Task<(CraftablesPageViewModel Page, ScumStudio.Level.Projects.Project Project)> PageAsync(AppTestContext ctx, string ingredients, string more = "")
    {
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""
            { "Items": [ { "Name": "Chair", "Token": "SS_Chair", "Source": "{{Table}}", "Mesh": "{{Table}}", "Exported": false, "Ingredients": [ {{ingredients}} ] }{{more}} ] }
            """);
        var page = new CraftablesPageViewModel(ctx.Services);
        Assert.Same(page.Items[0], page.SelectedItem);
        return (page, project);
    }

    private static IReadOnlyList<CraftIngredient> Saved(ScumStudio.Level.Projects.Project project) => CraftablesFile.Load(project.DirectoryPath).Items[0].Ingredients;

    // The picker's items without the game: three items of the catalogue's pick-up categories, one game group.
    private static async Task LoadPickerAsync(IngredientPickerViewModel picker)
    {
        IReadOnlyList<(DumpPackage, PackageEntry)> known =
        [
            (new DumpPackage(Scissors, "items.crafting", "Blueprint"), new PackageEntry(Scissors + ".uasset", Scissors, "Blueprint")),
            (new DumpPackage(Knife, "weapons.ranged", "Blueprint"), new PackageEntry(Knife + ".uasset", Knife, "Blueprint")),
            (new DumpPackage(Rag, "items.crafting", "Blueprint"), new PackageEntry(Rag + ".uasset", Rag, "Blueprint")),
        ];
        picker.Load(new IngredientSource(() => (known, []), () => [new IngredientGroup("CI_Group_Rags", null, Rag, [Rag])],
            _ => new IngredientLookup(ItemTagState.NoTag, null, [], true)));
        await picker.LoadCompletion;
    }

    private static IngredientTileViewModel Tile(IngredientPickerViewModel picker, string item)
    {
        foreach (var category in picker.Roots.SelectMany(r => r.Children.Prepend(r)).SelectMany(c => c.Children.Prepend(c)))
        {
            picker.SelectedCategory = category;
            if (picker.Tiles.FirstOrDefault(t => t.Item == item) is { } tile)
            {
                return tile;
            }
        }

        throw new InvalidOperationException(item + " is listed nowhere");
    }
}
