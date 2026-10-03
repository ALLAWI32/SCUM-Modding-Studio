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
/// Owner: "make the knife strong, as a new weapon with its own spawn command". The Weapons page lists the melee weapons,
/// shows a knife's hit damage (its WeaponDesc_Table row), and a clone's damage goes to a row of its own in the mod.
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class MeleeRealTests
{
    [AvaloniaFact]
    public async Task AKnifeCloneGetsItsOwnHitDamage()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Strong knife");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.Contains(weapons.Filters, f => f.Category == "Melee");
            Assert.True(await weapons.SelectAsync("1H_KitchenKnife"));
            weapons.CloneName = "StrongKnife";
            await weapons.CreateCloneAsync();
            Assert.Equal("StrongKnife", weapons.SelectedItem!.Name);

            var damagePart = Assert.Single(weapons.Parts, p => p.Row is not null);
            Assert.Equal("1H_KitchenKnife", damagePart.RowFrom);
            weapons.SelectedPart = damagePart;
            await weapons.ValuesCompletion;
            var damage = weapons.Groups.SelectMany(g => g.Rows).Single(r => r.Tunable.Name == "Damage");
            Assert.Equal("Rows[StrongKnife].Damage", damage.Tunable.Path);
            Assert.Equal("25", damage.Value);
            damage.Value = "150";
            weapons.ApplyChangesCommand.Execute(null);

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var table = written.LoadObject<UDataTable>(DataTableEdits.WeaponDescTable + ".WeaponDesc_Table");
            float Damage(string row) => table.RowMap.Single(r => r.Key.Text == row).Value.GetOrDefault<float>("Damage");
            Assert.Equal(150f, Damage("StrongKnife"));
            Assert.Equal(25f, Damage("1H_KitchenKnife")); // the stock knife stays
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
