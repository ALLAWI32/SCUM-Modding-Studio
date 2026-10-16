using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "the percentage of every zombie". A zone draws an encounter by its weight, the encounter a preset by its weight,
/// the preset a character by its weight: the real share of a character is the product along the way, and the shares of a
/// zone sum to 100 %.
/// </summary>
public sealed class SpawnChancesTests
{
    [Fact]
    public void VillageSeventyFiveTwentyFiveSplitsIntoTypePercentagesThatSumTo100()
    {
        var chances = SpawnChances.Effective(Village(75f, 25f));

        Assert.Equal(100d, chances.Sum(c => c.Percent), 6);
        Assert.Equal(56.25d, Percent(chances, "BP_Zombie_Civilian"), 6); // 75 % × 3/4
        Assert.Equal(18.75d, Percent(chances, "BP_Zombie_Police"), 6); // 75 % × 1/4
        Assert.Equal(12.5d, Percent(chances, "BP_Drifter_01"), 6); // 25 % × 1/2 × 1
        Assert.Equal(12.5d, Percent(chances, "BP_Guard_01"), 6);
        Assert.Equal(SpawnCategory.Zombies, chances.Single(c => c.Character == "BP_Zombie_Police").Kind);
        Assert.Equal(SpawnCategory.Npcs, chances.Single(c => c.Character == "BP_Guard_01").Kind);
        Assert.Equal("BP_Zombie_Civilian", chances[0].Character); // largest first
    }

    [Fact]
    public void ZeroWeightIsOff()
    {
        // NPCs off: only zombies, still 100 % together; a preset weighed 0 inside an encounter is off too.
        var noNpcs = SpawnChances.Effective(Village(75f, 0f));
        Assert.DoesNotContain(noNpcs, c => c.Kind == SpawnCategory.Npcs);
        Assert.Equal(100d, noNpcs.Sum(c => c.Percent), 6);
        Assert.Equal(75d, Percent(noNpcs, "BP_Zombie_Civilian"), 6);

        var noGuards = SpawnChances.Effective(Village(75f, 25f, guard: 0f));
        Assert.DoesNotContain(noGuards, c => c.Character == "BP_Guard_01");
        Assert.Equal(25d, Percent(noGuards, "BP_Drifter_01"), 6);

        // Everything off, or nothing known: nothing spawns.
        Assert.Empty(SpawnChances.Effective(Village(0f, 0f)));
        Assert.Empty(SpawnChances.Effective([]));
    }

    [Fact]
    public void FiftyFiftyAndTwentyEighty()
    {
        var even = SpawnChances.Effective(Village(50f, 50f));
        Assert.Equal(50d, even.Where(c => c.Kind == SpawnCategory.Zombies).Sum(c => c.Percent), 6);
        Assert.Equal(50d, even.Where(c => c.Kind == SpawnCategory.Npcs).Sum(c => c.Percent), 6);

        // Weights are relative: 1 and 4 are 20 % and 80 %, the same as 20 and 80.
        foreach (var (zombies, npcs) in new[] { (20f, 80f), (1f, 4f) })
        {
            var chances = SpawnChances.Effective(Village(zombies, npcs));
            Assert.Equal(20d, chances.Where(c => c.Kind == SpawnCategory.Zombies).Sum(c => c.Percent), 6);
            Assert.Equal(80d, chances.Where(c => c.Kind == SpawnCategory.Npcs).Sum(c => c.Percent), 6);
            Assert.Equal(100d, chances.Sum(c => c.Percent), 6);
        }
    }

    [Fact]
    public void AnEncounterWhosePresetsComeFromItsParentIsFollowed()
    {
        // A *_Child encounter stores no presets: its list is its parent's package, but it details the child's row.
        var lists = new List<SpawnWeightList>
        {
            List("/Game/Zones/MTZ_Test", 0, SpawnCategory.Other, ("MTZ_Test_Encounter_Child", 1f)),
            List("/Game/Encounters/MTZ_Test_Encounter", 1, SpawnCategory.Npcs, ("Guard_Test", 1f)) with { Of = "MTZ_Test_Encounter_Child" },
            List("/Game/Presets/Guard_Test", 2, SpawnCategory.Npcs, ("BP_Guard_01", 1f)),
        };

        var chance = Assert.Single(SpawnChances.Effective(lists));
        Assert.Equal(("BP_Guard_01", 100d), (chance.Character, chance.Percent));
    }

    /// <summary>A village zone: a zombie encounter (one preset: 3 civilians to 1 policeman) and an NPC encounter (drifters and guards, 1 to 1).</summary>
    private static List<SpawnWeightList> Village(float zombies, float npcs, float guard = 1f) =>
    [
        List("/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village", 0, SpawnCategory.Other, ("MTZ_Village_Character_Encounter", zombies), ("MTZ_Village_NPC_Encounter", npcs)),
        List("/Game/ConZ_Files/Encounters/EncounterClasses/MTZ_Village_Character_Encounter", 1, SpawnCategory.Zombies, ("Zombies_Civilian_All", 1f)),
        List("/Game/ConZ_Files/Encounters/EncounterClasses/MTZ_Village_NPC_Encounter", 1, SpawnCategory.Npcs, ("Drifter_Village", 1f), ("Guard_Village", guard)),
        List("/Game/ConZ_Files/Encounters/Character_Presets/Zombies/Zombies_Civilian_All", 2, SpawnCategory.Zombies, ("BP_Zombie_Civilian", 3f), ("BP_Zombie_Police", 1f)),
        List("/Game/ConZ_Files/Encounters/Character_Presets/NPCs/Drifter_Village", 2, SpawnCategory.Npcs, ("BP_Drifter_01", 1f)),
        List("/Game/ConZ_Files/Encounters/Character_Presets/NPCs/Guard_Village", 2, SpawnCategory.Npcs, ("BP_Guard_01", 1f)),
    ];

    private static SpawnWeightList List(string package, int level, SpawnCategory category, params (string Name, float Weight)[] entries) =>
        new(package, "Default", category, level, entries.Select((e, i) => new SpawnWeight(e.Name, $"[{i}]", e.Weight)).ToList());

    private static double Percent(IReadOnlyList<SpawnChance> chances, string character) => chances.Single(c => c.Character == character).Percent;
}
