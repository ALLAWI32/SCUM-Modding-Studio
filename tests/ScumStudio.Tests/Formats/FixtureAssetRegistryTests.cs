using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Formats;

/// <summary>AssetRegistry.bin read / write against the stock registry of SCUM 1.3.3 build 149664.</summary>
public sealed class FixtureAssetRegistryTests
{
    [FixturesFact]
    public void StockRegistryRoundTripsByteExact()
    {
        var original = File.ReadAllBytes(FixturePaths.StockAssetRegistry);
        var reg = AssetRegistryFile.Parse(original);
        Assert.Equal(8, reg.Version);
        Assert.Equal(126273, reg.AssetCount);
        Assert.Equal(368050, reg.Names.Count);
        Assert.Empty(reg.FindNameHashMismatches()); // CityHash64 v1.1.1 of the lower-cased names
        Assert.Null(RoundTripVerifier.FirstDifference(reg.Save(), original));
    }

    [FixturesFact]
    public void ListsWolfsWagenRecordsWithTags()
    {
        var reg = AssetRegistryFile.Load(FixturePaths.StockAssetRegistry);
        var sk = reg.Assets.Single(a => a.ObjectPath == "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen.SK_WolfsWagen");
        Assert.Equal("SkeletalMesh", sk.AssetClass);
        Assert.Equal("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/SK_WolfsWagen", sk.PackageName);
        Assert.Equal("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes", sk.PackagePath);
        Assert.Equal("SK_WolfsWagen", sk.AssetName);
        var tags = reg.GetTags(sk).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("5", tags["Bones"]);
        Assert.Equal("5", tags["LODs"]);
        Assert.Equal("PhysicsAsset'/Game/ConZ_Files/Vehicles/Car/WolfsWagen/PA_WolfsWagen.PA_WolfsWagen'", tags["PhysicsAsset"]);

        var bp = reg.Assets.Single(a => a.ObjectPath == "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen.BPC_WolfsWagen");
        var bpTags = reg.GetTags(bp).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("Blueprint", bp.AssetClass);
        Assert.Equal("Vehicle", bpTags["PrimaryAssetType"]);
    }

    [FixturesFact]
    public void CloneAndRemoveRecords()
    {
        var reg = AssetRegistryFile.Load(FixturePaths.StockAssetRegistry);
        var count = reg.AssetCount;
        var template = reg.Assets.Single(a => a.ObjectPath == "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen.BPC_WolfsWagen");
        var clone = reg.CloneAsset(template, "/Game/ConZ_Files/Vehicles/Car/TestCar/BPC_TestCar");
        Assert.Equal("/Game/ConZ_Files/Vehicles/Car/TestCar/BPC_TestCar.BPC_TestCar", clone.ObjectPath);
        Assert.Equal("Blueprint", clone.AssetClass);

        var reparsed = AssetRegistryFile.Parse(reg.Save());
        Assert.Equal(count + 1, reparsed.AssetCount);
        Assert.Empty(reparsed.FindNameHashMismatches());
        var copy = reparsed.GetAsset(count);
        Assert.Equal(clone, copy);
        var tags = reparsed.GetTags(copy).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal(reg.GetTags(template).Count, tags.Count);
        Assert.Equal("BlueprintGeneratedClass'/Game/ConZ_Files/Vehicles/Car/TestCar/BPC_TestCar.BPC_TestCar_C'", tags["GeneratedClass"]);
        Assert.Equal("BPC_TestCar", tags["PrimaryAssetName"]);
        Assert.Equal("Vehicle", tags["PrimaryAssetType"]);
        Assert.Equal(reparsed.GetAssetRawBytes(template.Index).Span[^8..].ToArray(), reparsed.GetAssetRawBytes(count).Span[^8..].ToArray());

        Assert.Equal(1, reparsed.Remove(a => a.PackageName == "/Game/ConZ_Files/Vehicles/Car/TestCar/BPC_TestCar"));
        Assert.Equal(count, reparsed.AssetCount);
    }

    [FixturesFact]
    public void ModRegistryRoundTripsByteExact()
    {
        var path = Path.Combine(FixturePaths.BuildSepTree("F16"), "SCUM", "AssetRegistry.bin");
        var original = File.ReadAllBytes(path);
        var reg = AssetRegistryFile.Parse(original);
        Assert.True(reg.AssetCount > 126273, "the F16 registry carries the added mod records");
        Assert.Null(RoundTripVerifier.FirstDifference(reg.Save(), original));
        Assert.Empty(reg.FindNameHashMismatches());
    }
}
