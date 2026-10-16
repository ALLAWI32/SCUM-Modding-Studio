using Avalonia.Media.Imaging;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's ingredient picker without the game (owner, 2026-10-12: "what he pays for them also chosen in 3D,
/// not typed"; CwHedgehog: "Can I add items as new crafting materials?"): a picked item survives the save with its tag,
/// the catalogue's categories and the ingredient groups, search, the new-tag opt-in, the amount range and the reads on a
/// worker. The real game files are in <see cref="CraftPickersRealTests"/>.
/// </summary>
[Collection(LanguageCollection.Name)] // compares texts of the UI language
public sealed class CraftIngredientPickerTests
{
    private const string LeadPlate = "/Game/ConZ_Files/Items/Crafting/Resources/Lead_Plate";
    private const string Rag = "/Game/ConZ_Files/Items/Crafting/Resources/Rag";
    private const string Bullet = "/Game/ConZ_Files/Items/Ammo/Cal_9mm";
    private const string Rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Apple = "/Game/ConZ_Files/Items/Food/Apple";
    private const string ModItem = "/Game/Mods/Forge/Items/Forged_Ingot";

    private static readonly IngredientLookup OwnLeadPlate = new(ItemTagState.OwnTag, "CI_Lead_Plate", ["CI_Group_Metal"], false);
    private static readonly IngredientLookup OwnRifle = new(ItemTagState.OwnTag, "CI_Weapon_AK47", [], false);
    private static readonly IngredientLookup RagGroup = new(ItemTagState.GroupOnly, "CI_Group_Rags", ["CI_Group_Rags"], true);
    private static readonly IngredientLookup None = new(ItemTagState.NoTag, null, [], false);

    [Fact]
    public async Task PickedItemSurvivesSave()
    {
        var picker = await PickerAsync();
        var answer = picker.PickAsync();
        picker.SelectCommand.Execute(picker.Tiles.Single(t => t.Item == LeadPlate));
        await picker.ResolveCompletion;
        picker.Amount = 3;
        Assert.True(picker.OkCommand.CanExecute(null));
        picker.OkCommand.Execute(null);
        var pick = Assert.IsType<IngredientPick>(await answer);
        Assert.False(picker.IsOpen);

        // The page adds it as it adds any ingredient; the file keeps the item, not only its tag.
        var row = Row();
        row.SetIngredients([.. row.ToModel().Ingredients, pick.ToIngredient()]);
        var saved = CraftablesFile.Parse(new CraftablesFile { Items = [row.ToModel()] }.ToJson()).Items.Single().Ingredients;
        Assert.Contains(new CraftIngredient("CI_Lead_Plate", 3, false, LeadPlate, false), saved);
        Assert.Contains(new CraftIngredient("CI_Plank", 2), saved);

        // A rag has no tag of its own: without the opt-in the recipe asks for its group, any rag.
        answer = picker.PickAsync();
        picker.SelectCommand.Execute(picker.Tiles.Single(t => t.Item == Rag));
        await picker.ResolveCompletion;
        picker.OkCommand.Execute(null);
        Assert.Equal(new CraftIngredient("CI_Group_Rags", 1), (await answer)!.ToIngredient());

        // With the opt-in, the file asks for a new tag on that item.
        answer = picker.PickAsync();
        picker.SelectCommand.Execute(picker.Tiles.Single(t => t.Item == Rag));
        await picker.ResolveCompletion;
        picker.AddTag = true;
        picker.OkCommand.Execute(null);
        row.SetIngredients([.. row.ToModel().Ingredients, (await answer)!.ToIngredient()]);
        Assert.Contains(new CraftIngredient("CI_Group_Rags", 1, false, Rag, true), row.ToModel().Ingredients);

        // Change on a row opens the picker on it: same item, amount and opt-in.
        var again = row.Ingredients.Single(i => i.Item == Rag);
        again.Show(Resolve(Rag), Rag);
        Assert.Equal(new IngredientPick(Rag, "CI_Group_Rags", 1, false, true, Resolve(Rag), Rag), again.ToPick());
    }

    [Fact]
    public async Task CategoriesComeFromTheCatalogue()
    {
        var picker = await PickerAsync(withMod: true);

        Assert.Equal([Loc.T("Assets.Group.PickUp"), Loc.T("Craftables.Picker.Groups"), Loc.T("Craftables.Gallery.Other")], picker.Roots.Select(r => r.Title));
        var pickUp = picker.Roots[0];
        Assert.Equal(["weapons", "ammo", "items"], pickUp.Children.Select(c => c.Ids.Single()));
        var items = pickUp.Children.Single(c => c.Ids.Single() == "items");
        Assert.Contains(items.Children, c => c.Ids.Single() == "items.food" && c.Count == 1);
        Assert.Equal(Groups().Count, picker.Roots[1].Count);

        // Each category lists its items; the groups show their representative item's picture; the mod's item has its own.
        picker.SelectedCategory = items.Children.Single(c => c.Ids.Single() == "items.food");
        Assert.Equal([Apple], picker.Tiles.Select(t => t.Item));
        picker.SelectedCategory = picker.Roots[1];
        var plank = picker.Tiles.Single(t => t.Group == "CI_Plank");
        Assert.True(plank.IsGroup);
        Assert.Equal("/Game/ConZ_Files/Items/Crafting/Resources/Wooden_Plank", plank.PicturePath);
        Assert.Equal("Wooden plank", plank.Name);
        picker.SelectedCategory = picker.Roots[2];
        Assert.Equal([ModItem], picker.Tiles.Select(t => t.Item));
    }

    [Fact]
    public async Task SearchFindsItemsAndTags()
    {
        var picker = await PickerAsync();
        picker.Query = "lead";
        await Until(() => picker.Tiles.Count == 2);
        Assert.Equal(["CI_Lead_Plate", null], picker.Tiles.Select(t => t.Group));
        Assert.Equal([null, LeadPlate], picker.Tiles.Select(t => t.Item));

        picker.Query = "rags";
        await Until(() => picker.Tiles.Count == 1);
        Assert.Equal("CI_Group_Rags", picker.Tiles.Single().Group);

        picker.Query = string.Empty;
        await Until(() => picker.Tiles.Count == Known().Count); // back to the selected category: every item a player can carry
    }

    [Fact]
    public async Task AddTagOnlyForGroupOnlyOrNoTagItems()
    {
        var picker = await PickerAsync(withMod: true);
        _ = picker.PickAsync();

        // An item with its own tag: nothing to add.
        picker.AddTag = true;
        picker.SelectCommand.Execute(picker.Tiles.First(t => t.Item == Rifle));
        await picker.ResolveCompletion;
        Assert.False(picker.CanAddTag);
        Assert.False(picker.AddTag);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.OwnTag"), picker.BadgeText);

        // Group only: the group is used, or a new tag when ticked.
        picker.SelectCommand.Execute(Tile(picker, Rag));
        await picker.ResolveCompletion;
        Assert.True(picker.CanAddTag);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.GroupOnly"), picker.BadgeText);
        picker.AddTag = true;
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.NewTag"), picker.BadgeText);

        // An imported mod's item without a tag: its setup is not the game's, so no tag is added and no recipe can ask for it.
        picker.SelectCommand.Execute(Tile(picker, ModItem));
        await picker.ResolveCompletion;
        Assert.False(picker.CanAddTag);
        Assert.False(picker.AddTag);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.NoTag"), picker.BadgeText);
        Assert.False(picker.OkCommand.CanExecute(null));

        // The row keeps the same rule: no new tag on an item with its own, nor on a group.
        var row = new IngredientRowViewModel(new CraftIngredient("CI_Lead_Plate", 1, false, LeadPlate), () => { }, _ => { });
        row.Show(Resolve(LeadPlate), LeadPlate);
        row.AddTag = true;
        Assert.False(row.AddTag);
        Assert.False(row.ToModel().AddTag);
        var group = new IngredientRowViewModel(new CraftIngredient("CI_Group_Rags", 1), () => { }, _ => { });
        group.AddTag = true;
        Assert.False(group.ToModel().AddTag);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.Group"), group.BadgeText);
        var rag = new IngredientRowViewModel(new CraftIngredient("CI_Group_Rags", 1, false, Rag), () => { }, _ => { });
        rag.Show(Resolve(Rag), Rag);
        rag.AddTag = true;
        Assert.True(rag.ToModel().AddTag);
        Assert.Equal(IngredientIndex.NewTagName(Rag), rag.Detail);
    }

    [Fact]
    public void AmountIsClampedTo1Through9999()
    {
        var picker = new IngredientPickerViewModel((_, _) => Task.FromResult<Bitmap?>(null));
        picker.Amount = 0;
        Assert.Equal(1, picker.Amount);
        picker.Amount = 12_000;
        Assert.Equal(9999, picker.Amount);

        var row = new IngredientRowViewModel(new CraftIngredient("CI_Plank", 5), () => { }, _ => { }) { Amount = 20_000 };
        Assert.Equal(9999, row.ToModel().Amount);
        row.Amount = 0;
        Assert.Equal(1, row.ToModel().Amount);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ResolveRunsOffTheUiThread()
    {
        var ui = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        using var gate = new ManualResetEventSlim();
        var source = new IngredientSource(
            () => { lock (threads) { threads.Add(Environment.CurrentManagedThreadId); } return (Known(), []); },
            () => { lock (threads) { threads.Add(Environment.CurrentManagedThreadId); } return Groups(); },
            item =>
            {
                lock (threads)
                {
                    threads.Add(Environment.CurrentManagedThreadId);
                }

                gate.Wait(TimeSpan.FromSeconds(30)); // the index of a whole game takes a while: the UI must not wait for it
                return Resolve(item);
            });
        var picker = new IngredientPickerViewModel((_, _) => Task.FromResult<Bitmap?>(null));
        picker.Load(source);
        Assert.True(picker.IsLoading);
        await picker.LoadCompletion;
        Assert.False(picker.IsLoading);

        _ = picker.PickAsync();
        picker.SelectCommand.Execute(Tile(picker, LeadPlate));
        Assert.True(picker.IsResolving); // the UI thread is free while the tags are read
        Assert.False(picker.OkCommand.CanExecute(null));
        Assert.Equal(Loc.T("Craftables.Ingredient.Checking"), picker.BadgeText);
        gate.Set();
        await picker.ResolveCompletion;

        Assert.False(picker.IsResolving);
        Assert.Equal(Loc.T("Craftables.Ingredient.Badge.OwnTag"), picker.BadgeText);
        Assert.True(picker.OkCommand.CanExecute(null));
        Assert.Equal(3, threads.Count);
        Assert.DoesNotContain(ui, threads);
    }

    /// <summary>How the fake game knows its items: the rifle and the lead plate have their own tag, a rag only its group.</summary>
    private static IngredientLookup Resolve(string item) => item switch
    {
        LeadPlate => OwnLeadPlate,
        Rifle => OwnRifle,
        Rag => RagGroup,
        _ => None,
    };

    /// <summary>A picker that has read the fake game (with an imported mod's item when <paramref name="withMod"/>).</summary>
    private static async Task<IngredientPickerViewModel> PickerAsync(bool withMod = false)
    {
        var picker = new IngredientPickerViewModel((_, _) => Task.FromResult<Bitmap?>(null));
        IReadOnlyList<PackageEntry> other = withMod ? [new PackageEntry(ModItem + ".uasset", ModItem, "Blueprint")] : [];
        picker.Load(new IngredientSource(() => (Known(), other), Groups, Resolve));
        await picker.LoadCompletion;
        return picker;
    }

    private static IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> Known() =>
    [
        Item(Rifle, "weapons.ranged"),
        Item(Bullet, "ammo.calibre"),
        Item(Apple, "items.food"),
        Item(LeadPlate, "items.crafting"),
        Item(Rag, "items.crafting"),
    ];

    private static IReadOnlyList<IngredientGroup> Groups() =>
    [
        new("CI_Group_Rags", null, Rag),
        new("CI_Lead_Plate", "Lead plate", LeadPlate),
        new("CI_Plank", "Wooden plank", "/Game/ConZ_Files/Items/Crafting/Resources/Wooden_Plank"),
    ];

    private static (DumpPackage, PackageEntry) Item(string path, string node) =>
        (new DumpPackage(path, node, "Blueprint"), new PackageEntry(path + ".uasset", path, "Blueprint"));

    private static CraftableRowViewModel Row() =>
        new(new Craftable { Name = "Forge", Source = "/Game/A/SM_Forge", Mesh = "/Game/A/SM_Forge", Ingredients = [new("CI_Plank", 2)] }, _ => { }, (_, _) => Task.FromResult<Bitmap?>(null));

    /// <summary>The tile of <paramref name="item"/>, searched for when its category is not the one shown.</summary>
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

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 5000, "timed out");
            await Task.Delay(20);
        }
    }
}

