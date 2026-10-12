using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.IO;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Modding;

namespace ScumStudio.Tests.Formats;

/// <summary>
/// FName numbers in <c>AssetRegistry.bin</c> (F039, F051, N020, N067): a name ending in <c>_N</c> is stored the way the
/// cooker stores it, as its base plus the number (index | 0x80000000, then the number), in records and in tag values; and
/// a record copies from one registry into another with its names, numbers, tags and bundles.
/// </summary>
public sealed class AssetRegistryNumberTests
{
    [Fact]
    public void AddAssetSplitsNumberedNames()
    {
        var registry = SyntheticGame.Registry();
        var seed = registry.Assets.Single(a => a.AssetClass == "Blueprint");
        registry.AddAsset(seed, "/Game/X/BPC_TestCar_2.BPC_TestCar_2", "/Game/X", "/Game/X/BPC_TestCar_2", "BPC_TestCar_2", []);

        var back = AssetRegistryFile.Parse(registry.Save());
        var record = back.GetAsset(back.AssetCount - 1);
        Assert.Equal("/Game/X/BPC_TestCar_2.BPC_TestCar_2", record.ObjectPath);
        Assert.Equal("/Game/X/BPC_TestCar_2", record.PackageName);
        Assert.Equal("BPC_TestCar_2", record.AssetName);
        Assert.Equal("/Game/X", record.PackagePath);

        // ObjectPath, PackagePath, AssetClass, PackageName, AssetName as (index, number), read the way the engine reads them.
        var raw = back.GetAssetRawBytes(back.AssetCount - 1).ToArray();
        var at = 0;
        (string Text, int Number) Next()
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
            at += 4;
            if ((index & 0x80000000) == 0)
            {
                return (back.Names[(int)index], 0);
            }

            at += 4;
            return (back.Names[(int)(index & 0x7FFFFFFF)], BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at - 4)));
        }

        Assert.Equal(("/Game/X/BPC_TestCar_2.BPC_TestCar", 3), Next());
        Assert.Equal(("/Game/X", 0), Next());
        Assert.Equal(("Blueprint", 0), Next());
        Assert.Equal(("/Game/X/BPC_TestCar", 3), Next());
        Assert.Equal(("BPC_TestCar", 3), Next());
        Assert.DoesNotContain("BPC_TestCar_2", back.Names);
        Assert.Empty(back.FindNameHashMismatches());
    }

    [Fact]
    public void NumberedNameValueRoundTrips()
    {
        var registry = SyntheticGame.Registry();
        var seed = registry.Assets.Single(a => a.AssetClass == "Blueprint");
        const string recipe = "/Game/R/CR_Improvised_Bow_20";
        registry.AddAsset(seed, recipe + ".CR_Improvised_Bow_20", "/Game/R", recipe, "CR_Improvised_Bow_20",
        [
            ("PrimaryAssetName", AssetRegistryValue.Name("CR_Improvised_Bow_20")),
            ("GeneratedClass", AssetRegistryValue.Export("BlueprintGeneratedClass", "CR_Improvised_Bow_20_C", recipe)),
            ("Product", AssetRegistryValue.AnyExport("BlueprintGeneratedClass", "SM_Bow_C", "/Game/R/SM_Bow")),
            ("Caption", AssetRegistryValue.Wide("قوس")),
            ("Type", AssetRegistryValue.AnyName("PlaceableCraftingRecipe")),
        ]);

        var back = AssetRegistryFile.Parse(registry.Save());
        var tags = back.GetTags(back.GetAsset(back.AssetCount - 1)).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(AssetRegistryValueKind.Name, tags["PrimaryAssetName"].Kind);
        Assert.Equal("CR_Improvised_Bow_20", tags["PrimaryAssetName"].Text);
        Assert.Equal(21, tags["PrimaryAssetName"].NameNumber);
        Assert.Equal(AssetRegistryValueKind.ExportPath, tags["GeneratedClass"].Kind);
        Assert.Equal(["BlueprintGeneratedClass", "CR_Improvised_Bow_20_C", recipe], tags["GeneratedClass"].ExportPath);
        Assert.Equal(AssetRegistryValueKind.NumberlessExportPath, tags["Product"].Kind);
        Assert.Equal("قوس", tags["Caption"].Text);
        Assert.Equal(AssetRegistryValueKind.NumberlessName, tags["Type"].Kind);
        Assert.Contains("CR_Improvised_Bow", back.Names);
        Assert.Contains("/Game/R/CR_Improvised_Bow", back.Names);
        Assert.DoesNotContain("CR_Improvised_Bow_20", back.Names);
        Assert.Empty(back.FindNameHashMismatches());

        // A value read back is added again with its stored number (no second split).
        var copy = AssetRegistryFile.Parse(back.Save());
        var id = copy.AddValue(tags["PrimaryAssetName"]);
        Assert.Equal(tags["PrimaryAssetName"], copy.GetValue(id));
    }

    [Fact]
    public void ImportAssetCopiesARecordAcrossRegistries()
    {
        const string bow = "/Game/R/CR_Improvised_Bow_20";
        var source = SyntheticGame.Registry(
            new SyntheticGame.Record("/Game/A/BP_Chair.BP_Chair", "Blueprint", SyntheticGame.PrimaryTags("BaseBuilding", "BP_Chair")),
            new SyntheticGame.Record(bow + ".CR_Improvised_Bow_20", "PlaceableCraftingRecipe",
            [
                ("PrimaryAssetType", AssetRegistryValue.NumberlessName("PlaceableCraftingRecipe")),
                ("PrimaryAssetName", AssetRegistryValue.Name("CR_Improvised_Bow_20")),
                ("Product", AssetRegistryValue.Export("BlueprintGeneratedClass", "BP_Bow_2_C", "/Game/R/BP_Bow_2")),
                ("Caption", AssetRegistryValue.Wide("قوس")),
                ("Weight", AssetRegistryValue.Ansi("2.5")),
            ]));
        var record = source.Assets.Single(a => a.ObjectPath == bow + ".CR_Improvised_Bow_20");

        // Another registry (its names in another order): the record lands with the same names, numbers, tags and bundle.
        var target = SyntheticGame.Registry(new SyntheticGame.Record("/Game/Z/BP_Other.BP_Other", "StaticMesh"));
        var count = target.AssetCount;
        target.ImportAsset(source, record);
        var back = AssetRegistryFile.Parse(target.Save());
        Assert.Equal(count + 1, back.AssetCount);
        var copy = back.GetAsset(count);
        Assert.Equal(record with { Index = count, TagMapHandle = copy.TagMapHandle }, copy);
        Assert.Equal(Describe(source, record), Describe(back, copy));
        Assert.Equal("Explicit", BundleName(back, count));
        Assert.Empty(back.FindNameHashMismatches());
        Assert.Throws<ArgumentException>(() => target.ImportAsset(source, record with { Index = 0 }));
    }

    [FixturesFact]
    public void ImportAssetCopiesStockRecordsIntoACopyWithoutThem()
    {
        var stock = AssetRegistryFile.Load(FixturePaths.StockAssetRegistry);
        var picked = stock.Assets.Where(a => a.TagCount > 0).Take(200)
            .Concat(stock.Assets.Where(a => FNameRef.Split(a.ObjectPath).Number != 0).Take(200)) // numbered names (Outpost_B_4, ..._2)
            .Append(stock.Assets.Single(a => a.ObjectPath == "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen.BPC_WolfsWagen"))
            .ToList();
        var paths = picked.Select(a => a.ObjectPath).ToHashSet(StringComparer.Ordinal);
        var copy = AssetRegistryFile.Parse(stock.Save());
        Assert.Equal(paths.Count, copy.Remove(a => paths.Contains(a.ObjectPath)));
        foreach (var record in picked.DistinctBy(a => a.ObjectPath))
        {
            copy.ImportAsset(stock, record);
        }

        var back = AssetRegistryFile.Parse(copy.Save());
        Assert.Equal(stock.AssetCount, back.AssetCount);
        ExportSafety.AssertRegistrySuperset(stock, back, []);
    }

    private static string[] Describe(AssetRegistryFile registry, AssetData asset) =>
        registry.GetTags(asset).Select(t => $"{t.Key}={t.Value.Kind}:{t.Value.Text}#{t.Value.NameNumber}").ToArray();

    // The first asset bundle's name of record index: after the five names (numbered ones carry their number) and the handle.
    private static string BundleName(AssetRegistryFile registry, int index)
    {
        var r = new ByteReader(registry.GetAssetRawBytes(index).ToArray());
        for (var k = 0; k < 5; k++)
        {
            if ((r.U32() & 0x80000000) != 0)
            {
                r.I32();
            }
        }

        r.U64();
        Assert.Equal(1, r.I32());
        return registry.Names[(int)r.U32()];
    }
}
