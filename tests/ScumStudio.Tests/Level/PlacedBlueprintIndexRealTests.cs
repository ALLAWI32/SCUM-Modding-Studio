using ScumStudio.Assets.Catalog;
using ScumStudio.Level.World;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord user igor: "It would be preferable to create blueprints like these, with the option to install them on the map".
/// A Blueprint is placed by copying one the game placed; the index finds them from the level headers. Real game files only.
/// </summary>
public sealed class PlacedBlueprintIndexRealTests
{
    [Fact]
    public void TheIslandsPlacedBlueprintsAndItemSpawnersAreIndexedFromTheHeaders()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var index = PlacedBlueprintIndex.Build(catalog, WorldIndex.FromCatalog(catalog));
        Assert.True(index.ClassCount > 2_700, $"only {index.ClassCount} placed classes");

        // An actor of its own comes before the child actors of other Blueprints.
        var tank = index.Of("/Game/ConZ_Files/Models/Objects/Indoor/Furniture/Water_Tank_Office/BP_Water_Tank_Office_01");
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/B_0_Gas_Station_Cont", tank[0].Level);
        Assert.False(tank[0].IsChildActor);
        Assert.Contains(tank, t => t.IsChildActor);

        // Items are never placed as actors: the game's world item spawners put them down.
        const string Drill = "/Game/ConZ_Files/Items/Equipment/Active_Items/Work_Drillpress_01";
        Assert.Empty(index.Of(Drill));
        Assert.True(index.Spawners.Count >= 20, $"only {index.Spawners.Count} world item spawners");
        Assert.Equal(Drill + ".Work_Drillpress_01_C", index.SpawnersFor(Drill + ".Work_Drillpress_01_C")[0].Item);
        Assert.Contains(index.Spawners, s => s.Item?.EndsWith(".Work_Lathe_Machine_01_C", StringComparison.Ordinal) == true);
    }
}
