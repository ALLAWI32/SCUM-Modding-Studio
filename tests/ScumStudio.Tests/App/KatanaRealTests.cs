using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Assets.Exports.Engine;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// CwHedgehog (Discord, 2026-10-16): he cloned a katana and set its "Damage" to 999, which did nothing in game (it is the
/// thrown damage), then wanted its MaxHealth, which the katana takes from a parent class. On the Weapons page the clone's
/// hit damage is the Hit damage part, the item's own damage is titled as thrown, and MaxHealth shows on the Entity setup
/// part as inherited and editable; the export writes both into the clone only. Real game files only (<c>SCUM_PAKS</c>, key
/// from this PC's store); the game's reader (CUE4Parse) reads the result.
/// </summary>
public sealed class KatanaRealTests
{
    private const string Katana = "/Game/ConZ_Files/Items/Weapons/New_Melee/2H_Katana";

    [AvaloniaFact]
    public async Task AKatanaCloneGetsItsOwnHitDamageAndMaxHealth()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Hedgehog katana");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.True(await weapons.SelectAsync("2H_Katana"));
            weapons.CloneName = "HedgehogKatana";
            await weapons.CreateCloneAsync();
            Assert.Equal("HedgehogKatana", weapons.SelectedItem!.Name);
            Assert.Equal(new[] { "Weapon", "Hit damage", "Entity setup" }, weapons.Parts.Take(3).Select(p => p.Label));

            // The item's own "Damage" is what a throw does: titled so, apart from the hit damage.
            Assert.True(await weapons.SelectPartAsync("Weapon"));
            var thrown = weapons.Groups.SelectMany(g => g.Rows).Single(r => r.Tunable.Path == "WeaponDesc.Damage");
            Assert.Equal("80", thrown.StockValue);
            Assert.StartsWith("When thrown, not the hit damage", weapons.Groups.Single(g => g.Rows.Contains(thrown)).Title, StringComparison.Ordinal);

            // The hit damage: the clone's own WeaponDesc_Table row, starting from the katana's.
            Assert.True(await weapons.SelectPartAsync("Hit damage"));
            var hit = weapons.FindRow("Damage")!;
            Assert.Equal(("Rows[HedgehogKatana].Damage", "150"), (hit.Tunable.Path, hit.StockValue));
            hit.Value = "999";
            weapons.ApplyChangesCommand.Execute(null);

            // MaxHealth: not in the katana's entity setup, inherited from the game's ItemEntitySetup; editable all the same.
            Assert.True(await weapons.SelectPartAsync("Entity setup"));
            var es = weapons.SelectedPart!.PackagePath;
            Assert.EndsWith("/HedgehogKatana_ES", es, StringComparison.Ordinal);
            var maxHealth = weapons.FindRow("MaxHealth")!;
            Assert.True(maxHealth.IsInherited && maxHealth.Tunable.CanEdit);
            Assert.Equal("inherited from ItemEntitySetup, editable", maxHealth.InheritedNote);
            Assert.Equal(("Default__HedgehogKatana_ES_C", string.Empty, "game default"), (maxHealth.Tunable.Export, maxHealth.StockValue, maxHealth.StockDisplay));
            Assert.Null(maxHealth.Error); // empty keeps the game's value
            Assert.Contains(weapons.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "MaxHealth"); // shown with the key stats
            maxHealth.Value = "500";
            weapons.ApplyChangesCommand.Execute(null);
            Assert.True(maxHealth.IsOverridden);
            HeadlessUi.SaveScreenshot(window, "module-weapon-inherited-maxhealth");

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("HedgehogKatana", StringComparison.Ordinal));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var table = written.LoadObject<UDataTable>(DataTableEdits.WeaponDescTable + ".WeaponDesc_Table");
            float Damage(string row) => table.RowMap.Single(r => r.Key.Text == row).Value.GetOrDefault<float>("Damage");
            Assert.Equal(999f, Damage("HedgehogKatana"));
            Assert.Equal(150f, Damage("2H_Katana")); // the stock katana hits as before

            var cdo = written.LoadPackage(es).GetExports().Single(e => e.Name == "Default__HedgehogKatana_ES_C");
            Assert.Equal(500f, cdo.GetOrDefault<float>("MaxHealth", -1f));
            Assert.Equal(1500f, cdo.GetOrDefault<float>("Weight", -1f)); // its stored values are still read after the new one
            Assert.NotNull(cdo.GetOrDefault<CUE4Parse.UE4.Objects.Core.i18N.FText>("Caption"));

            // The stock katana is not in the mod, and the game's copy still has no MaxHealth of its own.
            Assert.False(written.PackageExists(Katana + "_ES"));
            Assert.False(written.PackageExists(Katana));
            var stock = ctx.Services.Workspace.Catalog!.LoadPackage(Katana + "_ES").GetExports().Single(e => e.Name == "Default__2H_Katana_ES_C");
            Assert.Equal(-1f, stock.GetOrDefault<float>("MaxHealth", -1f));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
