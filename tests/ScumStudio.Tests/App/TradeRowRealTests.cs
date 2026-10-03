using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "a trader that sells my modded cars". A clone of a car the mechanic sells gets the car's row of the game's
/// tradeable table under its own class and name, with its own price; the stock car keeps its row. Real game files only.
/// </summary>
public sealed class TradeRowRealTests
{
    [AvaloniaFact]
    public async Task AClonedCarIsSoldByTheMechanicUnderItsOwnName()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Car dealer");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerGold";
            await vehicles.CreateCloneAsync();

            var trade = Assert.Single(vehicles.Parts, p => p.PackagePath == DataTableEdits.TradeableTable);
            Assert.Equal("BPC_RagerGold_C", trade.Row);
            Assert.Equal("BPC_Rager_C", trade.RowFrom);
            vehicles.SelectedPart = trade;
            await vehicles.ValuesCompletion;
            var price = vehicles.Groups.SelectMany(g => g.Rows).Single(r => r.Tunable.Name == "BasePurchasePrice");
            Assert.Equal("Rows[BPC_RagerGold_C].BasePurchasePrice", price.Tunable.Path);
            var stockPrice = price.Value;
            price.Value = "99999";
            vehicles.ApplyChangesCommand.Execute(null);

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var table = written.LoadObject<UDataTable>(DataTableEdits.TradeableTable + ".Table_TradeableDesc");
            var row = table.RowMap.Single(r => r.Key.Text == "BPC_RagerGold_C").Value;
            Assert.Equal("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_RagerGold.BPC_RagerGold_C", row.GetOrDefault<FSoftObjectPath>("TradeableClass").AssetPathName.Text);
            Assert.Equal("RagerGold", row.GetOrDefault<FText>("TradingEntryCaption")?.Text);
            Assert.Equal(99999, row.GetOrDefault<int>("BasePurchasePrice"));
            var stock = table.RowMap.Single(r => r.Key.Text == "BPC_Rager_C").Value;
            Assert.Equal(int.Parse(stockPrice, System.Globalization.CultureInfo.InvariantCulture), stock.GetOrDefault<int>("BasePurchasePrice"));
            Assert.EndsWith("BPC_Rager.BPC_Rager_C", stock.GetOrDefault<FSoftObjectPath>("TradeableClass").AssetPathName.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
