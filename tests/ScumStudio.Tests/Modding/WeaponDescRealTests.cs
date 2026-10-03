using CUE4Parse.UE4.Assets.Exports.Engine;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner: "a knife I make strong, under its own name". A melee weapon's hit damage is the WeaponDesc_Table row named
/// after it: the table's rows read as values, and a clone gets its template's row under its own name, with its own damage.
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store); the game's reader (CUE4Parse) reads the result.
/// </summary>
public sealed class WeaponDescRealTests
{
    private const string Knife = "/Game/ConZ_Files/Items/Weapons/New_Melee/1H_KitchenKnife";

    [Fact]
    public async Task AClonedKnifeGetsADamageRowOfItsOwn()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        Assert.True(ModdableAssets.IsMelee(Knife));
        var table = ModdableAssets.ReadPackage(catalog, DataTableEdits.WeaponDescTable);
        var damage = Assert.Single(TunableReader.Read(table), t => t.Path == DataTableRows.PathOf("1H_KitchenKnife", "Damage"));
        Assert.Equal("25", damage.Value);
        var rows = DataTableRows.Read(table, 0).Count;

        var plan = CloneFamilyPlanner.PlanItem(catalog, Knife, "StrongKnife");
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [DataTableEdits.WeaponDescTable] = [new(damage.Export, DataTableRows.PathOf("StrongKnife", "Damage"), "200")],
        }));
        Assert.Empty(result.Warnings);

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-melee-" + Guid.NewGuid().ToString("N"));
        try
        {
            var built = Assert.Single(result.Packages, p => p.PackagePath == DataTableEdits.WeaponDescTable);
            Assert.False(built.IsClone);
            await built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]);
            using var loose = AssetCatalog.OpenLoose(dir);
            var read = loose.LoadObject<UDataTable>(DataTableEdits.WeaponDescTable + ".WeaponDesc_Table");
            Assert.Equal(rows + 1, read.RowMap.Count);
            float Damage(string row) => read.RowMap.Single(r => r.Key.Text == row).Value.GetOrDefault<float>("Damage");
            Assert.Equal(25f, Damage("1H_KitchenKnife"));
            Assert.Equal(200f, Damage("StrongKnife"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
