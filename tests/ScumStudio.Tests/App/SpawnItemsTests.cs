using ScumStudio.App.ViewModels;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Spawns page's rows: a value the game, the project or the server's file already holds is shown as it is (never
/// rounded, clamped or rewritten) and is not an edit until the user moves it.
/// </summary>
public sealed class SpawnItemsTests
{
    [Fact]
    public void RowsFrom2True1000000AndMinus1AreNotPending()
    {
        // Hand-written ServerSettings.ini values: whole numbers, lower-case and 1/0 switches, values beyond the slider, -1.
        var rows = new[]
        {
            new ServerSettingViewModel("scum.MaxAllowedDrones", "Drones", string.Empty, "2", false, true, 0, 50, 1),
            new ServerSettingViewModel("scum.PuppetsCanOpenDoors", "Doors", string.Empty, "true", true, false, 0, 1, 1),
            new ServerSettingViewModel("scum.AllowSectorRespawn", "Sector", string.Empty, "1", true, false, 0, 1, 1),
            new ServerSettingViewModel("scum.SectorLocationPrice", "Price", string.Empty, "1000000", false, true, 0, 1000, 1),
            new ServerSettingViewModel("scum.EncounterBaseCharacterAmountMultiplier", "Amount", string.Empty, "1.5", false, false, 0, 5, 0.05),
            new ServerSettingViewModel("scum.MaxAllowedPuppets", "Puppets", string.Empty, "-1", false, true, 0, 2000, 10),
        };
        var texts = new[] { "2", "true", "1", "1000000", "1.5", "-1" };

        for (var i = 0; i < rows.Length; i++)
        {
            Assert.False(rows[i].IsPending, rows[i].Key);
            Assert.Equal(texts[i], rows[i].NewText);
        }

        // A switch turned off and on again is not an edit either.
        rows[1].Flag = false;
        rows[1].Flag = true;
        Assert.False(rows[1].IsPending);
        Assert.Equal(1000000, rows[3].Value);
    }

    [Fact]
    public void StoredValueAboveTheMaximumKeepsItsValueAndIsNotPending()
    {
        // 500 m stored as 50000 cm on a 0..300 m slider; 500 zombies on a 0..200 server slider.
        var distance = new SpawnSliderViewModel("Distance", string.Empty, 0, 300, 1, "m", [Target("50000")], scale: 100);
        var puppets = new ServerSettingViewModel("scum.MaxAllowedPuppets", "Puppets", string.Empty, "500", false, true, 0, 200, 10);

        Assert.Equal(500, distance.Value);
        Assert.True(distance.Maximum >= 500);
        Assert.False(distance.IsPending);
        Assert.Empty(distance.Changes());
        Assert.Equal(500, puppets.Value);
        Assert.True(puppets.Maximum >= 500);
        Assert.False(puppets.IsPending);
        Assert.True(distance.IsOutOfRange);
        Assert.True(puppets.IsOutOfRange);
    }

    [Fact]
    public void StoredWeight075IsNotRoundedTo1()
    {
        var weight = new SpawnSliderViewModel("Weight", string.Empty, 0, 100, 1, string.Empty, [Target("0.75")]);

        Assert.Equal(0.75, weight.Value, 6);
        Assert.False(weight.IsPending);
        Assert.Empty(weight.Changes());
    }

    [Fact]
    public void ResetGoesToTheGameValue()
    {
        // The game stores 40; the project made it 60.
        var fuel = new SpawnSliderViewModel("Fuel", "Fuel at spawn", 0, 100, 1, "%", [Target("40", committed: "60")]);
        Assert.Equal(60, fuel.Value);
        Assert.Contains("40", fuel.Tip, StringComparison.Ordinal);

        fuel.ResetCommand.Execute(null);

        Assert.Equal(40, fuel.Value);
        Assert.True(fuel.IsStock);
        Assert.True(fuel.IsPending);
        Assert.Equal(["40"], fuel.Changes().Select(c => c.Value));
    }

    [Fact]
    public void ChildEncounterSharesFollowTheGameAsSpawnChances()
    {
        // A *_Child encounter draws from its parent's preset list (same package and export, Of = the child): its rows
        // follow the child's 25 %, and every character's overall share is the game's (SpawnChances.Effective).
        const string encounters = "/Game/ConZ_Files/Encounters/EncounterClasses/Enc_A";
        SpawnWeightList Presets(string of) => new(encounters, "Default__Enc_A_C", SpawnCategory.Zombies, 1,
            [new SpawnWeight("Preset_X", "_possibleCharacters[0].Value", 1f), new SpawnWeight("Preset_Y", "_possibleCharacters[1].Value", 3f)]) { Of = of };
        IReadOnlyList<SpawnWeightList> lists =
        [
            new("/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Test", "MTZ_Test", SpawnCategory.Other, 0,
                [new SpawnWeight("Enc_A", "EncounterData[0].EncounterWeight", 3f), new SpawnWeight("Enc_A_Child", "EncounterData[1].EncounterWeight", 1f)]),
            Presets("Enc_A"),
            Presets("Enc_A_Child"),
            new("/Game/ConZ_Files/Encounters/Character_Presets/Preset_X", "Preset_X", SpawnCategory.Zombies, 2, [new SpawnWeight("BP_Zombie_1", "CharacterClasses[0].Value", 1f)]),
            new("/Game/ConZ_Files/Encounters/Character_Presets/Preset_Y", "Preset_Y", SpawnCategory.Zombies, 2, [new SpawnWeight("BP_Zombie_2", "CharacterClasses[0].Value", 1f)]),
        ];
        var groups = lists.Select(list =>
        {
            var group = new SpawnWeightGroup(list, 1, () => { });
            foreach (var entry in list.Entries)
            {
                _ = new SpawnWeightRowViewModel(group, entry, entry.Name, entry.Name, entry.Weight);
            }

            group.Commit();
            return group;
        }).ToList();

        SpawnWeightTree.UpdateOverall(groups);

        Assert.Equal(25d * 0.25, groups[2].Rows[0].Overall, 6); // the child's Preset_X: 25 % of the zone x 25 % of the list
        var page = groups.Where(g => g.List.Level == 2).ToDictionary(g => g.List.Entries[0].Name, g => g.Rows[0].Overall);
        Assert.All(SpawnChances.Effective(lists), c => Assert.Equal(c.Percent, page[c.Character], 6));
        Assert.Equal(SpawnCategory.Zombies, SpawnWeightTree.KindOf("Enc_A_Child", lists));
    }

    private static SpawnTarget Target(string stock, string? committed = null) =>
        new("/Game/ConZ_Files/Test/Preset", new Tunable("Default__Preset_C", "Value", TunableKind.Float, stock), committed ?? stock);
}
