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
        Assert.Equal(new TradeableOverride("Weapon_AK47") { PurchasePrice = 12000, SellPrice = 3700 }, item.ToOverride()); // the file's 3700 stays written
        Assert.True(item.IsChanged);
        Assert.Equal(1, changes);

        item.RemoveCommand.Execute(null);
        Assert.Same(item, removed); // the page decides: a default item goes off sale
        item.CanBePurchased = false;
        Assert.True(item.IsOffSale);
        Assert.True(item.CanPutBack);
        Assert.Equal(new TradeableOverride("Weapon_AK47") { PurchasePrice = 12000, SellPrice = 3700, CanBePurchased = false }, item.ToOverride());
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

    [Fact]
    public void AnUnknownCodeKeepsTheFilesValuesWhenAnotherValueChanges()
    {
        // A code the game's tables do not know (a mod's item): the row's defaults are placeholders, never written out.
        var row = new EconomyItemViewModel(new TradeableDefault("Mod_Item", "Mod Item", "", [], 0, 0, true, 0, false), new TradeableOverride("Mod_Item") { SellPrice = 400 }, _ => { }, isAdded: true);

        row.SellPrice = 500;

        var written = row.ToOverride();
        Assert.Null(written.PurchasePrice);
        Assert.Null(written.RequiredFame);
        Assert.Null(written.CanBePurchased);
        Assert.Equal(500, written.SellPrice);
    }

    [Fact]
    public void AnImportedValueEqualToTheGamesStaysWrittenOut()
    {
        var game = new TradeableDefault("Weapon_M9", "M9", "RangedWeapon", ["Armorer"], 1000, 300, true, 0, false);
        var row = new EconomyItemViewModel(game, new TradeableOverride("Weapon_M9") { PurchasePrice = 1000 }, _ => { });

        row.SellPrice = 350;

        Assert.Equal(1000, row.ToOverride().PurchasePrice); // the file said 1000: an edit of another value keeps it
        Assert.Equal(350, row.ToOverride().SellPrice);
    }

    [Fact]
    public void MinusOneMeansTheGamesValueAndPricesNeverWrap()
    {
        var row = new EconomyItemViewModel(Ak, new TradeableOverride("Weapon_AK47") { PurchasePrice = 12000 }, _ => { });

        row.PurchasePrice = -1;

        Assert.Null(row.ToOverride().PurchasePrice);
        Assert.Equal(Ak.PurchasePrice, row.PurchasePrice);

        row.SellPrice = int.MaxValue;
        Assert.Equal(int.MaxValue, row.SellPrice);
    }

    [Fact]
    public void ScalingKeepsPricesWithinTheLargestTheFileHolds()
    {
        var row = new EconomyItemViewModel(Ak, new TradeableOverride("Weapon_AK47") { PurchasePrice = 2_000_000_000 }, _ => { });

        Assert.True(row.ScalePrices(1.1));

        Assert.Equal(int.MaxValue, row.PurchasePrice);
        Assert.Equal(4070, row.SellPrice);
        var unknown = new EconomyItemViewModel(new TradeableDefault("Mod_Item", "Mod Item", "", [], 0, 0, true, 0, false), new TradeableOverride("Mod_Item"), _ => { }, isAdded: true, isUnknown: true);
        Assert.False(unknown.ScalePrices(1.1)); // bulk actions leave codes the game does not know alone
        Assert.True(unknown.ToOverride().IsDefault);
    }

    [Fact]
    public void TypedTextIsReadLenientlyAndCommittedWhole()
    {
        var changes = 0;
        var row = new EconomyItemViewModel(Ak, null, _ => changes++);

        row.PurchaseText = "12,000";
        Assert.Equal(12000, row.ToOverride().PurchasePrice);
        row.SellText = "4 500";
        Assert.Equal(4500, row.ToOverride().SellPrice);
        row.FameText = "1.000";
        Assert.Equal(1000, row.ToOverride().RequiredFame);
        Assert.Equal(3, changes);

        row.PurchaseText = "";
        Assert.Null(row.ToOverride().PurchasePrice);
        Assert.Equal("11200", row.PurchaseText); // the game's value shows
        row.SellText = "-1";
        Assert.Null(row.ToOverride().SellPrice);

        row.FameText = "abc";
        Assert.True(row.IsInvalid);
        Assert.Contains("abc", row.InvalidText, StringComparison.Ordinal);
        Assert.Equal(1000, row.ToOverride().RequiredFame); // unchanged
        row.FameText = "12.5";
        Assert.True(row.IsInvalid); // not a whole number: never read as 125
        row.FameText = "50";
        Assert.False(row.IsInvalid);
        Assert.Equal(50, row.RequiredFame);

        row.DeltaText = "0.25";
        Assert.Equal(0.25f, row.ToOverride().DeltaPrice);
        row.DeltaText = "";
        Assert.Null(row.ToOverride().DeltaPrice);
        Assert.Equal(int.MaxValue, EconomyItemViewModel.ParseInt("99999999999"));
    }

    [Fact]
    public void EachValueResetsOnItsOwn()
    {
        var row = new EconomyItemViewModel(Ak, new TradeableOverride("Weapon_AK47") { PurchasePrice = 15000, SellPrice = 5000, RequiredFame = 10, DeltaPrice = 0.5f, CanBePurchased = false, AfterSaleOnly = true }, _ => { });
        Assert.True(row.ResetPurchaseCommand.CanExecute(null));

        row.ResetPurchaseCommand.Execute(null);
        Assert.Equal(new TradeableOverride("Weapon_AK47") { SellPrice = 5000, RequiredFame = 10, DeltaPrice = 0.5f, CanBePurchased = false, AfterSaleOnly = true }, row.ToOverride());
        Assert.False(row.ResetPurchaseCommand.CanExecute(null));
        row.ResetDeltaCommand.Execute(null);
        row.ResetOnSaleCommand.Execute(null);
        Assert.Equal(new TradeableOverride("Weapon_AK47") { SellPrice = 5000, RequiredFame = 10, AfterSaleOnly = true }, row.ToOverride());
        row.ResetSellCommand.Execute(null);
        row.ResetFameCommand.Execute(null);
        row.ResetAfterSaleCommand.Execute(null);
        Assert.True(row.ToOverride().IsDefault);

        // An added item's Reset writes the game's value out (its entry stands on its own).
        var added = new EconomyItemViewModel(Beer, Beer.AddedEntry() with { PurchasePrice = 99 }, _ => { }, isAdded: true);
        added.ResetPurchaseCommand.Execute(null);
        Assert.Equal(Beer.AddedEntry(), added.ToOverride());
    }
}
