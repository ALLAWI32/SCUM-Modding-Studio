using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord igor8802: "when creating patrols at points of interest, the robots can be replaced with NPCs", then (r39) "When
/// replacing robots with NPCs, NPCs do not appear in the game". On the real barracks (D_0_Military_Barracks): the Map's
/// "Robot out, NPC area in" on SentrySpawner_0 records one step; the export (client, and the server role from the same
/// cook: this PC has no dedicated server) has the robot gone from the level and from its guarded zone manager's list, a
/// group of posts on the robot's route in the island's spawn data, and the NPC area as a sub-zone of the base's own threat
/// zone (the way the island's NPC-only areas are written, not a second zone inside it) whose own settings spawn only the
/// guard encounter (BP_Guard_Lvl_3/4/5), one at a time. Undo takes it all back. Real game files only (<c>SCUM_PAKS</c>, key
/// from this PC's store). Whether the game spawns the guards there is not something a file shows.
/// </summary>
public sealed class SentryGuardsRealTests
{
    private const string Barracks = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks";

    // The barracks' own threat zone (MTZ_POI_Military_Base), which holds every robot of the place.
    private const int BaseZone = 222;

    [Fact]
    public async Task ASentryBecomesNpcGuardPostsInItsOwnGuardZoneAndIsExported()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Guards");
        await map.LoadLevelsAsync([Barracks]);
        map.ShowSpawns = true;
        var catalog = ctx.Services.Workspace.Catalog!;
        var project = ctx.Services.Projects.Current!;

        // The panel shows on a sentry spawner and reads the game's NPC encounters in the background.
        var sentry = map.AllActors.Single(a => a.Name == "SentrySpawner_0");
        map.SelectedActor = sentry;
        Assert.True(map.HasGuardsOption);
        for (var waited = 0; map.IsLoadingGuardTypes && waited < 1800; waited++)
        {
            await Task.Delay(100);
        }

        var type = Assert.Single(map.GuardTypes, t => t.Name == "MTZ_POI_Military_Base_NPC_Encounter");
        Assert.Equal(["BP_Guard_Lvl_3", "BP_Guard_Lvl_4", "BP_Guard_Lvl_5"], type.Characters.Order(StringComparer.Ordinal));
        Assert.EndsWith("/MTZ_POI_Military_Base", type.ZonePackage, StringComparison.Ordinal);
        map.SelectedGuardType = type;
        map.GuardPosts = 3;
        var world = sentry.Actor.WorldTransform;
        var posts = NpcGuards.Posts([world.Translation, .. sentry.Actor.PatrolPoints.Select(world.TransformPosition)], 3);

        await map.ReplaceSentriesCommand.ExecuteAsync(null);

        var batch = Assert.IsType<BatchOp>(Assert.Single(project.Journal.Applied).Op);
        Assert.Equal(sentry.Reference, Assert.Single(batch.Ops.OfType<DeleteActorOp>()).Target);
        var copy = Assert.Single(batch.Ops.OfType<CloneAssetOp>());
        Assert.Equal(ZoneCopies.Kind, copy.Kind);
        Assert.True(project.State.IsDeleted(sentry.Reference));

        // Client, then the server role (from the same cook here), by one exporter as the app's export does: both write the
        // robot's removal, the new places and the sub-zone (it names a stock zone by its number, so the server's is checked
        // against the client's list: audit F281).
        var exporter = new ProjectExporter();
        foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
        {
            var result = await exporter.ExportAsync(project.State, "Guards", catalog,
                new ExportOptions { OutputDirectory = ctx.Combine("out-" + role), WritePak = false }, role);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("NewNpcGroup", StringComparison.Ordinal) || w.Contains("NewThreatZone", StringComparison.Ordinal) || w.Contains("Sentry", StringComparison.Ordinal)
                || w.Contains("ThreatZone_", StringComparison.Ordinal) || w.Contains("sub-zone", StringComparison.OrdinalIgnoreCase) || w.Contains("island data", StringComparison.Ordinal));
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);

            // The robot is gone (out of the level's actors, its export an inert Object: no class code runs, nothing spawns),
            // and its guarded zone manager lists the barracks' other seven.
            var cooked = Load(written, Barracks);
            Assert.DoesNotContain(LevelPackageEditor.ReadActorList(cooked), a => a.Name == "SentrySpawner_0");
            var robot = cooked.Exports.ToList().FindIndex(e => cooked.ResolveName(e.ObjectName) == "SentrySpawner_0");
            Assert.Equal("Object", cooked.GetExportClassName(robot));
            Assert.True(written.TryLoadPackage(Barracks, out var level));
            var manager = level.GetExports().Single(e => e.Name == "BP_GuardedZoneManager_2");
            Assert.True(manager.TryGetValue(out FPackageIndex[] listed, "_sentrySpawners"));
            Assert.Equal(7, listed.Length);
            Assert.DoesNotContain(listed, l => l.Name == "SentrySpawner_0");

            // The island's spawn data: one more group, on the route's three posts. The NPC area is no new zone (r39: a second
            // zone inside the base's own gave igor8802 no NPCs) but one more sub-zone of the base's zone, laid out as the
            // island's NPC-only ones (HTZ_Military_TV_Bunker_NPC): one box, the same box around it, its own settings,
            // priority 1, the class-loading switch off.
            var stockData = ScumStudio.Modding.Catalog.ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
            var stock = SpawnPlaces.Read(stockData);
            var data = Load(written, SpawnPlaces.StaticDataPath);
            var places = SpawnPlaces.Read(data);
            Assert.Equal(stock.Count(p => p.Kind == SpawnPlaceKind.CharacterGroup) + 1, places.Count(p => p.Kind == SpawnPlaceKind.CharacterGroup));
            Assert.Equal(stock.Count(p => p.Kind == SpawnPlaceKind.Zone), places.Count(p => p.Kind == SpawnPlaceKind.Zone));
            Assert.Equal(stock.Count(p => p.Kind == SpawnPlaceKind.Character), places.Count(p => p.Kind == SpawnPlaceKind.Character));
            var group = places.Last(p => p.Kind == SpawnPlaceKind.CharacterGroup);
            Assert.Equal(NpcGuards.PostType, group.Group);
            Assert.Equal(posts.Count, group.Points.Count);
            Assert.All(posts.Zip(group.Points), pair => Assert.True(FVector.Distance(pair.First, pair.Second) < 1f, $"{pair.First} / {pair.Second}"));

            var stockZones = (ArrayValue)stockData.ReadProperties(SpawnPlaces.StaticDataExport(stockData)).Find("StaticZoneDescriptions")!.Value;
            var zones = (ArrayValue)data.ReadProperties(SpawnPlaces.StaticDataExport(data)).Find("StaticZoneDescriptions")!.Value;
            Assert.Equal(stockZones.Items.Count, zones.Items.Count);
            var stockSubs = (ArrayValue)((StructValue)stockZones.Items[BaseZone]).Find("_subZoneData")!.Value;
            var subs = (ArrayValue)((StructValue)zones.Items[BaseZone]).Find("_subZoneData")!.Value;
            Assert.Equal(stockSubs.Items.Count + 1, subs.Items.Count);
            var sub = (StructValue)subs.Items[^1];
            Assert.Equal(["BoxDataArray", "SubZoneBox", "ShouldInstantlyLoadAllNecessaryClasses", "EncounterZoneData", "SubZonePriority"], sub.Properties.Select(p => p.Name));
            var area = Assert.IsType<StructValue>(Assert.Single(Assert.IsType<ArrayValue>(sub.Find("BoxDataArray")!.Value).Items));
            var box = Assert.IsType<BoxValue>(area.Find("AreaBox")!.Value);
            var around = Assert.IsType<BoxValue>(sub.Find("SubZoneBox")!.Value);
            Assert.Equal((box.Min.ToVector3(), box.Max.ToVector3(), (byte)1), (around.Min.ToVector3(), around.Max.ToVector3(), around.IsValid));
            Assert.Equal((byte)1, box.IsValid);
            var turn = Assert.IsType<RotatorValue>(area.Find("BoxRotation")!.Value);
            Assert.Equal((0f, 0f, 0f), (turn.Pitch, turn.Yaw, turn.Roll));
            Assert.All(posts, p => Assert.True(p.X >= box.Min.X && p.X <= box.Max.X && p.Y >= box.Min.Y && p.Y <= box.Max.Y && p.Z >= box.Min.Z && p.Z <= box.Max.Z, $"{p} not in {box}"));
            Assert.Equal(NpcGuards.SubZonePriority, Assert.IsType<IntValue>(sub.Find("SubZonePriority")!.Value).Value);
            Assert.False(sub.Find("ShouldInstantlyLoadAllNecessaryClasses")!.BoolValue);
            var settings = Assert.IsType<ObjectValue>(sub.Find("EncounterZoneData")!.Value);
            Assert.Equal(copy.NewPrimary + "." + copy.NewPrimary[(copy.NewPrimary.LastIndexOf('/') + 1)..], data.GetFullPath(settings.Index));
            Assert.Equal(copy.NewPrimary[(copy.NewPrimary.LastIndexOf('/') + 1)..], places.Single(p => p is { Kind: SpawnPlaceKind.Zone, Index: BaseZone }).SubZones[^1].Setting);
            Assert.True(RoundTripVerifier.Verify(data).IsLayoutIdentical, "the island data does not rebuild byte for byte");

            // The area's own settings: the zombie encounter weighs 0, the guard encounter keeps its 35, one encounter at a time.
            Assert.True(written.TryGetPackageFile(copy.NewPrimary, out _), copy.NewPrimary);
            var rows = SpawnChoices.ForZone(written, copy.NewPrimary).First(l => l.Level == 0).Entries;
            Assert.Equal([("MTZ_POI_Military_Base_Character_Encounter", 0f), ("MTZ_POI_Military_Base_NPC_Encounter", 35f)], rows.Select(r => (r.Name, r.Weight)));
            Assert.Equal("true", Assert.Single(TunableReader.Read(Load(written, copy.NewPrimary)), v => v.Path == "SingleEncounterPerZone").Value);
        }

        // One undo step takes it all back.
        ctx.Services.Projects.Undo();
        Assert.False(project.State.IsDeleted(sentry.Reference));
        Assert.DoesNotContain(project.State.AddedActors.Keys, a => SpawnPlaces.IsStaticData(a.Level));
        Assert.Empty(project.State.AssetClones);
        Assert.Empty(project.State.SubZones);
        Assert.True(project.State.IsEmpty);

        // The whole place: the barracks' eight robots in one step, one guard zone over all their posts.
        map.SelectedActor = map.AllActors.Single(a => a.Name == "SentrySpawner_0");
        map.GuardsWholePlace = true;
        Assert.True(map.HasGuardsOption);
        Assert.Contains("8", map.GuardTargetsText, StringComparison.Ordinal);
        await map.ReplaceSentriesCommand.ExecuteAsync(null);
        var all = Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(8, all.Ops.OfType<DeleteActorOp>().Count());
        Assert.Equal(8, all.Ops.OfType<AddSpawnPlaceOp>().Count(a => a.Kind == SpawnPlaceKind.CharacterGroup));
        Assert.DoesNotContain(all.Ops.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone);
        var whole = Assert.Single(all.Ops.OfType<AddSubZoneOp>());
        Assert.Equal(SpawnPlaces.ActorName(SpawnPlaceKind.Zone, BaseZone), whole.Zone.Actor);
        Assert.All(all.Ops.OfType<AddSpawnPlaceOp>().Where(a => a.Kind == SpawnPlaceKind.CharacterGroup).SelectMany(a => a.PostsAt(a.Transform)),
            p => Assert.True(whole.SubZone.Contains(p), $"{p} not in {whole.SubZone}"));
    }

    private static CookedPackage Load(AssetCatalog catalog, string path)
    {
        Assert.True(catalog.TryGetPackageFile(path, out var file), path);
        return CookedPackage.Parse(file.Read(), catalog.Provider.Files[file.Path[..file.Path.LastIndexOf('.')] + ".uexp"].Read(), null, path);
    }
}
