using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Spawns;
using ScumStudio.Tests.Modding;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Each page as its own mod (owner 2026-10-17): a project's edits split by page with nothing lost or doubled, page paks that
/// load in a fixed order below the Registry pak and are never read back as game files, and the package check that accepts a
/// reference to what another pak of the same project ships (and names that pak).
/// </summary>
public sealed class ModuleExportTests
{
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost";
    private const string Rager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager";
    private const string MyRager = "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_MyRager";
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Coins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Copper_Coins/Copper_Coins";
    private const string MyCoins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Copper_Coins/Hedgehog_Coins";
    private const string Village = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village";
    private const string GuardZone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village_Guards";
    private const string OwnZone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village_Mine";
    private const string Grass = "/Game/ConZ_Files/Landscape/LandscapeTextures/T_Grass_D";

    [Fact]
    public void EachPageGetsOnlyItsOwnChanges()
    {
        var state = new EditState();
        var guard = new ActorRef(Outpost, "BP_Sentry_2");
        var place = new ActorRef(SpawnPlaces.StaticDataPath, "VehicleSpawn_12");
        foreach (var op in new EditOp[]
                 {
                     new DeleteActorOp(new ActorRef(Outpost, "Rock_7")),
                     new SetSpawnChoiceOp(place, SpawnChoices.VehicleGroup, null, "Boat"),
                     new CloneAssetOp("vehicle", Rager, MyRager, [new PackagePair(Rager, MyRager), new PackagePair(Rager[..^"BPC_Rager".Length] + "Rager_ES", MyRager[..^"BPC_MyRager".Length] + "MyRager_ES")]),
                     new SetAssetValueOp(MyRager, "Default__BPC_MyRager_C", "ChassisMass", "Float", "1500", "900"),
                     new SetAssetValueOp(Ak47 + "_ES", "Weapon_AK47_ES", "MaxLoadedAmmo", "Int", "30", "45"),
                     new CloneAssetOp("item", Coins, MyCoins, [new PackagePair(Coins, MyCoins)]),
                     new CloneAssetOp("zone", Village, GuardZone, [new PackagePair(Village, GuardZone)]),
                     new SetSpawnChoiceOp(guard, SpawnChoices.ZoneData, null, GuardZone + "." + GuardZone[(GuardZone.LastIndexOf('/') + 1)..]),
                     new CloneAssetOp("zone", Village, OwnZone, [new PackagePair(Village, OwnZone)]),
                     new SetAssetValueOp(OwnZone, "MTZ_Village_Mine", "Weight", "Float", "1", "2"),
                     new ReplaceAssetOp(Grass, null, "/Game/ConZ_Files/Materials/Snow/T_GroundSnow_03_D"),
                 })
        {
            state.Apply(op);
        }

        var map = ModuleExport.StateOf(state, ExportModule.Map);
        Assert.Equal([Outpost, SpawnPlaces.StaticDataPath], map.ChangedLevels); // the island's spawn places stay for what its objects clear
        Assert.Equal([GuardZone], map.AssetClones.Select(c => c.NewPrimary)); // the guards' zone goes with the post that uses it
        Assert.Equal([Grass], map.AssetReplacements.Keys);
        Assert.Empty(map.AssetValueOverrides);

        var spawns = ModuleExport.StateOf(state, ExportModule.Spawns);
        Assert.Equal([SpawnPlaces.StaticDataPath], spawns.ChangedLevels);
        Assert.Equal([OwnZone], spawns.AssetClones.Select(c => c.NewPrimary));
        Assert.Equal([OwnZone], spawns.AssetValueOverrides.Select(v => v.Package));

        var vehicles = ModuleExport.StateOf(state, ExportModule.Vehicles);
        Assert.Empty(vehicles.ChangedLevels);
        Assert.Equal([MyRager], vehicles.AssetClones.Select(c => c.NewPrimary));
        Assert.Equal([MyRager], vehicles.AssetValueOverrides.Select(v => v.Package)); // a value of the clone goes with the clone

        var weapons = ModuleExport.StateOf(state, ExportModule.Weapons);
        Assert.Equal([Ak47 + "_ES"], weapons.AssetValueOverrides.Select(v => v.Package)); // an entity setup goes with its item
        Assert.Empty(weapons.AssetClones);

        var items = ModuleExport.StateOf(state, ExportModule.Items);
        Assert.Equal([MyCoins], items.AssetClones.Select(c => c.NewPrimary));
        Assert.True(ModuleExport.StateOf(state, ExportModule.Craftables).IsEmpty);
        Assert.True(ModuleExport.StateOf(state, ExportModule.Economy).IsEmpty);

        // Every edit is in exactly one page's part (the Map's copy of the spawn places aside).
        var parts = ModuleExport.PakModules.Select(m => ModuleExport.StateOf(state, m)).ToList();
        Assert.Equal(state.AssetClones.Count, parts.Sum(p => p.AssetClones.Count));
        Assert.Equal(state.AssetValueOverrides.Count, parts.Sum(p => p.AssetValueOverrides.Count));
        Assert.Equal(state.AssetReplacements.Count, parts.Sum(p => p.AssetReplacements.Count));
        Assert.Equal(state.DeletedActors.Count, parts.Sum(p => p.DeletedActors.Count));
        Assert.False(state.IsEmpty);

        // What the Vehicles pak may refer to in the other pages' paks: their clones, named by those paks.
        var elsewhere = ModuleExport.Elsewhere(state, ExportModule.Vehicles, "My Mod", ProjectExporter.DefaultPakChunkIndex);
        Assert.Equal("pakchunk914-My_Mod-Items_P.pak", elsewhere(MyCoins));
        Assert.Equal("pakchunk911-My_Mod-Map_P.pak", elsewhere(GuardZone));
        Assert.Null(elsewhere(MyRager)); // its own
        Assert.Null(elsewhere(Rager)); // the game's
    }

    /// <summary>
    /// Review 3.5.0: an edit's page was found from its package's folder alone, so the Weapons page's melee damage, every page's
    /// trader prices and a stock vehicle's paint went to the Map's pak, the Spawns page's vehicle presets to the Vehicles' pak
    /// and its animals to the Map's: that page's own export then had nothing to export.
    /// </summary>
    [Fact]
    public void EditsOutsideTheFolderRulesGoToThePageThatMadeThem()
    {
        const string Trade = ScumStudio.Modding.Tuning.DataTableEdits.TradeableTable;
        const string Damage = ScumStudio.Modding.Tuning.DataTableEdits.WeaponDescTable;
        const string Paint = "/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_Rager_Outer";
        const string Preset = "/Game/ConZ_Files/Vehicles/SpawningPresets/AutomaticSpawn/Pickup_AS";
        const string PartsPreset = "/Game/ConZ_Files/Vehicles/SpawningPresets/ManualSpawn/Rager_MS";
        const string Biome = "/Game/ConZ_Files/Hunting/BiomeData/BD_Forest";
        var state = new EditState();
        foreach (var op in new EditOp[]
                 {
                     new CloneAssetOp("vehicle", Rager, MyRager, [new PackagePair(Rager, MyRager)]),
                     new SetAssetValueOp(Damage, "WeaponDesc_Table", "Rows[1H_Katana].Damage", "Float", "40", "80"),
                     new SetAssetValueOp(Trade, "Table_TradeableDesc", "Rows[Weapon_AK47_C].BasePrice", "Float", "900", "500"),
                     new SetAssetValueOp(Trade, "Table_TradeableDesc", "Rows[BPC_MyRager_C].BasePrice", "Float", "9000", "5000"),
                     new SetAssetValueOp(Trade, "Table_TradeableDesc", "Rows[Copper_Coins_C].BasePrice", "Float", "1", "2"),
                     new SetAssetValueOp(Paint, "MI_Rager_Outer", "VectorParameterValues[Color]", "Color", "#FFFFFF", "#FF0000"),
                     new SetAssetValueOp(Preset, "Pickup_AS", "FuelAmount", "Float", "10", "90"),
                     new SetAssetValueOp(PartsPreset, "Rager_MS", "FuelAmount", "Float", "10", "90"),
                     new SetAssetValueOp(PartsPreset, "Rager_MS", "Children[2]", "Part", string.Empty, "/Game/ConZ_Files/Vehicles/Car/Rager/Attachments/BPC_Rager_Door_Left.BPC_Rager_Door_Left_C"),
                     new SetAssetValueOp(Biome, "BD_Forest", "PopulationData[0].Weight", "Float", "1", "3"),
                 })
        {
            state.Apply(op);
        }

        string[] Values(ExportModule module) => [.. ModuleExport.StateOf(state, module).AssetValueOverrides.Select(v => $"{v.Package}|{v.Path}").Order(StringComparer.Ordinal)];
        Assert.Empty(Values(ExportModule.Map));
        Assert.Equal(new[] { $"{Damage}|Rows[1H_Katana].Damage", $"{Trade}|Rows[Weapon_AK47_C].BasePrice" }.Order(StringComparer.Ordinal), Values(ExportModule.Weapons));
        Assert.Equal(new[] { $"{Paint}|VectorParameterValues[Color]", $"{PartsPreset}|Children[2]", $"{PartsPreset}|FuelAmount", $"{Trade}|Rows[BPC_MyRager_C].BasePrice" }.Order(StringComparer.Ordinal), Values(ExportModule.Vehicles));
        Assert.Equal([$"{Trade}|Rows[Copper_Coins_C].BasePrice"], Values(ExportModule.Items));
        Assert.Equal(new[] { $"{Biome}|PopulationData[0].Weight", $"{Preset}|FuelAmount" }.Order(StringComparer.Ordinal), Values(ExportModule.Spawns));
        Assert.Equal(state.AssetValueOverrides.Count, ModuleExport.PakModules.Sum(m => ModuleExport.StateOf(state, m).AssetValueOverrides.Count));
        Assert.False(ModuleExport.StateOf(state, ExportModule.Spawns).IsEmpty); // its Export this page has something to export
    }

    [Fact]
    public void PagePaksLoadInOrderUnderTheRegistryPakAndAreNeverReadAsGameFiles()
    {
        foreach (var chunk in new[] { ProjectExporter.DefaultPakChunkIndex, 500 })
        {
            var order = ModuleExport.PakModules.Select(m => ModuleExport.ChunkOf(m, chunk)).ToList();
            Assert.Equal(order.Order().ToList(), order);
            Assert.Equal(order.Count, order.Distinct().Count());
            Assert.True(order[0] > chunk + 1, "a page pak must load above the whole project's Craftables pak");
            Assert.True(ModuleExport.RegistryChunk(chunk) > order[^1], "the Registry pak must load above every page pak");
            Assert.True(ModuleExport.ChunkOf(ExportModule.Map, chunk) > ModuleExport.ChunkOf(ExportModule.Spawns, chunk), "the Map's spawn places (with the Spawns page's edits) must win");

            var names = ModuleExport.PakModules.Select(m => ModuleExport.PakFileName("My Mod", m, chunk)).Append(ModuleExport.RegistryPakFileName("My Mod", chunk)).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names, n => Assert.False(OwnPaks.IsGameSource(n, OwnPaks.ProjectNames("My Mod")), $"{n} would be read as a game file"));
            Assert.All(names, n => Assert.True(OwnPaks.IsGameSource(n, OwnPaks.ProjectNames("Other")) || chunk >= 900, $"{n} hides another project's pak"));
        }

        Assert.Equal("pakchunk912-My_Mod-Vehicles_P.pak", ModuleExport.PakFileName("My Mod", ExportModule.Vehicles, 900));
        Assert.Equal("pakchunk919-My_Mod-Registry_P.pak", ModuleExport.RegistryPakFileName("My Mod", 900));
        Assert.NotEqual(CraftablesExporter.StagingFolderName, ModuleExport.StagingFolder(ExportModule.Craftables)); // never the whole project's staging
    }

    /// <summary>
    /// The Craftables pak's registry keeps the project pak's records, and a page pak may use another page pak's clone: the
    /// package check named those records missing (a project with a vehicle clone and a craftable could not be exported).
    /// It accepts what another pak of the project ships, and names that pak.
    /// </summary>
    [Fact]
    public void ThePackageCheckAcceptsWhatAnotherPakOfTheProjectShips()
    {
        using var temp = new TempDirectory();
        var game = Path.Combine(temp.Path, "game");
        SyntheticGame.WriteGame(game);
        using var catalog = AssetCatalog.OpenLoose(game);

        const string Clone = "/Game/V/BPC_MyCar";
        var staging = Path.Combine(temp.Path, "staging");
        var registry = SyntheticGame.Registry(
            [.. SyntheticGame.StockRegistry().Assets.Where(a => !a.ObjectPath.StartsWith("/Game/Seed/", StringComparison.Ordinal))
                .Select(a => new SyntheticGame.Record(a.ObjectPath, a.AssetClass)),
             new SyntheticGame.Record(Clone + ".BPC_MyCar", "Blueprint", SyntheticGame.PrimaryTags("Vehicle", "BPC_MyCar"))]);
        SyntheticGame.Write(staging, registry);

        var alone = ModPackageValidator.Validate(staging, catalog);
        Assert.Contains(alone.Problems, p => p.Contains(Clone, StringComparison.Ordinal));
        Assert.Empty(alone.Needs);

        var together = ModPackageValidator.Validate(staging, catalog, default, p => p == Clone ? "pakchunk912-My-Vehicles_P.pak" : null);
        Assert.True(together.Passed, string.Join('\n', together.Problems));
        Assert.Equal(["pakchunk912-My-Vehicles_P.pak"], together.Needs);
    }

    /// <summary>
    /// r39 (igor8802): a robot replaced with NPCs is a Map edit (the robot removed) and Spawns edits (the posts, the NPC area,
    /// its settings), and neither page pak names the other's packages. Each page pak's install note names the other; other
    /// pages and projects without such an edit are left as they were.
    /// </summary>
    [Fact]
    public void ARobotReplacedWithNpcsTiesTheMapAndSpawnsPaks()
    {
        var barracks = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks";
        var posts = new AddSpawnPlaceOp("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, NpcGuards.PostType, ScumStudio.Level.Model.TransformValue.At(1f, 2f, 3f), Points: [ScumStudio.Core.Mathematics.FVector.Zero]);
        var state = EditState.Replay([new BatchOp("Robot out, NPC area in", [new DeleteActorOp(new ActorRef(barracks, "SentrySpawner_0")), posts])]);
        var (map, spawns) = (ModuleExport.PakFileName("My", ExportModule.Map, 902), ModuleExport.PakFileName("My", ExportModule.Spawns, 902));

        Assert.Equal([spawns], ModuleExport.GoesWith(state, ExportModule.Map, "My", 902));
        Assert.Equal([map], ModuleExport.GoesWith(state, ExportModule.Spawns, "My", 902));
        Assert.Empty(ModuleExport.GoesWith(state, ExportModule.Vehicles, "My", 902));
        Assert.Contains(spawns, ModuleExport.InstallNote(ProjectSourceRole.Client, map, string.Empty, ModuleExport.GoesWith(state, ExportModule.Map, "My", 902)), StringComparison.Ordinal);
        Assert.Contains(map, ModuleExport.InstallNote(ProjectSourceRole.Server, spawns, string.Empty, ModuleExport.GoesWith(state, ExportModule.Spawns, "My", 902)), StringComparison.Ordinal);

        // A rock removed and a group of points added, or a robot removed alone: no tie.
        Assert.Empty(ModuleExport.GoesWith(EditState.Replay([new DeleteActorOp(new ActorRef(barracks, "Rock_7")), posts]), ExportModule.Map, "My", 902));
        Assert.Empty(ModuleExport.GoesWith(EditState.Replay([new DeleteActorOp(new ActorRef(barracks, "SentrySpawner_0"))]), ExportModule.Spawns, "My", 902));
    }

    /// <summary>
    /// The dedicated server refuses every pak in its <c>~mods</c> ("Pak file or matching sig file integrity compromised") and
    /// quits unless it is started with -fileopenlog (3.5.0 check, server build 25803875): every server install text says so.
    /// </summary>
    [Fact]
    public void TheServerInstallTextsSayFileOpenLog()
    {
        Assert.Contains("-fileopenlog", ProjectExporter.InstallNote(ProjectSourceRole.Server), StringComparison.Ordinal);
        Assert.Contains("-fileopenlog", ModuleExport.InstallNote(ProjectSourceRole.Server, "pakchunk912-My-Vehicles_P.pak", string.Empty, []), StringComparison.Ordinal);
        Assert.All(ScumStudio.App.Localization.Loc.Languages, language =>
            Assert.Contains("-fileopenlog", ScumStudio.App.Localization.Loc.Table(language.Code)["Export.Module.Install"], StringComparison.Ordinal));
    }
}
