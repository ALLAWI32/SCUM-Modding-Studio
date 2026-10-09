using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// "I placed a bridge and the meadow grass grows through it" (Discord, 2026-10-09): a river bridge placed over a bush on
/// the grass of Landscape_B_4_2b, exported and read back the way the game reads the tile (CUE4Parse's
/// <c>GrassData</c>). Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class GrassClearingRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_B_4_2b";
    private const string Farm = MapSlice.MapsPath + "A_4_Farm_04";
    private const string Bridge = "/Game/ConZ_Files/Models/Road/RiverBridge/SM_RiverBridge_01.SM_RiverBridge_01";

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public async Task ABridgeOnTheGrassClearsTheGrassAndBushesUnderItAndNothingElse()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var tile = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Tile); // first: maps SCUM's foliage classes
        var stock = LandscapeLayerReader.ReadPackage(catalog, Tile);
        var surfaces = LandscapeExtractor.Extract(catalog, Tile, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
            .SelectMany(p => p.Components).ToDictionary(c => c.Name, c => c.Surface!);

        // The bush with the most grass around it (the meadow), and the ground under it.
        float GrassAt(Vector2 p)
        {
            foreach (var c in stock)
            {
                if (surfaces[c.Name].TryGetQuadCoordinates(p.X, p.Y, out var qx, out var qy))
                {
                    var i = ((int)MathF.Round(qy) * c.SampleCount) + (int)MathF.Round(qx);
                    return c.Grass.Where(g => !g.GrassTypeName.Contains("Underwater", StringComparison.Ordinal)).Sum(g => g.Density[i]);
                }
            }

            return 0;
        }

        var bushes = tile.Actors.SelectMany(a => a.InstanceTransforms.Where(i => a.FindComponent(i.ComponentName) is { } c && GrassClearing.IsClearedFoliage(c))).ToList();
        Assert.NotEmpty(bushes);
        var bush = bushes.MaxBy(b => GrassAt(new Vector2(b.WorldTransform.Translation.X, b.WorldTransform.Translation.Y)))!;
        var at = bush.WorldTransform.Translation;
        Assert.True(GrassAt(new Vector2(at.X, at.Y)) > 0, "no bush stands on grass");

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Grass.ssproj"), "Grass");
        var farm = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Farm);
        var add = EditOpFactory.AddStaticMeshActor(farm, Bridge, new TransformValue(at, new FRotator(0, 30, 0), FVector.One), project.State);
        project.Apply(add);
        Assert.True(project.State.ClearsGrass(add.Created));

        // The tile is an edited level of its own too (a coastal rock moved, as in RockCollisionRealTests): the clearing goes on top.
        var foliage = tile.Actors.Single(a => a.Name == "InstancedFoliageActor_0");
        var moved = new TransformValue(new FVector(579750.1f, -233354.9f, 54.175f), new FRotator(-23.997f, -94.429f, 0f), new FVector(0.9772637f));
        project.Apply(EditOpFactory.SetInstanceTransform(tile, foliage, "FoliageInstancedStaticMeshComponent_80", 8, moved, project.State));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        var level = Assert.Single(result.Levels, l => l.PackagePath == Tile);
        Assert.True(level.Report.ClearedGrassSamples > 0);
        Assert.True(level.Report.DeletedInstances > 0);

        // The footprint as the export saw it: the bridge as written into the farm level.
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var bridge = LevelDocument.Load(new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false }), Farm)
            .Actors.Single(a => a.Name == add.NewName);
        var bounds = new BendSupport(catalog);
        var footprints = GrassClearing.Footprints(bridge, m => bounds.Describe(m)?.Bounds);
        Assert.NotEmpty(footprints);
        var far = footprints.Select(p => GrassClearing.Grow(p, 2 * 150f)).ToList();

        // Grass: 0 under the bridge, as cooked two quads away and more; only the grass bytes of the components changed.
        var after = LandscapeLayerReader.ReadPackage(written, Tile);
        int inside = 0, outside = 0;
        foreach (var c in stock)
        {
            var a = after.Single(x => x.Name == c.Name);
            var s = surfaces[c.Name];
            Assert.Equal(c.Grass.Select(g => g.GrassTypeName), a.Grass.Select(g => g.GrassTypeName));
            for (var y = 0; y < c.SampleCount; y++)
            {
                for (var x = 0; x < c.SampleCount; x++)
                {
                    var w = s.WorldPosition(x, y);
                    var p = new Vector2(w.X, w.Y);
                    var i = (y * c.SampleCount) + x;
                    if (GrassClearing.Covers(footprints, p))
                    {
                        inside++;
                        Assert.All(a.Grass, g => Assert.Equal(0, g.Density[i]));
                    }
                    else if (!GrassClearing.Covers(far, p))
                    {
                        outside++;
                        for (var g = 0; g < c.Grass.Count; g++)
                        {
                            Assert.Equal(c.Grass[g].Density[i], a.Grass[g].Density[i]);
                        }
                    }
                }
            }
        }

        Assert.True(inside > 10, $"{inside} samples under the bridge");
        Assert.True(outside > 100_000);

        var stockPackage = ModdableAssets.ReadPackage(catalog, Tile);
        var writtenPackage = ModdableAssets.ReadPackage(written, Tile);
        Assert.Equal(stockPackage.Exports.Count, writtenPackage.Exports.Count);
        for (var e = 0; e < stockPackage.Exports.Count; e++)
        {
            if (stockPackage.GetExportClassName(e) != "LandscapeComponent")
            {
                continue;
            }

            var before = stockPackage.GetExportBytes(e);
            var now = writtenPackage.GetExportBytes(e);
            Assert.Equal(before.Length, now.Length);
            var block = stockPackage.ReadProperties(e);
            var maps = GrassClearing.ReadGrassMaps(before, block.EndOffset, stock[0].SampleCount)!;
            for (var b = 0; b < before.Length; b++)
            {
                if (before[b] != now[b])
                {
                    Assert.Contains(maps, m => b >= m.Offset && b < m.Offset + m.Length);
                    Assert.Equal(0, now[b]);
                }
            }
        }

        // Bushes and grass meshes standing under the bridge are gone (collapsed in place); everything else stands.
        var tileAfter = LevelDocument.Load(new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false }), Tile);
        var gone = 0;
        foreach (var actor in tile.Actors.Where(a => a.InstanceTransforms.Count > 0))
        {
            var now = tileAfter.Actors.Single(a => a.Name == actor.Name).InstanceTransforms
                .ToDictionary(i => (i.ComponentName, i.InstanceIndex));
            foreach (var instance in actor.InstanceTransforms)
            {
                var base0 = new Vector2(instance.WorldTransform.Translation.X, instance.WorldTransform.Translation.Y);
                var cleared = GrassClearing.IsClearedFoliage(actor.FindComponent(instance.ComponentName)!) && GrassClearing.Covers(footprints, base0);
                var scale = now[(instance.ComponentName, instance.InstanceIndex)].LocalTransform.Scale3D.X;
                Assert.True(cleared ? scale < 0.01f : MathF.Abs(scale - instance.LocalTransform.Scale3D.X) < 1e-3f, $"{actor.Name}.{instance.ComponentName}[{instance.InstanceIndex}] cleared={cleared} scale={scale}");
                gone += cleared ? 1 : 0;
            }
        }

        Assert.Equal(level.Report.DeletedInstances, gone);
        var rock = tileAfter.Actors.Single(a => a.Name == foliage.Name).InstanceTransforms.Single(i => i.ComponentName == "FoliageInstancedStaticMeshComponent_80" && i.InstanceIndex == 8);
        Assert.Equal(moved.Location.X, rock.WorldTransform.Translation.X, 0.5f);
        output.WriteLine($"bridge at ({at.X:0}, {at.Y:0}): {footprints.Count} footprint(s), {inside} samples under it, {level.Report.ClearedGrassSamples} densities zeroed, {gone} bush/grass instance(s) gone");
        Assert.True(GrassClearing.Covers(footprints, new Vector2(at.X, at.Y)));
    }
    [Fact]
    public async Task ACopiedBlueprintClearsTheGrassUnderItsMeshes()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var tile = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Tile);
        var bounds = new BendSupport(catalog);
        var bush = tile.Actors.SelectMany(a => a.InstanceTransforms.Where(i => a.FindComponent(i.ComponentName) is { } c && GrassClearing.IsClearedFoliage(c))).First();
        var farm = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Farm);

        var flat = LevelDocument.Load(new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false }), Farm);
        // A Blueprint whose meshes the level does not store (only its class has them) if the farm has one, else its first house.
        int Count(ActorRecord a) => GrassClearing.Footprints(a, m => bounds.Describe(m)?.Bounds).Count;
        var blueprints = farm.Actors.Where(a => a.ClassPath.EndsWith("_C", StringComparison.Ordinal) && Count(a) > 0).ToList();
        var blueprint = blueprints.FirstOrDefault(a => Count(flat.FindActor(a.Name)!) == 0) ?? blueprints.First(a => a.Name.StartsWith("BP_SB_House", StringComparison.Ordinal));
        output.WriteLine($"{blueprints.Count(a => Count(flat.FindActor(a.Name)!) == 0)} of {blueprints.Count} Blueprints keep all their meshes in the class only");
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Grass.ssproj"), "Grass");
        var copy = EditOpFactory.Duplicate(farm, blueprint, new TransformValue(bush.WorldTransform.Translation, new FRotator(0, 0, 0), FVector.One), project.State);
        project.Apply(copy);
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        var level = Assert.Single(result.Levels, l => l.PackagePath == Tile);
        Assert.True(level.Report.DeletedInstances > 0, $"{blueprint.ClassPath}: the bush under it stands");
        output.WriteLine($"{blueprint.ClassPath} at the bush: {level.Report.ClearedGrassSamples} densities zeroed, {level.Report.DeletedInstances} bush/grass instance(s) gone");
    }
}
