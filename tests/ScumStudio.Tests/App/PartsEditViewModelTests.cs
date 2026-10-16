using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Modding;
using static ScumStudio.Tests.Modding.PartsSynthetic;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Parts panel's journal (<see cref="PartsStudio"/>, driven by the Vehicles page): a picked part of a clone moves,
/// hides, leaves the spawn, gets another model, and a new part is bolted on; each step is one journaled batch that undo
/// takes back whole, and the project's export writes it (synthetic vehicle, real project journal).
/// </summary>
public sealed class PartsEditViewModelTests
{
    private const string Box = "/Game/T/SM_Box.SM_Box";
    private const string NewMesh = "/Game/T/SK_New";

    [Fact]
    public async Task APickedDoorShowsWhereTheGameHasIt()
    {
        using var rig = await Rig.CreateAsync();
        var door = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft");
        Assert.NotNull(door);
        Assert.Equal((Attachments + "/BPC_New_Door_FrontLeft", "Door FrontLeft", "s_Door_FrontLeft"), (door.Attachment, door.Label, door.Socket));
        Assert.False(door.IsWheel);
        Assert.False(door.Game!.OnMesh); // the skeleton's socket: the clone's mesh has none of its own yet
        Assert.Equal(door.Game, door.Place);
        Assert.Null(PartsStudio.OverrideOf(door));
        Assert.Equal(NewMesh, rig.Studio().Mesh);
        Assert.Null(rig.Studio().Select("/Game/T/Elsewhere/BPC_Other")); // not a part of this clone
    }

    [Fact]
    public async Task AMoveIsOneStepThatUndoTakesBack()
    {
        using var rig = await Rig.CreateAsync();
        var door = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        var (location, rotation, scale) = PartsStudio.Apply(door.Place!, new FTransform(new FVector(10, 0, 0)));
        Assert.Equal(door.Game!.Location + new Vector3(10, 0, 0), location);

        var step = rig.Studio().Move(door, location, rotation, scale);
        Assert.NotNull(step);
        Assert.Equal(4, step.Ops.Count); // bone, location, rotation, scale of the mesh's own copy of the socket
        Assert.All(step.Ops, o => Assert.Equal(NewMesh, Assert.IsType<SetAssetValueOp>(o).Package));
        var before = rig.Journal;
        rig.Projects.Apply(step);
        Assert.Equal(before + 1, rig.Journal);

        var moved = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        Assert.Equal(location, moved.Place!.Location);
        var delta = PartsStudio.OverrideOf(moved)!.Delta.Translation;
        Assert.Equal(new Vector3(10, 0, 0), new Vector3(delta.X, delta.Y, delta.Z), new ToleranceComparer());

        // The export writes the socket on the clone's mesh only.
        var built = rig.Build();
        Assert.DoesNotContain(built.Warnings, w => w.Contains("edits skipped", StringComparison.Ordinal));
        var mesh = Assert.Single(built.Packages, p => p.PackagePath == NewMesh);
        var own = Assert.Single(VehicleSockets.Read(CookedPackage.Parse(mesh.Bytes.UAsset, mesh.Bytes.UExp, null, NewMesh)));
        Assert.Equal(("s_Door_FrontLeft", location), (own.Name, own.Location));
        Assert.DoesNotContain(built.Packages, p => p.PackagePath == SkeletonPath);

        rig.Projects.Undo();
        Assert.Empty(rig.State.GetAssetValues(NewMesh));
        Assert.Equal(door.Game, rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!.Place);
    }

    [Fact]
    public async Task HideScalesThePartDownAndGamePositionPutsItBack()
    {
        using var rig = await Rig.CreateAsync();
        var door = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        rig.Projects.Apply(rig.Studio().Hide(door, hidden: true)!);
        var hidden = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        Assert.True(PartsStudio.OverrideOf(hidden)!.Hidden);
        Assert.Equal(new Vector3(0.001f), hidden.Place!.Scale);

        rig.Projects.Apply(rig.Studio().Reset(hidden)!);
        Assert.Empty(rig.State.GetAssetValues(NewMesh));
        Assert.Null(rig.Studio().Reset(rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!));
    }

    [Fact]
    public async Task WheelsAndSharedMeshesAreRefused()
    {
        using var rig = await Rig.CreateAsync();
        var wheel = rig.Studio().Select(Attachments + "/BPC_Car_Wheel_FrontLeft")!;
        Assert.True(wheel.IsWheel);
        Assert.Equal("Studio.Parts.WheelNote", Assert.Throws<PartRefusedException>(() => rig.Studio().Move(wheel, Vector3.Zero, Vector3.Zero, Vector3.One)).Key);

        // A clone made without its own paint shares the vehicle mesh with the game: nothing can move.
        var shared = new PartsStudio(rig.Clone with { Packages = rig.Clone.Packages.Where(p => p.New != NewMesh).ToList() }, rig.Read, rig.State);
        Assert.Null(shared.Mesh);
        var door = shared.Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        Assert.Equal("Studio.Parts.SharedMesh", Assert.Throws<PartRefusedException>(() => shared.Move(door, Vector3.Zero, Vector3.Zero, Vector3.One)).Key);
        Assert.Equal("Studio.Parts.SharedMesh", Assert.Throws<PartRefusedException>(() => shared.Add(Box, Vector3.Zero, _ => false)).Key);
    }

    [Fact]
    public async Task RemoveAndAnotherModelAreJournaledAndExported()
    {
        using var rig = await Rig.CreateAsync();
        var door = rig.Studio().Select(Attachments + "/BPC_Car_Door_FrontLeft")!;
        rig.Projects.Apply(rig.Studio().Remove(door)!);
        string? ClassOf(string p) => p.Contains("/SK_", StringComparison.Ordinal) ? "SkeletalMesh" : p.Contains("/SM_", StringComparison.Ordinal) ? "StaticMesh" : null;
        Assert.Throws<InvalidOperationException>(() => rig.Studio().SwapMesh(door, Box, ClassOf));
        rig.Projects.Apply(rig.Studio().SwapMesh(door, "/Game/T/SK_Hood.SK_Hood", ClassOf)!);

        var built = rig.Build();
        Assert.DoesNotContain(built.Warnings, w => w.Contains("edits skipped", StringComparison.Ordinal));
        var preset = Assert.Single(built.Packages, p => p.PackagePath == NewPreset);
        Assert.DoesNotContain(VehicleAttachmentEdits.ClassPath(Attachments + "/BPC_New_Door_FrontLeft"), VehicleParts.Spawned(CookedPackage.Parse(preset.Bytes.UAsset, preset.Bytes.UExp, null, NewPreset)));
        var part = Assert.Single(built.Packages, p => p.PackagePath == Attachments + "/BPC_New_Door_FrontLeft");
        Assert.Equal("/Game/T/SK_Hood.SK_Hood", VehicleAttachmentEdits.MeshOf(CookedPackage.Parse(part.Bytes.UAsset, part.Bytes.UExp, null, part.PackagePath)));
    }

    [Fact]
    public async Task AddingAPartIsOneStepWithItsCloneAndEdits()
    {
        using var rig = await Rig.CreateAsync();
        var (step, package) = rig.Studio().Add(Box, new Vector3(-30, 0, 160), p => rig.Catalog.PackageExists(p));
        Assert.Equal(Attachments + "/BPC_New_CustomA", package);
        var clone = Assert.IsType<CloneAssetOp>(step.Ops[0]);
        Assert.Equal((VehicleAttachmentEdits.PartKind, Attachments + "/BPC_Car_RoofRack"), (clone.Kind, clone.Template));
        Assert.All(step.Ops.Skip(1), o => Assert.IsType<SetAssetValueOp>(o));
        var before = rig.Journal;
        rig.Projects.Apply(step);
        Assert.Equal(before + 1, rig.Journal);
        Assert.Contains(rig.State.AssetClones, c => c.NewPrimary == package);
        Assert.DoesNotContain(rig.Catalog.PackageFiles, f => f.Contains("CustomA", StringComparison.Ordinal));

        // A second part gets the next letters and the next slot.
        var (second, next) = rig.Studio().Add(Box, new Vector3(30, 0, 160), p => rig.Catalog.PackageExists(p));
        Assert.Equal(Attachments + "/BPC_New_CustomB", next);
        Assert.Contains(second.Ops.OfType<SetAssetValueOp>(), o => o.Path == VehicleAttachmentEdits.SlotPath(3));

        var built = rig.Build();
        Assert.DoesNotContain(built.Warnings, w => w.Contains("edits skipped", StringComparison.Ordinal));
        Assert.All(built.Packages, p => Assert.False(rig.Catalog.PackageExists(p.PackagePath), p.PackagePath));
        var preset = Assert.Single(built.Packages, p => p.PackagePath == NewPreset);
        Assert.Contains(VehicleAttachmentEdits.ClassPath(package), VehicleParts.Spawned(CookedPackage.Parse(preset.Bytes.UAsset, preset.Bytes.UExp, null, NewPreset)));

        rig.Projects.Undo();
        Assert.DoesNotContain(rig.State.AssetClones, c => c.NewPrimary == package);
        Assert.Empty(rig.State.GetAssetValues(package));
        Assert.Empty(rig.State.GetAssetValues(NewMesh));
    }

    private static string NewPreset => PresetPath.Replace("CarManual", "NewManual", StringComparison.Ordinal);

    /// <summary>Equality of vectors within 1e-3 (a transform round trip through quaternions).</summary>
    private sealed class ToleranceComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) < 1e-3f;

        public int GetHashCode(Vector3 obj) => 0;
    }

    /// <summary>A synthetic WolfsWagen-like car, a project with its clone "New" (own mesh, attachments, preset).</summary>
    private sealed class Rig : IDisposable
    {
        private readonly AppTestContext _ctx;
        private readonly LevelTempDirectory _temp;

        private Rig(AppTestContext ctx, LevelTempDirectory temp, AssetCatalog catalog, CloneAssetOp clone)
        {
            (_ctx, _temp, Catalog, Clone) = (ctx, temp, catalog, clone);
        }

        public AssetCatalog Catalog { get; }

        public CloneAssetOp Clone { get; }

        public ScumStudio.App.Services.ProjectSession Projects => _ctx.Services.Projects;

        public EditState State => Projects.Current!.State;

        public int Journal => Projects.Current!.History.Count;

        public static async Task<Rig> CreateAsync()
        {
            var temp = new LevelTempDirectory();
            async Task Write(string package, PackageBytes bytes) => await bytes.WriteAsync(temp.Combine(["SCUM", "Content", .. package["/Game/".Length..].Split('/')]));
            await Write(Folder + "/BPC_Car", Vehicle());
            await Write(MeshPath, Mesh(withSocket: false));
            await Write(SkeletonPath, Skeleton());
            await Write(Attachments + "/BPC_Car_Body_Top", Part("Body_Top", "s_Body_Top", "/Game/T/SK_Top.SK_Top", [Class("BPC_Car_RoofRack")], [Class("BPC_Car_Door_FrontLeft")]));
            await Write(Attachments + "/BPC_Car_RoofRack", Part("RoofRack", "s_Body_Top", "/Game/T/SM_Rack.SM_Rack"));
            await Write(Attachments + "/BPC_Car_Door_FrontLeft", Part("Door_FrontLeft", "s_Door_FrontLeft", "/Game/T/SK_Door.SK_Door"));
            await Write(Attachments + "/BPC_Car_Wheel_FrontLeft", Part("Wheel_FrontLeft", "b_Wheel_FrontLeft", "/Game/T/SM_Wheel.SM_Wheel"));
            await Write(PresetPath, Preset("Body_Top", "RoofRack", "Door_FrontLeft"));
            var catalog = AssetCatalog.OpenLoose(temp.Path);

            var pairs = new[] { "BPC_Car_Body_Top", "BPC_Car_RoofRack", "BPC_Car_Door_FrontLeft", "BPC_Car_Wheel_FrontLeft" }
                .Select(l => new PackagePair($"{Attachments}/{l}", $"{Attachments}/{l.Replace("Car", "New", StringComparison.Ordinal)}"))
                .Prepend(new PackagePair(MeshPath, NewMesh))
                .Prepend(new PackagePair(Folder + "/BPC_Car", Folder + "/BPC_New"))
                .Append(new PackagePair(PresetPath, NewPreset))
                .ToList();
            var clone = new CloneAssetOp("vehicle", Folder + "/BPC_Car", Folder + "/BPC_New", pairs);
            var ctx = AppTestContext.Create();
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Parts");
            ctx.Services.Projects.Apply(clone);
            return new Rig(ctx, temp, catalog, clone);
        }

        /// <summary>A package as the mod starts from: stock, or the rename-clone of its template (what the Vehicles page reads).</summary>
        public CookedPackage? Read(string path)
        {
            if (State.FindCloneOf(path) is { } owner)
            {
                var pair = owner.Packages.First(p => string.Equals(p.New, path, StringComparison.OrdinalIgnoreCase));
                var map = new PackageMap(owner.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
                var cloned = PackageCloner.Clone(ModdableAssets.ReadPackage(Catalog, pair.Old), pair.Old, map);
                return CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, path);
            }

            return Catalog.PackageExists(path) ? ModdableAssets.ReadPackage(Catalog, path) : null;
        }

        public PartsStudio Studio() => new(Clone, Read, State);

        public AssetModBuildResult Build() => AssetModBuilder.Build(Catalog, ProjectExporter.BuildAssetRequest(State));

        public void Dispose()
        {
            Catalog.Dispose();
            _ctx.Dispose();
            _temp.Dispose();
        }

        private static PackageBytes Vehicle()
        {
            var p = new SyntheticLevelPackage();
            var mesh = p.ObjectImport(MeshPath, Engine, "SkeletalMesh", "SK_Car");
            var cls = p.ClassExport("BPC_Car_C", p.ScriptClass(Engine, "BlueprintGeneratedClass"), p.ScriptClass("/Script/SCUM", "Vehicle"));
            var cdo = p.Export("Default__BPC_Car_C", p.ScriptClass(Engine, "Actor"), 0, flags: 0x39);
            p.SetPayload(cls, p.Properties());
            p.SetPayload(cdo, p.Properties(t => t.Object("SkeletalMesh", mesh)));
            return p.Build();
        }
    }
}
