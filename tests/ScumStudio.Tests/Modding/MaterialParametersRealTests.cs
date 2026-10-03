using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner: "paint the armour too, so it looks the same on the car". Real game files only (<c>SCUM_PAKS</c>, key from this
/// PC's store): the armour material, which leaves paint to its parent, gains a paint colour, a second colour, metal,
/// clear coat and the white colour mask, and the game's own reader (CUE4Parse) reads them back with the parent's guids.
/// </summary>
public sealed class MaterialParametersRealTests
{
    private const string Armour = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Armor";
    private const string WhiteMask = "/Game/ConZ_Files/Textures/Basic/T_FlatWhiteMask_Dummy_01.T_FlatWhiteMask_Dummy_01";
    private const string FlatDiffuse = "/Game/ConZ_Files/Textures/Basic/T_FlatWhiteColor_Dummy_01.T_FlatWhiteColor_Dummy_01";

    [Fact]
    public async Task TheArmourMaterialCanBeGivenAPaint()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string gold = "1, 0.49, 0.026, 1";
        var edits = new List<TunableEdit>
        {
            new("MI_WW_Armor", MaterialParameters.PathOf("VectorParameterValues", "Base Color A"), gold),
            new("MI_WW_Armor", MaterialParameters.PathOf("VectorParameterValues", "Base Color B"), gold),
            new("MI_WW_Armor", MaterialParameters.PathOf("ScalarParameterValues", "CarPaint Metalness"), "1"),
            new("MI_WW_Armor", MaterialParameters.PathOf("ScalarParameterValues", "ClearCoat Amount"), "0.9"),
            new("MI_WW_Armor", MaterialParameters.PathOf("TextureParameterValues", "Color Mask"), WhiteMask),
            new("MI_WW_Armor", MaterialParameters.PathOf("VectorParameterValues", "Dirt Color"), "0.1, 0.1, 0.1, 0"), // one it already has
            new("MI_WW_Armor", MaterialParameters.PathOf("TextureParameterValues", "Diffuse Map"), FlatDiffuse), // a texture it does not import yet
        };
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>> { [Armour] = edits }));
        Assert.Empty(result.Warnings);

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-armour-" + Guid.NewGuid().ToString("N"));
        try
        {
            var built = Assert.Single(result.Packages);
            var header = Path.Combine(dir, built.HeaderFilePath());
            await built.Bytes.WriteAsync(header[..^".uasset".Length]);
            using var loose = AssetCatalog.OpenLoose(dir);
            var info = new MaterialInspector(loose).Inspect(Armour + ".MI_WW_Armor");

            Assert.Equal(new System.Numerics.Vector4(1, 0.49f, 0.026f, 1), info.Vectors.Single(v => v.Name == "Base Color A").Value);
            Assert.Equal(new System.Numerics.Vector4(1, 0.49f, 0.026f, 1), info.Vectors.Single(v => v.Name == "Base Color B").Value);
            Assert.Equal(new System.Numerics.Vector4(0.1f, 0.1f, 0.1f, 0), info.Vectors.Single(v => v.Name == "Dirt Color").Value);
            Assert.Equal(1f, info.Scalars.Single(s => s.Name == "CarPaint Metalness").Value);
            Assert.Equal(0.9f, info.Scalars.Single(s => s.Name == "ClearCoat Amount").Value);
            Assert.EndsWith("T_FlatWhiteMask_Dummy_01", info.Textures.Single(t => t.Name == "Color Mask").TexturePath, StringComparison.Ordinal);
            Assert.EndsWith("T_FlatWhiteColor_Dummy_01", info.Textures.Single(t => t.Name == "Diffuse Map").TexturePath, StringComparison.Ordinal);
            Assert.Contains(info.Textures, t => t.Name == "Normal Map" && t.TexturePath.EndsWith("T_Armor_N", StringComparison.Ordinal)); // the rest kept

            // The new entries carry the parent's guid, the same one the Rager's own Base Color A has.
            var rager = ModdableAssets.ReadPackage(catalog, "/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_Rager_Outer");
            var guids = MaterialParameters.ExpressionGuids(ModdableAssets.ReadPackage(catalog, MaterialParameters.ParentPackage(rager)!));
            Assert.Equal("87F8B3DD0746EB4398D26094568B44F1", Convert.ToHexString(guids["Base Color A"]));
            var armour = ScumStudio.Formats.Packages.CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, null, Armour);
            var payload = armour.GetExportData(0).ToArray();
            Assert.True(payload.AsSpan().IndexOf(guids["Base Color A"]) > 0);

            // The new texture is imported and the material waits for it like for its others (create before serialize).
            var import = armour.Imports.ToList().FindIndex(i => armour.ResolveName(i.ObjectName) == "T_FlatWhiteColor_Dummy_01");
            Assert.True(import >= 0);
            var e = armour.Exports[0];
            var deps = armour.ReadPreloadDependencies();
            Assert.Contains(-(import + 1), deps.Skip(e.FirstExportDependency + e.SerializationBeforeSerializationDependencies).Take(e.CreateBeforeSerializationDependencies));
            Assert.Equal(e.FirstExportDependency + e.PreloadDependencyTotal, deps.Length);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
