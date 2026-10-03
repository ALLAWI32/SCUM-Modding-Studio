using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Fixtures;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Vehicle/weapon modding library: rename map, rename-clone (payloads verbatim), stored-value read/patch, clone family
/// planning and registry registration. Real-data tests use the stock packages of the fixture archive
/// (<c>orig/</c> + <c>orig_extra/</c>: RPK-74, RPG-7, magazines, ammo, WolfsWagen, Kinglet planes, AssetRegistry.bin).
/// </summary>
public sealed class ModdingTests(ITestOutputHelper output)
{
    private const string Weapons = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/";
    private const string Rpk = Weapons + "Weapon_RPK-74";
    private const string Rpg = Weapons + "Weapon_RPG7";
    private const string WolfsWagen = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen";

    private static AssetCatalog OpenStock() =>
        AssetCatalog.OpenLoose(FixturePaths.OrigRoot, new AssetCatalogOptions { LooseOverlays = [FixturePaths.OrigExtraRoot] });

    [Fact]
    public void PackageMapRemapsPathsObjectsAndText()
    {
        var map = new PackageMap([new(Rpk, Weapons + "Weapon_RPK-74_Gold"), new(Rpk + "_ES", Weapons + "Weapon_RPK-74_Gold_ES")]);
        Assert.Equal(Weapons + "Weapon_RPK-74_Gold", map.RemapName(Rpk));
        Assert.Equal(Weapons + "Weapon_RPK-74_Gold.Weapon_RPK-74_Gold_C", map.RemapName(Rpk + ".Weapon_RPK-74_C"));
        Assert.Equal("Default__Weapon_RPK-74_Gold_C", map.RemapObject("Default__Weapon_RPK-74_C"));
        Assert.Equal("Weapon_RPK-74_Gold_ES_C", map.RemapObject("Weapon_RPK-74_ES_C"));
        Assert.Equal("Weapon_RPK-74_Gold_3", map.RemapObject("Weapon_RPK-74_3"));
        Assert.Equal("Chop_Weapon_RPK-74", map.RemapObject("Chop_Weapon_RPK-74")); // only exact leaves are renamed
        Assert.Equal("/Game/Other/Thing", map.RemapName("/Game/Other/Thing"));
        Assert.Equal($"Class'{Weapons}Weapon_RPK-74_Gold.Weapon_RPK-74_Gold_C'", map.RemapText($"Class'{Rpk}.Weapon_RPK-74_C'"));
        Assert.Equal("Weapon_RPK-74_Gold", map.RemapText("Weapon_RPK-74"));
        Assert.Throws<ArgumentException>(() => new PackageMap([new(Rpk, "/Game/A"), new(Rpk, "/Game/B")]));
    }

    [Fact]
    public void LeafRenamingFollowsTheTokenOrItsSquashedForm()
    {
        Assert.Equal("Hunter_ES", CloneFamilyPlanner.RenameLeaf("WolfsWagen_ES", "WolfsWagen", "Hunter"));
        Assert.Equal("Hunter_Item_Container", CloneFamilyPlanner.RenameLeaf("Wolfswagen_Item_Container", "WolfsWagen", "Hunter"));
        Assert.Equal("SeaHawkManualSpawnPreset", CloneFamilyPlanner.RenameLeaf("KingletMarinerManualSpawnPreset", "Kinglet_Mariner", "Sea_Hawk"));
        Assert.Equal("Hunter_Misc", CloneFamilyPlanner.RenameLeaf("Misc", "WolfsWagen", "Hunter"));
        Assert.True(CloneFamilyPlanner.IsValidName("Weapon_RPK-74_Gold"));
        Assert.False(CloneFamilyPlanner.IsValidName("1abc"));
        Assert.False(CloneFamilyPlanner.IsValidName("a b"));
        Assert.False(CloneFamilyPlanner.IsValidName("x/y"));
    }

    [Fact]
    public void AssetsAreClassifiedByFolderConventions()
    {
        Assert.Equal(ModdableKind.Weapon, ModdableAssets.Classify(Rpk)?.Kind);
        Assert.Null(ModdableAssets.Classify(Rpk + "_ES"));
        Assert.Equal(ModdableKind.Vehicle, ModdableAssets.Classify(WolfsWagen)?.Kind);
        Assert.Equal("Car", ModdableAssets.Classify(WolfsWagen)?.Category);
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/Attachments/BPC_WolfsWagen_Chassis"));
        Assert.Equal(ModdableKind.Magazine, ModdableAssets.Classify("/Game/ConZ_Files/Items/Weapons/Weapon_Clips/Magazine_AK47")?.Kind);
        Assert.Equal(ModdableKind.Ammo, ModdableAssets.Classify("/Game/ConZ_Files/Items/Ammunition/Cal_7_62x39mm")?.Kind);
        Assert.Equal(ModdableKind.Projectile, ModdableAssets.Classify("/Game/ConZ_Files/Items/Ammunition/Ammunition_Class/BP_WeaponBullet_762x39FMJ")?.Kind);
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Models/Weapons/Ranged_Weapons/Rifles/RPG/SK_RPG7"));
        Assert.Equal("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/WolfsWagen_ES", ModdableAssets.EntitySetupCandidate(ModdableAssets.Classify(WolfsWagen)!));
    }

    [Fact]
    public void InvariantTextEncodingMatchesTheOwnersSetDisplayName()
    {
        // struct.pack('<Ibi', 0, -1, 1) + int32 len + latin-1 + NUL
        var bytes = TunablePatcher.EncodeInvariantText("Hunter");
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0xFF, 1, 0, 0, 0, 7, 0, 0, 0, (byte)'H', (byte)'u', (byte)'n', (byte)'t', (byte)'e', (byte)'r', 0 }, bytes);
        var wide = TunablePatcher.EncodeInvariantText("صقر");
        Assert.Equal(-4, BitConverter.ToInt32(wide, 9));
        Assert.Equal(9 + 4 + 8, wide.Length);
    }

    [FixturesFact]
    public void StockCatalogListsVehiclesWeaponsMagazinesAndAmmo()
    {
        using var catalog = OpenStock();
        var assets = ModdableAssets.Find(catalog);
        Assert.Contains(assets, a => a.Kind == ModdableKind.Weapon && a.PackagePath == Rpk && a.EntitySetupPath == Rpk + "_ES");
        Assert.Contains(assets, a => a.Kind == ModdableKind.Vehicle && a.PackagePath == WolfsWagen && a.EntitySetupPath is not null);
        Assert.Contains(assets, a => a.Kind == ModdableKind.Vehicle && a.Name == "BPC_Kinglet_Mariner" && a.Category == "Airplane");
        Assert.Contains(assets, a => a.Kind == ModdableKind.Magazine);
        Assert.Contains(assets, a => a.Kind == ModdableKind.Ammo);
        Assert.Contains(assets, a => a.Kind == ModdableKind.Projectile);
        output.WriteLine(string.Join('\n', assets.Select(a => $"{a.Kind}\t{a.Category}\t{a.Name}\t{a.EntitySetupPath}")));
    }

    [FixturesFact]
    public void CloneRenamesTheNameTableAndKeepsEveryPayload()
    {
        using var catalog = OpenStock();
        var plan = CloneFamilyPlanner.PlanItem(catalog, Rpk, "Weapon_RPK-74_Gold");
        Assert.Equal([Rpk, Rpk + "_ES"], plan.Packages.Select(p => p.Key));
        var map = plan.ToMap();
        var source = ModdableAssets.ReadPackage(catalog, Rpk);
        var clone = PackageCloner.Clone(source, Rpk, map);
        Assert.Equal(3, clone.RenamedNames); // package path, Weapon_RPK-74_C, Default__Weapon_RPK-74_C

        var parsed = CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, null, plan.NewPrimary);
        Assert.Contains(Weapons + "Weapon_RPK-74_Gold", parsed.Names);
        Assert.Contains("Weapon_RPK-74_Gold_C", parsed.Names);
        Assert.Contains("Default__Weapon_RPK-74_Gold_C", parsed.Names);
        Assert.DoesNotContain(Rpk, parsed.Names);
        Assert.DoesNotContain("Weapon_RPK-74_C", parsed.Names);
        Assert.Contains("Chop_Weapon_RPK-74", parsed.Names);
        for (var i = 0; i < source.Exports.Count; i++)
        {
            Assert.True(parsed.GetExportData(i).Span.SequenceEqual(source.GetExportData(i).Span));
        }

        // CUE4Parse loads the clone as its own package.
        var root = Path.Combine(Path.GetTempPath(), "ss-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stem = Path.Combine(root, "SCUM", "Content", "ConZ_Files", "Items", "Weapons", "Ranged_Weapons", "Weapon_RPK-74_Gold");
            Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
            File.WriteAllBytes(stem + ".uasset", clone.Bytes.UAsset);
            File.WriteAllBytes(stem + ".uexp", clone.Bytes.UExp);
            using var cloned = AssetCatalog.OpenLoose(root);
            var exports = cloned.GetExports(plan.NewPrimary);
            Assert.Contains(exports, e => e.Name == "Weapon_RPK-74_Gold_C");
            Assert.Contains(exports, e => e.Name == "Default__Weapon_RPK-74_Gold_C");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [FixturesFact]
    public void WeaponStatsAreReadAndPatchedInPlace()
    {
        using var catalog = OpenStock();
        var rpg = ModdableAssets.ReadPackage(catalog, Rpg);
        var tunables = TunableReader.Read(rpg);
        var damage = Assert.Single(tunables, t => t.Name == "DamagePerShot");
        var range = Assert.Single(tunables, t => t.Name == "MaxRange");
        var category = Assert.Single(tunables, t => t.Name == "WeaponCategory");
        Assert.Equal(TunableKind.Float, damage.Kind);
        Assert.Equal("2.664", damage.Value);
        Assert.Equal(TunableKind.UInt, range.Kind);
        Assert.Equal("400", range.Value);
        Assert.Equal(TunableKind.Enum, category.Kind);
        Assert.Contains("EWeaponCategory::Rifles", category.Choices);
        Assert.Equal("Weapon_RPG7 (defaults)", damage.Group);

        var result = TunablePatcher.Apply(rpg, [new(damage.Export, damage.Path, "5.5"), new(range.Export, range.Path, "800")]);
        Assert.Equal(rpg.UExp.Length, result.Bytes.UExp.Length); // same-size patches
        Assert.Equal(rpg.UAsset, result.Bytes.UAsset);
        var back = TunableReader.Read(CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, null, Rpg)).ToDictionary(t => t.Key);
        Assert.Equal("5.5", back[damage.Key].Value);
        Assert.Equal("800", back[range.Key].Value);
        Assert.Equal(2, result.Applied.Count);

        Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(rpg, [new(damage.Export, damage.Path, "fast")]));
        Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(rpg, [new(category.Export, category.Path, "EWeaponCategory::NotAThing")]));
        Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(rpg, [new("NoSuchExport", "X", "1")]));
        output.WriteLine(string.Join('\n', tunables.Select(t => $"{t.Group}\t{t.Path}\t{t.Kind}\t{t.Value}")));
    }

    [FixturesFact]
    public void EntitySetupCaptionAndWeightAreEditable()
    {
        using var catalog = OpenStock();
        var es = ModdableAssets.ReadPackage(catalog, Rpk + "_ES");
        var tunables = TunableReader.Read(es);
        var caption = Assert.Single(tunables, t => t.Name == "Caption");
        var weight = Assert.Single(tunables, t => t.Name == "Weight");
        Assert.Equal("RPK", caption.Value);
        Assert.True(caption.CanEdit);
        Assert.Equal("4800", weight.Value);

        var result = TunablePatcher.Apply(es, [new(caption.Export, caption.Path, "RPK Gold Edition"), new(weight.Export, weight.Path, "3000")]);
        Assert.NotEqual(es.UExp.Length, result.Bytes.UExp.Length); // text resized the export
        var parsed = CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, null, Rpk + "_ES");
        var back = TunableReader.Read(parsed).ToDictionary(t => t.Key);
        Assert.Equal("RPK Gold Edition", back[caption.Key].Value);
        Assert.Equal("3000", back[weight.Key].Value);

        // The description (string table entry) stays untouched and every other export is byte-identical.
        Assert.Contains(back.Values, t => t.Name == "Description" && t.Value.Contains("ST_ItemsNoNamespace", StringComparison.Ordinal));
        var captionExport = TunableReader.ExportKeys(es).ToList().IndexOf(caption.Export);
        for (var i = 0; i < es.Exports.Count; i++)
        {
            if (i != captionExport)
            {
                Assert.True(parsed.GetExportData(i).Span.SequenceEqual(es.GetExportData(i).Span), $"export {i}");
            }
        }
    }

    [FixturesFact]
    public void ItemCloneIsBuiltPatchedAndRegistered()
    {
        using var catalog = OpenStock();
        var plan = CloneFamilyPlanner.PlanItem(catalog, Rpk, "Weapon_RPK-74_Gold");
        var esKey = "Default__Weapon_RPK-74_Gold_ES_C";
        var request = new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [plan.NewPrimary + "_ES"] = [new(esKey, "Caption", "RPK Gold"), new(esKey, "Weight", "2500")],
            [Rpg] = [new("Default__Weapon_RPG7_C", "MaxRange", "900")],
        });
        var result = AssetModBuilder.Build(catalog, request);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Packages.Count);
        Assert.Equal(2, result.Packages.Count(p => p.IsClone));
        Assert.Contains(result.Packages, p => !p.IsClone && p.PackagePath == Rpg);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal("SCUM/Content/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74_Gold.uasset", result.Packages.First(p => p.PackagePath == plan.NewPrimary).HeaderFilePath());

        Assert.NotNull(result.AssetRegistry);
        var registry = AssetRegistryFile.Parse(result.AssetRegistry);
        var stock = AssetRegistryFile.Load(FixturePaths.StockAssetRegistry);
        Assert.Equal(stock.AssetCount + 4, registry.AssetCount);
        Assert.Equal(4, result.Registered.Count);
        var cls = registry.Assets.Single(a => a.ObjectPath == plan.NewPrimary + ".Weapon_RPK-74_Gold_C");
        var tags = registry.GetTags(cls).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("Item", tags["PrimaryAssetType"]);
        Assert.Equal("Weapon_RPK-74_Gold", tags["PrimaryAssetName"]);
        var bp = registry.Assets.Single(a => a.ObjectPath == plan.NewPrimary + ".Weapon_RPK-74_Gold");
        var bpTags = registry.GetTags(bp).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("Weapon_RPK-74_Gold", bpTags["BlueprintPath"].Text);
        Assert.Equal(plan.NewPrimary, bpTags["GeneratedClass"].ExportPath![2]);
        Assert.Equal("Weapon_RPK-74_Gold_C", bpTags["GeneratedClass"].ExportPath![1]);
        Assert.Contains(registry.Assets, a => a.ObjectPath == plan.NewPrimary + "_ES.Weapon_RPK-74_Gold_ES_C");
        Assert.Empty(registry.FindNameHashMismatches());
    }

    [FixturesFact]
    public void VehicleFamilyIsPlannedClonedAndTuned()
    {
        using var catalog = OpenStock();
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, WolfsWagen, "Hunter");
        var folder = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/";
        var map = plan.Packages.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        output.WriteLine(string.Join('\n', plan.Packages.Select(p => $"{p.Key} -> {p.Value}")));
        Assert.Equal(folder + "BPC_Hunter", plan.NewPrimary);
        Assert.Equal(WolfsWagen, plan.Packages[0].Key);
        Assert.Equal(folder + "Hunter_ES", map[folder + "WolfsWagen_ES"]);
        Assert.Equal(folder + "Hunter_Item_Container", map[folder + "Wolfswagen_Item_Container"]);
        Assert.Equal(folder + "Attachments/BPC_Hunter_Chassis", map[folder + "Attachments/BPC_WolfsWagen_Chassis"]);
        Assert.Contains(plan.Packages, p => p.Key.StartsWith(CloneFamilyPlanner.ManualSpawnPresets, StringComparison.Ordinal) && p.Value.Contains("HunterManualSpawnPreset", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Packages, p => p.Key.Contains("/Models/", StringComparison.Ordinal)); // meshes stay shared

        var lean = CloneFamilyPlanner.PlanVehicle(catalog, WolfsWagen, "Hunter", new VehicleCloneOptions { IncludeAttachments = false, IncludeSpawnPresets = false });
        Assert.DoesNotContain(lean.Packages, p => p.Key.Contains("/Attachments/", StringComparison.Ordinal) || p.Key.Contains("SpawningPresets", StringComparison.Ordinal));
        Assert.True(lean.Packages.Count < plan.Packages.Count);
        Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanVehicle(catalog, WolfsWagen, "Rager")); // Rager_ES exists in the stock files

        var vehicle = ModdableAssets.ReadPackage(catalog, WolfsWagen);
        var mass = Assert.Single(TunableReader.Read(vehicle), t => t.Name == "ChassisMass");
        Assert.Equal("385", mass.Value);
        var request = new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [plan.NewPrimary] = [new(mass.Export.Replace("WolfsWagen", "Hunter", StringComparison.Ordinal), mass.Path, "700")],
        });
        var result = AssetModBuilder.Build(catalog, request);
        Assert.Empty(result.Warnings);
        Assert.Equal(plan.Packages.Count, result.Packages.Count);
        var hunter = result.Packages.Single(p => p.PackagePath == plan.NewPrimary);
        var tuned = TunableReader.Read(CookedPackage.Parse(hunter.Bytes.UAsset, hunter.Bytes.UExp, null, plan.NewPrimary));
        Assert.Equal("700", Assert.Single(tuned, t => t.Name == "ChassisMass").Value);
        Assert.Contains(result.Registered, r => r.PrimaryAssetType == "Vehicle" && r.ObjectPath.EndsWith("BPC_Hunter_C", StringComparison.Ordinal));
        Assert.Contains(result.Registered, r => r.PrimaryAssetType == "VehiclePreset");
        foreach (var package in result.Packages)
        {
            var parsed = CookedPackage.Parse(package.Bytes.UAsset, package.Bytes.UExp, package.UBulk, package.PackagePath);
            Assert.DoesNotContain(parsed.Names, n => n.Contains("BPC_WolfsWagen_C", StringComparison.Ordinal) && !n.Contains("ServiceBP", StringComparison.Ordinal));
        }
    }

    [FixturesFact]
    public void PlaneClonesRenameSquashedPresetNames()
    {
        using var catalog = OpenStock();
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, "/Game/ConZ_Files/Vehicles/Airplane/Mariner/BPC_Kinglet_Mariner", "Sea_Hawk");
        output.WriteLine(string.Join('\n', plan.Packages.Select(p => $"{p.Key} -> {p.Value}")));
        Assert.Contains(plan.Packages, p => p.Value.EndsWith("/Sea_Hawk_ES", StringComparison.Ordinal));
        Assert.Contains(plan.Packages, p => p.Value.EndsWith("/SeaHawkManualSpawnPreset", StringComparison.Ordinal));
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()));
        Assert.Empty(result.Warnings);
        Assert.Contains(result.Registered, r => r.PrimaryAssetType == "Vehicle");
    }
}
