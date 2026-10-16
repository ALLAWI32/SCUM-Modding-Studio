using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Parts like Blender on the stock WolfsWagen files of the fixture archive (<c>SCUM_FIXTURES</c>: loose cooked
/// <c>orig/</c>, no key needed): where the part sockets live, a door moved through a socket of a renamed copy of the
/// car's mesh, a door showing another model; read back with ours and with CUE4Parse.
/// </summary>
public sealed class PartsFixtureTests(ITestOutputHelper output)
{
    private const string Meshes = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Meshes/";
    private const string Vehicle = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen";
    private const string Attachments = "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/Attachments/";

    [FixturesFact]
    public void ThePartSocketsLiveOnTheSkeletonNotOnTheMesh()
    {
        using var catalog = Open();
        var vehicle = ModdableAssets.ReadPackage(catalog, Vehicle);
        Assert.Equal(Meshes + "SK_WolfsWagen", VehicleSockets.ImportedPackage(vehicle, "SkeletalMesh"));
        var mesh = ModdableAssets.ReadPackage(catalog, Meshes + "SK_WolfsWagen");
        Assert.Equal(Meshes + "SK_WolfsWagen_Skeleton", VehicleSockets.ImportedPackage(mesh, "Skeleton"));
        Assert.Empty(VehicleSockets.Read(mesh));
        Assert.Null(mesh.ReadProperties(VehicleSockets.MeshExport(mesh)).Find("Sockets"));

        var skeleton = ModdableAssets.ReadPackage(catalog, Meshes + "SK_WolfsWagen_Skeleton");
        var sockets = VehicleSockets.Read(skeleton);
        output.WriteLine($"{sockets.Count} skeleton sockets: {string.Join(", ", sockets.Select(s => $"{s.Name}@{s.Bone}"))}");
        var door = Assert.Single(sockets, s => s.Name == "s_Door_FrontLeft");
        Assert.Equal("Root", door.Bone);
        Assert.InRange(door.Location.X, 87.72f, 87.74f);
        Assert.All(sockets, s => Assert.Equal("Root", s.Bone));
        Assert.DoesNotContain(sockets, s => s.Name.StartsWith("b_Wheel", StringComparison.Ordinal)); // wheels hang on bones

        // What the cooker lists for a socket export (VehicleSockets.Apply writes the same shape).
        var index = Enumerable.Range(0, skeleton.Exports.Count).First(i => skeleton.GetExportClassName(i) == "SkeletalMeshSocket");
        output.WriteLine(skeleton.DescribeDependencies(index));
        var wheel = ModdableAssets.ReadPackage(catalog, Attachments + "BPC_WolfsWagen_Wheel_FrontLeft");
        Assert.Equal("b_Wheel_FrontLeft", VehicleAttachmentEdits.ParentSocketOf(wheel));
    }

    [FixturesFact]
    public void TheVehicleMeshIsInTheFamilyOnlyWhenItWearsPaint()
    {
        using var catalog = Open();
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, Vehicle, "PartsProbe");
        var models = plan.Packages.Where(p => p.Key.Contains("/Models/", StringComparison.Ordinal)).ToList();
        output.WriteLine("Models in the family: " + (models.Count == 0 ? "none" : string.Join(", ", models.Select(m => m.Key))));
        var mesh = ModdableAssets.ReadPackage(catalog, Meshes + "SK_WolfsWagen");
        output.WriteLine("SK_WolfsWagen imports: " + string.Join(", ", ModdableAssets.ReadImportedPackages(catalog, Meshes + "SK_WolfsWagen")));
        var paints = CloneFamilyPlanner.PaintPackages(catalog, [Vehicle]);
        Assert.Equal(paints.Contains(Meshes + "SK_WolfsWagen", StringComparer.OrdinalIgnoreCase), models.Any(m => m.Key == Meshes + "SK_WolfsWagen"));
        Assert.DoesNotContain(models, m => m.Key == Meshes + "SK_WolfsWagen_Skeleton"); // the skeleton (and the anims bound to it) stays shared
        Assert.NotNull(mesh);
    }

    [FixturesFact]
    public async Task TheFrontLeftDoorMovesTenCentimetresOnARenamedCopyOfTheMesh()
    {
        using var catalog = Open();
        using var temp = new LevelTempDirectory();
        const string copyPath = Meshes + "SK_PartsProbe";
        var stock = ModdableAssets.ReadPackage(catalog, Meshes + "SK_WolfsWagen");
        var cloned = PackageCloner.Clone(stock, Meshes + "SK_WolfsWagen", new PackageMap([new(Meshes + "SK_WolfsWagen", copyPath)]));
        var copy = CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, copyPath);
        var door = Assert.Single(VehicleSockets.Read(ModdableAssets.ReadPackage(catalog, Meshes + "SK_WolfsWagen_Skeleton")), s => s.Name == "s_Door_FrontLeft");
        var moved = door.Location + new Vector3(10, 0, 0);

        var bytes = VehicleSockets.Apply(copy, VehicleSockets.Override("SK_PartsProbe", door, moved, door.Rotation, door.Scale));
        var after = CookedPackage.Parse(bytes.UAsset, bytes.UExp, cloned.UBulk, copyPath);
        var own = Assert.Single(VehicleSockets.Read(after));
        Assert.Equal((moved, true), (own.Location, own.OnMesh));
        Assert.Equal(stock.GetExportBytes(VehicleSockets.MeshExport(stock)).Length + 41, after.GetExportBytes(VehicleSockets.MeshExport(after)).Length);

        // CUE4Parse reads the whole mesh (the native LOD data after the grown property block stays aligned) and resolves
        // its Sockets to the override.
        var basePath = temp.Combine(["SCUM", "Content", .. copyPath["/Game/".Length..].Split('/')]);
        await bytes.WriteAsync(basePath);
        if (cloned.UBulk is { } ubulk)
        {
            await File.WriteAllBytesAsync(basePath + ".ubulk", ubulk);
        }

        using var loose = AssetCatalog.OpenLoose(temp.Path);
        var read = loose.LoadObject<USkeletalMesh>(copyPath + ".SK_PartsProbe");
        var original = catalog.LoadObject<USkeletalMesh>(Meshes + "SK_WolfsWagen.SK_WolfsWagen");
        Assert.Equal(original.LODModels?.Length, read.LODModels?.Length);
        Assert.Equal(MeshExtractor.Extract(original, 0).TriangleCount, MeshExtractor.Extract(read, 0).TriangleCount);
        var socket = Assert.Single(read.GetOrDefault<FPackageIndex[]>("Sockets") ?? []).Load<UObject>()!;
        Assert.Equal("s_Door_FrontLeft", socket.GetOrDefault<FName>("SocketName").Text);
        Assert.Equal("Root", socket.GetOrDefault<FName>("BoneName").Text);
        Assert.Equal(moved.X, socket.GetOrDefault<FVector>("RelativeLocation").X, 3);
        output.WriteLine($"s_Door_FrontLeft {door.Location} -> {moved} on {copyPath} (skeleton untouched)");
    }

    [FixturesFact]
    public async Task TheFrontLeftDoorShowsAnotherDoorButNotARoofRack()
    {
        using var catalog = Open();
        using var temp = new LevelTempDirectory();
        const string door = Attachments + "BPC_WolfsWagen_Door_FrontLeft";
        var part = ModdableAssets.ReadPackage(catalog, door);
        Assert.Equal(Meshes + "SK_WolfsWagen_Door_FrontLeft.SK_WolfsWagen_Door_FrontLeft", VehicleAttachmentEdits.MeshOf(part));
        string? ClassOf(string path) => catalog.PackageExists(VehicleParts.PackageOf(path))
            ? catalog.GetExports(VehicleParts.PackageOf(path)).FirstOrDefault(e => e.ClassName is "StaticMesh" or "SkeletalMesh")?.ClassName
            : null;
        Assert.Throws<InvalidOperationException>(() => VehicleAttachmentEdits.SwapMesh(part, Meshes + "SM_WolfsWagen_RoofRack.SM_WolfsWagen_RoofRack", ClassOf));
        var other = Meshes + "SK_WolfsWagen_Door_BackLeft.SK_WolfsWagen_Door_BackLeft";
        var edit = VehicleAttachmentEdits.SwapMesh(part, other, ClassOf);

        var bytes = VehicleParts.Apply(part, [edit]);
        var after = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, door);
        Assert.Equal(other, VehicleAttachmentEdits.MeshOf(after));
        Assert.Equal(VehicleAttachmentEdits.ParentSocketOf(part), VehicleAttachmentEdits.ParentSocketOf(after));
        Assert.Equal(VehicleAttachmentEdits.SlotsOf(part).Select(s => string.Join(",", s)), VehicleAttachmentEdits.SlotsOf(after).Select(s => string.Join(",", s)));

        await bytes.WriteAsync(temp.Combine(["SCUM", "Content", .. door["/Game/".Length..].Split('/')]));
        using var loose = AssetCatalog.OpenLoose(temp.Path);
        var cdo = loose.LoadPackage(door).GetExportOrNull("Default__BPC_WolfsWagen_Door_FrontLeft_C", StringComparison.Ordinal);
        Assert.NotNull(cdo);
        Assert.Equal(other, cdo.GetOrDefault<FStructFallback>("MeshSetup")?.GetOrDefault<FSoftObjectPath>("Mesh").AssetPathName.Text);
    }

    private static AssetCatalog Open() =>
        AssetCatalog.OpenLoose(FixturePaths.OrigRoot, new AssetCatalogOptions { LooseOverlays = [FixturePaths.OrigExtraRoot] });
}
