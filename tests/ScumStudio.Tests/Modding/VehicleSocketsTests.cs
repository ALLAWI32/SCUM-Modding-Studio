using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-12): "every part of the car editable like Blender: move it, scale it, hide it". A part hangs on a socket
/// of the vehicle's skeleton, which the stock car and every clone share; a clone moves a part through a socket of its own
/// copy of the skeletal mesh (UE looks there first). Synthetic packages, read back with ours and with CUE4Parse.
/// </summary>
public sealed class VehicleSocketsTests
{
    [Fact]
    public void MovingASocketOfTheMeshChangesOnlyItsLocation()
    {
        var mesh = Parse(PartsSynthetic.Mesh(withSocket: true), PartsSynthetic.MeshPath);
        var socket = Assert.Single(VehicleSockets.Read(mesh));
        Assert.True(socket.OnMesh);

        var bytes = VehicleSockets.Apply(mesh, [new("SK_Car", VehicleSockets.Path("s_Body_Top", VehicleSockets.Location), "10, -20, 140.5")]);
        var after = Parse(bytes, PartsSynthetic.MeshPath);
        var moved = Assert.Single(VehicleSockets.Read(after));
        Assert.Equal(new Vector3(10, -20, 140.5f), moved.Location);
        Assert.Equal((socket.Bone, socket.Rotation, socket.Scale), (moved.Bone, moved.Rotation, moved.Scale));

        // Same tables, same exports; the socket's payload differs in its 12 location bytes only.
        Assert.Equal(mesh.Names, after.Names);
        Assert.Equal(mesh.Exports.Count, after.Exports.Count);
        for (var i = 0; i < mesh.Exports.Count; i++)
        {
            var (before, now) = (mesh.GetExportBytes(i), after.GetExportBytes(i));
            if (mesh.GetExportClassName(i) != "SkeletalMeshSocket")
            {
                Assert.Equal(before, now);
                continue;
            }

            var value = mesh.ReadProperties(i).Find(VehicleSockets.Location)!.ValueOffset;
            Assert.Equal(before.Length, now.Length);
            Assert.All(Enumerable.Range(0, before.Length).Where(k => before[k] != now[k]), k => Assert.InRange(k, value, value + 11));
        }
    }

    [Fact]
    public async Task ASocketOnlyTheSkeletonHasGetsAnOverrideOnTheMesh()
    {
        using var temp = new LevelTempDirectory();
        var skeletonBytes = PartsSynthetic.Skeleton();
        var skeleton = Parse(skeletonBytes, PartsSynthetic.SkeletonPath);
        var door = Assert.Single(VehicleSockets.Read(skeleton), s => s.Name == "s_Door_FrontLeft");
        Assert.False(door.OnMesh);
        var mesh = Parse(PartsSynthetic.Mesh(withSocket: false), PartsSynthetic.MeshPath);
        Assert.Empty(VehicleSockets.Read(mesh));

        var moved = door.Location + new Vector3(10, 0, 0);
        var bytes = VehicleSockets.Apply(mesh, VehicleSockets.Override("SK_Car", door, moved, door.Rotation, door.Scale));
        var after = Parse(bytes, PartsSynthetic.MeshPath);
        var own = Assert.Single(VehicleSockets.Read(after));
        Assert.Equal(("s_Door_FrontLeft", "Root", true), (own.Name, own.Bone, own.OnMesh));
        Assert.Equal((moved, door.Rotation, door.Scale), (own.Location, own.Rotation, own.Scale));

        // The mesh lists it in a new Sockets array; the cooker's dependencies: class and template before the socket is
        // created, the mesh before it (its outer), the socket before the mesh is serialized.
        var meshIndex = VehicleSockets.MeshExport(after);
        var socketIndex = after.Exports.Count - 1;
        var sockets = Assert.IsType<ArrayValue>(after.ReadProperties(meshIndex).Find("Sockets")?.Value);
        Assert.Equal(socketIndex + 1, Assert.IsType<ObjectValue>(Assert.Single(sockets.Items)).Index);
        Assert.Equal("SkeletalMeshSocket", after.GetExportClassName(socketIndex));
        Assert.Equal(meshIndex + 1, after.Exports[socketIndex].OuterIndex);
        Assert.Contains(socketIndex + 1, Groups(after, meshIndex)[1]);
        Assert.Equal(["/Script/Engine.SkeletalMeshSocket", "/Script/Engine.Default__SkeletalMeshSocket"], Groups(after, socketIndex)[2].Select(after.GetFullPath));
        Assert.Equal([meshIndex + 1], Groups(after, socketIndex)[3]);

        // CUE4Parse: the socket is an object of the mesh with the new place; the skeleton's file is untouched.
        await bytes.WriteAsync(temp.Combine("SCUM", "Content", "T", "SK_Car"));
        await skeletonBytes.WriteAsync(temp.Combine("SCUM", "Content", "T", "SK_Car_Skeleton"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var package = catalog.LoadPackage(PartsSynthetic.MeshPath);
        var read = package.GetExportOrNull(after.ResolveName(after.Exports[socketIndex].ObjectName), StringComparison.Ordinal);
        Assert.NotNull(read);
        Assert.Equal("s_Door_FrontLeft", read.GetOrDefault<FName>("SocketName").Text);
        Assert.Equal("Root", read.GetOrDefault<FName>("BoneName").Text);
        var location = read.GetOrDefault<FVector>("RelativeLocation");
        Assert.Equal(moved, new Vector3(location.X, location.Y, location.Z));
        Assert.Equal("SK_Car", read.Outer?.Name);
        Assert.Equal(skeletonBytes.UAsset.Concat(skeletonBytes.UExp), File.ReadAllBytes(temp.Combine("SCUM", "Content", "T", "SK_Car_Skeleton.uasset")).Concat(File.ReadAllBytes(temp.Combine("SCUM", "Content", "T", "SK_Car_Skeleton.uexp"))));
    }

    [Fact]
    public void ASocketNameEndingInANumberIsWrittenAsTextAndNumber()
    {
        var mesh = Parse(PartsSynthetic.Mesh(withSocket: false), PartsSynthetic.MeshPath);
        var after = Parse(VehicleSockets.Apply(mesh, [new("SK_Car", VehicleSockets.Path("s_Light_1", VehicleSockets.Bone), "Root"), new("SK_Car", VehicleSockets.Path("s_Light_1", VehicleSockets.Location), "1, 2, 3")]), PartsSynthetic.MeshPath);
        var name = Assert.IsType<NameValue>(after.ReadProperties(after.Exports.Count - 1).Find("SocketName")?.Value);
        Assert.Equal(("s_Light", 2), (after.Names[name.Ref.Index], name.Ref.Number));
        Assert.DoesNotContain("s_Light_1", after.Names);
        Assert.Equal("s_Light_1", Assert.Single(VehicleSockets.Read(after)).Name);
    }

    [Fact]
    public void AnAbsentScaleIsInsertedBeforeNone()
    {
        var mesh = Parse(PartsSynthetic.Mesh(withSocket: true), PartsSynthetic.MeshPath);
        var socketIndex = Enumerable.Range(0, mesh.Exports.Count).Single(i => mesh.GetExportClassName(i) == "SkeletalMeshSocket");
        Assert.Null(mesh.ReadProperties(socketIndex).Find("RelativeScale"));

        var after = Parse(VehicleSockets.Apply(mesh, [new("SK_Car", VehicleSockets.Path("s_Body_Top", VehicleSockets.Scale), "0.001, 0.001, 0.001")]), PartsSynthetic.MeshPath);
        Assert.Equal(mesh.GetExportBytes(socketIndex).Length + 61, after.GetExportBytes(socketIndex).Length);
        Assert.Equal(new Vector3(0.001f), Assert.Single(VehicleSockets.Read(after)).Scale);
        Assert.Equal(VehicleSockets.Scale, after.ReadProperties(socketIndex).Properties.Last().Name);
    }

    [Fact]
    public void ANewSocketNeedsItsBone()
    {
        var mesh = Parse(PartsSynthetic.Mesh(withSocket: false), PartsSynthetic.MeshPath);
        var ex = Assert.Throws<InvalidOperationException>(() => VehicleSockets.Apply(mesh, [new("SK_Car", VehicleSockets.Path("s_CustomA", VehicleSockets.Location), "1, 2, 3")]));
        Assert.Contains("bone", ex.Message, StringComparison.Ordinal);
        Assert.True(VehicleParts.IsEdit(VehicleSockets.Path("s_CustomA", VehicleSockets.Location)));
    }

    internal static CookedPackage Parse(PackageBytes bytes, string path) => CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, path);

    private static int[][] Groups(CookedPackage package, int exportIndex)
    {
        var e = package.Exports[exportIndex];
        var preload = package.ReadPreloadDependencies();
        var at = e.FirstExportDependency;
        return new[] { e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies }
            .Select(c =>
            {
                var g = at < 0 ? [] : preload[at..(at + c)];
                at = at < 0 ? at : at + c;
                return g;
            })
            .ToArray();
    }
}

/// <summary>Small cooked vehicle packages: a skeleton with door sockets, a skeletal mesh, parts with slots, a spawn preset.</summary>
internal static class PartsSynthetic
{
    public const string Engine = "/Script/Engine";
    public const string MeshPath = "/Game/T/SK_Car";
    public const string SkeletonPath = "/Game/T/SK_Car_Skeleton";
    public const string Folder = "/Game/ConZ_Files/Vehicles/Car/Car";
    public const string Attachments = Folder + "/Attachments";
    public const string PresetPath = "/Game/ConZ_Files/Vehicles/SpawningPresets/ManualSpawn/CarManualSpawnPreset";

    public static string Class(string leaf) => $"{Attachments}/{leaf}.{leaf}_C";

    /// <summary>SK_Car_Skeleton: s_Door_FrontLeft (location + rotation) and s_Body_Top on Root.</summary>
    public static PackageBytes Skeleton()
    {
        var p = new SyntheticLevelPackage();
        var cls = p.ScriptClass(Engine, "SkeletalMeshSocket");
        var skeleton = p.Export("SK_Car_Skeleton", p.ScriptClass(Engine, "Skeleton"), 0, flags: 0x3);
        var door = p.Export("SkeletalMeshSocket_0", cls, skeleton);
        var top = p.Export("SkeletalMeshSocket_1", cls, skeleton);
        p.SetPayload(door, p.Properties(t =>
        {
            t.NameValue("SocketName", "s_Door_FrontLeft");
            t.NameValue("BoneName", "Root");
            t.Vector("RelativeLocation", 87.7f, -86.9f, 49.7f);
            t.Rotator("RelativeRotation", 0, 5, 0);
        }));
        p.SetPayload(top, p.Properties(t =>
        {
            t.NameValue("SocketName", "s_Body_Top");
            t.NameValue("BoneName", "Root");
            t.Vector("RelativeLocation", -36.9f, 0, 127.5f);
        }));
        p.SetPayload(skeleton, p.Properties(t => t.ObjectArray("Sockets", door, top)));
        return p.Build();
    }

    /// <summary>SK_Car: a skeletal mesh on SK_Car_Skeleton, with or without a socket of its own (s_Body_Top); native data stands in as a few bytes.</summary>
    public static PackageBytes Mesh(bool withSocket)
    {
        var p = new SyntheticLevelPackage();
        var skeleton = p.ObjectImport(SkeletonPath, Engine, "Skeleton", "SK_Car_Skeleton");
        var mesh = p.Export("SK_Car", p.ScriptClass(Engine, "SkeletalMesh"), 0, flags: 0x3);
        var socket = withSocket ? p.Export("SkeletalMeshSocket_0", p.ScriptClass(Engine, "SkeletalMeshSocket"), mesh) : 0;
        if (withSocket)
        {
            p.SetPayload(socket, p.Properties(t =>
            {
                t.NameValue("SocketName", "s_Body_Top");
                t.NameValue("BoneName", "Root");
                t.Vector("RelativeLocation", -36.9f, 0, 127.5f);
            }));
        }

        p.SetPayload(mesh, p.Properties(t =>
        {
            t.Object("Skeleton", skeleton);
            if (withSocket)
            {
                t.ObjectArray("Sockets", socket);
            }
        }, w => w.I32(0x5EC0DE)));
        return p.Build();
    }

    /// <summary>
    /// A part Blueprint (<c>BPC_Car_&lt;part&gt;</c>): ParentSocket, MeshSetup.Mesh and, when given, <c>_slots</c> (each the
    /// classes it takes, plus a StateTags-like name the copy keeps).
    /// </summary>
    public static PackageBytes Part(string part, string socket, string mesh, params string[][] slots)
    {
        var p = new SyntheticLevelPackage();
        var name = "BPC_Car_" + part;
        var cls = p.ClassExport(name + "_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), p.ScriptClass("/Script/SCUM", "VehicleAttachment"));
        var cdo = p.Export("Default__" + name + "_C", p.ScriptClass(Engine, "Actor"), 0, flags: 0x39);
        p.SetPayload(cls, p.Properties());
        var w = new ByteWriter();
        var t = new SyntheticLevelPackage.TagWriter(p, w);
        t.NameValue("ParentSocket", socket);
        t.TaggedStruct("MeshSetup", "VehicleAttachmentMeshSetup", m =>
        {
            m.SoftObject("Mesh", mesh);
            m.Float("Mass", 5);
        });
        if (slots.Length > 0)
        {
            w.Raw(SlotsArray(p, slots));
        }

        t.Float("_health", 500);
        t.None();
        w.I32(0);
        p.SetPayload(cdo, w.ToArray());
        return p.Build();
    }

    /// <summary>
    /// CarManualSpawnPreset: a root node (the part <paramref name="root"/>) whose children are leaf nodes of
    /// <paramref name="children"/>, under a <c>VehiclePreset</c>.
    /// </summary>
    public static PackageBytes Preset(string root, params string[] children)
    {
        var p = new SyntheticLevelPackage();
        var nodeClass = p.ScriptClass("/Script/SCUM", "VehiclePresetNode");
        var preset = p.Export("CarManualSpawnPreset", p.ScriptClass("/Script/SCUM", "VehiclePreset"), 0, flags: 0x1);
        var rootNode = p.Export("VehiclePresetNode_0", nodeClass, preset);
        var leaves = children.Select((c, i) => p.Export("VehiclePresetNode_" + (i + 1), nodeClass, rootNode)).ToArray();
        p.SetPayload(preset, p.Properties(t =>
        {
            t.SoftObject("VehicleClass", Folder + "/BPC_Car.BPC_Car_C");
            t.Object("RootNode", rootNode);
        }));
        p.SetPayload(rootNode, p.Properties(t =>
        {
            t.SoftObject("AttachmentClass", Class("BPC_Car_" + root));
            t.ObjectArray("Children", leaves);
        }));
        for (var i = 0; i < leaves.Length; i++)
        {
            var child = children[i];
            p.SetPayload(leaves[i], p.Properties(t => t.SoftObject("AttachmentClass", Class("BPC_Car_" + child))));
        }

        return p.Build();
    }

    private static void Tag(ByteWriter w, SyntheticLevelPackage p, string name, string type, int size)
    {
        w.FName(p.Name(name));
        w.FName(p.Name(type));
        w.I32(size);
        w.I32(0);
    }

    /// <summary><c>_slots: Array&lt;VehicleAttachmentSlot&gt;</c>, each PossibleAttachmentClasses (soft class paths) and a tag name.</summary>
    private static byte[] SlotsArray(SyntheticLevelPackage p, string[][] slots)
    {
        var body = new ByteWriter();
        foreach (var slot in slots)
        {
            var classes = new ByteWriter();
            classes.I32(slot.Length);
            foreach (var c in slot)
            {
                classes.FName(p.Name(c));
                classes.FString(string.Empty);
            }

            Tag(body, p, "PossibleAttachmentClasses", "ArrayProperty", classes.Length);
            body.FName(p.Name("SoftObjectProperty"));
            body.U8(0);
            body.Raw(classes.WrittenSpan);
            new SyntheticLevelPackage.TagWriter(p, body).NameValue("StateTag", "Vehicle.Attachment.Type.RoofRack");
            body.FName(p.Name("None"));
        }

        var inner = new ByteWriter();
        Tag(inner, p, "_slots", "StructProperty", body.Length);
        inner.FName(p.Name("VehicleAttachmentSlot"));
        inner.Guid(default);
        inner.U8(0);
        var w = new ByteWriter();
        Tag(w, p, "_slots", "ArrayProperty", 4 + inner.Length + body.Length);
        w.FName(p.Name("StructProperty"));
        w.U8(0);
        w.I32(slots.Length);
        w.Raw(inner.WrittenSpan);
        w.Raw(body.WrittenSpan);
        return w.ToArray();
    }
}
