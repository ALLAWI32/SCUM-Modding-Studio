using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Spawns page over a made-up game (no paks): unsaved edits survive settings changes, a save after undo starts from
/// the project's current values, one save is one undo step, the server's rows are shared, and a zone's groups are named.
/// </summary>
public sealed class SpawnsPageTests
{
    private const string Rager = "/Game/ConZ_Files/Vehicles/SpawningPresets/AutomaticSpawn/RagerSpawnPreset";
    private const string RagerRadiation = "/Game/ConZ_Files/Vehicles/SpawningPresets/AutomaticSpawn/RagerSpawnPreset_RadiationZone";
    internal const string Bunker = "/Game/ConZ_Files/Encounters/EncounterZones/Hight_Threat/HTZ_Test_Bunker";
    private const string Broken = "/Game/ConZ_Files/Encounters/EncounterZones/Hight_Threat/HTZ_Test_Broken";
    private const string Festival = "/Game/ConZ_Files/Encounters/EncounterZones/Events/EVT_Test_Festival";

    [AvaloniaFact]
    public void SettingsChangeKeepsPendingAndCount()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);
        var fuelTo = Fuel(page).To;
        fuelTo.Value = 50;
        Assert.Equal(1, page.PendingCount);

        // Another setting (mouse, grass, quality): nothing is reloaded.
        ctx.Services.UpdateSettings(s => s with { Ui = s.Ui with { InvertMouseY = true } });
        HeadlessUi.Pump();
        Assert.Equal(1, page.PendingCount);
        Assert.Same(fuelTo, Fuel(page).To);

        // Another server: its limit rows come in, the moved slider keeps its value.
        var server = Server(ctx, "[Vehicles]\r\nscum.RagerMaxAmount=10\r\n");
        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = server });
        HeadlessUi.Pump();
        Assert.Equal(50, Fuel(page).To.Value);
        Assert.Contains(page.DetailCards.SelectMany(c => c.Rows).OfType<ServerSettingViewModel>(), r => r.Key == "scum.RagerMaxAmount");
        Assert.Equal(1, page.PendingCount);
    }

    [AvaloniaFact]
    public async Task ApplyAfterUndoRecordsOldEqualToTheCurrentState()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx);
        var state = ctx.Services.Projects.Current!.State;

        Fuel(page).To.Value = 50;
        page.ApplyCommand.Execute(null);
        Assert.Equal("50", state.GetAssetValue(Rager, "Preset", "FuelAmountSpawnPercentageRange.UpperBound.Value")?.Current);
        ctx.Services.Projects.Undo();
        HeadlessUi.Pump();
        Assert.Equal(10, Fuel(page).To.Value);
        Assert.Equal(0, page.PendingCount);

        // The edit starts from 10 (the state after undo), not from the 50 the slider last saved.
        Fuel(page).To.Value = 30;
        page.ApplyCommand.Execute(null);
        Assert.Equal("30", state.GetAssetValue(Rager, "Preset", "FuelAmountSpawnPercentageRange.UpperBound.Value")?.Current);
        Assert.Equal(0, page.PendingCount);
    }

    [AvaloniaFact]
    public async Task ThreeTargetsAreOneJournalEntryAndOneUndo()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx);
        var state = ctx.Services.Projects.Current!.State;
        var history = ctx.Services.Projects.History.Count;

        var chance = PartChance(page);
        Assert.Equal(3, chance.Targets.Count);
        chance.Value = 80;
        Assert.Equal(1, page.PendingCount);
        page.ApplyCommand.Execute(null);

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        Assert.All(Nodes, n => Assert.Equal("80", state.GetAssetValue(Rager, n, "SpawnChance")?.Current));
        ctx.Services.Projects.Undo();
        Assert.All(Nodes, n => Assert.Equal("50", state.GetAssetValue(Rager, n, "SpawnChance")?.Current ?? "50"));
    }

    [AvaloniaFact]
    public void TwoPresetsOfOneVehicleShareTheServerRow()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = Server(ctx, "[Vehicles]\r\nscum.RagerMaxAmount=10\r\nscum.RagerMaxFunctionalAmount=8\r\n");
        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = server });
        using var page = Open(ctx);
        var first = Limit(page);

        page.SelectedEntry = page.Entries.Single(e => e.PackagePath == RagerRadiation);
        Assert.True(HeadlessUi.PumpUntil(() => page.DetailCards.Count > 0));
        var second = Limit(page);

        Assert.Same(first, second);
        second.Value = 20;
        Assert.Equal(1, page.PendingCount);
        page.ApplyCommand.Execute(null);
        Assert.Contains("scum.RagerMaxAmount=20\r\n", File.ReadAllText(ServerSettingsFile.PathFor(server)!), StringComparison.Ordinal);
        Assert.Equal(0, page.PendingCount);
    }

    [AvaloniaFact]
    public void PartChanceOnlyTouchesVehiclePresetNodes()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);

        var chance = PartChance(page);

        Assert.Equal(Nodes, chance.Targets.Select(t => t.Tunable.Export).Order());
        Assert.DoesNotContain(chance.Targets, t => t.Tunable.Export == "Preset");
    }

    [AvaloniaFact]
    public void DuplicateEncounterLabelsAreNumbered()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);

        page.IsZombiesTab = true;
        Assert.Contains(page.Entries, e => e.PackagePath == Festival); // every zone settings folder, not only high and medium
        page.SelectedEntry = page.Entries.Single(e => e.PackagePath == Bunker);
        Assert.True(HeadlessUi.PumpUntil(() => page.DetailCards.Count > 0));

        var what = page.DetailCards.Single(c => c.Title == Loc.T("Spawns.Card.What")).Rows;
        var zombies = Loc.T("Spawns.Encounter.Zombies");
        Assert.Equal([zombies + " (1)", zombies + " (2)"], what.OfType<SpawnWeightRowViewModel>().Where(r => r.IsEntry).Select(r => r.Name));
        Assert.Equal([75d, 25d], what.OfType<SpawnWeightRowViewModel>().Where(r => r.IsEntry).Select(r => Math.Round(r.Percent, 3)));
        Assert.Contains(what.OfType<SpawnNoteViewModel>(), n => n.Text == Loc.T("Spawns.NoNpcGroup") && n.IsShown);
    }

    [AvaloniaFact]
    public void FailedReadShowsALocalizedState()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var page = Open(ctx);
        page.IsZombiesTab = true;
        page.SelectedEntry = page.Entries.Single(e => e.PackagePath == Bunker);
        Assert.True(HeadlessUi.PumpUntil(() => page.DetailCards.Count > 0));

        var broken = page.Entries.Single(e => e.PackagePath == Broken);
        page.SelectedEntry = broken;
        Assert.Empty(page.DetailCards); // never the previous zone's sliders under the new title
        Assert.True(HeadlessUi.PumpUntil(() => page.HasDetailNote));

        Assert.Equal(Loc.F("Spawns.ReadFailed", broken.Name, "bad header"), page.DetailNote);
        Assert.Empty(page.DetailCards);
        Assert.False(page.IsLoading);
    }

    [AvaloniaFact]
    public async Task SharedListEditAsksFirstAndIsOneUndoStep()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx);
        var window = new Window { Content = new SpawnsPageView { DataContext = page }, Width = 1500, Height = 900 };
        window.Show();
        try
        {
            page.IsZombiesTab = true;
            page.SelectedEntry = page.Entries.Single(e => e.PackagePath == Bunker);
            Assert.True(HeadlessUi.PumpUntil(() => page.HasTypes));

            // The encounter's presets: a header saying the list is shared by 3 zones, then the types, searchable.
            var list = HeadlessUi.FindNamed<ListBox>(window, "TypeList")!;
            Assert.True(list.IsEffectivelyVisible);
            Assert.Equal(3, list.ItemCount);
            Assert.True(page.TypeRows[0].IsShared);
            Assert.Equal(Loc.F("Spawns.Shared.Warning", 3), page.TypeRows[0].SharedText);
            page.TypeFilter = "military";
            Assert.Equal(["Zombies_Military"], page.TypeRows.Where(r => r.IsEntry).Select(r => r.Detail));
            page.TypeFilter = string.Empty;

            // 75/25 → 50/50: two weights move, the save asks first, then records one step.
            var civilians = page.TypeRows.Single(r => r.Detail == "Zombies_Civilian_All");
            Assert.Equal(75, civilians.Percent, 3);
            Assert.Equal(56.25, civilians.Overall, 3); // 75 % of the zone's Inside encounter, which is 75 % of the zone
            civilians.Percent = 50;
            Assert.Equal(2, page.PendingCount);
            var history = ctx.Services.Projects.History.Count;
            page.ApplyCommand.Execute(null);
            Assert.True(page.IsConfirmingShared);
            Assert.Equal(history, ctx.Services.Projects.History.Count);

            page.ConfirmApplyCommand.Execute(null);
            Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
            var state = ctx.Services.Projects.Current!.State;
            Assert.Equal("2", state.GetAssetValue(Encounter, EncounterCdo, "_possibleCharacters[0].Value")?.Current);
            Assert.Equal("2", state.GetAssetValue(Encounter, EncounterCdo, "_possibleCharacters[1].Value")?.Current);
            Assert.Equal(0, page.PendingCount);
            Assert.False(page.IsConfirmingShared);
        }
        finally
        {
            window.Close();
        }
    }

    internal const string Encounter = "/Game/ConZ_Files/Encounters/Encounters/HTZ_Test_Zombie_Encounter_Inside";
    internal const string EncounterCdo = "Default__HTZ_Test_Zombie_Encounter_Inside_C";
    private static readonly string[] Nodes = ["Node_Door", "Node_Seat", "Node_Wheel"];

    private static SpawnsPageViewModel Open(AppTestContext ctx)
    {
        var page = new SpawnsPageViewModel(ctx.Services, new FakeSource());
        Assert.True(HeadlessUi.PumpUntil(() => page.SelectedEntry?.PackagePath == Rager && page.DetailCards.Count > 0));
        return page;
    }

    private static SpawnRangeViewModel Fuel(SpawnsPageViewModel page) =>
        page.DetailCards.SelectMany(c => c.Rows).OfType<SpawnRangeViewModel>().First(r => r.To.Targets.Any(t => t.Tunable.Path.StartsWith("Fuel", StringComparison.Ordinal)));

    private static SpawnSliderViewModel PartChance(SpawnsPageViewModel page) =>
        page.DetailCards.SelectMany(c => c.Rows).OfType<SpawnSliderViewModel>().Single(s => s.Targets.All(t => t.Tunable.Path == "SpawnChance"));

    private static ServerSettingViewModel Limit(SpawnsPageViewModel page) =>
        page.DetailCards.SelectMany(c => c.Rows).OfType<ServerSettingViewModel>().Single(r => r.Key == "scum.RagerMaxAmount");

    // A server cook folder (an empty stock pak name) next to its Saved\Config\WindowsServer\ServerSettings.ini.
    private static string Server(AppTestContext ctx, string ini)
    {
        var name = "server-" + Guid.NewGuid().ToString("N")[..6];
        var paks = Directory.CreateDirectory(ctx.Combine(name, "SCUM", "Content", "Paks")).FullName;
        File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsServer.pak"), []);
        var config = Directory.CreateDirectory(ctx.Combine(name, "SCUM", "Saved", "Config", "WindowsServer")).FullName;
        File.WriteAllText(Path.Combine(config, ServerSettingsFile.FileName), ini);
        return paks;
    }

    /// <summary>A made-up game: two presets, three zone settings (one unreadable), a biome-free island with three zones by default.</summary>
    internal sealed class FakeSource : ISpawnSource
    {
        private readonly Dictionary<string, IReadOnlyList<Tunable>> _values = new(StringComparer.OrdinalIgnoreCase)
        {
            [Rager] = Preset("10"),
            [RagerRadiation] = Preset("5"),
            [Bunker] = [Value("HTZ_Test_Bunker", "EncounterSpawnChance", "60")], // a zone settings export is named after its package
            [Festival] = [Value("Festival", "EncounterSpawnChance", "100")],
        };

        public IReadOnlyList<string> Packages => [.. _values.Keys, Broken];

        public IReadOnlyList<Tunable> Read(string packagePath) =>
            _values.TryGetValue(packagePath, out var values) ? values : throw new InvalidDataException("bad header");

        public IReadOnlyList<SpawnWeightList> WeightLists(string packagePath) => packagePath == Bunker
            ?
            [
                new SpawnWeightList(Bunker, "HTZ_Test_Bunker", SpawnCategory.Other, 0,
                [
                    new SpawnWeight("HTZ_Test_Zombie_Encounter_Inside", "EncounterData[0].EncounterWeight", 75),
                    new SpawnWeight("HTZ_Test_Zombie_Encounter_Outside", "EncounterData[1].EncounterWeight", 25),
                ]),
                new SpawnWeightList(Encounter, EncounterCdo, SpawnCategory.Zombies, 1,
                [
                    new SpawnWeight("Zombies_Civilian_All", "_possibleCharacters[0].Value", 3),
                    new SpawnWeight("Zombies_Military", "_possibleCharacters[1].Value", 1),
                ]),
            ]
            : [];

        // The encounter is read by the bunker's settings (one zone) and another settings (two zones): three threat zones.
        public IReadOnlyList<SpawnPlace> Places { get; init; } =
            [Zone(0, "HTZ_Test_Bunker", 0f, 0f), Zone(1, "HTZ_Test_Other", 100_000f, 0f), Zone(2, "HTZ_Test_Other", 200_000f, 0f)];

        /// <summary>The ground everywhere (cm), or none.</summary>
        public float? Ground { get; init; }

        public IReadOnlyList<string> SettingsUsing(string package) => package == Encounter ? ["HTZ_Test_Bunker", "HTZ_Test_Other"] : [];

        public float? GroundAt(float x, float y) => Ground;

        public bool AboveGround(SpawnPlace place) => true;

        public CloneAssetOp ZoneCopy(string zonePackage, IReadOnlyCollection<string> planned)
        {
            var copy = zonePackage + "_Own";
            return new CloneAssetOp(ZoneCopies.Kind, zonePackage, copy, [new PackagePair(zonePackage, copy)]);
        }

        public PreviewModel? Preview(string? blueprint, SpawnCategory category) => null;

        public static SpawnPlace Zone(int index, string settings, float x, float y) =>
            new(SpawnPlaceKind.Zone, index, new FTransform(FQuat.Identity, new FVector(x, y, 0f), FVector.One), settings, 2000f, 1000f);

        public static SpawnPlace Point(SpawnPlaceKind kind, int index, string group, float x, float y, float z = 0f) =>
            new(kind, index, new FTransform(FQuat.Identity, new FVector(x, y, z), FVector.One), group);

        private static List<Tunable> Preset(string fuelTo) =>
        [
            Value("Preset", "FuelAmountSpawnPercentageRange.LowerBound.Value", "0", "VehicleSpawnPreset"),
            Value("Preset", "FuelAmountSpawnPercentageRange.UpperBound.Value", fuelTo, "VehicleSpawnPreset"),
            Value("Preset", "SpawnChance", "100", "VehicleSpawnPreset"), // the preset's own chance, not a part's
            .. Nodes.Select(n => Value(n, "SpawnChance", "50", "VehiclePresetNode")),
        ];

        private static Tunable Value(string export, string path, string value, string exportClass = "EncounterZoneData") =>
            new(export, path, TunableKind.Float, value) { ExportClass = exportClass };
    }
}
