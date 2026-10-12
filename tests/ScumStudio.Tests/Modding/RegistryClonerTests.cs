using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Registry records of clones: a record an earlier export left at the new path is replaced, not kept with old tags (F041),
/// while the game's own records are never rewritten; templates whose names carry FName numbers (Weapon_Deagle_357,
/// CR_Improvised_Bow_20) are registered too (N020, N067).
/// </summary>
public sealed class RegistryClonerTests
{
    private const string Gen = "/Game/T/BP_Gen";
    private const string Copy = "/Game/T/BP_Copy";

    [Fact]
    public void ExistingObjectPathIsReplaced()
    {
        var registry = SyntheticGame.Registry(
            new SyntheticGame.Record(Gen + ".BP_Gen", "Blueprint", [.. SyntheticGame.PrimaryTags("Item", "BP_Gen"), ("Kind", AssetRegistryValue.Ansi("Generator"))]),
            new SyntheticGame.Record(Copy + ".BP_Copy", "Blueprint", [.. SyntheticGame.PrimaryTags("OldType", "BP_Copy"), ("Kind", AssetRegistryValue.Ansi("Stale"))]));
        var count = registry.AssetCount;
        var game = registry.Assets.Where(a => a.ObjectPath != Copy + ".BP_Copy").Select(a => a.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = RegistryCloner.Register(registry, new PackageMap([new(Gen, Copy)]), kept: game);

        Assert.Equal([new RegisteredAsset(Copy + ".BP_Copy", "Item")], added);
        Assert.Equal(count, registry.AssetCount); // replaced in place
        var record = Assert.Single(registry.Assets, a => a.ObjectPath == Copy + ".BP_Copy");
        var tags = registry.GetTags(record).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("Item", tags["PrimaryAssetType"]);
        Assert.Equal("BP_Copy", tags["PrimaryAssetName"]);
        Assert.Equal("Generator", tags["Kind"]);
    }

    [Fact]
    public void AStockRecordIsNeverReplaced()
    {
        // The registry's own records (the game's) are kept as they are: a new path already there is skipped, with a warning.
        var registry = SyntheticGame.StockRegistry();
        var before = registry.Save();
        var warnings = new List<string>();

        var added = RegistryCloner.Register(registry, new PackageMap([new(SyntheticGame.Stock1, "/Game/S/BP_Stock2")]), warnings, owner: "Rebel Rifle");

        Assert.Empty(added);
        Assert.Equal(before, registry.Save());
        Assert.StartsWith("Rebel Rifle: " + SyntheticGame.Stock2 + " is a record of the game", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void NumberedNameTagIsRemapped()
    {
        const string deagle = "/Game/T/Weapon_Deagle_357";
        const string gold = "/Game/T/Weapon_Gold";
        var registry = SyntheticGame.Registry(
            new SyntheticGame.Record(deagle + ".Weapon_Deagle_357", "Blueprint",
            [
                ("PrimaryAssetType", AssetRegistryValue.NumberlessName("Item")),
                ("PrimaryAssetName", AssetRegistryValue.Name("Weapon_Deagle_357")),
                ("GeneratedClass", AssetRegistryValue.Export("BlueprintGeneratedClass", "Weapon_Deagle_357_C", deagle)),
            ]),
            new SyntheticGame.Record(deagle + ".Weapon_Deagle_357_C", "BlueprintGeneratedClass",
            [
                ("PrimaryAssetType", AssetRegistryValue.NumberlessName("Item")),
                ("PrimaryAssetName", AssetRegistryValue.Name("Weapon_Deagle_357")),
            ]));
        var warnings = new List<string>();

        var added = RegistryCloner.Register(registry, new PackageMap([new(deagle, gold)]), warnings, owner: gold);

        Assert.Empty(warnings);
        Assert.Equal([gold + ".Weapon_Gold", gold + ".Weapon_Gold_C"], added.Select(a => a.ObjectPath));
        var back = AssetRegistryFile.Parse(registry.Save());
        var bp = back.GetTags(back.Assets.Single(a => a.ObjectPath == gold + ".Weapon_Gold")).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("Weapon_Gold", bp["PrimaryAssetName"].Text);
        Assert.Equal(AssetRegistryValueKind.NumberlessName, bp["PrimaryAssetName"].Kind);
        Assert.Equal(["BlueprintGeneratedClass", "Weapon_Gold_C", gold], bp["GeneratedClass"].ExportPath);
        var cls = back.GetTags(back.Assets.Single(a => a.ObjectPath == gold + ".Weapon_Gold_C")).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.Equal("Weapon_Gold", cls["PrimaryAssetName"]);
        Assert.Empty(back.FindNameHashMismatches());
    }
}
