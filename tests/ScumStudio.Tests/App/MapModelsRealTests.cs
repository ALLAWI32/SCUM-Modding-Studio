using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Import;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;
using ScumStudio.Tests.Level;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.App;

/// <summary>
/// An imported 3D model on the real island (Pheidian: "import custom models into the editor"). This PC has no Unreal Engine
/// 4.27, so the model is a stand-in made without it: the game's cooked armory table cloned to an import's path
/// (<c>/Game/ScumStudio/Imports/SS_RealTable/SM_SS_RealTable</c>). It proves the studio's side (library, placement, the
/// level written against the game's real level, the model shipped and read back at its own path, the same model as a
/// craftable); whether the game loads a mesh cooked by UE 4.27 there only the game can show.
/// </summary>
public sealed class MapModelsRealTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Level = MapSlice.MapsPath + "A_0_Outpost_Ext_Saloon";

    [PaksFact]
    public async Task APlacedModelIsShippedWithItsLevelAndIsTheSameModelAsACraftable()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Models.ssproj"), "Models");

        // The stand-in import: the cooked table renamed into the project's imports folder.
        var model = new LibraryModel("SS_RealTable", UeCook.ImportsFolder + "/SS_RealTable");
        Assert.True(catalog.TryGetPackageFile(Table, out var file));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var clone = PackageCloner.Clone(CookedPackage.Parse(uasset, uexp, ubulk, Table), Table, new PackageMap([KeyValuePair.Create(Table, model.MeshPackage)]));
        var stem = Path.Combine(project.DirectoryPath, model.Folder, "SCUM", "Content", "ScumStudio", "Imports", model.Token, "SM_" + model.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        await clone.Bytes.WriteAsync(stem, ".uasset");
        if (clone.UBulk is not null)
        {
            await File.WriteAllBytesAsync(stem + ".ubulk", clone.UBulk);
        }

        Assert.Equal(model, Assert.Single(ModelLibrary.List(project.DirectoryPath)));

        // The Map draws it from its own files (EnsureMeshLoaded), not the game's.
        using (var own = AssetCatalog.OpenLoose(ModelLibrary.FolderOf(project.DirectoryPath, model.MeshObjectPath, out _)!))
        {
            var drawn = new ScumStudio.Viewport.LevelScenePreparer(own).PrepareMesh(model.MeshObjectPath);
            Assert.NotNull(drawn);
            Assert.True(drawn.Asset.Mesh.TriangleCount > 100, drawn.Asset.Mesh.TriangleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Placed next to the saloon, turned and scaled; exported against the game's real level.
        var near = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Level).Actors.First(a => a.StaticMeshPath is not null).WorldTransform.Translation;
        var at = new TransformValue(new FVector(near.X + 300, near.Y, near.Z), new FRotator(0, 30, 0), new FVector(1.5f, 1.5f, 1.5f));
        project.Apply(new AddStaticMeshActorOp(Level, "SM_SS_RealTable_Added", model.MeshObjectPath, at));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        Assert.Contains(result.Assets, a => a.PackagePath == model.MeshPackage);
        ExportSafety.AssertOnlyAdds(catalog, result.Assets.Select(a => a.PackagePath));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("SS_RealTable", StringComparison.Ordinal) && !w.Contains("collision", StringComparison.OrdinalIgnoreCase));
        using (var written = AssetCatalog.OpenLoose(result.StagingDirectory))
        {
            var actor = LevelDocument.Load(new Cue4ParseLevelReader(written), Level).FindActor("SM_SS_RealTable_Added")!;
            Assert.Equal("StaticMeshActor", actor.ClassName);
            Assert.Equal(model.MeshObjectPath, actor.StaticMeshPath);
            Assert.True(actor.Root!.Relative.IsNearlyEqual(at), actor.Root.Relative.ToString());
            Assert.True(written.TryLoadObject<UStaticMesh>(model.MeshObjectPath, out var mesh), "the shipped model does not load at its own path");
            Assert.Equal("SM_" + model.Token, mesh.Name);
        }

        // The same model on the Craftables page: described from its own files, carried by the Craftables pak.
        var craftable = CraftablesPageViewModel.DescribeModel(project.DirectoryPath, model)!;
        Assert.Equal(model.Folder, craftable.Imported);
        Assert.Equal(model.MeshPackage, craftable.Mesh);
        Assert.InRange(craftable.SizeMeters, 0.5f, 5f);
        var crafted = await CraftablesExporter.ExportAsync(new CraftablesFile { Items = [craftable with { Token = CraftablesFile.NewTokenFor(craftable.Name, []) }], ProjectDirectory = project.DirectoryPath },
            "Models", catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false }, ProjectSourceRole.Client);
        Assert.Contains(crafted!.Assets, a => a.PackagePath == model.MeshPackage);
    }
}
