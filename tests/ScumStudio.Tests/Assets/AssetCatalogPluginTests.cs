using ScumStudio.Assets.Catalog;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// The engine mounts a plugin's content (<c>SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/</c>) at its own root
/// (<c>/WoodlandHunterPack/</c>): the DLC levels reference their Blueprint classes and meshes that way, and the catalog
/// must find them (the Wild Hunter stall's 531 meshes were unknown and the stall unselectable).
/// </summary>
public sealed class AssetCatalogPluginTests
{
    [Fact]
    public void FindsPluginPackagesByTheirMountPoint()
    {
        using var temp = new LevelTempDirectory();
        Touch(temp.Combine("SCUM", "Content", "ConZ_Files", "A", "SM_B.uasset"));
        Touch(temp.Combine("SCUM", "Plugins", "GameFeatures", "WoodlandHunterPack", "Content", "Models", "BP_Stall.uasset"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        // CUE4Parse's own import resolution goes through the provider's virtual paths: the plugin is registered there.
        Assert.Equal("SCUM/Plugins/GameFeatures/WoodlandHunterPack", catalog.Provider.VirtualPaths["WoodlandHunterPack"].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith("WoodlandHunterPack/Content/Models/BP_Stall.uasset", catalog.Provider.FixPath("/WoodlandHunterPack/Models/BP_Stall.uasset").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        Assert.True(catalog.TryGetPackageFile("/Game/ConZ_Files/A/SM_B", out _));
        Assert.True(catalog.TryGetPackageFile("/WoodlandHunterPack/Models/BP_Stall.BP_Stall_C", out var byMount));
        Assert.True(catalog.TryGetPackageFile("/SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/Models/BP_Stall", out var byFile));
        Assert.Equal(byFile!.Path, byMount!.Path);
        Assert.EndsWith("WoodlandHunterPack/Content/Models/BP_Stall.uasset", byMount.Path.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.False(catalog.TryGetPackageFile("/OtherPack/Models/BP_Stall", out _));
        Assert.False(catalog.TryGetPackageFile("/WoodlandHunterPack/Models/Missing", out _));
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0xC1, 0x83, 0x2A, 0x9E]);
    }
}
