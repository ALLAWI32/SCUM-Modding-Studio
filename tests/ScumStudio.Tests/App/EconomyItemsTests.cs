using ScumStudio.App.ViewModels;
using ScumStudio.Level.Economy;

namespace ScumStudio.Tests.App;

/// <summary>A stock row of the Economy page: what it writes for a trader's own item and for an item added to it.</summary>
public sealed class EconomyItemsTests
{
    private static readonly TradeableDefault Ak = new("Weapon_AK47", "AK-47", "RangedWeapon", ["Armorer"], 11200, 3700, true, 100, false);
    private static readonly TradeableDefault Beer = new("Beer_Can", "Beer", "Drinks", ["Bartender"], 30, 10, false, 5, false);

    [Fact]
    public void ADefaultItemWritesOnlyWhatChangedAndGoesOffSaleWhenRemoved()
    {
        var changes = 0;
        EconomyItemViewModel? removed = null;
        var item = new EconomyItemViewModel(Ak, new TradeableOverride("Weapon_AK47") { PurchasePrice = 11200, SellPrice = 3700 }, _ => changes++, remove: i => removed = i);
        Assert.False(item.IsChanged); // values equal to the game's count as unchanged
        Assert.True(item.CanRemove);
        Assert.False(item.CanPutBack);

        item.PurchasePrice = 12000;
        Assert.Equal(new TradeableOverride("Weapon_AK47") { PurchasePrice = 12000 }, item.ToOverride());
        Assert.True(item.IsChanged);
        Assert.Equal(1, changes);

        item.RemoveCommand.Execute(null);
        Assert.Same(item, removed); // the page decides: a default item goes off sale
        item.CanBePurchased = false;
        Assert.True(item.IsOffSale);
        Assert.True(item.CanPutBack);
        Assert.Equal(new TradeableOverride("Weapon_AK47") { PurchasePrice = 12000, CanBePurchased = false }, item.ToOverride());
        item.PutBackCommand.Execute(null);
        Assert.False(item.IsOffSale);

        item.ResetCommand.Execute(null);
        Assert.True(item.ToOverride().IsDefault);
    }

    [Fact]
    public void AnAddedItemIsWrittenInFullAndStaysWhenReset()
    {
        var item = new EconomyItemViewModel(Beer, Beer.AddedEntry(), _ => { }, isAdded: true);
        Assert.False(item.IsChanged);
        Assert.True(item.CanBePurchased); // on sale at this trader although the game does not sell it at its own
        Assert.Equal(Beer.AddedEntry(), item.ToOverride());

        item.PurchasePrice = 45;
        Assert.True(item.IsChanged);
        Assert.Equal(Beer.AddedEntry() with { PurchasePrice = 45 }, item.ToOverride());

        item.ResetCommand.Execute(null);
        Assert.Equal(Beer.AddedEntry(), item.ToOverride()); // back to the values it was added with, still written out
        Assert.False(item.ToOverride().IsDefault);
    }
}
