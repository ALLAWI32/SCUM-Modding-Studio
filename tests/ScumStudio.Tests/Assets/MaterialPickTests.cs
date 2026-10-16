using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Assets;

/// <summary>Which texture of a SCUM material is the object's own colour (buildings and rocks rendered white before).</summary>
public sealed class MaterialPickTests
{
    private static TextureParameter T(string name, string texture) => new(name, "/Game/T/" + texture + "." + texture, "MI");

    [Fact]
    public void MasterDefaultColourBeatsBlendLayer()
    {
        // The instance sets only the patch layer; the wall's 'Color' is the master's own default (audit F264).
        Assert.Null(MaterialInspector.PickTexture([T("B Material Diffuse", "Wall_Patches_02_D"), T("Blend Color/Alpha", "T_Floor_Dirt_03_D")]));

        var textures = new Dictionary<string, TextureParameter>(StringComparer.OrdinalIgnoreCase) { ["B Material Diffuse"] = T("B Material Diffuse", "Wall_Patches_02_D") };
        MaterialInspector.AddDefaults(textures, [T("Color", "T_HospitalPlasterWall_03a_D"), T("B Material Diffuse", "T_Master_Patch_D")], t => t.Name);
        Assert.Equal("/Game/T/Wall_Patches_02_D.Wall_Patches_02_D", textures["B Material Diffuse"].TexturePath);
        Assert.Equal("/Game/T/T_HospitalPlasterWall_03a_D.T_HospitalPlasterWall_03a_D", MaterialInspector.PickTexture([.. textures.Values]));
        Assert.Equal("/Game/T/T_HospitalPlasterWall_03a_D.T_HospitalPlasterWall_03a_D",
            MaterialInspector.Pick(new([], [], [T("B Material Diffuse", "Wall_Patches_02_D")]), new([], [], [T("Color", "T_HospitalPlasterWall_03a_D")])).BaseColor);
    }

    [Fact]
    public void MasterDefaultDoesNotBeatAnInstanceTextureOfAnotherName()
    {
        // The instance's 'Diffuse' is the colour; the master's 'Base Color' default sits behind a switch the instance turned
        // off. Ranked together, 'Base Color' came first and the master's grime was drawn.
        var picked = MaterialInspector.Pick(new([], [], [T("Diffuse", "T_Crate_D")]), new([], [], [T("Base Color", "T_Grime_D")]));
        Assert.Equal("/Game/T/T_Crate_D.T_Crate_D", picked.BaseColor);
    }

    [Fact]
    public void MasterDefaultNormalDoesNotBeatAnInstanceNormalOfAnotherName()
    {
        var picked = MaterialInspector.Pick(new([], [], [T("Normal Map", "T_Crate_N")]), new([], [], [T("Normal", "T_Grime_N")]));
        Assert.Equal("/Game/T/T_Crate_N.T_Crate_N", picked.Normal);
        Assert.Equal("/Game/T/T_Grime_N.T_Grime_N", MaterialInspector.Pick(new([], [], []), new([], [], [T("Normal", "T_Grime_N")])).Normal);
    }

    [Fact]
    public void MasterScaleDefaultsAloneLeaveTheSurfaceGrey()
    {
        // The masters' white 'Base Material Tint' and brightness 1 are no colour of their own: an untextured surface stays
        // the neutral grey (before: white), while a colour the master gives by default and an instance's brightness count.
        var white = new VectorParameter("Base Material Tint", Vector4.One, "M_Master");
        var masterOnly = MaterialInspector.Pick(new([], [], []), new([new ScalarParameter("Base Material Brightness", 1f, "M_Master")], [white], []));
        Assert.Null(masterOnly.Tint);
        Assert.Null(masterOnly.Scale);

        var red = new Vector4(1f, 0f, 0f, 1f);
        Assert.Equal(red, MaterialInspector.Pick(new([], [], []), new([], [new VectorParameter("Color", red, "M_Master")], [])).Tint);
        var darker = MaterialInspector.Pick(new([new ScalarParameter("Base Material Brightness", 0.5f, "MI")], [], []), new([], [white], []));
        Assert.Equal(new Vector3(0.5f), darker.Scale);
    }

    [Fact]
    public void SpeedTreeColourTexturesAreDiffuse()
    {
        // The SpeedTree9 master's trees: ScotsPine_01_Color is the bark and needles; subsurface and masks are not colours.
        Assert.True(MaterialInspector.LooksLikeDiffuse("/Game/F/ScotsPine_01_Color.ScotsPine_01_Color"));
        Assert.True(MaterialInspector.LooksLikeDiffuse("/Game/F/European_White_Birch_Color.European_White_Birch_Color"));
        Assert.False(MaterialInspector.LooksLikeDiffuse("/Game/F/European_White_Birch_Subsurface.European_White_Birch_Subsurface"));
        Assert.False(MaterialInspector.LooksLikeDiffuse("/Game/F/T_Bush_03a_Mask.T_Bush_03a_Mask"));
        Assert.False(MaterialInspector.LooksLikeDiffuse("/Game/F/T_Leaves_SSS_Color.T_Leaves_SSS_Color"));
        Assert.True(MaterialInspector.LooksLikeDiffuse("/Game/F/T_Bush_03a_D.T_Bush_03a_D"));
    }

    [Fact]
    public void InstanceClipOverrideIsRead()
    {
        // MI_Leaves_Sparse overrides only the clip of its parent's masked blend (audit F348): the parent's 0.33 was used.
        using var temp = new LevelTempDirectory();
        WriteInstance(temp, "MI_Leaves", "/Game/T/M_Missing", "M_Missing", "Material", o =>
        {
            o.Bool("bOverride_BlendMode", true);
            o.ByteEnum("BlendMode", "EBlendMode", "BLEND_Masked");
            o.Float("OpacityMaskClipValue", 0.33f);
        });
        WriteInstance(temp, "MI_Leaves_Sparse", "/Game/T/MI_Leaves", "MI_Leaves", "MaterialInstanceConstant", o =>
        {
            o.Bool("bOverride_OpacityMaskClipValue", true);
            o.Float("OpacityMaskClipValue", 0.6f);
        });
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var info = new MaterialInspector(catalog).Inspect("/Game/T/MI_Leaves_Sparse.MI_Leaves_Sparse");
        Assert.Equal(0.6f, info.OpacityMaskClip.GetValueOrDefault(), 3);
    }

    private static void WriteInstance(LevelTempDirectory temp, string name, string parentPackage, string parentName, string parentClass, Action<SyntheticLevelPackage.TagWriter> overrides)
    {
        var p = new SyntheticLevelPackage();
        var parent = p.ObjectImport(parentPackage, "/Script/Engine", parentClass, parentName);
        var instance = p.Export(name, p.ScriptClass("/Script/Engine", "MaterialInstanceConstant"), 0);
        p.SetPayload(instance, p.Properties(t =>
        {
            t.Object("Parent", parent);
            t.TaggedStruct("BasePropertyOverrides", "MaterialInstanceBasePropertyOverrides", overrides);
        }));
        p.Build().WriteAsync(temp.Combine("SCUM", "Content", "T", name), ".uasset").GetAwaiter().GetResult();
    }

    [Fact]
    public void MasterShaderColourBeatsBlendLayers()
    {
        // MI_Plaster_CinderBlocks (hospital walls): the 'Color' slot is the wall, the 'B Material Diffuse' a patch decal.
        var picked = MaterialInspector.PickTexture(
        [
            T("B Material Diffuse", "Wall_Patches_02_D"),
            T("Blend Color/Alpha", "T_Floor_Dirt_03_D"),
            T("Color", "T_HospitalPlasterWall_03a_D"),
            T("Normal", "T_HospitalPlasterWall_03_N"),
        ]);
        Assert.Equal("/Game/T/T_HospitalPlasterWall_03a_D.T_HospitalPlasterWall_03a_D", picked);
    }

    [Fact]
    public void OverlaysAreNeverTheBaseColour()
    {
        // MI_LargeRock_01_Coastal: only masks, normals and a moss overlay; the rock colour comes from the master.
        Assert.Null(MaterialInspector.PickTexture(
        [
            T("Atlas_Masks", "T_Large_Rock_01_AO_Edge_Noise"),
            T("Atlas_Normal", "T_Large_Rock_01_N"),
            T("Organic Overlay D", "T_Moss_D"),
        ]));
        Assert.Equal("/Game/T/T_ArmoryBuilding_ATLAS_D.T_ArmoryBuilding_ATLAS_D",
            MaterialInspector.PickTexture([T("Normal", "T_ArmoryBuilding_ATLAS_N"), T("Diffuse", "T_ArmoryBuilding_ATLAS_D")]));
    }

    [Fact]
    public void TheMastersSnowLayerIsNeverTheBaseColour()
    {
        // D06 (seviscache, defolt_0999): Mi_Cliff_01 and MI_Shelves_04_Inst set only masks and normals, their colour slots
        // hold dummies; the master's 'SnowTexture' default (T_SnowTexture, a normal map cooked as TC_Default BC7) matched the
        // "Texture" hint and the cliffs and shelves were drawn purple-blue on the Map.
        var cliff = MaterialInspector.Pick(
            new([], [], [T("Atlas_Masks", "T_Cliff_01_Dirt_Edge_Mask"), T("Atlas_Normal", "T_Cliff_01_N"), T("Organic Overlay D", "T_Moss_D")]),
            new([], [], [T("PrecipitationRT", "RT_PrecipitationMask"), T("SnowMask", "T_FlatWhiteMask_Dummy_01"), T("SnowTexture", "T_SnowTexture")]));
        Assert.Null(cliff.BaseColor);
        Assert.Equal("/Game/T/T_Cliff_01_N.T_Cliff_01_N", cliff.Normal);

        var shelf = MaterialInspector.Pick(
            new([], [], [T("BM Normal", "T_PlasterDetail_N"), T("Mask 01", "T_MarketShelve_04_Mask"), T("Mesh Stains/AO Map", "T_MarketShelve_04_DetailMask")]),
            new([], [], [T("BM Diffuse", "T_FlatWhiteColor_Dummy_01"), T("SnowCoverageMask", "T_Snow_Mask"), T("SnowTexture", "T_SnowTexture")]));
        Assert.Null(shelf.BaseColor);

        // Moss and dirt layers named "... Texture" neither (a Zil truck drew T_CoastalRock_Moss_06_D), while a real colour still wins.
        Assert.Null(MaterialInspector.PickTexture([T("Moss Texture", "T_CoastalRock_Moss_06_D"), T("Snow Texture", "T_Snow_02_D")]));
        Assert.Equal("/Game/T/Gravel_Road_02_D.Gravel_Road_02_D", MaterialInspector.PickTexture([T("Snow Texture", "T_Snow_02_D"), T("Road Texture", "Gravel_Road_02_D")]));

        // M_Background_01 (the far hills): its first "...Texture" holds the normal map.
        Assert.Equal("/Game/T/Background_01_D.Background_01_D",
            MaterialInspector.PickTexture([T("Base_DistaneTexture_1", "Background_01_N"), T("DistaneTexture", "Background_01_D")]));
    }

    [Fact]
    public void EmissiveAndSubsurfaceTintsAreNoSurfaceColour()
    {
        // With the snow layer gone, untextured masters (SCUM spells it 'Emisive') and road sides ('SS Color') drew black.
        var white = new Vector4(1f, 1f, 1f, 1f);
        Assert.Equal(white, MaterialInspector.PickTint([new("B Emisive Tint", new Vector4(0f, 0f, 0f, 1f), "M"), new("B Material Tint", white, "M")]));
        Assert.Null(MaterialInspector.PickTint([new("SS Color", new Vector4(0f, 0f, 0f, 1f), "M_SideObject_RVT")]));
        Assert.Null(MaterialInspector.PickTint([new("Subsurface Color", new Vector4(0.2f, 0.5f, 0.1f, 1f), "M")]));
    }

    [Fact]
    public void TheObjectsOwnNormalMap_NotADetailOrBlendLayerOrAFlatDummy()
    {
        // MI_CliffRock_01: 'Normal' is the rock's; 'DetailNormal' a tiled flat dummy.
        Assert.Equal("/Game/T/T_CliffRock_01_N.T_CliffRock_01_N", MaterialInspector.PickNormal(
        [
            T("DetailNormal", "T_FlatNormal_Dummy_01"),
            T("Color", "T_CliffRock_01_D"),
            T("Normal", "T_CliffRock_01_N"),
        ]));
        Assert.Equal("/Game/T/T_Large_Rock_01_N.T_Large_Rock_01_N", MaterialInspector.PickNormal([T("Atlas_Normal", "T_Large_Rock_01_N"), T("Atlas_Masks", "T_M")]));
        Assert.Null(MaterialInspector.PickNormal([T("Detail Normal", "T_Concrete_Detail_Normal_01"), T("B Material Normal Blend", "T_X_N")]));
        Assert.Null(MaterialInspector.PickNormal([T("Normal", "T_FlatNormal_Dummy_01")]));
    }
}
