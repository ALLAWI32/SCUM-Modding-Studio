using ScumStudio.Assets.Materials;

namespace ScumStudio.Tests.Assets;

/// <summary>Which texture of a SCUM material is the object's own colour (buildings and rocks rendered white before).</summary>
public sealed class MaterialPickTests
{
    private static TextureParameter T(string name, string texture) => new(name, "/Game/T/" + texture + "." + texture, "MI");

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
}
