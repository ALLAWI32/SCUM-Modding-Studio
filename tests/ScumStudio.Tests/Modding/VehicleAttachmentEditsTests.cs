using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;
using static ScumStudio.Tests.Modding.PartsSynthetic;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-12): "swap a part, add parts: the person makes the car he wants". A part's model and socket, a new slot
/// on a part and the spawn preset node that fills it, and the whole add-a-part edit set through <see cref="AssetModBuilder"/>.
/// Synthetic packages, read back with ours and with CUE4Parse.
/// </summary>
public sealed class VehicleAttachmentEditsTests
{
    private const string Box = "/Game/T/SM_Box.SM_Box";
    private const string Rack = "/Game/T/SM_Rack.SM_Rack";

    [Fact]
    public async Task AModelSwapReadsBackTheNewSoftPath()
    {
        using var temp = new LevelTempDirectory();
        var part = Parse(Part("RoofRack", "s_Body_Top", Rack), "RoofRack");
        var edit = VehicleAttachmentEdits.SwapMesh(part, Box, p => p.StartsWith("/Game/T/SM_", StringComparison.Ordinal) ? "StaticMesh" : null);
        Assert.Equal(("Default__BPC_Car_RoofRack_C", VehicleAttachmentEdits.MeshPath), (edit.Export, edit.Path));

        var bytes = VehicleParts.Apply(part, [edit, new(edit.Export, VehicleAttachmentEdits.SocketPath, "s_CustomA")]);
        var after = Parse(bytes, "RoofRack");
        Assert.Equal(Box, VehicleAttachmentEdits.MeshOf(after));
        Assert.Equal("s_CustomA", VehicleAttachmentEdits.ParentSocketOf(after));
        Assert.Contains("/Game/T/SM_Box", after.Names);

        await bytes.WriteAsync(temp.Combine("SCUM", "Content", "ConZ_Files", "Vehicles", "Car", "Car", "Attachments", "BPC_Car_RoofRack"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var cdo = catalog.LoadPackage(Attachments + "/BPC_Car_RoofRack").GetExportOrNull("Default__BPC_Car_RoofRack_C", StringComparison.Ordinal);
        Assert.NotNull(cdo);
        Assert.Equal(Box, cdo.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text);
        Assert.Equal("s_CustomA", cdo.GetOrDefault<FName>("ParentSocket").Text);
        Assert.Equal(5f, cdo.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<float>("Mass"));
    }

    [Fact]
    public void AModelOfTheOtherKindIsRefused()
    {
        var door = Parse(Part("Door_FrontLeft", "s_Door_FrontLeft", "/Game/T/SK_Door.SK_Door"), "Door_FrontLeft");
        string? ClassOf(string path) => path.Contains("/SK_", StringComparison.Ordinal) ? "SkeletalMesh" : path.Contains("/SM_", StringComparison.Ordinal) ? "StaticMesh" : null;
        Assert.Throws<InvalidOperationException>(() => VehicleAttachmentEdits.SwapMesh(door, Box, ClassOf));
        Assert.Throws<InvalidOperationException>(() => VehicleAttachmentEdits.SwapMesh(door, "/Game/T/T_Paint.T_Paint", ClassOf));
        Assert.Equal("/Game/T/SK_Hood.SK_Hood", VehicleAttachmentEdits.SwapMesh(door, "/Game/T/SK_Hood.SK_Hood", ClassOf).Value);
    }

    [Fact]
    public void AnAppendedSlotTakesOnlyTheNewPart()
    {
        var top = Parse(Part("Body_Top", "s_Body_Top", "/Game/T/SK_Top.SK_Top", [Class("BPC_Car_Body_ArmorHeavy_Top")], [Class("BPC_Car_RoofRack")]), "Body_Top");
        Assert.Equal(2, VehicleAttachmentEdits.SlotsOf(top).Count);
        var custom = VehicleAttachmentEdits.ClassPath(Attachments + "/BPC_New_CustomA");

        var after = Parse(VehicleParts.Apply(top, [new("Default__BPC_Car_Body_Top_C", VehicleAttachmentEdits.SlotPath(2), VehicleAttachmentEdits.SlotValue(custom, 1))]), "Body_Top");
        var slots = VehicleAttachmentEdits.SlotsOf(after);
        Assert.Equal(3, slots.Count);
        Assert.Equal([custom], slots[2]);
        Assert.Equal([Class("BPC_Car_RoofRack")], slots[1]);
        Assert.Equal([Class("BPC_Car_Body_ArmorHeavy_Top")], slots[0]);
        Assert.Equal("Vehicle.Attachment.Type.RoofRack", (((ScumStudio.Formats.Properties.ArrayValue)after.ReadProperties(1).Find("_slots")!.Value).Items[2] as ScumStudio.Formats.Properties.StructValue)?.Find("StateTag")?.Value.ToString());
        Assert.Equal(500f, (after.ReadProperties(1).Find("_health")?.Value as ScumStudio.Formats.Properties.FloatValue)?.Value);

        // Only appended: a slot in the middle or past the end, or a donor that does not exist, is refused.
        Assert.Throws<InvalidOperationException>(() => VehicleParts.Apply(top, [new("Default__BPC_Car_Body_Top_C", VehicleAttachmentEdits.SlotPath(1), VehicleAttachmentEdits.SlotValue(custom, 0))]));
        Assert.Throws<InvalidOperationException>(() => VehicleParts.Apply(top, [new("Default__BPC_Car_Body_Top_C", VehicleAttachmentEdits.SlotPath(2), VehicleAttachmentEdits.SlotValue(custom, 5))]));
    }

    [Fact]
    public async Task TheNewSlotSpawnsTheNewPart()
    {
        using var temp = new LevelTempDirectory();
        var preset = Parse(Preset("Body_Top", "Body_ArmorHeavy_Top", "RoofRack"), PresetPath);
        var root = Assert.Single(VehicleParts.Read(preset, _ => null), t => t.Path == "Children[0]").Export;
        var custom = VehicleAttachmentEdits.ClassPath(Attachments + "/BPC_New_CustomA");

        var bytes = VehicleParts.Apply(preset, [new(root, "Children[2]", custom)]);
        var after = Parse(bytes, PresetPath);
        var slots = VehicleParts.Read(after, _ => null);
        Assert.Equal(3, slots.Count);
        Assert.Equal(custom, Assert.Single(slots, s => s.Path == "Children[2]").Value);
        Assert.Contains(custom, VehicleParts.Spawned(after));

        await bytes.WriteAsync(temp.Combine("SCUM", "Content", "ConZ_Files", "Vehicles", "SpawningPresets", "ManualSpawn", "CarManualSpawnPreset"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var node = catalog.LoadObject<UObject>(PresetPath + ".CarManualSpawnPreset").GetOrDefault<FPackageIndex>("RootNode").Load<UObject>()!;
        var children = node.GetOrDefault<FPackageIndex[]>("Children");
        Assert.Equal(3, children.Length);
        Assert.Equal(custom, children[2].Load<UObject>()!.GetOrDefault<FSoftObjectPath>("AttachmentClass").AssetPathName.Text);
    }

    [Fact]
    public async Task AddingAPartThroughTheBuilderOnlyAddsNewPackages()
    {
        // FAILS BEFORE: VehicleParts.IsEdit took only Children[i], so the socket, model and slot edits went to the stored
        // value patcher, which knows no '#Socket[…]' property: "edits skipped".
        using var temp = new LevelTempDirectory();
        async Task Write(string package, PackageBytes bytes) => await bytes.WriteAsync(temp.Combine(["SCUM", "Content", .. package["/Game/".Length..].Split('/')]));
        await Write(MeshPath, Mesh(withSocket: false));
        await Write(SkeletonPath, Skeleton());
        await Write(Attachments + "/BPC_Car_Body_Top", Part("Body_Top", "s_Body_Top", "/Game/T/SK_Top.SK_Top", [Class("BPC_Car_RoofRack")]));
        await Write(Attachments + "/BPC_Car_RoofRack", Part("RoofRack", "s_Body_Top", Rack));
        await Write(PresetPath, Preset("Body_Top", "RoofRack"));
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var family = new ClonePlan(Folder + "/BPC_Car", Folder + "/BPC_New",
        [
            new(MeshPath, "/Game/T/SK_New"),
            new(Attachments + "/BPC_Car_Body_Top", Attachments + "/BPC_New_Body_Top"),
            new(Attachments + "/BPC_Car_RoofRack", Attachments + "/BPC_New_RoofRack"),
            new(PresetPath, PresetPath.Replace("CarManual", "NewManual", StringComparison.Ordinal)),
        ]);
        var customPackage = VehicleAttachmentEdits.CustomName(Attachments, "New", _ => false);
        Assert.Equal(Attachments + "/BPC_New_CustomA", customPackage);
        var socket = VehicleAttachmentEdits.CustomSocket(customPackage);
        var part = new ClonePlan(Attachments + "/BPC_Car_RoofRack", customPackage, [new(Attachments + "/BPC_Car_RoofRack", customPackage)]);
        var custom = VehicleAttachmentEdits.ClassPath(customPackage);
        var newPreset = PresetPath.Replace("CarManual", "NewManual", StringComparison.Ordinal);
        var root = Assert.Single(VehicleParts.Read(Parse(Preset("Body_Top", "RoofRack"), PresetPath), _ => null)).Export;
        var request = new AssetModRequest([family, part], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            ["/Game/T/SK_New"] = [new("SK_New", VehicleSockets.Path(socket, VehicleSockets.Bone), "Root"), new("SK_New", VehicleSockets.Path(socket, VehicleSockets.Location), "-30, 0, 160")],
            [customPackage] = [new("Default__BPC_New_CustomA_C", VehicleAttachmentEdits.MeshPath, Box), new("Default__BPC_New_CustomA_C", VehicleAttachmentEdits.SocketPath, socket)],
            [Attachments + "/BPC_New_Body_Top"] = [new("Default__BPC_New_Body_Top_C", VehicleAttachmentEdits.SlotPath(1), VehicleAttachmentEdits.SlotValue(custom, 0))],
            [newPreset] = [new(root, "Children[1]", custom)],
        });

        var result = AssetModBuilder.Build(catalog, request);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("edits skipped", StringComparison.Ordinal));
        Assert.Equal(6, result.Applied.Count);

        // Every package is new: nothing of the game is written over.
        Assert.Equal(5, result.Packages.Count);
        Assert.All(result.Packages, p => Assert.True(p.IsClone && !catalog.PackageExists(p.PackagePath), p.PackagePath));

        CookedPackage Built(string path) => Assert.Single(result.Packages, p => p.PackagePath == path) is var b ? CookedPackage.Parse(b.Bytes.UAsset, b.Bytes.UExp, null, path) : null!;
        Assert.Equal((socket, "Root"), Assert.Single(VehicleSockets.Read(Built("/Game/T/SK_New")), s => s.OnMesh) is var s ? (s.Name, s.Bone) : default);
        Assert.Equal((Box, socket), (VehicleAttachmentEdits.MeshOf(Built(customPackage)), VehicleAttachmentEdits.ParentSocketOf(Built(customPackage))));
        Assert.Equal([custom], VehicleAttachmentEdits.SlotsOf(Built(Attachments + "/BPC_New_Body_Top"))[1]);
        Assert.Contains(custom, VehicleParts.Spawned(Built(newPreset)));
        Assert.Contains(Attachments + "/BPC_New_RoofRack.BPC_New_RoofRack_C", VehicleParts.Spawned(Built(newPreset)));
    }

    private static CookedPackage Parse(PackageBytes bytes, string pathOrPart) =>
        CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, pathOrPart.StartsWith('/') ? pathOrPart : Attachments + "/BPC_Car_" + pathOrPart);
}
