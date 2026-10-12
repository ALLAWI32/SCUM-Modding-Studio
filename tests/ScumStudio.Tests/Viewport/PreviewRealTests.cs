using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "there are trees that do not show in 3D … I want the original textures, the real colours". Real game files only
/// (<c>SCUM_PAKS</c>, key from this PC's store): the Assets page's foliage types, a vehicle part and a grenade show a model,
/// trees with their SpeedTree colour texture and their leaves cut out, and a whole vehicle keeps every part.
/// </summary>
public sealed class PreviewRealTests
{
    [Theory]
    [InlineData("ScotsPine_FT", true)]
    [InlineData("European_White_Birch_01_FT", false)]
    public void FoliageTypesShowTheirTree(string leaf, bool speedTree)
    {
        if (Open() is not { } catalog)
        {
            return; // not asked for
        }

        using (catalog)
        {
            var package = Find(catalog, leaf);
            var model = new MeshPreviewLoader(catalog) { TextureSize = 256 }.Load(package + "." + leaf, package, "FoliageType_InstancedStaticMesh")!;
            Assert.NotEmpty(model.Parts);
            Assert.Contains(model.Parts, p => p.AlphaCutoff > 0f);
            if (speedTree)
            {
                Assert.Contains(model.Parts, p => p.TexturePath?.EndsWith("_Color", StringComparison.OrdinalIgnoreCase) == true);
            }
        }
    }

    [Fact]
    public void AVehiclePartShowsItsSoftMesh()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            var model = new MeshPreviewLoader(catalog) { TextureSize = 64 }.LoadBlueprint("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/Attachments/BPC_WolfsWagen_Door_FrontLeft");
            Assert.NotEmpty(model!.Parts);
        }
    }

    [Fact]
    public void TheFragGrenadeHasAModelOrAnIcon()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            const string grenade = "/Game/ConZ_Files/Items/Grenades/Frag_Grenade";
            var model = new MeshPreviewLoader(catalog) { TextureSize = 64 }.LoadBlueprint(grenade);
            Assert.True(model is { Parts.Count: > 0 } || InventoryIcons.FindIconPath(catalog, grenade + "_ES") is not null);
        }
    }

    [Fact]
    public void TheWholeVehicleKeepsEveryPart()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            const string vehicle = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen";
            var model = new MeshPreviewLoader(catalog) { TextureSize = 64 }.Load(vehicle + ".BPC_WolfsWagen_C", vehicle, "BlueprintGeneratedClass")!;
            var meshes = model.Parts.Select(p => p.Name.Split(" / ")[0]).Distinct().ToList();
            Assert.Equal(4, meshes.Count(m => m.Contains("_Door_", StringComparison.Ordinal)));
            Assert.Contains(meshes, m => m.EndsWith("_DoorHood", StringComparison.Ordinal));
            Assert.All(model.Parts, p => Assert.True(p.Mesh.TriangleCount > 0, p.Name));
        }
    }

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    private static string Find(AssetCatalog catalog, string leaf) =>
        catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).First(p => p.EndsWith("/" + leaf, StringComparison.OrdinalIgnoreCase));
}
