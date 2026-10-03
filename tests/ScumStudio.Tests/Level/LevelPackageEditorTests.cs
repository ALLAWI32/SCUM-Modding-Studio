using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

public sealed class LevelPackageEditorTests
{
    private const string Engine = "/Script/Engine";
    private const string TilePath = "/Game/ConZ_Files/Maps/The_Island/A_0_Tile";

    private static CookedPackage Synthetic()
    {
        var bytes = SyntheticLevels.BuildLevel();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp);
    }

    private static LevelDocument LoadThroughCue4Parse(PackageBytes bytes, string levelName, string packagePath)
    {
        using var temp = new LevelTempDirectory();
        var basePath = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", levelName);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        bytes.WriteAsync(basePath, ".umap").GetAwaiter().GetResult();
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), packagePath);
    }

    private static void AssertXyz(PropertyTag? tag, float x, float y, float z)
    {
        Assert.NotNull(tag);
        var actual = tag.Value switch
        {
            VectorValue v => (v.X, v.Y, v.Z),
            RotatorValue r => (r.Pitch, r.Yaw, r.Roll),
            _ => throw new Xunit.Sdk.XunitException($"{tag.Name} is {tag.Value}, not a vector or rotator."),
        };
        Assert.Equal((x, y, z), actual);
    }

    private static int ExportNamed(CookedPackage package, string name, string outerSuffix) =>
        Enumerable.Range(0, package.Exports.Count).Single(i =>
            package.ResolveName(package.Exports[i].ObjectName) == name
            && package.ResolveIndex(package.Exports[i].OuterIndex).EndsWith(outerSuffix, StringComparison.Ordinal));

    /// <summary>
    /// A_0_Tile: World, PersistentLevel, Actor_1 (RootComponent -> Root with only RelativeLocation) and, on request, an
    /// Actor_2 that exists in the package but is not in the level's actor list.
    /// </summary>
    private static SyntheticLevelPackage MinimalLevel(bool withOrphanActor = false)
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var actor = p.Export("Actor_1", p.ScriptClass(Engine, "Actor"), level);
        var root = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), actor);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [actor])));
        p.SetPayload(actor, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties(t => t.Vector("RelativeLocation", 1, 2, 3)));
        if (withOrphanActor)
        {
            var orphan = p.Export("Actor_2", p.ScriptClass(Engine, "Actor"), level);
            p.SetPayload(orphan, p.Properties());
        }

        return p;
    }

    [Fact]
    public void ReadsTheActorList()
    {
        var actors = LevelPackageEditor.ReadActorList(Synthetic());
        Assert.Equal(new[] { "StaticMeshActor_1", null, "Rocks_Actor", "BP_Lamp_C_1" }, actors.Select(a => a.Name));
        Assert.Equal(0, actors[1].PackageIndex);
    }

    [Fact]
    public void RemovesActorsFromTheLevelActorListOnly()
    {
        var package = Synthetic();
        var levelIndex = LevelPackageEditor.FindLevelExport(package);
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteActors = ["staticmeshactor_1"] });

        Assert.Equal("PersistentLevel", report.LevelExport);
        Assert.Equal(4, report.ActorsBefore);
        Assert.Equal(3, report.ActorsAfter);
        Assert.Equal(new[] { "StaticMeshActor_1" }, report.RemovedActors);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.AddedNames);
        Assert.Empty(report.PatchedTransforms);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(new[] { null, "Rocks_Actor", "BP_Lamp_C_1" }, LevelPackageEditor.ReadActorList(edited).Select(a => a.Name));
        Assert.Equal(package.Names, edited.Names);
        Assert.Equal(package.Exports.Count, edited.Exports.Count);
        Assert.Equal(package.UAsset.Length, edited.UAsset.Length);
        Assert.Equal(package.UExp.Length - 4, edited.UExp.Length);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (i != levelIndex)
            {
                Assert.Equal(package.GetExportBytes(i), edited.GetExportBytes(i));
            }
        }

        // The native tail after the array (FURL, Model, ...) is intact.
        var before = package.GetExportBytes(levelIndex);
        var after = edited.GetExportBytes(levelIndex);
        Assert.Equal(before[^40..], after[^40..]);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(new[] { "Rocks_Actor", "BP_Lamp_C_1" }, document.Actors.Select(a => a.Name));
    }

    [Fact]
    public void WarnsAboutUnknownActorsAndLeavesTheListAlone()
    {
        var package = Synthetic();
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteActors = ["Nope_7", "Door"] });
        Assert.Equal(4, report.ActorsAfter);
        Assert.Empty(report.RemovedActors);
        Assert.Equal(2, report.Warnings.Count);
        Assert.All(report.Warnings, w => Assert.Contains("was not found in the level", w, StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("'Nope_7'", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("'Door'", StringComparison.Ordinal)); // a component, not an actor

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.UExp, edited.UExp);

        var built = MinimalLevel(withOrphanActor: true).Build();
        var minimal = CookedPackage.Parse(built.UAsset, built.UExp);
        var (_, orphanReport) = LevelPackageEditor.Apply(minimal, new LevelEditRequest { DeleteActors = ["Actor_2"] });
        Assert.Empty(orphanReport.RemovedActors);
        var warning = Assert.Single(orphanReport.Warnings);
        Assert.Contains("'Actor_2' exists in the package but is not in the level's actor list", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchesStoredTransformsInPlace()
    {
        var package = Synthetic();
        var value = new TransformValue(new FVector(5, 6, 7), new FRotator(0, 180, 0), new FVector(1, 1, 1));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Transforms = [new TransformPatch("StaticMeshActor_1", null, value)],
        });

        Assert.Equal(new[] { "StaticMeshActor_1.StaticMeshComponent0" }, report.PatchedTransforms);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.AddedNames);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.UExp.Length, edited.UExp.Length);
        var props = edited.ReadProperties(ExportNamed(edited, "StaticMeshComponent0", "StaticMeshActor_1"));
        AssertXyz(props.Find("RelativeLocation"), 5, 6, 7);
        AssertXyz(props.Find("RelativeRotation"), 0, 180, 0);
        AssertXyz(props.Find("RelativeScale3D"), 1, 1, 1);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.True(document.FindActor("StaticMeshActor_1")!.Root!.Relative.IsNearlyEqual(value));
    }

    [Fact]
    public void InsertsMissingTransformTags()
    {
        var package = Synthetic();
        var value = new TransformValue(new FVector(1, 2, 3), new FRotator(10, 20, 30), new FVector(2, 2, 2));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Transforms = [new TransformPatch("Rocks_Actor", "DefaultSceneRoot", value)],
        });

        Assert.Equal(new[] { "Rocks_Actor.DefaultSceneRoot" }, report.PatchedTransforms);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.AddedNames); // every name already exists in this package

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var root = ExportNamed(edited, "DefaultSceneRoot", "Rocks_Actor");
        var props = edited.ReadProperties(root);
        Assert.Equal(3, props.Properties.Count);
        AssertXyz(props.Find("RelativeLocation"), 1, 2, 3);
        AssertXyz(props.Find("RelativeRotation"), 10, 20, 30);
        AssertXyz(props.Find("RelativeScale3D"), 2, 2, 2);
        Assert.Equal(package.GetExportBytes(root).Length + 2 * 61, edited.GetExportBytes(root).Length);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.True(document.FindActor("Rocks_Actor")!.Root!.Relative.IsNearlyEqual(value));
    }

    [Fact]
    public void ReportsUnresolvableTransformTargets()
    {
        var package = Synthetic();
        var (_, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Transforms =
            [
                new TransformPatch("Ghost", null, TransformValue.Identity),
                new TransformPatch("Rocks_Actor", null, TransformValue.Identity), // no RootComponent stored
                new TransformPatch("Rocks_Actor", "Missing", TransformValue.Identity),
            ],
        });
        Assert.Empty(report.PatchedTransforms);
        Assert.Equal(3, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("Ghost", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("no RootComponent", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("Rocks_Actor.Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AddsNamesAndKeepsTheWorldTileInfoReadable()
    {
        var p = MinimalLevel();
        var tile = new WorldTileInfo((1, 2, 0), new FVector(-100, -100, -10), new FVector(100, 100, 10), true,
            new WorldTileLayer("CityBlock10000", 0, (0, 0), 10000, true), false, "Landscape_A_0_1", [], 3);
        var tileBytes = new ByteWriter();
        tile.Write(tileBytes);
        var blob = new byte[4 + tileBytes.Length];
        tileBytes.WrittenSpan.CopyTo(blob.AsSpan(4));

        // First build to learn where the asset registry block lands, second build with the tile offset pointing into it.
        var firstBytes = p.Build(blob);
        var first = CookedPackage.Parse(firstBytes.UAsset, firstBytes.UExp);
        var tileOffset = first.Summary.AssetRegistryDataOffset + 4;
        var second = p.Build(blob, first.Summary with { WorldTileInfoDataOffset = tileOffset });
        var package = CookedPackage.Parse(second.UAsset, second.UExp);
        var original = WorldTileInfo.TryRead(package);
        Assert.NotNull(original);
        Assert.Equal((1, 2, 0), original.Position);

        var value = new TransformValue(new FVector(5, 6, 7), new FRotator(10, 20, 30), new FVector(2, 2, 2));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { Transforms = [new TransformPatch("Actor_1", null, value)] });
        Assert.Equal(new[] { "Actor_1.Root" }, report.PatchedTransforms);
        Assert.Equal(new[] { "RelativeRotation", "Rotator", "RelativeScale3D" }, report.AddedNames);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.True(edited.UAsset.Length > package.UAsset.Length, "the name table grew");
        var relocated = WorldTileInfo.TryRead(edited);
        Assert.NotNull(relocated);
        Assert.Equal(original.Position, relocated.Position);
        Assert.Equal(original.BoundsMin, relocated.BoundsMin);
        Assert.Equal(original.BoundsMax, relocated.BoundsMax);
        Assert.Equal(original.Layer, relocated.Layer);
        Assert.Equal(original.ParentTilePackageName, relocated.ParentTilePackageName);
        Assert.Equal(original.ZOrder, relocated.ZOrder);

        var document = LoadThroughCue4Parse(bytes, "A_0_Tile", TilePath);
        Assert.True(document.FindActor("Actor_1")!.Root!.Relative.IsNearlyEqual(value));
    }

    [Fact]
    public void CollapsesAndMovesInstancesInPlace()
    {
        var package = Synthetic();
        var hint = new InstanceArrayHint("Rocks_Actor", "Rocks", [SyntheticLevels.Instance0, SyntheticLevels.Instance1]);
        var movedTo = new FTransform(new FRotator(0, 45, 0), new FVector(10, 20, 30), new FVector(2, 2, 2));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Instances =
            [
                new InstancePatch("Rocks_Actor", "Rocks", 1, null),
                new InstancePatch("Rocks_Actor", "Rocks", 0, movedTo),
                new InstancePatch("Rocks_Actor", "Rocks", 7, null), // outside the array
                new InstancePatch("Rocks_Actor", "Nope", 0, null), // no such component
                new InstancePatch("BP_Lamp_C_1", "Bulb", 0, null), // no hint given
            ],
            InstanceHints = [hint],
        });

        Assert.Equal(1, report.DeletedInstances);
        Assert.Equal(1, report.MovedInstances);
        Assert.Equal(3, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("[7]", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("Rocks_Actor.Nope", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("BP_Lamp_C_1.Bulb", StringComparison.Ordinal) && w.Contains("no pristine instance list", StringComparison.Ordinal));

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.UExp.Length, edited.UExp.Length);
        var ism = ExportNamed(edited, "Rocks", "Rocks_Actor");
        Assert.Equal(package.GetExportBytes(ism).Length, edited.GetExportBytes(ism).Length);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        var instances = document.FindActor("Rocks_Actor")!.InstanceTransforms.OrderBy(i => i.InstanceIndex).ToList();
        Assert.Equal(2, instances.Count);
        Assert.True(instances[0].LocalTransform.Equals(movedTo, 0.01f), instances[0].LocalTransform.ToString());
        Assert.InRange(instances[1].LocalTransform.Translation.X, 99.5f, 100.5f);
        Assert.InRange(instances[1].LocalTransform.Scale3D.Y, 0f, 0.001f);
    }

    [Fact]
    public void FindsTheInstanceArrayOnlyWhenTheMatricesMatch()
    {
        var package = Synthetic();
        var ism = ExportNamed(package, "Rocks", "Rocks_Actor");
        var payload = package.GetExportBytes(ism);
        var end = package.ReadProperties(ism).EndOffset;

        var found = LevelPackageEditor.FindInstanceArray(payload, end, [SyntheticLevels.Instance0, SyntheticLevels.Instance1]);
        Assert.NotNull(found);
        Assert.Equal(64, found.Value.ElementSize);
        Assert.Equal(payload.Length - 8 - 2 * 64, found.Value.DataOffset); // followed only by the empty custom-data array

        Assert.Null(LevelPackageEditor.FindInstanceArray(payload, end, [SyntheticLevels.Instance1, SyntheticLevels.Instance0])); // wrong order
        Assert.Null(LevelPackageEditor.FindInstanceArray(payload, end, [SyntheticLevels.Instance0])); // wrong count
        Assert.Null(LevelPackageEditor.FindInstanceArray(payload, end, []));
    }

    [Fact]
    public void CopiesActorsWithTheirComponentsAndDependencies()
    {
        var package = Synthetic();
        var at = new TransformValue(new FVector(2000, 0, 0), new FRotator(0, 90, 0), new FVector(1, 1, 1));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Copies =
            [
                new ActorCopy("StaticMeshActor_1", "StaticMeshActor_1_Copy", at),
                new ActorCopy("BP_Lamp_C_1", "BP_Lamp_C_7", null),
                new ActorCopy("Ghost", "Ghost_Copy", null),
                new ActorCopy("Rocks_Actor", "StaticMeshActor_1", null), // name taken
            ],
        });

        Assert.Equal(new[] { "StaticMeshActor_1_Copy", "BP_Lamp_C_7" }, report.AddedActors);
        Assert.Equal(2, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("Ghost", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal(4, report.ActorsBefore);
        Assert.Equal(6, report.ActorsAfter);
        Assert.Contains("StaticMeshActor_1_Copy", report.AddedNames); // "BP_Lamp_C_7" reuses the "BP_Lamp_C" base name with number 8

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.Exports.Count + 3 + 3, edited.Exports.Count); // house + mesh + door, lamp + root + bulb
        Assert.Equal(new[] { "StaticMeshActor_1", null, "Rocks_Actor", "BP_Lamp_C_1", "StaticMeshActor_1_Copy", "BP_Lamp_C_7" },
            LevelPackageEditor.ReadActorList(edited).Select(a => a.Name));
        var levelIdx = LevelPackageEditor.FindLevelExport(package);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (i == levelIdx)
            {
                Assert.Equal(package.GetExportBytes(i).Length + 8, edited.GetExportBytes(i).Length); // two more actor indices
            }
            else
            {
                Assert.Equal(package.GetExportBytes(i), edited.GetExportBytes(i));
            }
        }

        // The copies point at their own components (not the source's) and the component's outer is the copy.
        var copyIndex = ExportNamed(edited, "StaticMeshActor_1_Copy", "PersistentLevel");
        var copyRoot = edited.ReadProperties(copyIndex).Find("RootComponent")!.Value as ObjectValue;
        Assert.NotNull(copyRoot);
        Assert.True(copyRoot.Index - 1 > package.Exports.Count - 1, "the copy's root is a new export");
        Assert.Equal(copyIndex + 1, edited.Exports[copyRoot.Index - 1].OuterIndex);
        Assert.Equal("StaticMeshComponent0", edited.ResolveName(edited.Exports[copyRoot.Index - 1].ObjectName));
        var door = edited.ReadProperties(ExportNamed(edited, "Door", "StaticMeshActor_1_Copy"));
        Assert.Equal(copyRoot.Index, ((ObjectValue)door.Find("AttachParent")!.Value).Index);

        // Preload dependencies: the copy's groups mirror the source's with remapped indices; the level lists the new actors.
        var sourceIndex = ExportNamed(package, "StaticMeshActor_1", "PersistentLevel");
        Assert.Equal(package.Exports[sourceIndex].CreateBeforeSerializationDependencies, edited.Exports[copyIndex].CreateBeforeSerializationDependencies);
        var levelEntry = edited.Exports[LevelPackageEditor.FindLevelExport(edited)];
        Assert.Equal(package.Exports[LevelPackageEditor.FindLevelExport(package)].CreateBeforeSerializationDependencies + 2, levelEntry.CreateBeforeSerializationDependencies);
        Assert.True(edited.ReadPreloadDependencies().Length >= package.ReadPreloadDependencies().Length);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(new[] { "StaticMeshActor_1", "Rocks_Actor", "BP_Lamp_C_1", "StaticMeshActor_1_Copy", "BP_Lamp_C_7" }, document.Actors.Select(a => a.Name));
        var copy = document.FindActor("StaticMeshActor_1_Copy")!;
        Assert.Equal("StaticMeshActor", copy.ClassName);
        Assert.True(copy.Root!.Relative.IsNearlyEqual(at), copy.Root.Relative.ToString());
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", copy.StaticMeshPath);
        Assert.Equal(2, copy.Components.Count);
        var original = document.FindActor("StaticMeshActor_1")!;
        Assert.True(original.Root!.Relative.IsNearlyEqual(new TransformValue(new FVector(1000, 0, 0), new FRotator(0, 90, 0), new FVector(2, 2, 2))));
        var lampCopy = document.FindActor("BP_Lamp_C_7")!;
        Assert.Equal("BP_Lamp_C", lampCopy.ClassName);
        Assert.True(lampCopy.Root!.Relative.IsNearlyEqual(document.FindActor("BP_Lamp_C_1")!.Root!.Relative));
    }

    [Fact]
    public void AddsBentSplineMeshActorsTheReaderBendsBack()
    {
        var package = Synthetic();
        var at = new TransformValue(new FVector(5, 6, 7), new FRotator(0, 30, 0), FVector.One);
        var bounds = new ScumStudio.Core.Geometry.BoundingBox(new System.Numerics.Vector3(-20, -300, 0), new System.Numerics.Vector3(20, 300, 250));
        var spline = BendShape.For(bounds, new FVector(1, 1.5f, 2), 60f); // long on Y: ForwardAxis Y with a quarter roll
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds = [new StaticMeshActorAdd("Wall_Bent", SyntheticLevels.RockPackage + ".SM_Rock", at, spline)],
        });

        Assert.Equal(new[] { "Wall_Bent" }, report.AddedActors);
        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var added = ExportNamed(edited, "Wall_Bent", "PersistentLevel");
        Assert.Equal("SplineMeshActor", edited.GetExportClassName(added));
        var component = ExportNamed(edited, "SplineMeshComponent0", "Wall_Bent");
        Assert.Equal("SplineMeshComponent", edited.GetExportClassName(component));
        Assert.NotNull(edited.ReadProperties(component).Find("SplineParams"));

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        var wall = document.FindActor("Wall_Bent")!;
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", wall.StaticMeshPath);
        Assert.True(wall.Root!.Relative.IsNearlyEqual(at), wall.Root.Relative.ToString());
        var read = wall.Root.SplineMesh!;
        Assert.Equal(SplineMeshAxis.Y, read.ForwardAxis);
        Assert.Equal(-300f, read.SplineBoundaryMin);
        Assert.Equal(300f, read.SplineBoundaryMax);
        foreach (var (written, back) in new[] { (spline.StartPos, read.StartPos), (spline.EndPos, read.EndPos), (spline.StartTangent, read.StartTangent), (spline.EndTangent, read.EndTangent) })
        {
            Assert.True(FVector.Distance(written, back) < 1e-3f, $"{back} instead of {written}");
        }

        Assert.Equal(spline.StartScale, read.StartScale);
        Assert.Equal(spline.EndRoll, read.EndRoll, 1e-6f);

        // A tower with longer legs stands on a vertical spline: forward Z and an up direction across it.
        var legs = BendShape.Legs(bounds, FVector.One, 1500f);
        (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds = [new StaticMeshActorAdd("Tower_Bent", SyntheticLevels.RockPackage + ".SM_Rock", at, legs)],
        });
        Assert.Empty(report.Warnings);
        var tower = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath).FindActor("Tower_Bent")!.Root!.SplineMesh!;
        Assert.Equal(SplineMeshAxis.Z, tower.ForwardAxis);
        Assert.Equal(FVector.Forward, tower.SplineUpDir);
        Assert.True(FVector.Distance(legs.StartPos, tower.StartPos) < 1e-3f, tower.StartPos.ToString());
    }

    [Fact]
    public void ANumberedMeshReusesTheLevelsOwnImport()
    {
        // As the cooker stores them: "/Game/X/ConcreteBlock002_1" is the name "/Game/X/ConcreteBlock002" with number 2.
        Assert.Equal(("/Game/X/ConcreteBlock002", 2), LevelPackageEditor.SplitNumber("/Game/X/ConcreteBlock002_1"));
        Assert.Equal(("Rocket_04", 0), LevelPackageEditor.SplitNumber("Rocket_04"));
        Assert.Equal(("Rocket", 1), LevelPackageEditor.SplitNumber("Rocket_0"));
        Assert.Equal(("SplineMeshComponent0", 0), LevelPackageEditor.SplitNumber("SplineMeshComponent0"));

        List<string> names = ["None", "/Script/CoreUObject", "Package", "/Script/Engine", "StaticMesh", "/Game/X/ConcreteBlock002", "ConcreteBlock002"];
        List<ImportEntry> imports =
        [
            new(new FNameRef(1), new FNameRef(2), 0, new FNameRef(5, 2)),
            new(new FNameRef(3), new FNameRef(4), -1, new FNameRef(6, 2)),
        ];
        var (wide, added) = (names.Select(_ => false).ToList(), new List<string>());

        // A bent block needs its mesh: the level's own imports are found, nothing is added (a second one crashed the game).
        var package = LevelPackageEditor.GetOrAddImport(imports, names, wide, added, "/Script/CoreUObject", "Package", 0, "/Game/X/ConcreteBlock002_1");
        Assert.Equal(-1, package);
        Assert.Equal(-2, LevelPackageEditor.GetOrAddImport(imports, names, wide, added, "/Script/Engine", "StaticMesh", package, "ConcreteBlock002_1"));
        Assert.Equal(2, imports.Count);
        Assert.Empty(added);

        // A new numbered one is stored split, the way the game reads it.
        var other = LevelPackageEditor.GetOrAddImport(imports, names, wide, added, "/Script/CoreUObject", "Package", 0, "/Game/X/Fence_3");
        Assert.Equal(-3, other);
        Assert.Equal(new FNameRef(names.IndexOf("/Game/X/Fence"), 4), imports[2].ObjectName);
    }

    [Fact]
    public void AddsStaticMeshActorsWithImports()
    {
        var package = Synthetic();
        var at = new TransformValue(new FVector(5, 6, 7), new FRotator(0, 30, 0), new FVector(2, 2, 2));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds =
            [
                new StaticMeshActorAdd("SM_Rock_Added", SyntheticLevels.RockPackage + ".SM_Rock", at), // mesh import exists
                new StaticMeshActorAdd("SM_New_Added", "/Game/ConZ_Files/Models/Props/SM_New", TransformValue.Identity), // package path, new imports
                new StaticMeshActorAdd("Rocks_Actor", SyntheticLevels.RockPackage, at), // name taken
                new StaticMeshActorAdd("Bad", "", at),
            ],
        });

        Assert.Equal(new[] { "SM_Rock_Added", "SM_New_Added" }, report.AddedActors);
        Assert.Equal(2, report.Warnings.Count);
        Assert.Equal(6, report.ActorsAfter);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.Exports.Count + 4, edited.Exports.Count);
        Assert.True(edited.Imports.Count >= package.Imports.Count + 2, "the new mesh package and object imports were added");
        var meshImports = edited.Imports.Where(i => edited.ResolveName(i.ClassName) == "StaticMesh").Select(i => edited.ResolveName(i.ObjectName)).ToList();
        Assert.Contains("SM_New", meshImports);
        Assert.Equal(1, meshImports.Count(n => n == "SM_Rock")); // reused, not duplicated
        var added = ExportNamed(edited, "SM_Rock_Added", "PersistentLevel");
        Assert.Equal("StaticMeshActor", edited.GetExportClassName(added));
        var component = ExportNamed(edited, "StaticMeshComponent0", "SM_Rock_Added");
        Assert.Equal(added + 1, edited.Exports[component].OuterIndex);
        Assert.True(edited.Exports[added].FirstExportDependency >= 0 && edited.Exports[component].FirstExportDependency >= 0);
        var props = edited.ReadProperties(component);
        Assert.Equal(4, props.Properties.Count);
        AssertXyz(props.Find("RelativeLocation"), 5, 6, 7);
        AssertXyz(props.Find("RelativeScale3D"), 2, 2, 2);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(new[] { "StaticMeshActor_1", "Rocks_Actor", "BP_Lamp_C_1", "SM_Rock_Added", "SM_New_Added" }, document.Actors.Select(a => a.Name));
        var rock = document.FindActor("SM_Rock_Added")!;
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", rock.StaticMeshPath);
        Assert.True(rock.Root!.Relative.IsNearlyEqual(at), rock.Root.Relative.ToString());
        Assert.Equal("/Game/ConZ_Files/Models/Props/SM_New.SM_New", document.FindActor("SM_New_Added")!.StaticMeshPath);
    }

    /// <summary>Minimal ULevel native data: Actors, FURL, Model, ModelComponents, LevelScriptActor, NavListStart/End.</summary>
    private static void LevelTail(ByteWriter w, IReadOnlyList<int> actors)
    {
        w.I32(actors.Count);
        foreach (var a in actors)
        {
            w.I32(a);
        }

        w.FString("unreal");
        w.FString(string.Empty);
        w.FString("A_0_Tile");
        w.FString(string.Empty);
        w.I32(0);
        w.I32(7777);
        w.I32(1);
        w.I32(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
    }
}
