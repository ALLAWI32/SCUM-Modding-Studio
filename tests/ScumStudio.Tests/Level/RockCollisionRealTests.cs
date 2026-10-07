using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
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
/// Owner (B_4 outpost filled with rocks, 2026-10-07): "walking on these rocks half my body is inside them", "when a player
/// is knocked out he falls under the rocks: from there he shoots others and nobody can hit him". His coastal rocks
/// (foliage instances of Landscape_B_4_2b) and the rocks he added to A_4_Farm_04, read back the way the game loads them.
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class RockCollisionRealTests
{
    private const string Tile = MapSlice.MapsPath + "Landscape_B_4_2b";
    private const string Farm = MapSlice.MapsPath + "A_4_Farm_04";
    private const string Coastal = "FoliageInstancedStaticMeshComponent_80"; // Coastal_Rock_08a, 46 instances
    private const string CoastalA = "/Game/ConZ_Files/Landscape/Rocks/Coastal_Rock_08/Coastal_Rock_08a.Coastal_Rock_08a";
    private const string CoastalB = "/Game/ConZ_Files/Landscape/Rocks/Coastal_Rock_08/Coastal_Rock_08b.Coastal_Rock_08b";
    private const string BigRock = "/Game/ConZ_Files/Landscape/Rocks/Big_Rock_01_Paint/Big_Rock_01_01.Big_Rock_01_01";
    private const string Cliff = "/Game/ConZ_Files/Landscape/Rocks/Cliff_02/Cliff_02b.Cliff_02b";
    private const string Stream = "/Game/ConZ_Files/Landscape/Rivers/Stream/Stream_Water_01.Stream_Water_01";

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks) => AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [Fact]
    public async Task MovedAndAddedCoastalRocksAreWrittenAsTheGameBuildsThemAndALiftedOneIsNamed()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Tile);
        var foliage = document.Actors.Single(a => a.Name == "InstancedFoliageActor_0");
        var pristine = foliage.InstanceTransforms.Where(i => i.ComponentName == Coastal).OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList();
        Assert.Equal(46, pristine.Count);

        // The owner's own: [8] moved (still sunk in), [56] where he stood (tilted on the outpost's pad, its open underside
        // up to 6.7 m over the ground), [47] added sunk in.
        var moved = new TransformValue(new FVector(579750.1f, -233354.9f, 54.175f), new FRotator(-23.997f, -94.429f, 0f), new FVector(0.9772637f));
        var lifted = new TransformValue(new FVector(575894.3f, -226489.2f, 605.842f), new FRotator(-23.768f, -86.024f, 17.373f), new FVector(0.9772637f));
        var sunk = new TransformValue(new FVector(578134.3f, -233531f, -16.47f), new FRotator(-23.997f, -94.429f, 0f), new FVector(0.9772637f));
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Rocks.ssproj"), "Rocks");
        project.Apply(EditOpFactory.SetInstanceTransform(document, foliage, Coastal, 8, moved, project.State));
        project.Apply(EditOpFactory.AddInstance(document, foliage, Coastal, lifted, project.State));
        project.Apply(EditOpFactory.AddInstance(document, foliage, Coastal, sunk, project.State));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var (stockPayload, stockBlock) = Component(ModdableAssets.ReadPackage(catalog, Tile));
        var (payload, block) = Component(ModdableAssets.ReadPackage(written, Tile));
        var (stockAt, _) = LevelPackageEditor.FindInstanceArray(stockPayload, stockBlock.EndOffset, pristine)!.Value;
        var (at, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, [.. pristine.Take(8), moved.ToTransform(), .. pristine.Skip(9), lifted.ToTransform(), sunk.ToTransform()])!.Value;
        Assert.Equal(64, size);
        Assert.Equal(48, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at - 4)));

        // Untouched instances are the stock bytes; the edited ones are exactly the matrix of the transform the map shows.
        for (var i = 0; i < 46; i++)
        {
            if (i != 8)
            {
                Assert.True(stockPayload.AsSpan(stockAt + (i * 64), 64).SequenceEqual(payload.AsSpan(at + (i * 64), 64)), $"instance {i} changed");
            }
        }

        foreach (var (index, value) in new[] { (8, moved), (46, lifted), (47, sunk) })
        {
            Assert.True(Matrix(value.ToTransform()).AsSpan().SequenceEqual(payload.AsSpan(at + (index * 64), 64)), $"instance {index} is not its transform's matrix");
        }

        // The game draws them and builds their bodies from this array (UE 4.27 InitPerInstanceRenderData / CreateAllInstanceBodies):
        // no cooked draw copy left, the reorder table covers all 48, and NumBuiltInstances (46) != 48 makes it rebuild the
        // cluster tree (BuildTreeIfOutdated). Bodies collide as the game's own coastal rocks: the component's SCUM_Solid_Wall.
        var arrayEnd = at + (48 * 64);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(arrayEnd + 8)));
        var reorder = ((ArrayValue)block.Find("InstanceReorderTable")!.Value).Items.OfType<IntValue>().Select(v => v.Value).ToList();
        Assert.Equal(48, reorder.Distinct().Count());
        Assert.Equal(46, ((IntValue)block.Find("NumBuiltInstances")!.Value).Value);
        Assert.Equal("SCUM_Solid_Wall", Profile(block));
        Assert.Equal(Profile(stockBlock), Profile(block));
        var component = written.LoadPackage(Tile).GetExports().OfType<UInstancedStaticMeshComponent>().Single(c => c.Name == Coastal);
        Assert.Equal(48, component.PerInstanceSMData!.Length);

        // The rock collides with its triangles (LOD 3) and blocks walking players and knocked-out bodies alike.
        var collision = MeshCollision.Read(catalog.LoadObject<UStaticMesh>(CoastalA))!;
        Assert.Equal("CTF_UseComplexAsSimple", collision.TraceFlag);
        Assert.Equal("SCUM_Solid_Wall", collision.DefaultProfile);
        Assert.Empty(collision.LetsThrough);

        // Collision check: the lifted rock is named (players get under it and inside), the sunk ones are not.
        var hollow = result.Warnings.Where(w => w.Contains("is hollow and open underneath", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(hollow);
        Assert.Contains(Coastal + "(added) at X=575894", line, StringComparison.Ordinal);
        Assert.Contains("collision check", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RocksAddedToTheFarmCollideAsTheGamesOwnAndABentCliffIsWrittenStraight()
    {
        if (Paks is not { } paks)
        {
            return; // needs the real game files
        }

        using var catalog = Open(paks);
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Farm.ssproj"), "Farm");

        // The owner's: a coastal rock standing on its open side (85% of that edge over the ground), a big rock (closed), a cliff
        // he bent, and a stream's water.
        var shell = new AddStaticMeshActorOp(Farm, "Coastal_Rock_08b_Added", CoastalB,
            new TransformValue(new FVector(569444.1f, -229212.25f, 240.80537f), new FRotator(0f, -159.6448f, 0f), new FVector(0.50200033f)));
        var big = new AddStaticMeshActorOp(Farm, "Big_Rock_01_01_Added", BigRock,
            new TransformValue(new FVector(577039.6f, -226606.47f, 377.02847f), FRotator.Zero, new FVector(2.2264f)));
        var cliff = new AddStaticMeshActorOp(Farm, "Cliff_02b_Added", Cliff,
            new TransformValue(new FVector(572121.3f, -223319.53f, -603.5198f), new FRotator(5.8406096f, 138.16989f, -5.7933025f), new FVector(1.5472889f)));
        var stream = new AddStaticMeshActorOp(Farm, "Stream_Water_01_Added", Stream, TransformValue.At(569000f, -229000f, 100f));
        foreach (var op in new EditOp[] { shell, big, cliff, stream, new BendActorOp(cliff.Created, 0f, 20f) })
        {
            project.Apply(op);
        }

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        // The bent cliff is written straight (the game's own triangle collision; bent it got 15 cm slabs of boxes).
        Assert.Contains(result.Warnings, w => w.Contains("Cliff_02b_Added: exported straight - a rock or cliff", StringComparison.Ordinal));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var package = ModdableAssets.ReadPackage(written, Farm);
        foreach (var name in new[] { shell.NewName, big.NewName, cliff.NewName })
        {
            var actor = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == name);
            Assert.Equal("StaticMeshActor", package.GetExportClassName(actor));
            var componentIndex = Enumerable.Range(0, package.Exports.Count).Single(i => package.Exports[i].OuterIndex == actor + 1);
            Assert.Equal("StaticMeshComponent", package.GetExportClassName(componentIndex));

            // No BodyInstance written: the component collides as its mesh by default (bUseDefaultCollision of a
            // StaticMeshActor), and every SCUM rock defaults to SCUM_Solid_Wall, as the game's coastal rock foliage collides.
            var props = package.ReadProperties(componentIndex);
            Assert.Null(props.Find("BodyInstance"));
            Assert.Null(props.Find("bUseDefaultCollision"));
        }

        foreach (var mesh in new[] { CoastalB, BigRock, Cliff })
        {
            var collision = MeshCollision.Read(catalog.LoadObject<UStaticMesh>(mesh))!;
            Assert.Equal("SCUM_Solid_Wall", collision.DefaultProfile);
            Assert.Empty(collision.LetsThrough);
        }

        // Collision check: the lifted shell and the water are named; the big rock (a closed mesh) is not.
        Assert.Single(result.Warnings, w => w.Contains(shell.NewName + ": Coastal_Rock_08b is hollow and open underneath", StringComparison.Ordinal));
        Assert.Single(result.Warnings, w => w.Contains(stream.NewName + ": Stream_Water_01 has no collision for players", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains(big.NewName, StringComparison.Ordinal));
    }

    private static (byte[] Payload, PropertyBlock Block) Component(CookedPackage package)
    {
        var index = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == Coastal);
        return (package.GetExportData(index).ToArray(), package.ReadProperties(index));
    }

    private static string? Profile(PropertyBlock block) =>
        block.Find("BodyInstance")?.Value is StructValue body && body.Properties.FirstOrDefault(p => p.Name == "CollisionProfileName")?.Value is NameValue name ? name.Value : null;

    private static byte[] Matrix(FTransform t)
    {
        var m = t.ToMatrixWithScale();
        var bytes = new byte[64];
        float[] f = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), f[i]);
        }

        return bytes;
    }
}
