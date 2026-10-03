using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Vehicles and Weapons pages over the stock packages of the fixture archive (<c>orig/</c>): list, values of the
/// selected asset and its parts, apply through the project journal, undo, clone with an in-game name, remove clone.
/// Headless screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class ModulePagesTests
{
    [Fact]
    public void PropertyNamesAreReadable()
    {
        Assert.Equal("Damage per shot", TunableRowViewModel.Humanize("DamagePerShot"));
        Assert.Equal("Max push force", TunableRowViewModel.Humanize("_maxPushForce"));
        Assert.Equal("Auto brake", TunableRowViewModel.Humanize("bAutoBrake"));
        Assert.Equal("Max RPM", TunableRowViewModel.Humanize("MaxRPM"));
        Assert.Equal("ROF", TunableRowViewModel.Humanize("ROF"));
        Assert.Equal("EM interference max disabled time", TunableRowViewModel.Humanize("_EMInterferenceMaxDisabledTime"));
        Assert.Null(TunableRowViewModel.Validate(ScumStudio.Modding.Tuning.TunableKind.Float, "2,5", []));
        Assert.NotNull(TunableRowViewModel.Validate(ScumStudio.Modding.Tuning.TunableKind.Int, "2.5", []));
        Assert.Equal("2.5", TunableRowViewModel.Normalize(ScumStudio.Modding.Tuning.TunableKind.Float, "2,5"));
    }

    [FixturesFact]
    public async Task WeaponsPageTunesClonesAndUndoes()
    {
        using var ctx = AppTestContext.Create();
        using var page = new WeaponsPageViewModel(ctx.Services);
        Assert.True(page.ShowEmptyState);
        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));
        Assert.False(page.ShowEmptyState);
        Assert.Contains(page.AllItems, i => i.Name == "Weapon_RPK-74");
        Assert.Contains(page.AllItems, i => i.Asset.Kind == ModdableKind.Magazine);
        Assert.Contains(page.AllItems, i => i.Asset.Kind == ModdableKind.Ammo);
        Assert.DoesNotContain(page.AllItems, i => i.Asset.Kind == ModdableKind.Vehicle);
        Assert.Equal(["All", "Weapons", "Magazines", "Ammunition", "Projectiles"], page.Filters.Select(f => f.Label));
        page.SelectedFilter = page.Filters[2];
        Assert.All(page.Items, i => Assert.Equal(ModdableKind.Magazine, i.Asset.Kind));

        Assert.True(await page.SelectAsync("Weapon_RPG7"));
        Assert.Equal(["Weapon", "Entity setup"], page.Parts.Select(p => p.Label));
        Assert.Equal("#SpawnItem Weapon_RPG7", page.SpawnCommandText);
        var damage = page.FindRow("DamagePerShot")!;
        Assert.Equal("2.664", damage.StockValue);
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "DamagePerShot");
        Assert.DoesNotContain(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "CapsuleRadius"); // key stats only
        page.KeyStatsOnly = false;
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "CapsuleRadius");

        // Without a project nothing is recorded.
        damage.Value = "5";
        Assert.Equal(1, page.PendingCount);
        page.ApplyChanges();
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "No project open");

        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Guns");
        damage.Value = "abc";
        page.ApplyChanges();
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Invalid value");
        damage.Value = "5";
        page.ApplyChanges();
        var set = Assert.IsType<SetAssetValueOp>(Assert.Single(ctx.Services.Projects.Current!.Journal.Applied).Op);
        Assert.Equal(("Weapon_RPG7", "DamagePerShot", "2.664", "5"), (ModuleItemViewModel.ShortName(set.Package), set.Path, set.Old, set.New));
        Assert.Equal(0, page.PendingCount);
        Assert.True(damage.IsOverridden);
        Assert.Equal(1, page.AllItems.Single(i => i.Name == "Weapon_RPG7").EditCount);

        ctx.Services.Projects.Undo();
        Assert.Equal("2.664", damage.CommittedValue);
        Assert.False(damage.IsOverridden);
        ctx.Services.Projects.Redo();
        Assert.Equal("5", damage.CommittedValue);

        // Clone the RPK-74 with an in-game name.
        Assert.True(await page.SelectAsync("Weapon_RPK-74"));
        await page.PreviewCompletion;
        Assert.NotNull(page.Preview);
        Assert.Contains("SK_RPK-74", page.Preview!.Parts[0].Mesh.Name, StringComparison.Ordinal);
        Assert.False(page.CreateCloneCommand.CanExecute(null));
        page.CloneName = "Weapon RPK";
        Assert.NotNull(page.CloneNameError);
        page.CloneName = "Weapon_RPK-74_Gold";
        page.CloneCaption = "RPK Gold";
        Assert.Contains("#SpawnItem Weapon_RPK-74_Gold", page.ClonePreview, StringComparison.Ordinal);
        Assert.True(page.CreateCloneCommand.CanExecute(null));
        await page.CreateCloneAsync();
        var clone = Assert.Single(page.AllItems, i => i.IsClone);
        Assert.Equal("Weapon_RPK-74_Gold", clone.Name);
        Assert.Same(clone, page.SelectedItem);
        Assert.Equal("RPK Gold", page.Caption);
        Assert.Equal("#SpawnItem Weapon_RPK-74_Gold", page.SpawnCommandText);
        var state = ctx.Services.Projects.Current!.State;
        Assert.Single(state.AssetClones);
        Assert.Equal("RPK Gold", state.AssetValueOverrides.Single(v => v.Path == "Caption").Current);
        Assert.True(await page.SelectPartAsync("Weapon"));
        Assert.Equal("75", page.FindRow("EventMaxAmmo")!.StockValue); // values of the in-memory clone

        await page.RemoveCloneAsync();
        Assert.Empty(state.AssetClones);
        Assert.DoesNotContain(page.AllItems, i => i.IsClone);
        Assert.Single(state.AssetValueOverrides); // the RPG-7 edit stays
    }

    [FixturesFact]
    public async Task VehiclesPageShowsPartsAndClonesTheFamily()
    {
        using var ctx = AppTestContext.Create();
        using var page = new VehiclesPageViewModel(ctx.Services);
        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));
        Assert.Contains(page.Filters, f => f.Label == "Car");
        Assert.Contains(page.Filters, f => f.Label == "Airplane");
        Assert.True(await page.SelectAsync("BPC_WolfsWagen"));
        Assert.Equal("#SpawnVehicle BPC_WolfsWagen", page.SpawnCommandText);
        await page.PreviewCompletion;
        Assert.NotNull(page.Preview);
        Assert.True(page.Preview!.Parts.Count > 1, page.PreviewText); // chassis + attachments
        Assert.False(page.IsPreviewLoading);
        Assert.Equal("Vehicle", page.Parts[0].Label);
        Assert.Equal("Entity setup", page.Parts[1].Label);
        Assert.Contains(page.Parts, p => p.Label == "Part: Chassis");
        Assert.Equal("385", page.FindRow("ChassisMass")!.StockValue);
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "MaxRPM");

        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Cars");
        page.FindRow("ChassisMass")!.Value = "700";
        page.ApplyChanges();
        page.CloneName = "Hunter";
        page.IncludeSpawnPresets = false;
        await page.CreateCloneAsync();
        var clone = Assert.Single(page.AllItems, i => i.IsClone);
        Assert.Equal("BPC_Hunter", clone.Name);
        Assert.Equal("#SpawnVehicle BPC_Hunter", page.SpawnCommandText);
        Assert.Contains(page.Parts, p => p.Label == "Part: Chassis" && p.PackagePath.EndsWith("/BPC_Hunter_Chassis", StringComparison.Ordinal));
        var op = Assert.Single(ctx.Services.Projects.Current!.State.AssetClones);
        Assert.DoesNotContain(op.Packages, p => p.New.Contains("SpawningPresets", StringComparison.Ordinal));
        Assert.Equal("385", page.FindRow("ChassisMass")!.StockValue); // a clone starts from the stock family
    }

    [AvaloniaFixturesFact]
    public async Task ModulePagesRenderWithTheStockFiles()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Showcase");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            Assert.True(await weapons.OpenLooseFolderAsync(FixturePaths.OrigRoot));
            Assert.True(HeadlessUi.PumpUntil(() => HeadlessUi.Find<ModulePageView>(window).Any()));
            Assert.True(await weapons.SelectAsync("Weapon_RPG7"));
            weapons.FindRow("MaxRange")!.Value = "800";
            weapons.ApplyChanges();
            weapons.FindRow("DamagePerShot")!.Value = "4.5";
            weapons.CloneName = "Weapon_RPG7_Heavy";
            HeadlessUi.Pump();
            Assert.Contains(HeadlessUi.Find<Avalonia.Controls.TextBlock>(window), t => t.Text == "Damage per shot");
            HeadlessUi.SaveScreenshot(window, "module-weapons");

            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_WolfsWagen"));
            vehicles.FindRow("ChassisMass")!.Value = "700";
            vehicles.CloneName = "Hunter";
            HeadlessUi.Pump();
            Assert.Contains(HeadlessUi.Find<Avalonia.Controls.TextBlock>(window), t => t.Text == "Chassis mass");
            HeadlessUi.SaveScreenshot(window, "module-vehicles");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [FixturesFact]
    public async Task WeaponsPageContextMenuCommandsAndInventoryIcons()
    {
        var dialogs = new ScriptedDialogService();
        using var ctx = AppTestContext.Create(dialogs: dialogs);
        using var page = new WeaponsPageViewModel(ctx.Services);
        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));
        Assert.True(await page.SelectAsync("Weapon_RPK-74"));
        Assert.False(page.ShowPreviewTab);

        // The list's context menu: View in 3D, Copy spawn command, Clone…
        page.ViewIn3DCommand.Execute(null);
        Assert.True(page.ShowPreviewTab);
        await page.CopySpawnCommandCommand.ExecuteAsync(null);
        Assert.Equal("#SpawnItem Weapon_RPK-74", Assert.Single(dialogs.Clipboard));
        var cloneRequests = 0;
        page.CloneRequested += (_, _) => cloneRequests++;
        page.CloneItemCommand.Execute(null);
        Assert.False(page.ShowPreviewTab);
        Assert.Equal(1, cloneRequests);

        // Search ranks names that start with the word first (ammunition is Cal_*).
        page.SearchText = "cal";
        Assert.NotEmpty(page.Items);
        Assert.StartsWith("Cal_", page.Items[0].Name, StringComparison.Ordinal);

        // The entity setup names the inventory icon (the ICO_ texture itself is not in the fixtures).
        var icon = InventoryIcons.FindIconPath(ctx.Services.Workspace.Catalog!, page.SelectedItem!.Asset.EntitySetupPath);
        Assert.EndsWith("/ICO_RPK74_Inventory.ICO_RPK74_Inventory", icon, StringComparison.Ordinal);
        Assert.Null(InventoryIcons.FindIconPath(ctx.Services.Workspace.Catalog!, null));
    }
}
