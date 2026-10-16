using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using BoundingBox = ScumStudio.Core.Geometry.BoundingBox;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

public sealed class LevelPackageEditorTests
{
    private const string Engine = "/Script/Engine";
    private const string TilePath = "/Game/ConZ_Files/Maps/The_Island/A_0_Tile";

    [Fact]
    public void AnInstanceMovedInALevelAlreadyWithoutItsRenderCopyStillGrowsItsCluster()
    {
        // A HISM this studio wrote before (the owner imported his own pak back into the project): no render copy left, so
        // nothing was dropped, but the clusters must still reach the moved rock or the game culls it by its old place.
        var w = new ByteWriter();
        w.I32(64);
        w.I32(1);
        foreach (var f in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })
        {
            w.F32(f);
        }

        var arrayEnd = w.Length;
        w.I32(4); // PerInstanceSMCustomData: element size, count
        w.I32(0);
        w.I64(0); // render copy: gone
        w.I32(64); // one cluster node around the instance's old place
        w.I32(1);
        foreach (var f in new[] { -10f, -10, -10 })
        {
            w.F32(f);
        }

        w.I32(-1);
        foreach (var f in new[] { 10f, 10, 10 })
        {
            w.F32(f);
        }

        w.I32(-1);
        w.I32(0);
        w.I32(0);
        foreach (var f in new[] { 1f, 1, 1, 1, 1, 1 })
        {
            w.F32(f);
        }

        var payload = w.ToArray();
        var moved = new FTransform(new FVector(5000, 0, 0));
        var grown = InstanceRenderData.Refresh(payload, new PropertyBlock { Properties = [] }, arrayEnd, 1, 64, 0, [(0, FTransform.Identity, moved)]);

        Assert.Equal(payload.Length, grown.Length);
        var node = arrayEnd + 8 + 8 + 8;
        Assert.Equal(-10f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(grown.AsSpan(node)));
        Assert.Equal(5010f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(grown.AsSpan(node + 16)));
        Assert.Equal(10f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(node + 16))); // the input is left as it was
    }

    /// <summary>
    /// defolt_0999 (D05, 3.2.0): trees and bushes blinked after foliage was deleted or moved. A foliage HISM of four trees
    /// (two leaves of two, drawn in another order than the array) with its cooked draw copy: one tree moved, turned and
    /// grown, one collapsed. The copy stays and draws each tree where the array has it (random and light map as cooked),
    /// the clusters are refitted as BuildTree measures them and the component's bounds are the root's.
    /// </summary>
    [Fact]
    public void AnEditedFoliageComponentKeepsItsDrawCopyAndItsClustersHoldTheirInstances()
    {
        FTransform[] trees = [new(new FVector(0, 0, 0)), new(new FVector(1000, 0, 0)), new(new FVector(100, 0, 0)), new(new FVector(1100, 0, 0))];
        int[] reorder = [0, 2, 1, 3]; // array index -> draw index: leaf 1 draws trees 0 and 2, leaf 2 trees 1 and 3
        var mesh = new BoundingBox(new System.Numerics.Vector3(-50, -50, 0), new System.Numerics.Vector3(150, 50, 400));
        var boxes = trees.Select(t => HismTail.InstanceBox(t, mesh)).ToArray();
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var actor = p.Export("InstancedFoliageActor_0", p.ScriptClass(Engine, "InstancedFoliageActor"), level);
        var hism = p.Export("FoliageInstancedTree_0", p.ScriptClass(Engine, "HierarchicalInstancedStaticMeshComponent"), actor);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [actor])));
        p.SetPayload(actor, p.Properties());
        p.SetPayload(hism, p.Properties(t =>
        {
            t.IntArray("SortedInstances", 0, 2, 1, 3);
            t.Box("BuiltInstanceBounds", new FVector(-50, -50, 0), new FVector(1250, 50, 400));
            t.TaggedStruct("CacheMeshExtendedBounds", "BoxSphereBounds", b =>
            {
                b.Vector("Origin", 50, 0, 200);
                b.Vector("BoxExtent", 100, 50, 200);
                b.Float("SphereRadius", 229);
            });
            t.IntArray("InstanceReorderTable", reorder);
        }, w =>
        {
            w.I32(0); // LODData
            w.I32(1); // bCooked
            w.I32(64);
            w.I32(trees.Length);
            foreach (var t in trees)
            {
                var m = t.ToMatrixWithScale();
                foreach (var f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                {
                    w.F32(f);
                }
            }

            w.I32(4); // PerInstanceSMCustomData
            w.I32(0);
            var copy = new ByteWriter();
            copy.I32(1); // bUseHalfFloat
            copy.I32(trees.Length);
            copy.I32(0); // NumCustomDataFloats
            copy.I32(16);
            copy.I32(trees.Length);
            for (var d = 0; d < trees.Length; d++)
            {
                var t = trees[Array.IndexOf(reorder, d)];
                copy.F32(t.Translation.X);
                copy.F32(t.Translation.Y);
                copy.F32(t.Translation.Z);
                copy.F32(0.1f * (d + 1)); // PerInstanceRandom
            }

            copy.I32(8);
            copy.I32(trees.Length);
            for (var d = 0; d < trees.Length * 4; d++)
            {
                copy.U16((ushort)(1000 + d)); // light map / shadow map biases
            }

            copy.I32(24);
            copy.I32(trees.Length);
            for (var d = 0; d < trees.Length; d++)
            {
                foreach (var f in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 })
                {
                    copy.U16(BitConverter.HalfToUInt16Bits((Half)f));
                }
            }

            copy.I32(4);
            copy.I32(0);
            w.I64(copy.Length);
            w.Raw(copy.WrittenSpan);
            w.I32(64); // cluster tree: root, then two leaves
            w.I32(3);
            void Node(BoundingBox b, int firstChild, int lastChild, int first, int last)
            {
                w.F32(b.Min.X);
                w.F32(b.Min.Y);
                w.F32(b.Min.Z);
                w.I32(firstChild);
                w.F32(b.Max.X);
                w.F32(b.Max.Y);
                w.F32(b.Max.Z);
                w.I32(lastChild);
                w.I32(first);
                w.I32(last);
                foreach (var f in new[] { 1f, 1, 1, 1, 1, 1 })
                {
                    w.F32(f);
                }
            }

            BoundingBox Union(params int[] i) => new(i.Select(k => boxes[k].Min).Aggregate(System.Numerics.Vector3.Min), i.Select(k => boxes[k].Max).Aggregate(System.Numerics.Vector3.Max));
            Node(Union(0, 1, 2, 3), 1, 2, 0, 3);
            Node(Union(0, 2), -1, -1, 0, 1);
            Node(Union(1, 3), -1, -1, 2, 3);
        }));
        var built0 = p.Build();
        var package = CookedPackage.Parse(built0.UAsset, built0.UExp);
        var moved = new FTransform(new FRotator(0, 90, 0), new FVector(5000, 3000, 50), new FVector(2, 2, 2));

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Instances = [new InstancePatch("InstancedFoliageActor_0", "FoliageInstancedTree_0", 2, moved), new InstancePatch("InstancedFoliageActor_0", "FoliageInstancedTree_0", 3, null)],
            InstanceHints = [new InstanceArrayHint("InstancedFoliageActor_0", "FoliageInstancedTree_0", trees)],
        });

        Assert.Equal((1, 1), (report.MovedInstances, report.DeletedInstances));
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var index = ExportNamed(edited, "FoliageInstancedTree_0", "InstancedFoliageActor_0");
        var before = package.GetExportBytes(index);
        var payload = edited.GetExportBytes(index);
        Assert.Equal(before.Length, payload.Length); // the draw copy is still there
        var now = trees.ToArray();
        now[2] = moved;
        now[3] = new FTransform(FQuat.Identity, trees[3].Translation, new FVector(LevelPackageEditor.CollapsedInstanceScale));
        var block = edited.ReadProperties(index);
        var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, now) ?? throw new InvalidOperationException("instance array not found");
        var arrayEnd = offset + (size * trees.Length);
        var check = HismTail.Check(payload, arrayEnd, now, reorder, mesh, requireCopy: true);
        Assert.Empty(check.Problems);

        // Only the moved and the collapsed trees' origins and rows changed: randoms, light maps and the other two as cooked.
        var copyAt = arrayEnd + 16;
        var originsAt = copyAt + 20;
        var lightAt = originsAt + (16 * 4) + 8;
        var rowsAt = lightAt + (8 * 4) + 8;
        Assert.True(before.AsSpan(lightAt, 32).SequenceEqual(payload.AsSpan(lightAt, 32)));
        for (var d = 0; d < 4; d++)
        {
            Assert.Equal(0.1f * (d + 1), System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(originsAt + (d * 16) + 12)));
            Assert.Equal(d != 1, before.AsSpan(originsAt + (d * 16), 16).SequenceEqual(payload.AsSpan(originsAt + (d * 16), 16))); // draw 1 moved; draw 3 collapsed in place
            Assert.Equal(d is 0 or 2, before.AsSpan(rowsAt + (d * 24), 24).SequenceEqual(payload.AsSpan(rowsAt + (d * 24), 24)));
        }

        // Leaf 1 holds the turned, grown tree exactly; leaf 2 shrank to the tree left; the root and the component's bounds hold both.
        var treeAt = copyAt + (int)check.CopyBytes + 8;
        System.Numerics.Vector3 Vec(int at) => new(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at)),
            System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at + 4)), System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at + 8)));
        var turned = HismTail.InstanceBox(moved, mesh);
        Assert.True(System.Numerics.Vector3.Distance(Vec(treeAt + 64), System.Numerics.Vector3.Min(boxes[0].Min, turned.Min)) < 0.01f, Vec(treeAt + 64).ToString());
        Assert.True(System.Numerics.Vector3.Distance(Vec(treeAt + 64 + 16), System.Numerics.Vector3.Max(boxes[0].Max, turned.Max)) < 0.01f, Vec(treeAt + 80).ToString());
        Assert.True(System.Numerics.Vector3.Distance(Vec(treeAt + 128 + 16), boxes[1].Max) < 0.01f, Vec(treeAt + 144).ToString());
        Assert.Equal(new System.Numerics.Vector3(1), Vec(treeAt + 128 + 40)); // the collapsed tree is not in leaf 2's scale range
        Assert.True(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(2), Vec(treeAt + 64 + 52)) < 1e-4f); // the grown one is in leaf 1's
        Assert.True(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(2), Vec(treeAt + 52)) < 1e-4f);
        Assert.True(System.Numerics.Vector3.Distance(Vec(treeAt + 16), System.Numerics.Vector3.Max(boxes[1].Max, turned.Max)) < 0.01f, Vec(treeAt + 16).ToString());
        var built = block.Find("BuiltInstanceBounds")!;
        Assert.True(payload.AsSpan(built.ValueOffset, 12).SequenceEqual(payload.AsSpan(treeAt, 12)));
        Assert.True(payload.AsSpan(built.ValueOffset + 12, 12).SequenceEqual(payload.AsSpan(treeAt + 16, 12)));
    }

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
    public void ALevelWithMoreNullSlotsThanExportsStillExports()
    {
        // The cook keeps a null slot for each editor-only actor it strips (B_3_Stone_Mine_01: 53 slots, 29 exports).
        var p = MinimalLevel();
        var actor = 3;
        p.SetPayload(2, p.Properties(native: w => LevelTail(w, [0, 0, 0, actor, 0, 0, 0, 0, 0])));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);
        Assert.True(package.Exports.Count < 9);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteActors = ["Actor_1"] });
        Assert.Equal(["Actor_1"], report.RemovedActors);
        Assert.All(LevelPackageEditor.ReadActorList(CookedPackage.Parse(bytes.UAsset, bytes.UExp)), a => Assert.Null(a.Name));
    }

    [Fact]
    public void RemovesActorsFromTheListAndMakesTheirObjectsInert()
    {
        // B_4 (owner): the deleted trade posts kept spawning their traders - the game creates every export of the package
        // and runs the Blueprint's code on it, listed or not. A deleted actor's objects become plain Objects in place.
        var package = Synthetic();
        var levelIndex = LevelPackageEditor.FindLevelExport(package);
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteActors = ["staticmeshactor_1", "BP_Lamp_C_1"] });

        Assert.Equal("PersistentLevel", report.LevelExport);
        Assert.Equal(4, report.ActorsBefore);
        Assert.Equal(2, report.ActorsAfter);
        Assert.Equal(new[] { "StaticMeshActor_1", "BP_Lamp_C_1" }, report.RemovedActors);
        Assert.Equal(6, report.NeutralizedExports); // house + mesh + door, lamp + root + bulb
        Assert.Empty(report.Warnings);
        Assert.Empty(report.PatchedTransforms);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(new[] { null, "Rocks_Actor" }, LevelPackageEditor.ReadActorList(edited).Select(a => a.Name));
        Assert.Equal(package.Exports.Count, edited.Exports.Count); // same indices: nothing to remap anywhere
        var inert = new[] { ("StaticMeshActor_1", "PersistentLevel"), ("StaticMeshComponent0", "StaticMeshActor_1"), ("Door", "StaticMeshActor_1"), ("BP_Lamp_C_1", "PersistentLevel"), ("DefaultSceneRoot", "BP_Lamp_C_1"), ("Bulb", "BP_Lamp_C_1") }
            .Select(x => ExportNamed(edited, x.Item1, x.Item2)).ToHashSet();
        foreach (var i in inert)
        {
            Assert.Equal("Object", edited.GetExportClassName(i));
            Assert.Equal("/Script/CoreUObject.Default__Object", edited.GetFullPath(edited.Exports[i].TemplateIndex));
            Assert.Equal(package.Exports[i].OuterIndex, edited.Exports[i].OuterIndex);
            Assert.Equal(12, edited.GetExportBytes(i).Length); // "None" and no guid: no class code, nothing to run
            Assert.Empty(edited.ReadProperties(i).Properties);
            var entry = edited.Exports[i];
            var deps = edited.ReadPreloadDependencies().Skip(entry.FirstExportDependency).Take(entry.PreloadDependencyTotal).ToList();
            Assert.Equal(entry.OuterIndex > 0 ? 3 : 2, deps.Count); // class + template before serialize, the outer before create
        }

        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (i != levelIndex && !inert.Contains(i))
            {
                Assert.Equal(package.GetExportBytes(i), edited.GetExportBytes(i));
            }
        }

        // The native tail after the array (FURL, Model, ...) is intact.
        var before = package.GetExportBytes(levelIndex);
        var after = edited.GetExportBytes(levelIndex);
        Assert.Equal(before[^40..], after[^40..]);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(new[] { "Rocks_Actor" }, document.Actors.Select(a => a.Name));
    }

    [Fact]
    public void NeutralizesComponentsOfAKeptActorAndLeavesTheOtherExportsByteIdentical()
    {
        // Owner: the fire, light and sound of a deleted fireplace part go; the building and everything else stay as they were.
        var package = Synthetic();
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteComponents = [("StaticMeshActor_1", "Door")] });

        Assert.Equal(["StaticMeshActor_1.Door"], report.NeutralizedComponents);
        Assert.Equal(1, report.NeutralizedExports);
        Assert.Empty(report.RemovedActors);
        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var door = ExportNamed(edited, "Door", "StaticMeshActor_1");
        Assert.Equal("Object", edited.GetExportClassName(door));
        Assert.Empty(edited.ReadProperties(door).Properties);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (i != door)
            {
                Assert.Equal(package.GetExportBytes(i), edited.GetExportBytes(i));
            }
        }

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(new[] { "StaticMeshActor_1", "Rocks_Actor", "BP_Lamp_C_1" }, document.Actors.Select(a => a.Name));
        Assert.Null(document.FindActor("StaticMeshActor_1")!.FindComponent("Door"));
    }

    [Fact]
    public void DetachRemovesAttachParentAndKeepsTheWorldPlace()
    {
        // Audit F053: a kept child of a deleted parent is taken off it and written where it stood in the world.
        var original = SyntheticLevels.BuildLevel();
        var before = LoadThroughCue4Parse(original, "A_0_TestLevel", SyntheticLevels.LevelPath).FindActor("StaticMeshActor_1")!.FindComponent("Door")!;
        Assert.NotNull(before.AttachParent);
        var world = TransformValue.FromTransform(before.WorldTransform);

        var (bytes, report) = LevelPackageEditor.Apply(CookedPackage.Parse(original.UAsset, original.UExp), new LevelEditRequest
        {
            Transforms = [new TransformPatch("StaticMeshActor_1", "Door", world)],
            Detach = [("StaticMeshActor_1", "Door")],
        });

        Assert.Empty(report.Warnings);
        Assert.Equal(new[] { "StaticMeshActor_1.Door", "StaticMeshActor_1.Door (detached)" }, report.PatchedTransforms);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var props = edited.ReadProperties(ExportNamed(edited, "Door", "StaticMeshActor_1"));
        Assert.Null(props.Find("AttachParent"));
        Assert.NotNull(props.Find("StaticMesh"));
        var after = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath).FindActor("StaticMeshActor_1")!.FindComponent("Door")!;
        Assert.Null(after.AttachParent);
        Assert.True(FVector.Distance(after.WorldTransform.Translation, before.WorldTransform.Translation) < 0.5f,
            $"{after.WorldTransform.Translation} vs {before.WorldTransform.Translation}");
    }

    [Fact]
    public void ADeletedSentrySpawnerLeavesTheGuardedZoneManagersList()
    {
        // Audit F059/F187: a deleted stock spawner stayed listed (as an inert object) and a copy was appended behind it.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_4_Military", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var spawnerClass = p.ScriptClass("/Script/SCUM", "SentrySpawner2");
        var spawners = new List<int>();
        for (var i = 1; i <= 3; i++)
        {
            var spawner = p.Export($"SentrySpawner_{i}", spawnerClass, level);
            var root = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), spawner);
            p.SetPayload(spawner, p.Properties(t => t.Object("RootComponent", root)));
            p.SetPayload(root, p.Properties(t => t.Vector("RelativeLocation", i * 1000, 0, 0)));
            spawners.Add(spawner);
        }

        var manager = p.Export("BP_GuardedZoneManager_2", p.ScriptClass("/Script/SCUM", "BP_GuardedZoneManager_C"), level);
        p.SetPayload(manager, p.Properties(t => t.ObjectArray("_sentrySpawners", [.. spawners])));
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [.. spawners, manager])));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            DeleteActors = ["SentrySpawner_1"],
            Copies = [new ActorCopy("SentrySpawner_2", "SentrySpawner_2_Copy", TransformValue.At(2500, 0, 0), "Root")],
        });

        Assert.Equal(["SentrySpawner_1"], report.RemovedActors);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var list = (ArrayValue)edited.ReadProperties(ExportNamed(edited, "BP_GuardedZoneManager_2", "PersistentLevel")).Find("_sentrySpawners")!.Value;
        Assert.Equal(new[] { "SentrySpawner_2", "SentrySpawner_3", "SentrySpawner_2_Copy" },
            list.Items.OfType<ObjectValue>().Select(o => edited.ResolveName(edited.Exports[o.Index - 1].ObjectName)));
    }

    [Fact]
    public void TheLevelScriptActorIsNeverMadeInert()
    {
        // The level's own native pointers (LevelScriptActor, Model) are typed: a plain Object there would be read wrong.
        var p = MinimalLevel();
        p.SetPayload(2, p.Properties(native: w => LevelTail(w, [3], levelScriptActor: 3)));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest { DeleteActors = ["Actor_1"] });

        Assert.Equal(["Actor_1"], report.RemovedActors);
        Assert.Equal(0, report.NeutralizedExports);
        Assert.Single(report.Warnings, w => w.Contains("part of the level itself", StringComparison.Ordinal));
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal("Actor", edited.GetExportClassName(2));
        Assert.All(LevelPackageEditor.ReadActorList(edited), a => Assert.Null(a.Name));
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
    public void APartScaledToNothingLosesItsMesh()
    {
        // A zero-scaled mesh still gets a body in the game (invisible walls where the owner's buildings were); without a
        // mesh the component has none. The stored mesh is nulled, a template-provided one overridden with null.
        var package = Synthetic();
        var gone = new TransformValue(new FVector(1, 2, 3), new FRotator(0, 0, 0), FVector.Zero);
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Transforms =
            [
                new TransformPatch("StaticMeshActor_1", "Door", gone),
                new TransformPatch("BP_Lamp_C_1", "Bulb", gone),
                new TransformPatch("StaticMeshActor_1", null, new TransformValue(new FVector(5, 6, 7), new FRotator(0, 0, 0), FVector.One)),
            ],
        });

        Assert.Equal(3, report.PatchedTransforms.Count);
        Assert.Empty(report.Warnings);

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var door = edited.ReadProperties(ExportNamed(edited, "Door", "StaticMeshActor_1"));
        Assert.Equal(0, Assert.IsType<ObjectValue>(door.Find("StaticMesh")!.Value).Index);
        AssertXyz(door.Find("RelativeScale3D"), 0, 0, 0);
        var bulb = edited.ReadProperties(ExportNamed(edited, "Bulb", "BP_Lamp_C_1"));
        Assert.Equal(0, Assert.IsType<ObjectValue>(bulb.Find("StaticMesh")!.Value).Index); // inserted
        AssertXyz(bulb.Find("RelativeScale3D"), 0, 0, 0);
        var root = edited.ReadProperties(ExportNamed(edited, "StaticMeshComponent0", "StaticMeshActor_1"));
        Assert.NotEqual(0, Assert.IsType<ObjectValue>(root.Find("StaticMesh")!.Value).Index); // scaled, not gone: keeps its mesh

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Null(document.FindActor("StaticMeshActor_1")!.FindComponent("Door")!.StaticMeshPath);
        Assert.NotNull(document.FindActor("StaticMeshActor_1")!.Root!.StaticMeshPath);
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

    /// <summary>Hektor (Discord): a copied tree must be a tree of the same foliage component, so it is chopped like the others.</summary>
    [Fact]
    public void AppendsInstancesBehindTheStoredOnes()
    {
        var package = Synthetic();
        var hint = new InstanceArrayHint("Rocks_Actor", "Rocks", [SyntheticLevels.Instance0, SyntheticLevels.Instance1]);
        var movedTo = new FTransform(new FRotator(0, 45, 0), new FVector(10, 20, 30), new FVector(2, 2, 2));
        var planted = new FTransform(new FRotator(0, -30, 0), new FVector(300, 400, 0), new FVector(1.5f, 1.5f, 1.5f));
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Instances = [new InstancePatch("Rocks_Actor", "Rocks", 0, movedTo)],
            InstanceAdds = [new InstanceAdd("Rocks_Actor", "Rocks", planted), new InstanceAdd("Rocks_Actor", "Nope", planted)],
            InstanceHints = [hint],
        });

        Assert.Equal(1, report.AddedInstances);
        Assert.Equal(1, report.MovedInstances);
        Assert.Contains(report.Warnings, w => w.Contains("Rocks_Actor.Nope", StringComparison.Ordinal));

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var ism = ExportNamed(edited, "Rocks", "Rocks_Actor");
        Assert.Equal(package.GetExportBytes(ism).Length + 64, edited.GetExportBytes(ism).Length); // one more FMatrix, no custom floats

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        var instances = document.FindActor("Rocks_Actor")!.InstanceTransforms.OrderBy(i => i.InstanceIndex).ToList();
        Assert.Equal(3, instances.Count);
        Assert.True(instances[0].LocalTransform.Equals(movedTo, 0.01f), instances[0].LocalTransform.ToString());
        Assert.True(instances[1].LocalTransform.Equals(SyntheticLevels.Instance1, 0.01f), instances[1].LocalTransform.ToString());
        Assert.True(instances[2].LocalTransform.Equals(planted, 0.01f), instances[2].LocalTransform.ToString());
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
    public void ACopiedChildActorIsNoChildOfTheComponentButStaysAttachedToIt()
    {
        // Salvador (Discord): a duplicated door did not open in the game. A building's door is a child actor (ParentComponent
        // -> the building's ChildActorComponent, which still spawns the original): its copy must not claim that component.
        var p = MinimalLevel();
        var building = p.Export("Building", p.ScriptClass(Engine, "Actor"), 2);
        var slot = p.Export("DoorSlot", p.ScriptClass(Engine, "ChildActorComponent"), building);
        var door = p.Export("Door_CAT", p.ScriptClass(Engine, "Actor"), 2);
        var doorRoot = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), door);
        p.SetPayload(2, p.Properties(native: w => LevelTail(w, [3, building, door])));
        p.SetPayload(building, p.Properties(t => t.Object("RootComponent", slot)));
        p.SetPayload(slot, p.Properties(t => t.Object("ChildActor", door)));
        p.SetPayload(door, p.Properties(t =>
        {
            t.Object("RootComponent", doorRoot);
            t.Object("ParentComponent", slot);
        }));
        p.SetPayload(doorRoot, p.Properties(t => t.Object("AttachParent", slot)));
        var built = p.Build();

        var (bytes, report) = LevelPackageEditor.Apply(CookedPackage.Parse(built.UAsset, built.UExp),
            new LevelEditRequest { Copies = [new ActorCopy("Door_CAT", "Door_CAT_Copy", null)] });

        Assert.Equal(new[] { "Door_CAT_Copy" }, report.AddedActors);
        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        int Reference(string name, string outer, string property) => ((ObjectValue)edited.ReadProperties(ExportNamed(edited, name, outer)).Find(property)!.Value).Index;
        Assert.Equal(0, Reference("Door_CAT_Copy", "PersistentLevel", "ParentComponent"));
        Assert.Equal(slot, Reference("Root", "Door_CAT_Copy", "AttachParent"));
        Assert.Equal(slot, Reference("Door_CAT", "PersistentLevel", "ParentComponent")); // the original is still the slot's
        Assert.Equal(door, Reference("DoorSlot", "Building", "ChildActor"));
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
        Assert.Equal(("/Game/X/ConcreteBlock002", 2), FNameRef.Split("/Game/X/ConcreteBlock002_1"));
        Assert.Equal(("Rocket_04", 0), FNameRef.Split("Rocket_04"));
        Assert.Equal(("Rocket", 1), FNameRef.Split("Rocket_0"));
        Assert.Equal(("SplineMeshComponent0", 0), FNameRef.Split("SplineMeshComponent0"));

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
    private static void LevelTail(ByteWriter w, IReadOnlyList<int> actors, int levelScriptActor = 0)
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
        w.I32(0); // Model
        w.I32(0); // ModelComponents
        w.I32(levelScriptActor);
        w.I32(0); // NavListStart
        w.I32(0); // NavListEnd
    }
    [Fact]
    public void ReplacesTheMeshOfAStoredComponent()
    {
        const string boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder";
        var package = Synthetic();
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Meshes =
            [
                new MeshPatch("StaticMeshActor_1", null, boulder), // its root: the stored StaticMesh tag is rewritten in place
                new MeshPatch("BP_Lamp_C_1", "Bulb", boulder), // the mesh came from the template: a tag is inserted
                new MeshPatch("Nobody", null, boulder),
            ],
            Transforms = [new TransformPatch("StaticMeshActor_1", null, new TransformValue(new FVector(1000, 0, 0), new FRotator(0, 90, 0), new FVector(2, 1, 1)))],
        });

        Assert.Equal(["StaticMeshActor_1.StaticMeshComponent0", "StaticMeshActor_1.StaticMeshComponent0 (mesh)", "BP_Lamp_C_1.Bulb (mesh)"], report.PatchedTransforms);
        Assert.Single(report.Warnings, w => w.Contains("Nobody", StringComparison.Ordinal));
        Assert.Equal(package.Imports.Count + 2, CookedPackage.Parse(bytes.UAsset, bytes.UExp).Imports.Count); // the package and the mesh, once

        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        foreach (var (component, actor) in new[] { ("StaticMeshComponent0", "StaticMeshActor_1"), ("Bulb", "BP_Lamp_C_1") })
        {
            var index = ExportNamed(edited, component, actor);
            var mesh = Assert.IsType<ObjectValue>(edited.ReadProperties(index).Find("StaticMesh")!.Value);
            Assert.Equal("/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder", edited.GetFullPath(mesh.Index));
            var entry = edited.Exports[index];
            var deps = edited.ReadPreloadDependencies();
            var createBeforeSerialize = deps.Skip(entry.FirstExportDependency + entry.SerializationBeforeSerializationDependencies).Take(entry.CreateBeforeSerializationDependencies);
            Assert.Contains(mesh.Index, createBeforeSerialize); // the loader creates the mesh before it reads the component
        }

        // The new mesh draws its own materials: the replaced component's OverrideMaterials are gone, the door keeps its own.
        Assert.Null(edited.ReadProperties(ExportNamed(edited, "StaticMeshComponent0", "StaticMeshActor_1")).Find("OverrideMaterials"));
        Assert.NotNull(edited.ReadProperties(ExportNamed(edited, "Door", "StaticMeshActor_1")).Find("OverrideMaterials"));
        AssertXyz(edited.ReadProperties(ExportNamed(edited, "StaticMeshComponent0", "StaticMeshActor_1")).Find("RelativeLocation"), 1000, 0, 0);

        var document = LoadThroughCue4Parse(bytes, "A_0_TestLevel", SyntheticLevels.LevelPath);
        Assert.Equal(boulder, document.FindActor("StaticMeshActor_1")!.StaticMeshPath);
        Assert.Equal(boulder, document.FindActor("BP_Lamp_C_1")!.FindComponent("Bulb")!.StaticMeshPath);
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", document.FindActor("StaticMeshActor_1")!.FindComponent("Door")!.StaticMeshPath); // the door keeps its own
        Assert.True(document.FindActor("StaticMeshActor_1")!.Root!.Relative.Scale.Equals(new FVector(2, 1, 1), 0.001f));
    }
}
