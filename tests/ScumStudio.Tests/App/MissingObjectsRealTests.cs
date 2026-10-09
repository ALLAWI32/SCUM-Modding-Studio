using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord: objects that are in the game were missing from the map (a green wheelie bin, chairs) and "some static meshes
/// can't be exported". SCUM cooks some meshes with LOD 0 stripped, which failed to load; the abandoned city's levels were
/// never loaded at all. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class MissingObjectsRealTests
{
    private const string Island = "/Game/ConZ_Files/Maps/The_Island/";
    private const string Chair = "/Game/ConZ_Files/Models/Objects/Items/Halloween/SM_Chair";
    private const string CityHall = Island + WorldNameParser.PripyatFolder + "/C_0_AbandonedCity_02_CityHall_Ext";

    [Fact]
    public async Task MeshesWithoutLod0AreDrawnAndExport()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var chair = catalog.LoadObject(Chair + ".SM_Chair");
        var info = MeshExtractor.Describe(chair);
        Assert.True(info.Lods[0].IsStripped);
        Assert.Equal(3, MeshExtractor.ExtractLods(chair).Lods.Count);
        Assert.Equal(info.Lods[1].VertexCount, MeshExtractor.Extract(chair).VertexCount);

        // The Assets page's glTF export (it threw "LOD 0 has no render data").
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-chair-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = await AssetExportService.ExportMeshGltfAsync(catalog, Chair, Path.Combine(dir, "SM_Chair.gltf"));
            Assert.Contains(files, f => f.EndsWith(".gltf", StringComparison.Ordinal));
            Assert.All(files, f => Assert.True(new FileInfo(f).Length > 0, f));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // The airfield outpost's hangar chairs (9 instances) and everything else there is drawn.
        var reader = new Cue4ParseLevelReader(catalog);
        var preparer = new LevelScenePreparer(catalog);
        var airfield = preparer.Prepare([LevelDocument.Load(reader, Island + "A_0_Outpost_Airfield")], new LevelSceneOptions { TextureSize = 0 });
        Assert.Empty(airfield.MissingMeshes);
        Assert.Equal(airfield.RequestedPlacements, airfield.Placements.Count);
        Assert.Equal(9, airfield.Placements.Count(p => p.MeshPath == Chair + ".SM_Chair"
            && p.Name.StartsWith("BP_Airplane_Hangar2/HierarchicalInstancedStaticMesh1[", StringComparison.Ordinal)));

        // Prigradica's green wheelie bin.
        var town = preparer.Prepare([LevelDocument.Load(reader, Island + "A_2_Prigradica")], new LevelSceneOptions { TextureSize = 0 });
        Assert.Contains(town.Placements, p => p.Actor.Name == "SM_WheelieBin_01_Green_694" && p.MeshPath.EndsWith(".SM_WheelieBin_01b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAbandonedCityStreamsInAndItsEditsExport()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);

        // Flying over the city hall (and picking cell C_0) loads the abandoned city's levels.
        var hall = world.Packages.Single(p => p.PackagePath == CityHall).Tile!;
        var camera = (hall.BoundsMin + hall.BoundsMax) * 0.5f;
        var around = MapPageViewModel.LevelsAround(world, camera, 10_000f);
        Assert.Contains(CityHall, around);
        Assert.Contains(Island + WorldNameParser.PripyatFolder + "/C_0_AbandonedCity_00", around);
        Assert.True(MapCell.TryParse("C_0", out var c0));
        Assert.Contains(CityHall, MapPageViewModel.CellPackages(world, c0));

        // Move the city hall 10 m and copy it 400 m away (outside the level's own area), then export.
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), CityHall);
        var building = document.Actors.Single(a => a.Name == "BP_AC_City_Hall_2");
        var start = TransformValue.FromTransform(building.WorldTransform);
        var moved = start with { Location = start.Location + new FVector(1_000f, 0f, 0f) };
        var copyAt = start with { Location = start.Location + new FVector(40_000f, 0f, 0f) };
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-city-" + Guid.NewGuid().ToString("N"));
        try
        {
            string copyName;
            using (var project = await Project.CreateAsync(Path.Combine(dir, "project"), "City"))
            {
                project.Apply(EditOpFactory.SetTransform(document, building, moved, project.State));
                var copy = EditOpFactory.Duplicate(document, building, copyAt, project.State);
                project.Apply(copy);
                copyName = copy.NewName;
            }

            // The journal keeps the abandoned city's level paths.
            using var reopened = await Project.OpenAsync(Path.Combine(dir, "project"));
            var result = await new ProjectExporter().ExportAsync(reopened, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            var level = Assert.Single(result.Levels, l => l.PackagePath == CityHall);
            // Besides the hall's level, only landscape tiles whose grass and bushes the copy clears (no actor added or removed there).
            Assert.All(result.Levels.Where(l => !ReferenceEquals(l, level)), l =>
            {
                Assert.Contains("/Landscape_", l.PackagePath, StringComparison.Ordinal);
                Assert.Equal(l.Report.ActorsBefore, l.Report.ActorsAfter);
            });
            Assert.Equal(CityHall, level.PackagePath);
            Assert.EndsWith("/" + WorldNameParser.PripyatFolder + "/C_0_AbandonedCity_02_CityHall_Ext.umap", level.VirtualPath, StringComparison.Ordinal);
            Assert.NotEmpty(result.FarModels); // the hall is cut out of the city's far view

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var writtenLevel = LevelDocument.Load(new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false }), CityHall);
            Assert.Equal(moved.Location.X, writtenLevel.Actors.Single(a => a.Name == building.Name).WorldTransform.Translation.X, 1f);
            Assert.Equal(copyAt.Location.X, writtenLevel.Actors.Single(a => a.Name == copyName).WorldTransform.Translation.X, 1f);

            // The level streams in where the copy stands.
            var file = Directory.EnumerateFiles(result.StagingDirectory, "C_0_AbandonedCity_02_CityHall_Ext.umap", SearchOption.AllDirectories).Single();
            var grown = WorldTileInfo.TryRead(CookedPackage.Load(file))!;
            Assert.True(grown.BoundsMax.X >= copyAt.Location.X, $"{grown.BoundsMax.X} < {copyAt.Location.X}");
            Assert.Equal(hall.BoundsMin.X, grown.BoundsMin.X, 1f);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
