using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// "Own copy of this NPC" on the real game files (<c>SCUM_PAKS</c>, key from this PC's store): the Military Base guards'
/// Lvl 3 guard is copied as a chain (zone settings, encounter, preset, character, common data), the export builds the five
/// packages, they point at each other and at nothing of the stock chain, the copied encounter and zone spawn only that
/// guard, and the stock chain is not in the export at all (so every other guard of the island is untouched). Whether the
/// game spawns the copy is not something a file shows.
/// </summary>
public sealed class NpcCopiesRealTests
{
    private const string Folder = "/Game/ConZ_Files/";

    [Fact]
    public async Task AnOwnCopyOfAGuardIsABuiltChainAndTheStockChainIsNotInTheExport()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "OwnNpc");
        var catalog = ctx.Services.Workspace.Catalog!;
        var project = ctx.Services.Projects.Current!;

        var type = NpcGuards.Types(catalog).Single(t => t.Name == "MTZ_POI_Military_Base_NPC_Encounter");
        var copy = NpcCopies.Plan(catalog, type, "BP_Guard_Lvl_3");

        // The chain, the zone settings first, each under the stock name and _Own, in the stock folders.
        Assert.Equal(
        [
            "MTZ_POI_Military_Base_Own", "MTZ_POI_Military_Base_NPC_Encounter_Own", "Guard_Class_03_Own", "BP_Guard_Lvl_3_Own", "NPCGuardCommonData_Lvl_3_Own",
        ], copy.Chain.Packages.Select(p => Leaf(p.New)));
        Assert.All(copy.Chain.Packages, p => Assert.Equal(p.Old[..(p.Old.LastIndexOf('/') + 1)], p.New[..(p.New.LastIndexOf('/') + 1)]));
        Assert.Equal(ZoneCopies.Kind, copy.Chain.Kind);
        Assert.Equal("MTZ_POI_Military_Base_NPC_Encounter_Own", copy.Type.Name);
        Assert.Equal(["BP_Guard_Lvl_3_Own"], copy.Type.Characters);

        var stockRows = SpawnChoices.ForZone(catalog, type.ZonePackage).First(l => l.Level == 0);
        var rows = NpcCopies.Rows(stockRows, copy.Chain);
        Assert.Equal(["MTZ_POI_Military_Base_Character_Encounter", "MTZ_POI_Military_Base_NPC_Encounter_Own"], rows.Entries.Select(e => e.Name));
        var posts = NpcPlacement.PostsAround(new FVector(-860000f, 500000f, 100f), 3);
        var ops = NpcPlacement.Plan([new NpcPostGroup(posts)], copy.Type, copy.Chain, rows, [], null, null, copy.Tunes);
        ctx.Services.Projects.Apply(new BatchOp("Own NPC", ops));

        var exporter = new ProjectExporter();
        foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
        {
            var result = await exporter.ExportAsync(project.State, "OwnNpc", catalog,
                new ExportOptions { OutputDirectory = ctx.Combine("out-" + role), WritePak = false }, role);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("_Own", StringComparison.Ordinal) || w.Contains("not created", StringComparison.OrdinalIgnoreCase));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);

            // The five copies are in the export, the stock chain is not.
            foreach (var pair in copy.Chain.Packages)
            {
                Assert.True(written.TryGetPackageFile(pair.New, out _), $"{pair.New} is not in the export");
                Assert.False(written.TryGetPackageFile(pair.Old, out _), $"{pair.Old} is in the export: the stock NPC would change");
            }

            var (zone, encounter, preset, character, data) = (copy.Chain.Packages[0].New, copy.Chain.Packages[1].New, copy.Chain.Packages[2].New, copy.Chain.Packages[3].New, copy.Chain.Packages[4].New);

            // The character points at its own copy of the common data (health, weapons, loot).
            var blueprint = Load(written, character);
            var keys = TunableReader.ExportKeys(blueprint);
            var cdo = Array.FindIndex(keys, k => k.StartsWith("Default__", StringComparison.Ordinal));
            var reference = Assert.IsType<ObjectValue>(blueprint.ReadProperties(cdo).Find("_armedNPCBaseCommonData")!.Value);
            Assert.Equal($"{data}.{Leaf(data)}", blueprint.GetFullPath(reference.Index));

            // The preset names the copied character and only it.
            Assert.Equal([$"{character}.{Leaf(character)}_C"], SpawnChoices.PresetCharacters(written, preset));

            // The encounter draws from the copied preset only: the stock ones of its list weigh 0.
            var presets = SpawnChoices.StoredPresets(written, $"{encounter}.{Leaf(encounter)}_C");
            Assert.Equal(3, presets.Count);
            Assert.Equal($"{preset}.{Leaf(preset)}", presets[0].Preset);
            Assert.True(presets[0].Weight > 0f);
            Assert.All(presets.Skip(1), p => Assert.Equal(0f, p.Weight));
            Assert.DoesNotContain(presets, p => p.Preset.EndsWith("/Guard_Class_03." + "Guard_Class_03", StringComparison.Ordinal));

            // The zone settings weigh the copied encounter and nothing else.
            var level0 = SpawnChoices.ForZone(written, zone).First(l => l.Level == 0);
            Assert.Equal([("MTZ_POI_Military_Base_Character_Encounter", 0f), ("MTZ_POI_Military_Base_NPC_Encounter_Own", 35f)], level0.Entries.Select(e => (e.Name, e.Weight)));
        }

        // The stock chain still reads as the game has it.
        Assert.Equal(60f, SpawnChoices.StoredPresets(catalog, type.EncounterClass)[0].Weight);

        // One undo step takes the whole copy back.
        ctx.Services.Projects.Undo();
        Assert.Empty(project.State.AssetClones);
        Assert.True(project.State.IsEmpty);
    }

    private static string Leaf(string package) => package[(package.LastIndexOf('/') + 1)..];

    private static CookedPackage Load(AssetCatalog catalog, string path)
    {
        Assert.True(catalog.TryGetPackageFile(path, out var file), path);
        return CookedPackage.Parse(file.Read(), catalog.Provider.Files[file.Path[..file.Path.LastIndexOf('.')] + ".uexp"].Read(), null, path);
    }
}
