using System.Numerics;
using System.Text;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.App;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner (a screenshot of a yellow diamond over a wrecked car): "no arrows or pins, show me the object itself as a 3D
/// model, the pin looks primitive". A spawn place's pin is the object that spawns there, half transparent, standing on the
/// place; it keeps the pin's keys. Pure parts here, the game files in <see cref="SpawnModelsRealTests"/>.
/// </summary>
public sealed class SpawnModelTests
{
    [Fact]
    public void AStandInKeyCarriesItsKindAndModel()
    {
        const string model = "/Game/ConZ_Files/Vehicles/Car/SK_Car.SK_Car";
        var key = SpawnMarkers.MeshKey(SpawnKind.VehiclePlace, model);

        Assert.True(SpawnMarkers.IsMarker(key));
        Assert.Equal(SpawnKind.VehiclePlace, SpawnMarkers.KindOfMesh(key));
        Assert.Equal(model, SpawnMarkers.ModelOf(key));
        Assert.Null(SpawnMarkers.ModelOf(SpawnMarkers.MeshKey(SpawnKind.VehiclePlace)));
        Assert.Null(SpawnMarkers.ModelOf(model));
        // The plain box when the model fails to load, under the stand-in key (the viewport looks the mesh up by the placement's path).
        var fallback = SpawnMarkers.AssetFor(key)!;
        Assert.Equal(key, fallback.MeshPath);
        Assert.Equal(SpawnMarkers.Asset(SpawnKind.VehiclePlace).Mesh.TriangleCount, fallback.Mesh.TriangleCount);
    }

    [Fact]
    public void AModelStandsUnscaledOnTheFloorOfItsBox()
    {
        // A car shop's box: 11 x 5 x 3.5 m, turned a quarter, centred 175 cm over its floor.
        var yaw = FQuat.MakeFromEuler(new FVector(0f, 0f, 90f));
        var box = new FTransform(yaw, new FVector(1000f, 2000f, 175f), new FVector(11f, 5f, 3.5f));

        var at = SpawnMarkers.ModelAt(SpawnKind.Vehicle, box);

        Assert.Equal(FVector.One, at.Scale3D);
        Assert.True(FVector.Distance(new FVector(1000f, 2000f, 0f), at.Translation) < 0.01f);
        Assert.Equal(yaw, at.Rotation);
        var capsule = SpawnMarkers.PinAt(new FTransform(yaw, new FVector(1f, 2f, 3f), FVector.One), SpawnKind.Zombie);
        Assert.Equal(capsule.Translation, SpawnMarkers.ModelAt(SpawnKind.Zombie, capsule).Translation); // a person: on the place itself
        Assert.Equal(90f, capsule.Rotation.Rotator().Yaw, 0.01f); // facing the spawn's way
        Assert.Equal(capsule.Rotation, SpawnMarkers.ModelAt(SpawnKind.Zombie, capsule).Rotation);
    }

    [Fact]
    public void AStandInIsLiftedOntoItsFootAndTintedHalfTransparent()
    {
        // A metre-wide block from 50 cm below the origin to 50 above, one red material.
        float[] positions = [-50, -50, -50, 50, -50, -50, 50, 50, -50, -50, 50, 50];
        var mesh = MeshData.Create("Block", positions, [0, 1, 2, 0, 2, 3], sections: [new MeshSection("/Game/M.M", 0, 6)]);
        var model = new PreparedMeshAsset("/Game/Block.Block", mesh, null)
        {
            MaterialTints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { ["/Game/M.M"] = new(1f, 0f, 0f, 1f) },
        };

        var key = SpawnMarkers.MeshKey(SpawnKind.Zombie, model.MeshPath);
        var standIn = SpawnMarkers.StandIn(model, SpawnKind.Zombie, key);

        Assert.Equal(key, standIn.MeshPath);
        Assert.Equal(0f, standIn.Mesh.Bounds.Min.Z);
        Assert.Equal(100f, standIn.Mesh.Bounds.Max.Z);
        Assert.Equal(0f, standIn.Mesh.Positions[2]);
        Assert.Equal(-50f, model.Mesh.Positions[2]); // the model itself is untouched
        var tint = standIn.MaterialTints["/Game/M.M"];
        var expected = Vector4.Lerp(new Vector4(1f, 0f, 0f, 1f), SpawnMarkers.Color(SpawnKind.Zombie), SpawnMarkers.ModelTintShare);
        Assert.Equal(SpawnMarkers.StandInAlpha, tint.W);
        Assert.Equal(expected.X, tint.X, 1e-5f);
        Assert.Equal(expected.Y, tint.Y, 1e-5f);
        Assert.Equal(expected.Z, tint.Z, 1e-5f);
    }

    /// <summary>Owner: "traders must be the full character". A character's parts (body, head on its neck) merge into one mesh, each at its place, a section per material.</summary>
    [Fact]
    public void ACharactersPartsMergeIntoOneMeshAtTheirPlaces()
    {
        // A body triangle (textured, masked) and a head triangle on a bone 170 cm up, turned a quarter about Z.
        var body = MeshData.Create("SK_Body", [0, 0, 0, 100, 0, 0, 0, 0, 100], [0, 1, 2], [0, 1, 0, 0, 1, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1]);
        var head = MeshData.Create("SK_Head", [0, 0, 0, 10, 0, 0, 0, 0, 10], [0, 1, 2]);
        var neck = new FTransform(FQuat.MakeFromEuler(new FVector(0f, 0f, 90f)), new FVector(0f, 0f, 170f), FVector.One);
        var model = new PreviewModel("BP_Trader",
        [
            new PreviewPart("SK_Body / M_Body", body, "/Game/T_Body.T_Body", FTransform.Identity, null, 0.3f) { Material = "/Game/M_Body.M_Body" },
            new PreviewPart("SK_Head", head, null, neck, new Vector4(1f, 0f, 0f, 1f)) { Material = "/Game/M_Head.M_Head" },
        ], new Dictionary<string, TextureImage>());

        var merged = MeshPreviewLoader.Merge(model, "key");

        Assert.Equal("key", merged.MeshPath);
        Assert.Equal(6, merged.Mesh.VertexCount);
        Assert.Equal(2, merged.Mesh.Sections.Length);
        Assert.Equal(["/Game/M_Body.M_Body", "/Game/M_Head.M_Head"], merged.MaterialSlots);
        Assert.Equal("/Game/T_Body.T_Body", merged.MaterialTextures["/Game/M_Body.M_Body"]);
        Assert.Equal(0.3f, merged.MaterialAlphaCutoffs["/Game/M_Body.M_Body"]);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), merged.MaterialTints["/Game/M_Head.M_Head"]);
        Assert.Equal(3u, merged.Mesh.Indices[3]); // the head's indices follow the body's vertices
        // The head's second vertex (10,0,0): turned a quarter (→ +Y) and lifted onto the neck.
        var v = new FVector(merged.Mesh.Positions[12], merged.Mesh.Positions[13], merged.Mesh.Positions[14]);
        Assert.Equal(0f, v.X, 1e-3f);
        Assert.Equal(10f, v.Y, 1e-3f);
        Assert.Equal(170f, v.Z, 1e-3f);
        Assert.Equal(180f, merged.Mesh.Bounds.Max.Z, 1e-3f);
        Assert.Equal(1f, merged.Mesh.Normals[1 * 3 + 1], 1e-3f); // the body keeps its normals
        Assert.Equal(1f, merged.Mesh.Normals[3 * 3 + 2], 1e-3f); // the head has none: up is written for it
    }

    /// <summary>Owner: an item without a mesh shows its inventory icon on a small camera-facing card instead of the crate.</summary>
    [Fact]
    public void AnItemWithoutAMeshShowsItsIconOnACameraFacingCard()
    {
        const string icon = "/Game/ConZ_Files/Modding/Catalog/InventoryIcons/ICO_Card.ICO_Card";
        var key = SpawnMarkers.MeshKey(SpawnKind.Loot, SpawnMarkers.IconPrefix + icon);
        Assert.Equal(SpawnMarkers.IconPrefix + icon, SpawnMarkers.ModelOf(key));

        var card = SpawnMarkers.IconAsset(key, icon, 128, 64);

        Assert.True(card.Billboard);
        Assert.True(card.Shimmer);
        Assert.Equal(icon, card.MaterialTextures[key]);
        Assert.Equal(0.5f, card.MaterialAlphaCutoffs[key]); // the icon's transparent background is cut out
        Assert.Equal(2, card.Mesh.TriangleCount);
        Assert.Equal(0f, card.Mesh.Bounds.Min.Z); // standing on the place
        Assert.Equal(SpawnMarkers.IconSize, card.Mesh.Bounds.Max.Z);
        Assert.Equal(SpawnMarkers.IconSize * 2f, card.Mesh.Bounds.Max.X - card.Mesh.Bounds.Min.X, 1e-3f); // the icon's aspect
        var standIn = SpawnMarkers.StandIn(card, SpawnKind.Loot, key);
        Assert.True(standIn.Billboard && standIn.Shimmer);
        Assert.Equal(SpawnMarkers.StandInAlpha, standIn.MaterialTints[key].W);
    }
}

/// <summary>
/// The outpost's car shop, the airfield's world vehicle spawns, zombie points and hangar shelves drawn as the objects that
/// spawn there (translucent vehicles, zombies, items), picking with the keys the pins had. Real game files and OpenGL;
/// pictures with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class SpawnModelsRealTests
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    /// <summary>
    /// Discord feature request: "toggle between the old simple shapes of spawned objects and zombies/NPCs and the 3D
    /// models". With the models off the same pins are there, as simple markers, and nothing glints (the view stops redrawing).
    /// </summary>
    [Fact]
    public void SpawnModelsOffDrawsTheSamePinsAsSimpleShapes()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var outpost = LevelDocument.Load(reader, Maps + "A_0_Outpost");
        var places = SpawnPlaces.Over(SpawnPlaces.ReadFrom(catalog), [SpawnPlaces.AreaAround([outpost], 5_000f)!.Value])!;
        var preparer = new LevelScenePreparer(catalog);
        var models = preparer.Prepare([outpost, places], new LevelSceneOptions { TextureSize = 0, IncludeLandscape = false });
        var shapes = preparer.Prepare([outpost, places], new LevelSceneOptions { TextureSize = 0, IncludeLandscape = false, SpawnModels = false });

        static List<uint> Pins(PreparedLevelScene scene) => scene.Placements.Where(p => SpawnMarkers.IsMarker(p.MeshPath)).Select(p => p.SelectableId).Distinct().Order().ToList();
        Assert.NotEmpty(Pins(shapes));
        Assert.Equal(Pins(models), Pins(shapes));
        Assert.Contains(models.Meshes.Values, m => m.Shimmer);
        Assert.DoesNotContain(shapes.Meshes.Values, m => m.Shimmer);
        Assert.All(shapes.Placements.Where(p => SpawnMarkers.IsMarker(p.MeshPath)), p => Assert.Null(SpawnMarkers.ModelOf(p.MeshPath)));
    }

    [GlFact]
    public async Task SpawnPlacesShowTheirObjectsAsTranslucentModelsThatPickLikeThePins()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var outpost = LevelDocument.Load(reader, Maps + "A_0_Outpost");
        var airfield = LevelDocument.Load(reader, Maps + "A_4_Airfield");
        var areas = new[] { outpost, airfield }.Select(d => SpawnPlaces.AreaAround([d], 5_000f)!.Value).ToList();
        var places = SpawnPlaces.Over(SpawnPlaces.ReadFrom(catalog), areas)!;
        var documents = new[] { outpost, airfield, places };
        var prepared = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256 });

        // The car shop's box: the vehicle the shop sells, on the floor of the box, picking as the box part.
        var shop = outpost.Actors.Single(a => a.Name == "BP_Outpost_CarShop_NPC_and_VehicleSpawner_5");
        var shopId = LevelScenePreparer.SelectableIdOf(0, shop);
        var box = prepared.Placements.Single(p => p.SelectableId == shopId && p.Spawner == "VehicleSpawnBox" && SpawnMarkers.IsMarker(p.MeshPath));
        AssertModel(prepared, box, SpawnKind.Vehicle);
        Assert.Equal(InstanceKey.Of(shopId, "VehicleSpawnBox", InstanceKey.Part), box.PickKey(false));
        Assert.Equal(FVector.One, box.World.Scale3D);
        Assert.DoesNotContain('.', SpawnMarkers.ModelOf(box.MeshPath)![SpawnMarkers.ModelOf(box.MeshPath)!.LastIndexOf('/')..]); // a vehicle: its Blueprint, body and stock parts
        Assert.True(prepared.Meshes[box.MeshPath].Mesh.Sections.Length > 1, "the vehicle's body and parts as one mesh");

        // A world vehicle spawn point: the first vehicle of its group; it picks as the whole place.
        var vehicle = prepared.Placements.First(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.VehiclePlace);
        AssertModel(prepared, vehicle, SpawnKind.VehiclePlace);
        Assert.Null(vehicle.PickKey(false));
        Assert.Equal(LevelScenePreparer.SelectableIdOf(2, vehicle.Actor), vehicle.SelectableId);

        // A zombie spawn point: a zombie figure.
        var zombie = prepared.Placements.First(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Zombie);
        AssertModel(prepared, zombie, SpawnKind.Zombie);
        Assert.Equal(SpawnModels.ZombieMesh, SpawnMarkers.ModelOf(zombie.MeshPath));

        // A hangar shelf's loot point: an item of its preset, or the crate; still the loot point's own key.
        var hangar = airfield.Actors.Single(a => a.Name == "BP_Airplane_Hangar2_2");
        var loot = prepared.Placements.First(p => ReferenceEquals(p.Actor, hangar) && SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Loot);
        Assert.True(loot.InstanceKey!.Value.IsLootPoint);
        Assert.Equal(loot.InstanceKey, loot.PickKey(false));
        Assert.True(SpawnMarkers.ModelOf(loot.MeshPath) is not null || loot.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Loot));
        Assert.True(prepared.Meshes[loot.MeshPath].Mesh.TriangleCount > 0);

        // Every stand-in: half transparent in every material, its foot on the place.
        foreach (var (path, asset) in prepared.Meshes.Where(m => SpawnMarkers.ModelOf(m.Key) is not null))
        {
            Assert.All(asset.MaterialTints.Values, t => Assert.Equal(SpawnMarkers.StandInAlpha, t.W));
            Assert.InRange(asset.Mesh.Bounds.Min.Z, -0.01f, 0.01f);
            Assert.True(asset.Mesh.TriangleCount > 0, path);
        }

        // Zones, loot zones and drop zones stay pins; the pick keys are the ones the pins had.
        Assert.All(prepared.Placements.Where(p => SpawnMarkers.KindOfMesh(p.MeshPath) is SpawnKind.Zone or SpawnKind.LootZone or SpawnKind.PlayerDrop or SpawnKind.Animal),
            p => Assert.Null(SpawnMarkers.ModelOf(p.MeshPath)));
        var before = documents.SelectMany((d, i) => LevelScenePreparer.CollectPlacements(d, i)).Where(p => SpawnMarkers.IsMarker(p.MeshPath)).Select(Pick).OrderBy(k => k).ToList();
        var after = prepared.Placements.Where(p => SpawnMarkers.IsMarker(p.MeshPath)).Select(Pick).OrderBy(k => k).ToList();
        Assert.Equal(before, after);

        using var harness = GlHarness.Create(1024, 768);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        await ShotAsync("standins-vehicle", vehicle.World.Translation + new FVector(0f, 0f, 80f), -50f, -22f, 900f);
        await ShotAsync("standins-carshop", box.World.Translation + new FVector(0f, 0f, 100f), -40f, -25f, 1100f);
        await ShotAsync("standins-zombie", zombie.World.Translation + new FVector(0f, 0f, 90f), -60f, -15f, 420f);
        await ShotAsync("standins-loot", loot.World.Translation + new FVector(0f, 0f, 20f), -60f, -40f, 380f);

        static string Pick(ScenePlacement p) =>
            $"{p.SelectableId}|{SpawnMarkers.KindOfMesh(p.MeshPath)}|{p.PickKey(false)}|{p.PickKey(true)}|{p.Spawner}|{p.LootMarker}|{p.SpawnPoint}";

        async Task ShotAsync(string name, FVector target, float yaw, float pitch, float distance)
        {
            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder)
            {
                return;
            }

            var camera = new FlyCamera();
            camera.SetClipRange(5f, 1_000_000f);
            camera.Orbit(UeToGl.Point(target), yaw, pitch, distance);
            harness.Renderer.Render(harness.Target, level.Scene, camera);
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(harness.Target.ReadColorRgba(), 1024, 768, Path.Combine(folder, name + ".png"));
        }
    }

    /// <summary>
    /// Owner: "traders must be the full character, not a floating body". A trader's Blueprint composes its body, head,
    /// hair, beard and gear into one figure of a person's height with its feet on the ground, posed from its idle; the
    /// outpost's traders are drawn that way. Frames go to <c>SCUMSTUDIO_SCREENSHOTS</c> as <c>npcs-*.png</c> with a
    /// <c>npcs-parts.txt</c> listing the parts.
    /// </summary>
    [GlFact]
    public async Task TradersAreWholePeopleComposedFromTheirBlueprints()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var report = new StringBuilder();
        var loader = new MeshPreviewLoader(catalog) { TextureSize = 0 };
        foreach (var leaf in new[] { "BP_Banker01", "BP_Doctor_01", "BP_ArmsDealer_01" })
        {
            var package = catalog.PackageFiles
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), leaf, StringComparison.OrdinalIgnoreCase))
                .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
                .FirstOrDefault();
            Assert.True(package is not null, leaf + " is not in the game files");
            var model = loader.LoadBlueprint(package!);
            Assert.True(model is not null, leaf + " has no mesh");
            var meshes = model!.Parts.Select(p => p.Mesh.Name).Distinct().ToList();
            report.AppendLine($"{leaf}: {package}");
            foreach (var part in model.Parts)
            {
                report.AppendLine($"  {part.Name} @ {part.Transform.Translation} tris {part.Mesh.TriangleCount} z {part.Mesh.Bounds.Min.Z:F0}..{part.Mesh.Bounds.Max.Z:F0}");
            }

            foreach (var material in model.Parts.Select(p => p.Material).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new MaterialInspector(catalog).Inspect(material);
                    report.AppendLine($"  material {material[(material.LastIndexOf('/') + 1)..]}: base {info.BaseColorTexture?[(info.BaseColorTexture.LastIndexOf('/') + 1)..] ?? "-"}; "
                                      + $"params {string.Join(", ", info.Textures.Select(t => t.Name + "=" + t.TexturePath[(t.TexturePath.LastIndexOf('/') + 1)..]))}; "
                                      + $"refs {string.Join(", ", info.ReferencedTextures.Select(r => r[(r.LastIndexOf('/') + 1)..]))}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    report.AppendLine($"  material {material}: {ex.Message}");
                }
            }

            Assert.True(meshes.Count >= 2, $"{leaf}: one part only ({string.Join(", ", meshes)})");
            var key = SpawnMarkers.MeshKey(SpawnKind.Trader, package!);
            var figure = SpawnMarkers.StandIn(MeshPreviewLoader.Merge(model, package!), SpawnKind.Trader, key);
            var bounds = figure.Mesh.Bounds;
            report.AppendLine($"  figure {bounds.Min} .. {bounds.Max}");
            Assert.InRange(bounds.Min.Z, -0.01f, 0.01f);
            Assert.InRange(bounds.Max.Z - bounds.Min.Z, 150f, 200f);
            Assert.InRange(bounds.Max.X - bounds.Min.X, 30f, 150f); // a standing person, not a T
            Assert.InRange(bounds.Max.Y - bounds.Min.Y, 30f, 150f);
            Assert.True(figure.Mesh.Sections.Length >= 2);
            Assert.True(figure.Shimmer);
        }

        // The outpost: every trader a composed figure under its pin's key.
        var reader = new Cue4ParseLevelReader(catalog);
        var outpost = LevelDocument.Load(reader, Maps + "A_0_Outpost");
        var prepared = new LevelScenePreparer(catalog).Prepare([outpost], new LevelSceneOptions { TextureSize = 256 });
        var traders = prepared.Placements.Where(p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Trader).ToList();
        Assert.True(traders.Count >= 5, $"{traders.Count} traders");
        foreach (var trader in traders)
        {
            var model = SpawnMarkers.ModelOf(trader.MeshPath);
            var asset = prepared.Meshes[trader.MeshPath];
            report.AppendLine($"trader {trader.Actor.Name} ({string.Join(", ", trader.Actor.TraderMarkers.Select(m => m.Name))}): {model} at {trader.World.Translation}, "
                              + $"{asset.Mesh.Sections.Length} sections, {asset.Mesh.TriangleCount} tris, z {asset.Mesh.Bounds.Min.Z:F0}..{asset.Mesh.Bounds.Max.Z:F0}");
            Assert.NotNull(model);
            Assert.DoesNotContain('.', model![model.LastIndexOf('/')..]); // a Blueprint package: the whole character
            Assert.True(asset.Mesh.Sections.Length >= 2, trader.MeshPath);
            Assert.InRange(asset.Mesh.Bounds.Max.Z, 150f, 200f);
            Assert.True(asset.Shimmer);
        }

        // Loot points whose first items have no mesh (cards, papers): the item's inventory icon on a camera-facing card.
        var icons = prepared.Placements.Where(p => SpawnMarkers.ModelOf(p.MeshPath)?.StartsWith(SpawnMarkers.IconPrefix, StringComparison.Ordinal) == true).ToList();
        report.AppendLine($"{icons.Count} icon cards: {string.Join(", ", icons.Select(p => SpawnMarkers.ModelOf(p.MeshPath)).Distinct())}");
        foreach (var icon in icons)
        {
            var asset = prepared.Meshes[icon.MeshPath];
            Assert.True(asset.Billboard && asset.Shimmer, icon.MeshPath);
            Assert.NotEmpty(asset.MaterialTextures);
            Assert.Equal(2, asset.Mesh.TriangleCount);
        }

        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "npcs-parts.txt"), report.ToString());
        using var harness = GlHarness.Create(1280, 800);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        harness.Renderer.Time = 0.375f; // the pulse's brightest
        var armory = traders.First(t => t.Actor.TraderMarkers.Any(m => m.Name == "A_0_Armory"));
        var other = traders.First(t => !ReferenceEquals(t.Actor, armory.Actor));
        await ShotAsync("npcs-armory-a", armory, 0f, -8f, 420f);
        await ShotAsync("npcs-armory-b", armory, 180f, -8f, 420f);
        await ShotAsync("npcs-armory-head", armory, 180f, -5f, 130f, 165f);
        await ShotAsync("npcs-second-a", other, 0f, -8f, 420f);
        await ShotAsync("npcs-second-b", other, 180f, -8f, 420f);
        if (icons.Count > 0)
        {
            await ShotAsync("npcs-icon", icons[0], 0f, -30f, 260f);
        }

        async Task ShotAsync(string name, ScenePlacement trader, float yawOffset, float pitch, float distance, float height = 90f)
        {
            var camera = new FlyCamera();
            camera.SetClipRange(5f, 1_000_000f);
            // Facing the figure: it faces its pin's yaw, the camera looks back along it (UE yaw → GL heading is mirrored).
            camera.Orbit(UeToGl.Point(trader.World.Translation + new FVector(0f, 0f, height)), trader.World.Rotation.Rotator().Yaw + 180f + yawOffset, pitch, distance);
            harness.Renderer.Render(harness.Target, level.Scene, camera);
            await ImageExport.SavePngAsync(harness.Target.ReadColorRgba(), 1280, 800, Path.Combine(folder, name + ".png"));
        }
    }

    private static void AssertModel(PreparedLevelScene prepared, ScenePlacement placement, SpawnKind kind)
    {
        Assert.Equal(kind, SpawnMarkers.KindOfMesh(placement.MeshPath));
        var model = SpawnMarkers.ModelOf(placement.MeshPath);
        Assert.NotNull(model);
        Assert.StartsWith("/Game/", model, StringComparison.Ordinal);
        var asset = prepared.Meshes[placement.MeshPath];
        Assert.True(asset.Mesh.TriangleCount > 500, $"{placement.MeshPath}: {asset.Mesh.TriangleCount} triangles");
        Assert.NotEqual(SpawnMarkers.MeshKey(kind), asset.MeshPath);
    }
}
