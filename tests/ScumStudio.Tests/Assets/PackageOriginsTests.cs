using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Assets;

/// <summary>Writes small paks of synthetic packages into a Paks folder.</summary>
internal static class SyntheticPaks
{
    /// <summary>Packs one synthetic package per path (<c>/Game/A/B</c>) into <paramref name="paksFolder"/>/<paramref name="pakName"/>.</summary>
    public static async Task WriteAsync(string paksFolder, string pakName, params string[] packagePaths)
    {
        using var staging = new TempDirectory("scumstudio-origins-");
        var bytes = new SyntheticPackage().BuildPackage();
        foreach (var path in packagePaths)
        {
            var file = "SCUM/Content/" + path["/Game/".Length..];
            staging.Write(file + ".uasset", bytes.UAsset);
            staging.Write(file + ".uexp", bytes.UExp);
        }

        Directory.CreateDirectory(paksFolder);
        await new PakWriter().WriteFromDirectoryAsync(staging.Path, Path.Combine(paksFolder, pakName));
    }
}

public sealed class PackageOriginsTests
{
    [Fact]
    public async Task AModPakIsNotStockAndNamesItsContainer()
    {
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        await SyntheticPaks.WriteAsync(paks, "pakchunk0-WindowsNoEditor.pak", "/Game/A/Stock", "/Game/A/Shared");
        await SyntheticPaks.WriteAsync(paks, "pakchunk99-Mod_P.pak", "/Game/A/Added", "/Game/A/Shared");

        using var catalog = AssetCatalog.OpenPaks(paks);
        Assert.True(catalog.IsStock("/Game/A/Stock"));
        Assert.False(catalog.IsStock("/Game/A/Added"));
        Assert.True(catalog.IsStock("/Game/A/Shared.Shared")); // a mod overrides it; the game still ships it
        Assert.False(catalog.IsStock("/Game/A/Missing"));

        Assert.Equal("pakchunk0-WindowsNoEditor.pak", catalog.ContainerOf("/Game/A/Stock"));
        Assert.Equal("pakchunk99-Mod_P.pak", catalog.ContainerOf("/Game/A/Added"));
        Assert.Equal("pakchunk99-Mod_P.pak", catalog.ContainerOf("/Game/A/Shared"));
        Assert.Null(catalog.ContainerOf("/Game/A/Missing"));
    }

    [Fact]
    public void ALooseCatalogIsAllStock()
    {
        using var temp = new TempDirectory();
        var bytes = new SyntheticPackage().BuildPackage();
        temp.Write("SCUM/Content/A/Loose.uasset", bytes.UAsset);
        temp.Write("SCUM/Content/A/Loose.uexp", bytes.UExp);

        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        Assert.True(catalog.IsStock("/Game/A/Loose"));
        Assert.Equal(temp.Combine("SCUM"), catalog.ContainerOf("/Game/A/Loose"));
    }

    [Theory]
    [InlineData("pakchunk99-Mod_P.pak", true)]
    [InlineData("pakchunk900-MyModCraftables_P.PAK", true)]
    [InlineData("pakchunk0-WindowsNoEditor.pak", false)]
    [InlineData("pakchunk0_s51-WindowsNoEditor.pak", false)]
    public void ModContainersEndInP(string name, bool mod) => Assert.Equal(mod, PackageOrigins.IsModContainer(name));
}
