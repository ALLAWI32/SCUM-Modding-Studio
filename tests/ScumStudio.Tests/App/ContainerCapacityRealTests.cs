using CUE4Parse.UE4.Assets.Exports;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord #modding-help, unknownskill (2026-10-19): "I see some limits, like changing a backpack: how much size it has
/// inside, load capacity". What a bag holds is the grid inventory of its entity setup (<c>EntityGridInventoryComponentSetup_0</c>:
/// <c>NumRows</c>, <c>NumColumns</c>, <c>MaxContainedWeight</c>, <c>HasContainedWeightLimit</c>). 195 of the game's 444 grids
/// store their size: a colour variant takes its parent's and the code's weight limit applies where none is stored. On the
/// real game files (<c>SCUM_PAKS</c>): the Items page lists the four values as key values of the entity setup, stored or
/// inherited, a clone's new room and load are written into its grid (the game's reader, CUE4Parse, reads them back), and
/// the game's bags stay as they are. Rows and columns are one byte in the file (ByteProperty): 256 is an error on the row
/// before Apply (it used to be journaled, and the export then dropped every edit of the item), 255 is written, stored or
/// inherited. What only the game shows: the bigger bag in the inventory.
/// </summary>
public sealed class ContainerCapacityRealTests
{
    private const string Folder = "/Game/ConZ_Files/Items/Clothes/Backpacks_Bags/";
    private const string Grid = "EntityGridInventoryComponentSetup_0";

    [PaksFact]
    public async Task ABagsRoomAndLoadAreKeyValuesAndItsCloneHoldsMore()
    {
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Big bags");
        using var page = new ItemsPageViewModel(ctx.Services);
        await page.LoadCompletion;
        var game = ctx.Services.Workspace.Catalog!;

        // The Assault Backpack stores its grid: 14 x 8 cells, 50 kg; the weight limit is the code's.
        Assert.True(await page.SelectAsync("Military_Backpack_01_01"));
        Assert.True(await page.SelectPartAsync("Entity setup"));
        var assault = KeyRows(page);
        Assert.Equal(("14", "8", "50.000004", "true"), (assault["NumRows"].StockValue, assault["NumColumns"].StockValue, assault["MaxContainedWeight"].StockValue, assault["HasContainedWeightLimit"].StockValue));
        Assert.Equal(InheritedValues.GridInventory, assault["HasContainedWeightLimit"].Tunable.InheritedFrom);
        Assert.Null(assault["NumRows"].Tunable.InheritedFrom);
        Assert.Contains("Num rows", page.ValuesNote, StringComparison.Ordinal);

        // A colour variant stores none of it: its parent's grid (10 x 8, 40 kg) is listed, editable.
        Assert.True(await page.SelectAsync("Backpack_02_02"));
        Assert.True(await page.SelectPartAsync("Entity setup"));
        var variant = KeyRows(page);
        Assert.Equal(("10", "8", "40"), (variant["NumRows"].StockValue, variant["NumColumns"].StockValue, variant["MaxContainedWeight"].StockValue));
        Assert.All(new[] { "NumRows", "NumColumns", "MaxContainedWeight" }, n => Assert.Equal(Folder + "Backpack_02_01_ES.Backpack_02_01_ES_C", variant[n].Tunable.InheritedFrom));

        // Its clone: 255 x 10 cells (the most a row count holds) and 100 kg. 256 rows is an error and Apply records nothing.
        page.CloneName = "Backpack_Big";
        await page.CreateCloneAsync();
        Assert.True(await page.SelectPartAsync("Entity setup"));
        var recorded = project.State.AssetValueOverrides.Count;
        TooBig(page, "NumRows");
        Assert.Equal(recorded, project.State.AssetValueOverrides.Count);
        Set(page, "NumRows", "10", "255");
        Set(page, "NumColumns", "8", "10");
        Set(page, "MaxContainedWeight", "40", "100");
        page.ApplyChanges();

        // The Improvised Courier Backpack stores 6 x 6 and no weight: the game's code holds it to 10 kg. Its clone: 8 x 255
        // (a stored byte: 256 is an error there too), 25 kg.
        Assert.True(await page.SelectAsync("Improvised_Bag_Small_01"));
        page.CloneName = "Courier_Big";
        await page.CreateCloneAsync();
        Assert.True(await page.SelectPartAsync("Entity setup"));
        Assert.Null(page.FindRow("NumColumns")!.Tunable.InheritedFrom);
        TooBig(page, "NumColumns");
        Set(page, "NumRows", "6", "8");
        Set(page, "NumColumns", "6", "255");
        Set(page, "MaxContainedWeight", "10", "25");
        page.ApplyChanges();

        var result = await new ProjectExporter().ExportAsync(project, game, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("_Big", StringComparison.Ordinal));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var big = Export(written, Folder + "Backpack_Big_ES", Grid);
        Assert.Equal((255, 10, 100f), (big.GetOrDefault<byte>("NumRows"), big.GetOrDefault<byte>("NumColumns"), big.GetOrDefault<float>("MaxContainedWeight", -1f)));
        var courier = Export(written, Folder + "Courier_Big_ES", Grid);
        Assert.Equal((8, 255, 25f), (courier.GetOrDefault<byte>("NumRows"), courier.GetOrDefault<byte>("NumColumns"), courier.GetOrDefault<float>("MaxContainedWeight", -1f)));

        // The game's bags are not in the mod and keep their grids.
        foreach (var stock in new[] { "Backpack_02_02_ES", "Backpack_02_01_ES", "Improvised_Bag_Small_01_ES" })
        {
            Assert.False(written.PackageExists(Folder + stock), stock);
        }

        Assert.Equal(-1f, Export(game, Folder + "Improvised_Bag_Small_01_ES", Grid).GetOrDefault<float>("MaxContainedWeight", -1f));
        Assert.Equal(0, Export(game, Folder + "Backpack_02_02_ES", Grid).GetOrDefault<byte>("NumRows"));
    }

    private static Dictionary<string, TunableRowViewModel> KeyRows(ModulePageViewModel page) =>
        page.Groups.SelectMany(g => g.Rows).Where(r => r.Tunable.Export == Grid).ToDictionary(r => r.Tunable.Name);

    /// <summary>Sets the row <paramref name="name"/> of the selected part (checking its stock value).</summary>
    private static void Set(ModulePageViewModel page, string name, string stock, string value)
    {
        var row = page.FindRow(name);
        Assert.True(row is not null, $"{page.SelectedPart?.PackagePath} has no {name}");
        Assert.True(row!.Tunable.CanEdit, $"{name} is read-only");
        Assert.Equal(stock, row.StockValue);
        row.Value = value;
        Assert.Null(row.Error);
    }

    /// <summary>
    /// The row <paramref name="name"/> (a ByteProperty) shows an error for 256 and Apply records nothing; the row is reset.
    /// </summary>
    private static void TooBig(ModulePageViewModel page, string name)
    {
        var row = page.FindRow(name)!;
        Assert.Equal("ByteProperty", row.Tunable.TagType);
        var committed = row.CommittedValue;
        row.Value = "256";
        Assert.Equal(ScumStudio.App.Localization.Loc.F("Module.OutOfRange", "0", "255"), row.Error);
        page.ApplyChanges();
        Assert.Equal(committed, row.CommittedValue);
        row.Value = committed;
        Assert.False(row.IsPending);
    }

    private static UObject Export(AssetCatalog catalog, string package, string export) =>
        catalog.LoadPackage(package).GetExports().Single(e => e.Name == export);
}
