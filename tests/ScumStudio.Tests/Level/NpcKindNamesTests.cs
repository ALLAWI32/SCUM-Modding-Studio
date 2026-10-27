using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>The NPC wizard shows readable names for the game's encounter and character names.</summary>
public sealed class NpcKindNamesTests
{
    [Theory]
    [InlineData("MTZ_POI_Military_Base_NPC_Encounter", NpcThreat.Medium, "Military Base")]
    [InlineData("HTZ_Military_TV_Bunker_NPC_Encounter", NpcThreat.High, "Military TV Bunker")]
    [InlineData("LTZ_Village_NPC_Encounter", NpcThreat.Low, "Village")]
    [InlineData("Village_NPC_Encounter", NpcThreat.Unknown, "Village")]
    [InlineData("NPC_Encounter", NpcThreat.Unknown, "NPC Encounter")] // nothing left: the whole name, readable
    public void AnEncounterNameBecomesAThreatLevelAndAPlace(string name, NpcThreat threat, string place)
    {
        Assert.Equal(threat, NpcKindNames.ThreatOf(name));
        Assert.Equal(place, NpcKindNames.PlaceOf(name));
    }

    [Theory]
    [InlineData("BP_Guard_Lvl_3", "Guard Lvl 3")]
    [InlineData("Drifter_Class_01", "Drifter Class 1")]
    [InlineData("BP_Guard_Lvl_10", "Guard Lvl 10")]
    [InlineData("BP_NPC", "BP NPC")] // nothing left: the whole name, readable
    public void ACharacterNameLosesItsPrefixAndLeadingZeros(string name, string shown)
    {
        Assert.Equal(shown, NpcKindNames.CharacterOf(name));
    }
}
