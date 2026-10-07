using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Economy page in the app window: a trader placed from the Map's Add object box, "Edit stock" opens its section, a price
/// typed in the row's box lands in the project's EconomyOverride.json, a category goes off sale, Export writes the file for
/// the server; a placed trader's section is there at once with its stock and goes with the trader; items of other traders
/// are added and removed; a deleted outpost's traders leave the page and the exported file. Real game files only
/// (<c>SCUM_PAKS</c>); pictures to <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class EconomyPageRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task APlacedTradersPriceIsEditedOnTheEconomyPage()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ctx.Combine("exports") });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Economy");
            await map.LoadLevelsAsync([Farm]);
            var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(800f, 0f, 0f);
            map.AimPointProvider = () => aim;
            HeadlessUi.Pump();

            // Add object → Trader: the box fills its defaults from the cell when it opens; Place trader adds it.
            var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddObjectButton");
            add.Flyout!.ShowAt(add);
            HeadlessUi.Pump();
            var panel = (Control)((Flyout)add.Flyout).Content!;
            var types = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TraderTypeBox");
            types.SelectedItem = map.TraderTypes.First(t => t.Type == "Armorer");
            HeadlessUi.Pump();
            Assert.Equal("A_3_Armory_1", panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TraderNameBox").Text);
            Assert.Equal("Outpost_A_3", panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TraderOutpostBox").Text);
            HeadlessUi.SaveScreenshot(window, "economy-add-trader");
            panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PlaceTraderButton").Command!.Execute(null);
            Assert.True(await map.AddCompletion);
            add.Flyout.Hide();
            HeadlessUi.Pump();

            // The trader is selected: "Edit stock" opens the Economy page at its section.
            var editStock = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EditStockButton");
            Assert.True(editStock.IsVisible);
            editStock.Command!.Execute(null);
            var economy = Assert.IsType<EconomyPageViewModel>(vm.CurrentPage);
            Assert.True(HeadlessUi.PumpUntil(() => economy.SelectedTrader?.Name == "A_3_Armory_1" && economy.Items.Count > 0, TimeSpan.FromSeconds(60)));
            Assert.True(economy.SelectedTrader!.IsPlaced);
            Assert.Contains(economy.Traders, t => t.Name == "A_0_Armory" && !t.IsPlaced); // the game's traders are there too
            Assert.Contains(economy.Items, i => i.Code == "Weapon_AK47");

            // A price typed into the AK-47's row.
            economy.FilterText = "AK-47";
            Assert.True(HeadlessUi.PumpUntil(() => window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "BuyBox" && t.DataContext is EconomyItemViewModel { Code: "Weapon_AK47" })));
            var buy = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "BuyBox" && t.DataContext is EconomyItemViewModel { Code: "Weapon_AK47" });
            Assert.Equal("11200", buy.Text); // the game's price
            buy.Text = "12345";
            HeadlessUi.Pump();
            Assert.Equal(12345, economy.Economy.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
            var saved = EconomyOverride.LoadFrom(ctx.Services.Projects.Current!.DirectoryPath)!;
            Assert.Equal(12345, saved.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
            HeadlessUi.SaveScreenshot(window, "economy-price");

            // A whole category off the shelf, then +10 % on what is shown.
            economy.FilterText = string.Empty;
            economy.SelectedCategory = economy.Categories.Single(c => c.Key == "Explosives");
            HeadlessUi.Pump();
            economy.SetOnSaleCommand.Execute("false");
            Assert.All(economy.Items.Where(i => !i.IsLocked), i => Assert.False(i.CanBePurchased));
            var grenade = economy.Items.First(i => !i.IsLocked && i.Default.CanBePurchased);
            Assert.False(economy.Economy.Find("A_3_Armory_1", grenade.Code)!.CanBePurchased);
            economy.ScalePricesCommand.Execute("1.1");
            Assert.Equal((int)Math.Round(grenade.Default.PurchasePrice * 1.1), economy.Economy.Find("A_3_Armory_1", grenade.Code)!.PurchasePrice);

            // Export: the file for the server next to the paks.
            economy.ExportCommand.Execute(null);
            Assert.Equal(Path.Combine(ctx.Combine("exports"), "Server", EconomyOverride.FileName), economy.LastExportPath);
            var written = EconomyOverride.Parse(File.ReadAllText(economy.LastExportPath!));
            Assert.Equal(12345, written.Find("A_3_Armory_1", "Weapon_AK47")?.PurchasePrice);
            Assert.True(File.Exists(Path.Combine(ctx.Combine("exports"), "Client", EconomyOverride.FileName)));
            HeadlessUi.SaveScreenshot(window, "economy-category");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task APlacedTradersStockIsReadyGetsItemsAddedAndRemovedAndLeavesWithTheTrader()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ctx.Combine("exports") });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stock");
            await map.LoadLevelsAsync([Farm]);
            var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(800f, 0f, 0f);
            map.AimPointProvider = () => aim;
            var store = ctx.Services.Economy;
            var defaults = (await store.LoadDefaultsAsync())!;
            Assert.True(defaults.Tradeables.Count > 2500);
            var stock = defaults.StockCodes("Armorer").ToList();

            // Placed: the section is there at once, listing the armory's stock with the game's values ("-1" / "default").
            Assert.True(await map.AddTraderAsync("Armorer"));
            const string name = "A_3_Armory_1";
            Assert.True(HeadlessUi.PumpUntil(() => store.Economy.Entries(name).Count == stock.Count, TimeSpan.FromSeconds(60)));
            Assert.All(store.Economy.Entries(name), e => Assert.True(e.IsDefault));
            Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Economy ready");
            Assert.Equal(stock.Count, EconomyOverride.LoadFrom(project.DirectoryPath)!.Entries(name).Count);

            // The page shows it with the trade menu's pictures.
            vm.OpenEconomy(name);
            var economy = Assert.IsType<EconomyPageViewModel>(vm.CurrentPage);
            Assert.True(HeadlessUi.PumpUntil(() => economy.SelectedTrader?.Name == name && economy.Items.Count > 0, TimeSpan.FromSeconds(60)));
            Assert.Equal(("A_3", true), (economy.SelectedTrader!.Cell, economy.IsPlacedTrader));
            Assert.Equal(defaults.SoldBy("Armorer").Count(), economy.Items.Count);
            Assert.True(HeadlessUi.PumpUntil(() => economy.Items.Take(6).Count(i => i.Thumbnail is not null) >= 3, TimeSpan.FromSeconds(60)));
            HeadlessUi.SaveScreenshot(window, "economy-stock-pictures");

            // Add items: two of other traders (a saloon's and a hospital's), ticked in the box and added in one go.
            economy.ToggleAddCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.True(economy.IsAddOpen);
            Assert.DoesNotContain(economy.AddItems, p => p.Default.IsSoldBy("Armorer"));
            var drink = economy.AddItems.First(p => p.Default.IsSoldBy("Bartender") && p.Default.IconPath is not null);
            var medicine = economy.AddItems.First(p => p.Default.IsSoldBy("Doctor") && p.Default.IconPath is not null);
            drink.IsChecked = true;
            medicine.IsChecked = true;
            Assert.Equal(2, economy.AddCheckedCount);
            economy.AddFilterText = drink.Code;
            Assert.True(HeadlessUi.PumpUntil(() => drink.Thumbnail is not null, TimeSpan.FromSeconds(60)));
            HeadlessUi.SaveScreenshot(window, "economy-add-items");
            economy.AddCheckedCommand.Execute(null);
            Assert.Equal(drink.Default.AddedEntry(), store.Economy.Find(name, drink.Code));
            Assert.Equal(medicine.Default.AddedEntry(), store.Economy.Find(name, medicine.Code));
            Assert.True(economy.Items.Take(2).All(i => i.IsAdded)); // added items lead the list
            Assert.Equal("2 added", economy.SelectedTrader!.ChangesText);
            economy.ToggleAddCommand.Execute(null);

            // Remove: the added drink goes; the AK-47 of the armory's own stock goes off sale.
            economy.RemoveItem(economy.Items.Single(i => i.Code == drink.Code));
            Assert.Null(store.Economy.Find(name, drink.Code));
            Assert.DoesNotContain(economy.Items, i => i.Code == drink.Code);
            var ak = economy.Items.Single(i => i.Code == "Weapon_AK47");
            economy.RemoveItem(ak);
            Assert.True(ak.IsOffSale && ak.CanPutBack);
            Assert.Equal(new TradeableOverride("Weapon_AK47") { CanBePurchased = false }, store.Economy.Find(name, "Weapon_AK47"));
            economy.FilterText = "AK";
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "economy-removed");
            economy.FilterText = string.Empty;
            var file = EconomyOverride.LoadFrom(project.DirectoryPath)!;
            Assert.Equal(medicine.Default.AddedEntry(), file.Find(name, medicine.Code));
            Assert.False(file.Find(name, "Weapon_AK47")!.CanBePurchased);
            Assert.Equal(stock.Count + 1, file.Entries(name).Count);

            // The export carries the same section.
            var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            var exported = EconomyOverride.Parse(File.ReadAllText(result.EconomyPath!));
            Assert.Equal(file.Entries(name), exported.Entries(name));

            // The trader deleted on the map: its section leaves the project's economy, the page and the export.
            map.SelectedActor = map.AllActors.Single(a => a.IsAdded && project.State.AddedActors.GetValueOrDefault(a.Reference) is AddBlueprintActorOp { Trader.Name: name });
            map.DeleteSelectedCommand.Execute(null);
            Assert.True(HeadlessUi.PumpUntil(() => !store.Economy.HasSection(name) && economy.Traders.All(t => t.Name != name)));
            Assert.False(EconomyOverride.LoadFrom(project.DirectoryPath)!.HasSection(name));
            economy.ExportCommand.Execute(null);
            Assert.False(EconomyOverride.Parse(File.ReadAllText(economy.LastExportPath!)).HasSection(name));

            // Undone: it is back with its edits.
            ctx.Services.Projects.Undo();
            Assert.True(HeadlessUi.PumpUntil(() => store.Economy.HasSection(name) && economy.Traders.Any(t => t.Name == name)));
            Assert.Equal(medicine.Default.AddedEntry(), store.Economy.Find(name, medicine.Code));
            Assert.False(store.Economy.Find(name, "Weapon_AK47")!.CanBePurchased);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ADeletedOutpostsTradersLeaveThePageAndTheExportAndAPlacedHospitalIsNumbered()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ctx.Combine("exports") });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Outpost");
            await map.LoadLevelsAsync([Outpost]);
            var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(500f, 500f, 0f);
            map.AimPointProvider = () => aim;
            var store = ctx.Services.Economy;

            // B_4 has the game's hospital: a placed doctor is B_4_Hospital_2.
            Assert.True(await map.AddTraderAsync("Doctor"));
            Assert.Equal("B_4_Hospital_2", Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op).Trader!.Name);

            // The game's armory gets a price of its own.
            vm.OpenEconomy("B_4_Armory");
            var economy = Assert.IsType<EconomyPageViewModel>(vm.CurrentPage);
            Assert.True(HeadlessUi.PumpUntil(() => economy.SelectedTrader?.Name == "B_4_Armory" && economy.Items.Count > 0, TimeSpan.FromSeconds(60)));
            economy.Items.Single(i => i.Code == "Weapon_AK47").PurchasePrice = 999;
            Assert.Equal(999, store.Economy.Find("B_4_Armory", "Weapon_AK47")!.PurchasePrice);

            // The whole outpost's trade posts deleted in one step: B_4's traders leave the page (its hunters, in their grotto, stay).
            var posts = map.AllActors.Where(a => !a.IsAdded && a.Actor.TraderMarkers.Any(m => m.PersonalityPath.Length > 0)).Select(a => (EditOp)new DeleteActorOp(a.Reference)).ToList();
            Assert.True(posts.Count >= 7);
            ctx.Services.Projects.Apply(new BatchOp("Delete B_4's traders", posts));
            Assert.True(HeadlessUi.PumpUntil(() => store.RemovedStock.Contains("B_4_Armory"), TimeSpan.FromSeconds(120)));
            Assert.True(HeadlessUi.PumpUntil(() => economy.Traders.All(t => t.Name != "B_4_Armory")));
            Assert.DoesNotContain(economy.Traders, t => t.Name is "B_4_Trader" or "B_4_Hospital" or "B_4_Saloon" or "B_4_Mechanic");
            Assert.Contains(economy.Traders, t => t.Name == "B_4_Hospital_2" && t.IsPlaced);
            Assert.Contains(economy.Traders, t => t.Name == "B_4_Hunter");
            Assert.Contains(economy.Traders, t => t.Name == "A_0_Armory");
            HeadlessUi.SaveScreenshot(window, "economy-outpost-deleted");

            // The export leaves their sections out; the project keeps the armory's edit for an undo.
            var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            var exported = EconomyOverride.Parse(File.ReadAllText(result.EconomyPath!));
            Assert.False(exported.HasSection("B_4_Armory"));
            Assert.True(exported.HasSection("B_4_Hospital_2"));
            Assert.Contains("B_4_Armory", result.RemovedTraders);
            Assert.Contains("Left out (their trade posts are deleted): ", File.ReadAllText(result.ReportPath!), StringComparison.Ordinal);
            Assert.True(EconomyOverride.LoadFrom(project.DirectoryPath)!.HasSection("B_4_Armory"));
            economy.ExportCommand.Execute(null);
            Assert.False(EconomyOverride.Parse(File.ReadAllText(economy.LastExportPath!)).HasSection("B_4_Armory"));

            // Undone: the armory is back with its price.
            ctx.Services.Projects.Undo();
            Assert.True(HeadlessUi.PumpUntil(() => store.RemovedStock.Count == 0 && economy.Traders.Any(t => t.Name == "B_4_Armory")));
            Assert.Equal(999, store.Economy.Find("B_4_Armory", "Weapon_AK47")!.PurchasePrice);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
