using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// CwHedgehog: "I want to change this weapon's MaxHealth. It seems to be inherited from a parent?" On the game files: the
/// katana's entity setup stores no MaxHealth (its parent is the game code's <c>ItemEntitySetup</c>), and <c>2H_Katana1</c>
/// is a Blueprint child of <c>2H_Katana</c> that stores none of the values it shares with it. A clone of 2H_Katana1 gets
/// both kinds written into its own defaults; the katana and its entity setup are not in the mod. Real game files only
/// (<c>SCUM_PAKS</c>, key from this PC's store); the game's reader (CUE4Parse) reads the result.
/// </summary>
public sealed class InheritedRealTests
{
    private const string Katana = "/Game/ConZ_Files/Items/Weapons/New_Melee/2H_Katana";

    [Fact]
    public async Task AKatana1CloneOverridesWhatItInherits()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        CookedPackage? Read(string path)
        {
            try
            {
                return ModdableAssets.ReadPackage(catalog, path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        var es = ModdableAssets.ReadPackage(catalog, Katana + "_ES");
        Assert.DoesNotContain(TunableReader.Read(es), t => t.Name == "MaxHealth");
        Assert.Equal("/Script/SCUM.ItemEntitySetup", Assert.Single(InheritedValues.Read(es, Read), t => t.Name == "MaxHealth").InheritedFrom);
        Assert.Contains(AssetEditing.ReadValues(catalog, null, Katana + "_ES"), t => t.Key == "Default__2H_Katana_ES_C|MaxHealth"); // what the CLI and MCP tools list and set

        // What 2H_Katana1 does not store it takes from 2H_Katana, with the katana's value.
        var katana = TunableReader.Read(ModdableAssets.ReadPackage(catalog, Katana)).Where(t => t.Export == "Default__2H_Katana_C").ToDictionary(t => t.Path, t => t.Value);
        var fromKatana = InheritedValues.Read(ModdableAssets.ReadPackage(catalog, Katana + "1"), Read).Where(t => t.InheritedFrom == Katana + ".2H_Katana_C").ToList();
        Assert.NotEmpty(fromKatana);
        Assert.All(fromKatana, t => Assert.Equal(katana[t.Path], t.Value));
        var inherited = fromKatana.First(t => t.Kind == TunableKind.Float);
        var changed = TunableValue.Format(TunableValue.ParseFloat(inherited.Value) * 2 + 1);

        var plan = CloneFamilyPlanner.PlanItem(catalog, Katana + "1", "HedgehogKatana1");
        var newEs = plan.Packages.Single(p => p.Key.EndsWith("/2H_Katana1_ES", StringComparison.OrdinalIgnoreCase)).Value;
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [plan.NewPrimary] = [new("Default__HedgehogKatana1_C", inherited.Path, changed)],
            [newEs] = [new("Default__HedgehogKatana1_ES_C", "MaxHealth", "750")],
        }));
        Assert.Empty(result.Warnings);
        Assert.Contains(result.Applied, a => a.Key == "Default__HedgehogKatana1_ES_C|MaxHealth" && a.New == "750");
        Assert.DoesNotContain(result.Packages, p => p.PackagePath.Equals(Katana, StringComparison.OrdinalIgnoreCase) || p.PackagePath.Equals(Katana + "_ES", StringComparison.OrdinalIgnoreCase));

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-inherited-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var built in result.Packages)
            {
                await built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]);
            }

            using var loose = AssetCatalog.OpenLoose(dir);
            var item = loose.LoadPackage(plan.NewPrimary).GetExports().Single(e => e.Name == "Default__HedgehogKatana1_C");
            Assert.Equal(TunableValue.ParseFloat(changed), item.GetOrDefault(inherited.Path, float.NaN));
            var setup = loose.LoadPackage(newEs).GetExports().Single(e => e.Name == "Default__HedgehogKatana1_ES_C");
            Assert.Equal(750f, setup.GetOrDefault("MaxHealth", float.NaN));

            // The katana keeps its own value in the game files.
            var stock = catalog.LoadPackage(Katana).GetExports().Single(e => e.Name == "Default__2H_Katana_C");
            Assert.Equal(TunableValue.ParseFloat(inherited.Value), stock.GetOrDefault(inherited.Path, float.NaN));
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
