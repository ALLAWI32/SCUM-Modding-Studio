using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The map against the real game (<c>SCUM_PAKS</c>, key from this PC's store; skipped without): spawn pins stand on the
/// game's own spawn data, and placements sit where the game's own far-view model of each level has geometry.
/// </summary>
public sealed class SpawnPlacesRealTests
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    [Fact]
    public void EveryLootPointOfTheGasStationIsPinned()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Maps + "B_3_Gas_Station");
        var group = doc.Actors.Single(a => a.Name == "ItemSpawnerGroup_1");
        var placements = LevelScenePreparer.CollectPlacements(doc, 0);
        var loot = placements.Where(p => p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Loot)).ToList();

        // 11 points of the spawner group (they pick as the group) and 42 in the shop, the shed and a container (they do not).
        Assert.Equal(53, loot.Count);
        Assert.Equal(11, loot.Count(p => p.SelectableId == LevelScenePreparer.SelectableIdOf(0, group)));
        Assert.DoesNotContain(loot, p => p.World.Translation == group.WorldTransform.Translation);
        Assert.Equal(("Spawn.Loot", 11), (SpawnMarkers.Describe(group)!.Value.Key, (int)SpawnMarkers.Describe(group)!.Value.Args[0]));
        Assert.Contains(doc.Actors, a => SpawnMarkers.Describe(a) is { Key: "Spawn.Building" } d && d.Args[1].ToString()!.Contains("World_Shelf_Food", StringComparison.Ordinal));
    }

    [Fact]
    public void SentriesShowWhereTheyStandAndTheirPatrol()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Maps + "A_4_Military_Base");
        var pins = doc.Actors.SelectMany(a => SpawnMarkers.PinsOf(a)).ToList();

        Assert.Equal(4, pins.Count(p => p.Kind == SpawnKind.Sentry));
        Assert.Equal(doc.Actors.Where(a => a.ClassName == "SentrySpawner2").Sum(a => a.PatrolPoints.Count), pins.Count(p => p.Kind == SpawnKind.Patrol));
        Assert.True(pins.Count(p => p.Kind == SpawnKind.Patrol) > 0);
        Assert.All(doc.Actors.SelectMany(a => a.Components).Where(c => c.ClassName.Contains("ItemSpawner", StringComparison.Ordinal)),
            c => Assert.NotEmpty(c.SpawnMarkers));
    }

    /// <summary>
    /// The game's far-view model of a level is built from the level's real layout, so what the map draws must cover it:
    /// a misplaced building leaves part of the far model bare.
    /// </summary>
    [Fact]
    public void PlacementsMatchTheGamesOwnFarModels()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var island = PackageFiles.Read(catalog, FarModels.IslandLevel, Path.GetTempPath())!;
        var descriptions = FarModels.ReadDescriptions(CookedPackage.Parse(island.Header, island.Exports, null, FarModels.IslandLevel), out _);
        var reader = new Cue4ParseLevelReader(catalog);
        var meshes = new BendSupport(catalog).Describe;
        foreach (var name in new[] { "B_3_Gas_Station", "A_0_Outpost_Exterior", "B_3_Mirkovci_01", "A_0_Dr_Tudman_Bridge", "C_2_Power_Plant" })
        {
            var doc = LevelDocument.Load(reader, Maps + name);
            var boxes = LevelScenePreparer.CollectPlacements(doc, 0)
                .Where(p => !SpawnMarkers.IsMarker(p.MeshPath))
                .Select(p => (p, b: meshes(p.MeshPath.Split('#')[0])?.Bounds))
                .Where(x => x.b is { IsEmpty: false })
                .Select(x => new CutBox(x.p.World, x.b!.Value))
                .ToList();
            var grid = boxes.SelectMany(b =>
            {
                var (min, max) = b.WorldBounds();
                return from x in Enumerable.Range((int)Math.Floor(min.X / 1000), (int)Math.Floor(max.X / 1000) - (int)Math.Floor(min.X / 1000) + 1)
                       from y in Enumerable.Range((int)Math.Floor(min.Y / 1000), (int)Math.Floor(max.Y / 1000) - (int)Math.Floor(min.Y / 1000) + 1)
                       select (Cell: (x, y), Box: b);
            }).ToLookup(e => e.Cell, e => e.Box);
            var far = descriptions.Single(d => d.Name == name);
            var verts = catalog.LoadObject<UStaticMesh>(far.Meshes[0]).RenderData!.LODs![0].PositionVertexBuffer!.Verts!;
            var covered = verts.Select(v => far.World.TransformPosition(new FVector(v.X, v.Y, v.Z)))
                .Count(p => grid[((int)Math.Floor(p.X / 1000), (int)Math.Floor(p.Y / 1000))].Any(b => b.Contains(p, 100f)));

            Assert.True(covered >= verts.Length * 0.995, $"{name}: only {covered} of {verts.Length} far-model vertices lie on what the map draws.");
        }
    }
}
