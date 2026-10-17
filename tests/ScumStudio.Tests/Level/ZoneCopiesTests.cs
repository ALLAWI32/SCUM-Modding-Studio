using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "for the selected area, choose exactly which zombie or NPC types spawn there". A zone gets settings of its own: a
/// copy of the zone settings it shares, under a new name in the same folder, built by the export's clone path. Names that
/// the game would read as another package are refused (audit F089, F090).
/// </summary>
public sealed class ZoneCopiesTests
{
    private const string Stock = SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Village";

    [Fact]
    public void PlanClonesOnePackageInTheSameFolder()
    {
        using var data = Village();
        using var catalog = data.Open();

        var op = ZoneCopies.Plan(catalog, Stock, "MTZ_Village_Own");
        Assert.Equal(ZoneCopies.Kind, op.Kind);
        Assert.Equal(Stock, op.Template);
        Assert.Equal(SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Village_Own", op.NewPrimary);
        Assert.Equal([new PackagePair(Stock, op.NewPrimary)], op.Packages);

        // Journaled like any clone, with an edit of the copy's own weights: the export builds the copy as a new package and
        // leaves the game's settings (shared by other zones) alone.
        var state = new EditState();
        state.Apply(op);
        state.Apply(new SetAssetValueOp(op.NewPrimary, "MTZ_Village_Own", "EncounterData[0].EncounterWeight", "Float", "3", "0"));
        var result = AssetModBuilder.Build(catalog, ProjectExporter.BuildAssetRequest(state));

        var built = Assert.Single(result.Packages);
        Assert.Equal(op.NewPrimary, built.PackagePath);
        Assert.True(built.IsClone);
        var copy = CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, null, op.NewPrimary);
        var export = Assert.Single(Enumerable.Range(0, copy.Exports.Count), i => copy.GetExportClassName(i) == "EncounterZoneData");
        Assert.Equal("MTZ_Village_Own", copy.ResolveName(copy.Exports[export].ObjectName));
        var rows = ((ArrayValue)copy.ReadProperties(export).Find("EncounterData")!.Value!).Items.Cast<StructValue>().ToList();
        Assert.Equal([0f, 1f], rows.Select(r => ((FloatValue)SpawnPlaces.Member(r, "EncounterWeight")!.Value!).Value));
        Assert.Contains(result.Applied, a => a.Package == op.NewPrimary && a.Key == "MTZ_Village_Own|EncounterData[0].EncounterWeight");

        // A suggested name is free.
        Assert.Equal("MTZ_Village_Own", ZoneCopies.SuggestName("MTZ_Village", ZoneCopies.Taken(catalog)));
        Assert.Equal("MTZ_Village_OwnB", ZoneCopies.SuggestName("MTZ_Village", ZoneCopies.Taken(catalog, [op.NewPrimary])));
    }

    [Fact]
    public void ZoneCopyWithARepointedRowIsExportedWithOnlyNpcs()
    {
        // igor8802: "assign NPCs instead of zombies". The whole flow of a zone's own settings: the copy, its zombie row
        // re-pointed to the NPC encounter and re-weighed (every row spawns NPCs now), the export, and the copy read back.
        using var data = Village();
        using var catalog = data.Open();
        const string Zombies = SyntheticSpawnData.Encounters + "MTZ_Village_Encounter.MTZ_Village_Encounter_C";
        const string Npcs = SyntheticSpawnData.Encounters + "MTZ_Village_NPC_Encounter.MTZ_Village_NPC_Encounter_C";
        var op = ZoneCopies.Plan(catalog, Stock, "MTZ_Village_Own");
        var state = new EditState();
        state.Apply(op);
        state.Apply(new SetAssetValueOp(op.NewPrimary, "MTZ_Village_Own", "EncounterData[0].EncounterClass", "Object", Zombies, Npcs));
        state.Apply(new SetAssetValueOp(op.NewPrimary, "MTZ_Village_Own", "EncounterData[0].EncounterWeight", "Float", "3", "1"));

        var result = AssetModBuilder.Build(catalog, ProjectExporter.BuildAssetRequest(state));

        Assert.DoesNotContain(result.Warnings, w => w.StartsWith(op.NewPrimary, StringComparison.OrdinalIgnoreCase)); // "edits skipped" before
        var built = Assert.Single(result.Packages); // the copy only: the game's settings, shared by other zones, stay as they are
        Assert.Equal(op.NewPrimary, built.PackagePath);
        Assert.True(built.IsClone);
        var copy = CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, null, op.NewPrimary);
        var references = TunableReader.ObjectReferences(copy).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        Assert.Equal(Npcs, references["MTZ_Village_Own|EncounterData[0].EncounterClass"]);
        Assert.Equal(Npcs, references["MTZ_Village_Own|EncounterData[1].EncounterClass"]);
        Assert.Contains(result.Applied, a => a.Package == op.NewPrimary && a.Key == "MTZ_Village_Own|EncounterData[0].EncounterClass" && a.New == Npcs);
        var weights = TunableReader.Read(copy).Where(t => t.Path.EndsWith(".EncounterWeight", StringComparison.Ordinal)).Select(t => t.Value).ToList();
        Assert.True(TunableValue.AreEqual(TunableKind.Float, weights[0], "1"));
    }

    [Fact]
    public void NameEndingInUnderscoreNumberIsRefused()
    {
        using var data = Village();
        using var catalog = data.Open();

        // MTZ_Village_2 is stored as the FName MTZ_Village with number 3: the game would look for another package.
        var refused = Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, "MTZ_Village_2"));
        Assert.Contains("_<number>", refused.Message, StringComparison.Ordinal);
        foreach (var bad in new[] { "MTZ Village", "2MTZ", "MTZ-Village", "MTZ/Village", string.Empty })
        {
            Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, bad));
        }

        Assert.Equal("MTZ_Village_2B", ZoneCopies.Plan(catalog, Stock, "MTZ_Village_2B").NewPrimary[(Stock.LastIndexOf('/') + 1)..]);
        Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Missing", "MTZ_Missing_Own"));
    }

    [Fact]
    public void NameUsedByAnotherPackageAnywhereIsRefused()
    {
        using var data = Village();
        data.Empty("/Game/ConZ_Files/Somewhere/Else/MTZ_Village_Own");
        using var catalog = data.Open();

        // A package of the same name in another folder (any case), or a copy the project already plans.
        Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, "MTZ_Village_Own"));
        Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, "mtz_village_own"));
        Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, "MTZ_Village"));
        Assert.Throws<ArgumentException>(() => ZoneCopies.Plan(catalog, Stock, "MTZ_Village_Mine", [SyntheticSpawnData.Zones + "High_Threat/MTZ_Village_Mine"]));
        Assert.Equal("MTZ_Village_OwnB", ZoneCopies.SuggestName("MTZ_Village", ZoneCopies.Taken(catalog)));
    }

    private static SyntheticSpawnData Village()
    {
        var data = new SyntheticSpawnData();
        data.Zone("Medium_Threat/MTZ_Village", ("MTZ_Village_Encounter", 3f), ("MTZ_Village_NPC_Encounter", 1f));
        return data;
    }
}
