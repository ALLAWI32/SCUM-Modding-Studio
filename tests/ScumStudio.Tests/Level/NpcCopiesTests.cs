using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// An own copy of an NPC (no game files): the names of the copies and the rows of the zone settings as the copy reads them.
/// The real chain, built and exported from the game's files: <c>NpcCopiesRealTests</c>.
/// </summary>
public sealed class NpcCopiesTests
{
    [Fact]
    public void TheSuffixIsTheFirstOneThatFreesEveryName()
    {
        string[] leaves = ["BP_Guard_Lvl_3", "Guard_Class_03"];
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("_Own", NpcCopies.SuggestSuffix(leaves, none));

        // One taken name moves every copy to the next suffix, so the chain stays named alike.
        Assert.Equal("_OwnB", NpcCopies.SuggestSuffix(leaves, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Guard_Class_03_Own" }));
        Assert.Equal("_OwnC", NpcCopies.SuggestSuffix(leaves, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BP_Guard_Lvl_3_own", "guard_class_03_ownb" }));

        // Never ends in _<number> (the game reads that as an FName number), also far down the list.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BP_Guard_Lvl_3_Own" };
        foreach (var letter in "BCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            taken.Add("BP_Guard_Lvl_3_Own" + letter);
        }

        var far = NpcCopies.SuggestSuffix(leaves, taken);
        Assert.Equal("_OwnAA", far);
        Assert.Matches("[A-Za-z]$", far);
    }

    [Fact]
    public void TheZoneRowsAreTheStockOnesNamedAfterTheCopiedEncounter()
    {
        const string Zone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_POI_Military_Base";
        const string Encounter = "/Game/ConZ_Files/Encounters/EncounterClasses/Medium_Threat/MTZ_POI_Military_Base_NPC_Encounter";
        var stock = new SpawnWeightList(Zone, "MTZ_POI_Military_Base", SpawnCategory.Other, 0,
        [
            new SpawnWeight("MTZ_POI_Military_Base_Character_Encounter", "EncounterData[0].EncounterWeight", 65f),
            new SpawnWeight("MTZ_POI_Military_Base_NPC_Encounter", "EncounterData[1].EncounterWeight", 35f),
        ]);
        var chain = new CloneAssetOp(ZoneCopies.Kind, Zone, Zone + "_Own", [new PackagePair(Zone, Zone + "_Own"), new PackagePair(Encounter, Encounter + "_Own")]);

        var rows = NpcCopies.Rows(stock, chain);

        // The copied encounter's row has its new name; the zombie encounter, not copied, keeps its own. Paths and weights stay.
        Assert.Equal(Zone + "_Own", rows.Package);
        Assert.Equal("MTZ_POI_Military_Base_Own", rows.Export);
        Assert.Equal(
            [("MTZ_POI_Military_Base_Character_Encounter", "EncounterData[0].EncounterWeight", 65f), ("MTZ_POI_Military_Base_NPC_Encounter_Own", "EncounterData[1].EncounterWeight", 35f)],
            rows.Entries.Select(e => (e.Name, e.Path!, e.Weight)));
        Assert.True(rows.IsEditable);
    }

    [Fact]
    public void TheCopiesValueEditsFollowTheCloneInTheOnePlan()
    {
        const string Zone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_POI_Military_Base";
        const string Encounter = "/Game/ConZ_Files/Encounters/EncounterClasses/Medium_Threat/MTZ_POI_Military_Base_NPC_Encounter";
        var chain = new CloneAssetOp(ZoneCopies.Kind, Zone, Zone + "_Own", [new PackagePair(Zone, Zone + "_Own"), new PackagePair(Encounter, Encounter + "_Own")]);
        var type = new NpcGuardType(Encounter + "_Own." + "MTZ_POI_Military_Base_NPC_Encounter_Own_C", ["BP_Guard_Lvl_3_Own"], Zone + "_Own");
        var rows = new SpawnWeightList(Zone + "_Own", "MTZ_POI_Military_Base_Own", SpawnCategory.Other, 0,
        [
            new SpawnWeight("MTZ_POI_Military_Base_NPC_Encounter_Own", "EncounterData[0].EncounterWeight", 35f),
        ]);
        var tune = new SetAssetValueOp(Encounter + "_Own", "Default__MTZ_POI_Military_Base_NPC_Encounter_Own_C", "_possibleCharacters[1].Value", "Float", "30", "0");

        var ops = NpcPlacement.Plan([new NpcPostGroup([new ScumStudio.Core.Mathematics.FVector(0f, 0f, 0f)])], type, chain, rows, [], null, null, [tune]);

        // The clone first, then the copy's own edits, then the zone settings' (the edits name packages the clone creates).
        var at = ops.ToList();
        Assert.True(at.IndexOf(chain) >= 0 && at.IndexOf(tune) == at.IndexOf(chain) + 1, "the copy's edit does not follow its clone");
    }
}
