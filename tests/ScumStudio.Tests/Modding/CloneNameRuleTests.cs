using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Modding.Cloning;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// A clone's name never ends in <c>_&lt;number&gt;</c>: the engine reads <c>Rager_2</c> as "Rager" number 3, a different
/// FName from the one written as text (checkpoint 46).
/// </summary>
public sealed class CloneNameRuleTests
{
    [Theory]
    [InlineData("Rager_2")]
    [InlineData("Knife_0")]
    [InlineData("Weapon_AK47_2")]
    public void ANameThatSplitsIsRefused(string name) => Assert.False(CloneFamilyPlanner.IsValidName(name));

    [Theory]
    [InlineData("Rager_02")]
    [InlineData("Rager2")]
    [InlineData("Rager_v2")]
    [InlineData("AK47")]
    [InlineData("Weapon_RPK-74_Gold")]
    public void ANameThatDoesNotSplitIsAccepted(string name) => Assert.True(CloneFamilyPlanner.IsValidName(name));

    [Fact]
    public void ThePlannersRefuseANameThatSplits()
    {
        using var temp = new TempDirectory();
        using var catalog = Catalog(temp, "/Game/A/Weapon_X", "/Game/A/BPC_Car");

        var item = Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanItem(catalog, "/Game/A/Weapon_X", "X_2"));
        Assert.Contains("_<number>", item.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanVehicle(catalog, "/Game/A/BPC_Car", "Car_2"));
        Assert.Equal("/Game/A/X2", CloneFamilyPlanner.PlanItem(catalog, "/Game/A/Weapon_X", "X2", includePaint: false).NewPrimary);
    }

    [Theory]
    [InlineData("SK_Winchester_1897", "Weapon_M1887", "Weapon_M1887_Gold", "Weapon_M1887_Gold_SK_Winchester_v1897")]
    [InlineData("MI_Trench_Gun_1897", "Trench_Gun", "Riot_Gun", "MI_Riot_Gun_v1897")]
    [InlineData("SK_SF19_2", "SF19", "SF20", "SK_SF20_v2")]
    [InlineData("WolfsWagen_ES", "WolfsWagen", "Hunter", "Hunter_ES")]
    public void AMembersNewLeafNeverSplits(string leaf, string token, string newToken, string expected)
    {
        var renamed = CloneFamilyPlanner.RenameLeaf(leaf, token, newToken);
        Assert.Equal(expected, renamed);
        Assert.Equal(0, FNameRef.Split(renamed).Number);
    }

    [Fact]
    public void AnExistingPathNamesWhereItWasFound()
    {
        using var temp = new TempDirectory();
        using var catalog = Catalog(temp, "/Game/A/Weapon_X", "/Game/A/Weapon_X_Gold");

        var error = Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanItem(catalog, "/Game/A/Weapon_X", "Weapon_X_Gold", includePaint: false));
        Assert.Contains(temp.Combine("SCUM"), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AssetCatalog Catalog(TempDirectory temp, params string[] packages)
    {
        var bytes = new SyntheticPackage().BuildPackage();
        foreach (var package in packages)
        {
            var file = "SCUM/Content/" + package["/Game/".Length..];
            temp.Write(file + ".uasset", bytes.UAsset);
            temp.Write(file + ".uexp", bytes.UExp);
        }

        return AssetCatalog.OpenLoose(temp.Path);
    }
}
