using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// igor8802: "assign NPCs instead of zombies". In a zone's own settings (a copy the mod creates) a row of
/// <c>EncounterData</c> is re-pointed to another encounter class: the class's import is added when the package does not
/// have it, the 4 bytes of the reference change and nothing else, and the value reads back. A game asset is shared by
/// every zone that uses it, so there it is refused. Synthetic zone settings, no game files.
/// </summary>
public sealed class TunableObjectRefTests
{
    private const string Folder = "/Game/ConZ_Files/Encounters/EncounterClasses/";
    private const string Zombies = Folder + "MTZ_Village_Encounter.MTZ_Village_Encounter_C";
    private const string Npcs = Folder + "MTZ_Village_NPC_Encounter.MTZ_Village_NPC_Encounter_C";
    private const string Guards = Folder + "MTZ_Bunker_Guard_Encounter.MTZ_Bunker_Guard_Encounter_C";
    private const string Row0 = "MTZ_Village_Own|EncounterData[0].EncounterClass";
    private const string Row1 = "MTZ_Village_Own|EncounterData[1].EncounterClass";

    [Fact]
    public void RepointingAnEncounterClassAddsTheImportAndReadsBack()
    {
        var package = Copy();
        Assert.Equal(Zombies, References(package)[Row0]);
        Assert.Equal(Npcs, References(package)[Row1]);

        // Row 0 to a class the package does not import yet, row 1 to the class row 0 had.
        var result = TunablePatcher.Apply(package, [Edit(0, Guards), Edit(1, Zombies)], isClone: true);
        var patched = Parse(result);
        Assert.Equal(Guards, References(patched)[Row0]);
        Assert.Equal(Zombies, References(patched)[Row1]);
        Assert.Contains((Row0, Zombies, Guards), result.Applied);

        // One package import and one class import added, of the class the reference had (a Blueprint class).
        Assert.Equal(package.Imports.Count + 2, patched.Imports.Count);
        var added = patched.Imports[^1];
        Assert.Equal(("/Script/Engine", "BlueprintGeneratedClass", "MTZ_Bunker_Guard_Encounter_C"),
            (patched.ResolveName(added.ClassPackage), patched.ResolveName(added.ClassName), patched.ResolveName(added.ObjectName)));
        Assert.Contains(-patched.Imports.Count, patched.ReadPreloadDependencies()); // created before the zone is read

        // None empties a row.
        var none = Parse(TunablePatcher.Apply(patched, [Edit(1, "None")], isClone: true));
        Assert.Equal(string.Empty, References(none)[Row1]);
        Assert.Equal(Guards, References(none)[Row0]);
    }

    [Fact]
    public void OtherBytesUnchanged()
    {
        var package = Copy();
        var before = package.GetExportData(0).ToArray();
        var patched = Parse(TunablePatcher.Apply(package, [Edit(1, Zombies)], isClone: true));
        var after = patched.GetExportData(0).ToArray();

        // An import the package has already: no table grows, and only the 4 bytes of row 1's reference differ.
        Assert.Equal(package.Imports.Count, patched.Imports.Count);
        Assert.Equal(package.Names, patched.Names);
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.InRange(changed.Count, 1, 4);
        Assert.True(changed[^1] - changed[0] < 4);
        Assert.Equal(TunableReader.Read(package).Select(t => (t.Key, t.Value)), TunableReader.Read(patched).Select(t => (t.Key, t.Value)));

        // Re-pointing and re-weighing in one go (one type only: the NPCs at 100 %).
        var both = Parse(TunablePatcher.Apply(package, [Edit(0, Npcs), new TunableEdit("MTZ_Village_Own", "EncounterData[0].EncounterWeight", "1")], isClone: true));
        Assert.Equal(Npcs, References(both)[Row0]);
        Assert.True(TunableValue.AreEqual(TunableKind.Float, TunableReader.Read(both).Single(t => t.Path == "EncounterData[0].EncounterWeight").Value, "1"));
    }

    [Fact]
    public void StockPackageIsRefused()
    {
        var package = Copy();
        var refused = Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(package, [Edit(0, Npcs)]));
        Assert.Contains("copy of its own", refused.Message, StringComparison.Ordinal);

        // Not an object path, or a non-class object the reference held no import for: not written.
        Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(package, [Edit(0, "MTZ_Village_NPC_Encounter")], isClone: true));
        Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(package, [new TunableEdit("MTZ_Village_Own", "EncounterData[5].EncounterClass", Npcs)], isClone: true));

        // Numbers of a game asset are still written as before.
        var weight = Parse(TunablePatcher.Apply(package, [new TunableEdit("MTZ_Village_Own", "EncounterData[1].EncounterWeight", "0")]));
        Assert.True(TunableValue.AreEqual(TunableKind.Float, TunableReader.Read(weight).Single(t => t.Path == "EncounterData[1].EncounterWeight").Value, "0"));
    }

    [Fact]
    public void TargetEndingInUnderscoreNumberIsRefused()
    {
        // Review (audit C1 pattern): the added import names are stored whole, and the engine reads Arena_2 as "Arena" number 3,
        // so the game would look for another package. A leading zero (Arena_01) is no number and is written.
        var package = Copy();
        foreach (var bad in new[] { Folder + "HTZ_Arena_Encounter_2.HTZ_Arena_Encounter_2_C", Folder + "HTZ_Arena_Encounter_0.HTZ_Arena_Encounter_0_C", Folder + "HTZ_Arena.HTZ_Arena_7" })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => TunablePatcher.Apply(package, [Edit(0, bad)], isClone: true));
            Assert.Contains("_<number>", refused.Message, StringComparison.Ordinal);
        }

        const string Arena = Folder + "HTZ_APEX_Bear_Arena_01.HTZ_APEX_Bear_Arena_01_C";
        Assert.Equal(Arena, References(Parse(TunablePatcher.Apply(package, [Edit(0, Arena)], isClone: true)))[Row0]);
    }

    private static CookedPackage Copy()
    {
        var bytes = SyntheticSpawnData.ZoneBytes("Medium_Threat/MTZ_Village_Own", ("MTZ_Village_Encounter", 3f), ("MTZ_Village_NPC_Encounter", 1f));
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Village_Own");
    }

    private static CookedPackage Parse(TunablePatchResult result) =>
        CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, null, SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Village_Own");

    private static Dictionary<string, string> References(CookedPackage package) =>
        TunableReader.ObjectReferences(package).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    private static TunableEdit Edit(int row, string value) => new("MTZ_Village_Own", $"EncounterData[{row}].EncounterClass", value);
}
