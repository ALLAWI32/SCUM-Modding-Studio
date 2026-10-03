using ScumStudio.Assets.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "some doors are missing, show me the car complete". Real game files only (<c>SCUM_PAKS</c>, key from this
/// PC's store): a vehicle's 3D view has every part its world spawn preset gives a stock vehicle, doors included, and
/// none of the add-ons (armour, roof racks).
/// </summary>
public sealed class VehiclePreviewRealTests
{
    [Theory]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager", 4)]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen", 4)]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Laika/BPC_Laika", 2)]
    public void TheVehicleShowsAllItsDoorsAndNoArmour(string vehicle, int sideDoors)
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var model = new MeshPreviewLoader(catalog).LoadBlueprint(vehicle)!;
        var meshes = model.Parts.Select(p => p.Name.Split(" / ")[0]).Distinct().ToList();

        Assert.Equal(sideDoors, meshes.Count(m => m.Contains("_Door_", StringComparison.Ordinal)));
        Assert.Contains(meshes, m => m.EndsWith("_DoorHood", StringComparison.Ordinal));
        Assert.DoesNotContain(meshes, m => m.Contains("Armor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Owner: "show the armour you can buy on the car, so I can paint it too".</summary>
    [Fact]
    public void TheRagerCanWearItsLightOrHeavyArmour()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var stock = new MeshPreviewLoader(catalog).LoadBlueprint("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager")!;
        Assert.Equal(["ArmorHeavy", "ArmorLight"], stock.AddOns);

        var light = new MeshPreviewLoader(catalog).LoadBlueprint("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager", "ArmorLight")!;
        var armour = light.Parts.Where(p => p.Name.Contains("Armor", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(armour.Count >= 6, string.Join("\n", light.Parts.Select(p => p.Name)));
        Assert.DoesNotContain(light.Parts, p => p.Name.Contains("ArmorHeavy", StringComparison.OrdinalIgnoreCase));
        Assert.All(armour, p => Assert.EndsWith(".MI_WW_Armor", p.Material, StringComparison.Ordinal)); // the shared armour material
    }
}
