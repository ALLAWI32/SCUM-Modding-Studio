using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Journal operations of the Vehicles/Weapons modules (clone, remove clone, set value): JSON, inverses, validation,
/// replay — and an end-to-end export of a cloned and tuned weapon into a mod pak with its registry.
/// </summary>
public sealed class AssetEditTests
{
    private const string Rpk = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74";
    private const string Gold = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74_Gold";

    private static CloneAssetOp GoldClone() =>
        new("weapon", Rpk, Gold, [new PackagePair(Rpk, Gold), new PackagePair(Rpk + "_ES", Gold + "_ES")]);

    [Fact]
    public void OpsRoundTripThroughJsonAndInvertExactly()
    {
        var clone = GoldClone();
        var set = new SetAssetValueOp(Gold + "_ES", "Default__Weapon_RPK-74_Gold_ES_C", "Weight", "Float", "4800", "3000");
        foreach (EditOp op in new EditOp[] { clone, clone.Inverse(), set, set.Inverse() })
        {
            var json = op.ToJson();
            Assert.Equal(op, EditOp.FromJson(json));
            Assert.Equal(op, op.Inverse().Inverse());
        }

        Assert.StartsWith("{\"op\":\"cloneAsset\"", clone.ToJson(), StringComparison.Ordinal);
        Assert.StartsWith("{\"op\":\"setAssetValue\"", set.ToJson(), StringComparison.Ordinal);
        Assert.Equal([Gold, Gold + "_ES"], clone.GetTouchedAssets());
        Assert.Empty(clone.GetTouchedLevels());
        Assert.Equal("Clone weapon Weapon_RPK-74 as Weapon_RPK-74_Gold (2 packages)", clone.Describe());
        Assert.Equal("Set Weapon_RPK-74_Gold_ES Weight = 3000 (was 4800)", set.Describe());
    }

    [Fact]
    public void StateTracksClonesAndValueOverrides()
    {
        var state = new EditState();
        var clone = GoldClone();
        state.Apply(clone);
        Assert.False(state.IsEmpty);
        Assert.Equal([Gold, Gold + "_ES"], state.ChangedAssets);
        Assert.Same(clone, state.FindCloneOf(Gold + "_ES"));
        Assert.NotNull(state.Validate(clone)); // already cloned

        var esExport = "Default__Weapon_RPK-74_Gold_ES_C";
        var set = new SetAssetValueOp(Gold + "_ES", esExport, "Weight", "Float", "4800", "3000");
        state.Apply(set);
        Assert.Equal("3000", state.GetAssetValue(Gold + "_ES", esExport, "Weight")!.Current);
        Assert.NotNull(state.Validate(set with { Old = "4800", New = "2000" })); // out of date: current is 3000
        Assert.Null(state.Validate(set with { Old = "3000.0", New = "2000" })); // numeric compare
        Assert.NotNull(state.Validate(set with { Old = "3000", New = "3000" })); // no change
        Assert.NotNull(state.Validate(clone.Inverse())); // values still edited

        state.Apply(set.Inverse());
        Assert.Null(state.GetAssetValue(Gold + "_ES", esExport, "Weight"));
        state.Apply(clone.Inverse());
        Assert.True(state.IsEmpty);

        // A stock package can be overridden without a clone.
        var stock = new SetAssetValueOp(Rpk, "Default__Weapon_RPK-74_C", "EventMaxAmmo", "Int", "75", "100");
        state.Apply(stock);
        Assert.Equal([Rpk], state.ChangedAssets);
        Assert.Contains("value " + Rpk.ToLowerInvariant(), state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectJournalReplaysAssetEditsAfterReopening()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Guns.ssproj");
        using (var project = Project.Create(dir, "Guns"))
        {
            project.Apply(GoldClone());
            project.Apply(new SetAssetValueOp(Gold + "_ES", "Default__Weapon_RPK-74_Gold_ES_C", "Caption", "Text", "RPK", "RPK Gold"));
            Assert.Equal([Gold, Gold + "_ES"], project.PendingAssetSet);
            Assert.Empty(project.PendingExportSet);
            Assert.Equal(Gold + "_ES", project.History[^1].Level);
        }

        using var reopened = Project.Open(dir);
        Assert.Equal(2, reopened.Journal.UndoPointer);
        Assert.Equal("RPK Gold", Assert.Single(reopened.State.AssetValueOverrides).Current);
        reopened.Undo();
        Assert.Empty(reopened.State.AssetValueOverrides);
        Assert.Single(reopened.State.AssetClones);
    }

    [FixturesFact]
    public async Task ClonedWeaponIsExportedWithItsRegistryRecords()
    {
        using var temp = new LevelTempDirectory();
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot, new AssetCatalogOptions { LooseOverlays = [FixturePaths.OrigExtraRoot] });
        var plan = CloneFamilyPlanner.PlanItem(catalog, Rpk, "Weapon_RPK-74_Gold");
        var es = ModdableAssets.ReadPackage(catalog, Rpk + "_ES");
        var weight = Assert.Single(TunableReader.Read(es), t => t.Name == "Weight");

        using var project = Project.Create(temp.Combine("Guns.ssproj"), "Guns");
        project.Apply(new CloneAssetOp("weapon", Rpk, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList()));
        var esExport = "Default__Weapon_RPK-74_Gold_ES_C";
        project.Apply(new SetAssetValueOp(Gold + "_ES", esExport, "Caption", "Text", "RPK", "RPK Gold"));
        project.Apply(new SetAssetValueOp(Gold + "_ES", esExport, weight.Path, "Float", weight.Value, "2500"));
        project.Apply(new SetAssetValueOp(Rpk, "Default__Weapon_RPK-74_C", "EventMaxAmmo", "Int", "75", "100"));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out") });
        Assert.Empty(result.Levels);
        Assert.Equal(3, result.Assets.Count);
        Assert.Equal(2, result.Assets.Count(a => a.IsClone));
        Assert.Equal(3, result.AssetValues.Count);
        Assert.Equal(4, result.Registered.Count);
        Assert.True(File.Exists(result.PakPath));
        var report = await File.ReadAllTextAsync(result.ReportPath!);
        Assert.Contains("## Vehicles and items", report, StringComparison.Ordinal);
        Assert.Contains("Registered (Item)", report, StringComparison.Ordinal);

        // The pak alone holds the clone, the overridden stock weapon and the merged registry; CUE4Parse reads them.
        using var mod = AssetCatalog.OpenPaks(result.PakPath!);
        Assert.True(mod.PackageExists(Gold));
        Assert.True(mod.PackageExists(Gold + "_ES"));
        Assert.True(mod.PackageExists(Rpk));
        Assert.Contains(mod.GetExports(Gold), e => e.Name == "Weapon_RPK-74_Gold_C");
        Assert.True(mod.Provider.Files.TryGetValue("SCUM/AssetRegistry.bin", out var registryFile));
        var registry = AssetRegistryFile.Parse(registryFile.Read());
        Assert.Contains(registry.Assets, a => a.ObjectPath == Gold + ".Weapon_RPK-74_Gold_C");

        var exported = TunableReader.Read(ModdableAssets.ReadPackage(mod, Gold + "_ES")).ToDictionary(t => t.Name);
        Assert.Equal("RPK Gold", exported["Caption"].Value);
        Assert.Equal("2500", exported["Weight"].Value);
        Assert.Equal("100", Assert.Single(TunableReader.Read(ModdableAssets.ReadPackage(mod, Rpk)), t => t.Name == "EventMaxAmmo").Value);
    }
}
