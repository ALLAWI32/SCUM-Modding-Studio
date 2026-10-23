using CUE4Parse.UE4.Assets.Exports;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The game's own files: a searchable mesh asset carries loot of its own (Discord, redriver6155, 3.5: "default and custom
/// loot merged. it keeps both"), its loot-free copy carries none and is the same model, loot changed on a stock crate is
/// all the exported crate gives, and a police station placed with "No loot inside" (defolt_0999) has no loot in any desk,
/// cabinet, locker or loot point, and its level still loads. Real game files only (<c>SCUM_PAKS</c>; the server's cook
/// with <c>SCUM_SERVER_PAKS</c>).
/// </summary>
public sealed class LootOverrideRealTests(ITestOutputHelper output)
{
    private const string Samobor = "/Game/ConZ_Files/Maps/The_Island/D_4_Samobor_02_PoliceStation";
    private const string Station = "BP_Police_Station_2a_NoLadder_3";
    private const string MilitaryBase = "/Game/ConZ_Files/Maps/The_Island/A_4_Military_Base";
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Tarp = "SM_Crate_Tarp_88"; // a mesh actor whose only loot is its mesh's (SM_Crate_Tarp_03: Examine_Crate_Tarp_Camo)
    private const string Dumpster = "SM_Dumpster_Green_Open_Trash_21"; // stores Examine_Trash_Container; its mesh carries Examine_Trash_Container_Green

    private static readonly string[] Meshes =
    [
        "/Game/ConZ_Files/Models/Objects/Indoor/Storage/Locker/SM_Locker_closed_Military.SM_Locker_closed_Military",
        "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Big_Closet_Closed/SM_Big_Closet_Closed_05.SM_Big_Closet_Closed_05",
        "/Game/ConZ_Files/Models/Objects/Outdoor/Military/Crate_Tarp/SM_Crate_Tarp_03.SM_Crate_Tarp_03",
    ];

    private static AssetCatalog Open(string paks, params string[] overlays) =>
        AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore(), LooseOverlays = overlays });

    private static readonly Cue4ParseLevelReaderOptions Plain = new() { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };

    [PaksFact]
    public async Task TheGamesSearchableMeshesCarryLootAndTheirCopiesAreTheSameModelWithout()
    {
        using var catalog = Open(RealPaks.Folder!);
        var meshes = MeshLoot.FromCatalog(catalog);
        using var temp = new LevelTempDirectory();
        foreach (var mesh in Meshes)
        {
            var stock = meshes.Of(mesh);
            output.WriteLine($"{mesh}: {string.Join(", ", stock.Presets)}");
            Assert.True(stock.HasExamineData && stock.Complete && stock.Presets.Count > 0, mesh);
            Assert.All(stock.Presets, p => Assert.Contains("/SpawnerPresets2/", p, StringComparison.Ordinal));

            var package = ModdableAssets.ReadPackage(catalog, mesh[..mesh.IndexOf('.')]);
            var copy = MeshLoot.BuildCopy(package, mesh[..mesh.IndexOf('.')]);
            Assert.Equal(MeshLoot.CopyPath(mesh[..mesh.IndexOf('.')]), copy.NewPackagePath);
            Assert.True(catalog.TryGetPackageFile(mesh, out var file));
            var stockFile = file.Path.Replace('\\', '/');
            var target = temp.Combine([.. (stockFile[..(stockFile.LastIndexOf('/') + 1)] + copy.NewPackagePath[(copy.NewPackagePath.LastIndexOf('/') + 1)..]).Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await copy.Bytes.WriteAsync(target, ".uasset");
            if (copy.UBulk is not null)
            {
                File.WriteAllBytes(target + ".ubulk", copy.UBulk);
            }
        }

        // Read back as the game's loader reads them (CUE4Parse, the mod's files over the game's): the same LODs, vertices,
        // triangles, materials and collision, and no loot.
        using var written = Open(RealPaks.Folder!, temp.Path);
        var copies = MeshLoot.FromCatalog(written);
        foreach (var mesh in Meshes)
        {
            var copyPath = MeshLoot.CopyPath(mesh);
            var copyInfo = MeshLoot.Read(ModdableAssets.ReadPackage(written, copyPath[..copyPath.IndexOf('.')]));
            Assert.False(copyInfo.HasExamineData, copyPath);
            Assert.True(copies.Of(mesh).HasExamineData, "the stock mesh keeps its loot");
            var stockMesh = written.LoadObject(mesh);
            var copyMesh = written.LoadObject(copyPath);
            var (a, b) = (MeshExtractor.Describe(stockMesh), MeshExtractor.Describe(copyMesh));
            Assert.Equal(a.Lods.Select(l => (l.VertexCount, l.TriangleCount)), b.Lods.Select(l => (l.VertexCount, l.TriangleCount)));
            Assert.Equal(a.Materials.Select(m => m.MaterialPath), b.Materials.Select(m => m.MaterialPath));
            Assert.Equal(a.Bounds.Min, b.Bounds.Min);
            Assert.Equal(a.Bounds.Max, b.Bounds.Max);
            Assert.Equal(
                MeshCollision.Read((CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh)stockMesh)?.Boxes.Count,
                MeshCollision.Read((CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh)copyMesh)?.Boxes.Count);
            Assert.DoesNotContain(((UObject)copyMesh).GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("AssetUserData") ?? [],
                e => e.Load()?.Class?.Name == "ExamineAssetData");
            output.WriteLine($"{copyPath}: {b.Lods.Count} LODs, {b.Lods[0].VertexCount} vertices, no loot");
        }

        // The presets read from a mesh are named as the level reader names a component's: the police locker stores the
        // Prison folder's Examine_Weapon_Locker_Lockpick, its mesh carries the TV bunker's preset of the same name.
        var samobor = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), Samobor);
        var locker = samobor.Actors.Where(a => a.IsItemContainer).SelectMany(a => a.Components).First(c => c.StaticMeshPath == Meshes[1] && c.LootPresets.Count > 0);
        var (fromMesh, fromLevel) = (meshes.Of(Meshes[1]).Presets[0], locker.LootPresets[0]);
        output.WriteLine($"mesh {fromMesh}; level {fromLevel}");
        Assert.Equal(fromMesh[(fromMesh.LastIndexOf('/') + 1)..], fromLevel[(fromLevel.LastIndexOf('/') + 1)..]);
        Assert.StartsWith("/Game/ConZ_Files/Items/SpawnerPresets2/Buildings/Armory/", fromMesh, StringComparison.Ordinal);
        Assert.StartsWith("/Game/ConZ_Files/Items/SpawnerPresets2/Buildings/Armory/", fromLevel, StringComparison.Ordinal);
    }

    [PaksFact]
    public async Task LootChangedOnStockCratesIsAllTheExportedCratesGive()
    {
        using var catalog = Open(RealPaks.Folder!);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), MilitaryBase);
        var meshes = MeshLoot.FromCatalog(catalog);
        var tarp = document.FindActor(Tarp)!;
        var dumpster = document.FindActor(Dumpster)!;
        Assert.Empty(tarp.Root!.LootPresets); // its loot is its mesh's only
        Assert.True(meshes.Of(tarp.Root.StaticMeshPath).HasExamineData);
        Assert.NotEmpty(dumpster.Root!.LootPresets);
        Assert.NotEqual(meshes.Of(dumpster.Root.StaticMeshPath).Presets, dumpster.Root.LootPresets); // stored: another preset than its mesh's
        var weapon = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), Samobor).Actors.Where(a => a.IsItemContainer)
            .SelectMany(a => a.Components).First(c => c.LootPresets.Count > 1).LootPresets[1];

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Loot.ssproj"), "Loot");
        var tarpLoot = new LootSetting(true, meshes.Of(tarp.Root.StaticMeshPath).Presets);
        project.Apply(new SetLootOp(new ActorRef(MilitaryBase, Tarp), null, tarpLoot, new LootSetting(true, [weapon])));
        project.Apply(new SetLootOp(new ActorRef(MilitaryBase, Dumpster), null, new LootSetting(true, dumpster.Root.LootPresets), new LootSetting(false, [])));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        foreach (var warning in result.Warnings)
        {
            output.WriteLine("warning: " + warning);
        }

        Assert.NotNull(result.Validation); // the package check read every staged package back
        Assert.Equal(2, result.Assets.Count(a => MeshLoot.IsCopy(a.PackagePath)));
        using var written = Open(RealPaks.Folder!, result.StagingDirectory);
        var read = LevelDocument.Load(new Cue4ParseLevelReader(written, Plain), MilitaryBase);
        var writtenMeshes = MeshLoot.FromCatalog(written);
        foreach (var (name, loot) in new[] { (Tarp, new[] { weapon }), (Dumpster, Array.Empty<string>()) })
        {
            var root = read.FindActor(name)!.Root!;
            output.WriteLine($"{name}: {root.StaticMeshPath} [{string.Join(", ", root.LootPresets)}]");
            Assert.Equal(loot, root.LootPresets);
            Assert.True(MeshLoot.IsCopy(root.StaticMeshPath!), root.StaticMeshPath);
            Assert.False(MeshLoot.Read(ModdableAssets.ReadPackage(written, root.StaticMeshPath![..root.StaticMeshPath!.IndexOf('.')])).HasExamineData);
            Assert.True(written.PackageExists(root.StaticMeshPath));
        }

        // Every other searchable thing of the base is as the game has it.
        var others = document.Actors.Where(a => a.Name != Tarp && a.Name != Dumpster).SelectMany(a => a.Components).Where(c => c.LootPresets.Count > 0).ToList();
        Assert.All(others, c => Assert.False(MeshLoot.IsCopy(read.Actors.SelectMany(a => a.Components).First(r => r.ExportIndex == c.ExportIndex).StaticMeshPath ?? string.Empty)));
        Assert.All(others, c => Assert.Equal(meshes.Of(c.StaticMeshPath).Presets, writtenMeshes.Of(c.StaticMeshPath).Presets));

        // The report says why the copies are there.
        var report = await File.ReadAllTextAsync(Directory.EnumerateFiles(temp.Combine("out"), ProjectExporter.ReportFileName, SearchOption.AllDirectories).Single());
        Assert.Contains("## Loot", report, StringComparison.Ordinal);
        Assert.Contains("_NoLoot.uasset", report, StringComparison.Ordinal);
    }

    [PaksFact]
    public async Task APoliceStationPlacedWithNoLootInsideHasNoneAndItsLevelLoads()
    {
        using var catalog = Open(RealPaks.Folder!);
        var source = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), Samobor);
        var station = source.FindActor(Station)!;
        var lockers = source.Actors.Where(a => a.ParentComponent is { } p && station.Components.Any(c => c.ExportIndex == p) && a.IsItemContainer).ToList();
        var meshes = MeshLoot.FromCatalog(catalog);
        var searchable = station.Components.Count(c => !c.IsSynthesized && c.IsStaticMeshComponent && (c.LootPresets.Count > 0 || meshes.Of(c.StaticMeshPath).HasExamineData));
        var points = station.Components.Where(c => !c.IsSynthesized && c.SpawnMarkers.Count > 0 && c.SpawnMarkers.All(m => m.ItemClassPath is null)).Sum(c => c.SpawnMarkers.Count);
        output.WriteLine($"{Station}: {searchable} searchable part(s), {lockers.Count} locker(s), {points} loot point(s)");
        Assert.True(searchable >= 20 && lockers.Count >= 8 && points > 0);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("NoLoot.ssproj"), "NoLoot");
        var at = new ScumStudio.Core.Mathematics.FVector(320_000f, -488_700f, 10_550f);
        var placed = new AddBlueprintActorOp(Farm, Station + "_Placed", station.ClassPath, new ActorRef(Samobor, Station),
            TransformValue.FromTransform(station.WorldTransform with { Translation = at }));
        project.Apply(new BatchOp("Place", [placed, new SetNoLootOp(placed.Created, false, true)]));
        var plain = new AddBlueprintActorOp(Farm, Station + "_WithLoot", station.ClassPath, new ActorRef(Samobor, Station),
            TransformValue.FromTransform(station.WorldTransform with { Translation = at + new ScumStudio.Core.Mathematics.FVector(6000f, 0f, 0f) }));
        project.Apply(plain);

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("client"), WritePak = false });
        foreach (var warning in result.Warnings)
        {
            output.WriteLine("warning: " + warning);
        }

        Assert.NotNull(result.Validation);
        Assert.Contains(result.Warnings, w => w.Contains($"{Station}_Placed", StringComparison.Ordinal) && w.Contains("no loot inside", StringComparison.Ordinal));
        Assert.Equal(searchable, AssertNoLoot(RealPaks.Folder!, result.StagingDirectory, lockers.Count, "client"));

        // The dedicated server's cook gives the same, from its own meshes.
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
        {
            using var server = Open(serverPaks);
            var cooked = await new ProjectExporter().ExportAsync(project, server,
                new ExportOptions { OutputDirectory = temp.Combine("server"), WritePak = false, ClientCatalog = catalog }, ProjectSourceRole.Server);
            foreach (var warning in cooked.Warnings.Where(w => w.Contains("loot", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine("server warning: " + warning);
            }

            Assert.NotNull(cooked.Validation);
            Assert.True(AssertNoLoot(serverPaks, cooked.StagingDirectory, lockers.Count, "server") > 0);
        }

        // Returns how many searchable parts the copy placed without the option keeps.
        int AssertNoLoot(string paks, string staging, int lockerCount, string cook)
        {
            using var written = Open(paks, staging);
            var farm = LevelDocument.Load(new Cue4ParseLevelReader(written, Plain), Farm);
            var copy = farm.FindActor(Station + "_Placed")!;
            var withLoot = farm.FindActor(Station + "_WithLoot")!;
            var copyLockers = farm.Actors.Where(a => a.ParentComponent is { } p && copy.Components.Any(c => c.ExportIndex == p) && a.IsItemContainer).ToList();
            Assert.Equal(lockerCount, copyLockers.Count);
            var copyParts = copy.Components.Concat(copyLockers.SelectMany(l => l.Components)).Where(c => !c.IsSynthesized && c.IsStaticMeshComponent).ToList();
            var writtenMeshes = MeshLoot.FromCatalog(written);
            var looted = copyParts.Where(c => c.LootPresets.Count > 0 || writtenMeshes.Of(c.StaticMeshPath).HasExamineData).Select(c => c.Name).ToList();
            Assert.True(looted.Count == 0, "still searchable: " + string.Join(", ", looted));
            Assert.True(copyParts.Count(c => MeshLoot.IsCopy(c.StaticMeshPath ?? string.Empty)) > 0);
            Assert.All(copy.Components.Where(c => c.SpawnMarkers.Count > 0), c => Assert.All(c.SpawnMarkers, m => Assert.NotNull(m.ItemClassPath)));
            Assert.Contains(copy.Components, c => c.ClassName.Contains("ItemSpawner", StringComparison.Ordinal) && c.SpawnMarkers.Count == 0);

            // The same building placed without the option keeps every bit of its loot.
            var kept = withLoot.Components.Where(c => !c.IsSynthesized && c.IsStaticMeshComponent && (c.LootPresets.Count > 0 || writtenMeshes.Of(c.StaticMeshPath).HasExamineData)).Count();
            Assert.DoesNotContain(withLoot.Components, c => MeshLoot.IsCopy(c.StaticMeshPath ?? string.Empty));
            output.WriteLine($"{cook}: {copyParts.Count} mesh part(s) in the copy and its lockers, none searchable; {kept} searchable in the plain copy");
            return kept;
        }
    }
}
