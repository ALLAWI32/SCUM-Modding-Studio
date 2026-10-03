using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Level;

/// <summary>
/// End-to-end tests of <see cref="Cue4ParseLevelReader"/> on synthetic cooked packages written with the in-house
/// package writer and read back through CUE4Parse (loose-file catalog), plus fixture checks on the stock archive.
/// </summary>
public sealed class Cue4ParseLevelReaderTests
{
    private const string LevelPath = SyntheticLevels.LevelPath;
    private const string RockPackage = SyntheticLevels.RockPackage;
    private const string BlueprintPackage = SyntheticLevels.BlueprintPackage;
    private const float Tolerance = 1e-3f;

    [Fact]
    public void ReadsActorsComponentsAndInstancesFromACookedLevel()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: false);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var reader = new Cue4ParseLevelReader(catalog);

        Assert.True(reader.LevelExists(LevelPath));
        Assert.False(reader.LevelExists(RockPackage));
        var data = reader.ReadLevel(LevelPath);

        Assert.Equal(LevelPath, data.PackagePath);
        Assert.Equal(0, data.WorldExportIndex);
        Assert.Equal(1, data.LevelExportIndex);
        Assert.Equal(new[] { 2, 5, 8 }, data.ActorIndices); // the null entry of ULevel.Actors is skipped
        Assert.Equal("/Script/Engine.StaticMeshActor", data.Exports[2].ClassPath);
        Assert.Equal("StaticMeshActor", data.Exports[2].ClassName);
        Assert.False(data.Exports[2].IsBlueprintClass);
        Assert.True(data.Exports[8].IsBlueprintClass);
        Assert.Equal($"{BlueprintPackage}.BP_Lamp_C", data.Exports[8].ClassPath);
        Assert.Equal(1, data.Exports[2].OuterIndex);
        Assert.Contains("RootComponent", data.Exports[2].PropertyNames);

        var doc = LevelDocument.FromData(data);
        Assert.Equal(3, doc.Actors.Count);

        var house = doc.FindActor("StaticMeshActor_1")!;
        Assert.Equal(ActorKind.StaticMeshActor, house.Kind);
        Assert.Equal(3, house.RootComponent);
        Assert.False(house.RootInferred);
        Assert.Equal($"{RockPackage}.SM_Rock", house.StaticMeshPath);
        AssertNear(new FTransform(new FRotator(0, 90, 0), new FVector(1000, 0, 0), new FVector(2, 2, 2)), house.WorldTransform);
        var door = house.FindComponent("Door")!;
        Assert.Equal(3, door.AttachParent);
        AssertNear(new FVector(1000, 200, 0), door.WorldTransform.Translation);

        var rocks = doc.FindActor("Rocks_Actor")!;
        Assert.Equal(ActorKind.Other, rocks.Kind);
        Assert.True(rocks.RootInferred);
        Assert.Equal("DefaultSceneRoot", rocks.Root!.Name);
        var ism = rocks.FindComponent("Rocks")!;
        Assert.True(ism.IsInstanced);
        Assert.Equal("InstancedStaticMeshComponent", ism.ClassName);
        Assert.Equal(2, ism.Instances.Count);
        AssertNear(SyntheticLevels.Instance0, ism.Instances[0]);
        AssertNear(SyntheticLevels.Instance1, ism.Instances[1]);
        AssertNear(new FVector(10, 0, 100), ism.WorldTransform.Translation);
        Assert.Equal(45f, ism.WorldTransform.Rotator().Yaw, Tolerance); // bAbsoluteRotation
        Assert.Equal(2, rocks.InstanceTransforms.Count);
        AssertNear(SyntheticLevels.Instance1 * ism.WorldTransform, rocks.InstanceTransforms[1].WorldTransform);

        var lamp = doc.FindActor("BP_Lamp_C_1")!;
        Assert.Equal(ActorKind.Blueprint, lamp.Kind);
        Assert.Equal("BP_Lamp_C", lamp.ClassName);
        AssertNear(new FVector(-500, 250, 0), lamp.WorldTransform.Translation);
    }

    [Fact]
    public void FollowsBlueprintTemplatesForValuesTheLevelDoesNotStore()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var doc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), LevelPath);
        var lamp = doc.FindActor("BP_Lamp_C_1")!;

        // RootComponent comes from the Blueprint CDO (DefaultSceneRoot_GEN_VARIABLE -> DefaultSceneRoot).
        Assert.False(lamp.RootInferred);
        Assert.Equal("DefaultSceneRoot", lamp.Root!.Name);

        // Mesh, location and scale of the bulb exist only on its SCS template.
        var bulb = lamp.FindComponent("Bulb")!;
        Assert.True(bulb.UsesTemplateValues);
        Assert.Equal($"{RockPackage}.SM_Rock", bulb.StaticMeshPath);
        AssertNear(new FVector(0, 0, 30), bulb.RelativeTransform.Translation);
        AssertNear(new FVector(0.5f, 0.5f, 0.5f), bulb.RelativeTransform.Scale3D);
        AssertNear(new FVector(-500, 250, 30), bulb.WorldTransform.Translation);
        Assert.Equal($"{BlueprintPackage}.Bulb_GEN_VARIABLE", doc.Data.Exports[bulb.ExportIndex].TemplatePath);

        // Without template resolution the same level reads only its own deltas.
        var flat = LevelDocument.Load(new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions { ResolveTemplates = false }), LevelPath);
        var flatLamp = flat.FindActor("BP_Lamp_C_1")!;
        Assert.True(flatLamp.RootInferred);
        Assert.Null(flatLamp.FindComponent("Bulb")!.StaticMeshPath);
    }

    [Fact]
    public void ReadsStreamingLevelsAndCrossChecksTheWorldIndex()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: false);
        SyntheticLevels.WritePersistentLevel(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var reader = new Cue4ParseLevelReader(catalog);

        var streaming = reader.ReadStreamingLevels(WorldIndex.PersistentLevelPackagePath);
        Assert.Equal(2, streaming.Count);
        var first = streaming[0];
        Assert.Equal("LevelStreamingDynamic_0", first.ExportName);
        Assert.Equal("LevelStreamingDynamic", first.ClassName);
        Assert.Equal($"{LevelPath}.A_0_TestLevel", first.WorldAsset);
        Assert.Equal(LevelPath, first.PackagePath);
        Assert.Equal("A_0_TestLevel", first.LevelName);
        AssertNear(new FVector(100, 0, 0), first.LevelTransform.Translation);
        Assert.True(first.InitiallyLoaded);
        Assert.False(first.InitiallyVisible);
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/A_0_Missing", streaming[1].PackagePath);
        Assert.True(streaming[1].InitiallyVisible);

        var index = WorldIndex.Load(catalog, reader);
        Assert.Equal(new[] { "A_0_TestLevel", "The_Island" }, index.Packages.Select(p => p.Name).Order());
        var check = index.CrossCheck!;
        Assert.Equal(1, check.Matched);
        Assert.Equal("A_0_Missing", Assert.Single(check.MissingPackages).LevelName);
        Assert.Empty(check.NotStreamed);
        Assert.True(index.Find("A_0_TestLevel")!.IsStreamed);

        Assert.Throws<FileNotFoundException>(() => reader.ReadStreamingLevels(RockPackage));
    }

    [Fact]
    public void RejectsPackagesThatAreNotLevels()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var reader = new Cue4ParseLevelReader(catalog);
        Assert.Throws<InvalidDataException>(() => reader.ReadLevel(BlueprintPackage));
        Assert.Throws<InvalidDataException>(() => reader.ReadStreamingLevels(BlueprintPackage));
        Assert.Throws<FileNotFoundException>(() => reader.ReadLevel("/Game/ConZ_Files/Maps/The_Island/Nope"));
    }

    [FixturesFact]
    public void StockArchiveHasNoLevelsAndMeshesAreNotLevels()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var index = WorldIndex.FromCatalog(catalog);
        Assert.Empty(index.Packages);
        Assert.Null(index.PersistentLevel);
        Assert.Same(index, index.TryCrossCheck(new Cue4ParseLevelReader(catalog)));

        var mesh = catalog.PackageFiles.First(p => p.Contains("/SM_", StringComparison.Ordinal));
        var reader = new Cue4ParseLevelReader(catalog);
        Assert.False(reader.LevelExists(mesh));
        Assert.Throws<InvalidDataException>(() => reader.ReadLevel(mesh));
    }

    private static void AssertNear(FVector expected, FVector actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"expected {expected}, got {actual}");

    private static void AssertNear(FTransform expected, FTransform actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"expected {expected} [{expected.Rotation} {expected.Translation.X:R},{expected.Translation.Y:R},{expected.Translation.Z:R} {expected.Scale3D.X:R},{expected.Scale3D.Y:R},{expected.Scale3D.Z:R}], got {actual} [{actual.Rotation} {actual.Translation.X:R},{actual.Translation.Y:R},{actual.Translation.Z:R} {actual.Scale3D.X:R},{actual.Scale3D.Y:R},{actual.Scale3D.Z:R}]");
}
