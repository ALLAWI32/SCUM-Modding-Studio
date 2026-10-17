using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Import;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak.Inspection;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Imported 3D models on the Map (Pheidian, 2026-10-13: "Is there a way to import custom models into the editor?"): the
/// project's library, the placement in the journal, and the export that carries the model with its placement.
/// </summary>
public sealed class MapModelsTests
{
    private const string LevelPath = SyntheticLevels.LevelPath;

    /// <summary>
    /// Writes stand-in cooked files of an import (<see cref="UeCook.ImportAsync"/> leaves <c>imports/&lt;token&gt;/SCUM/Content/
    /// ScumStudio/Imports/&lt;token&gt;/</c>): the mesh and a texture. The bytes are not a package; the library and the export
    /// only list and copy them.
    /// </summary>
    internal static LibraryModel WriteModel(string projectDirectory, string token, bool withMesh = true)
    {
        var own = Path.Combine(projectDirectory, UeCook.ImportsFolder, token, "SCUM", "Content", "ScumStudio", "Imports", token);
        Directory.CreateDirectory(own);
        foreach (var stem in withMesh ? new[] { "SM_" + token, "T_" + token + "_D" } : ["T_" + token + "_D"])
        {
            File.WriteAllBytes(Path.Combine(own, stem + ".uasset"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(own, stem + ".uexp"), [4, 5, 6]);
        }

        return new LibraryModel(token, UeCook.ImportsFolder + "/" + token);
    }

    [Fact]
    public void TheLibraryIsEveryImportWithItsCookedMesh()
    {
        using var temp = new LevelTempDirectory();
        var project = temp.Combine("Library.ssproj");
        var table = WriteModel(project, "SS_Wooden_Table");
        WriteModel(project, "SS_Half", withMesh: false); // a cook that stopped half-way
        var outside = WriteModel(project, "SS_Outside");
        var stray = Path.Combine(project, outside.Folder, "SCUM", "Content", "ConZ_Files", "SM_Stray.uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllBytes(stray, [0]); // a hand-made folder that would ship a game path

        var model = Assert.Single(ModelLibrary.List(project));
        Assert.Equal(table, model);
        Assert.Equal("Wooden Table", model.Name);
        Assert.Equal("/Game/ScumStudio/Imports/SS_Wooden_Table/SM_SS_Wooden_Table", model.MeshPackage);
        Assert.Equal("/Game/ScumStudio/Imports/SS_Wooden_Table/SM_SS_Wooden_Table.SM_SS_Wooden_Table", model.MeshObjectPath);
        Assert.Empty(ModelLibrary.List(temp.Combine("NoProject")));
        Assert.Empty(ModelLibrary.List(null));

        Assert.True(ModelLibrary.IsModelMesh(model.MeshObjectPath));
        Assert.False(ModelLibrary.IsModelMesh(SyntheticLevels.RockPackage + ".SM_Rock"));
        Assert.False(ModelLibrary.IsModelMesh(UeCook.ImportsRoot + "/SS_X/Materials/M_X")); // not a model's mesh folder
        Assert.Equal("SS_Wooden_Table", ModelLibrary.TokenOf(model.MeshPackage));
        Assert.Equal(Path.GetFullPath(Path.Combine(project, model.Folder)), ModelLibrary.FolderOf(project, model.MeshObjectPath, out _));
        Assert.Null(ModelLibrary.FolderOf(project, UeCook.ImportsRoot + "/SS_Half/SM_SS_Half.SM_SS_Half", out var half));
        Assert.Contains("no cooked mesh SM_SS_Half", half, StringComparison.Ordinal);
        Assert.Null(ModelLibrary.FolderOf(project, outside.MeshObjectPath, out var stray2));
        Assert.Contains("outside", stray2, StringComparison.Ordinal);
        Assert.Null(ModelLibrary.FolderOf(project, UeCook.ImportsRoot + "/SS_None/SM_SS_None.SM_SS_None", out _));
    }

    [Fact]
    public void APlacedModelIsAJournalEditMovedAndUndoneLikeAnyMesh()
    {
        using var temp = new LevelTempDirectory();
        var directory = temp.Combine("Journal.ssproj");
        const string mesh = "/Game/ScumStudio/Imports/SS_Box/SM_SS_Box.SM_SS_Box";
        var op = new AddStaticMeshActorOp(LevelPath, "SM_SS_Box_Added", mesh, TransformValue.At(1, 2, 3));
        Assert.Equal(op, EditOp.FromJson(op.ToJson()));
        Assert.Equal(op, op.Inverse().Inverse());
        var moved = new TransformValue(new FVector(500, -20, 30), new FRotator(0, 90, 0), new FVector(2, 2, 2));
        using (var project = Project.Create(directory, "Journal"))
        {
            project.Apply(op);
            project.Apply(new SetTransformOp(op.Created, op.Transform, moved)); // the gizmo: moved, turned and scaled
            Assert.Equal(new[] { mesh }, ModelLibrary.UsedOnMap(project.State));
        }

        using var reopened = Project.Open(directory);
        Assert.Equal(moved, reopened.State.GetAddedTransform(op.Created));
        Assert.Equal(new[] { mesh }, ModelLibrary.UsedOnMap(reopened.State));
        reopened.Undo();
        Assert.Equal(op.Transform, reopened.State.GetAddedTransform(op.Created));
        reopened.Undo();
        Assert.False(reopened.State.IsAdded(op.Created));
        Assert.Empty(ModelLibrary.UsedOnMap(reopened.State));
        reopened.Redo();
        Assert.Equal(new[] { mesh }, ModelLibrary.UsedOnMap(reopened.State));
        reopened.Apply(new DeleteActorOp(op.Created));
        Assert.Empty(ModelLibrary.UsedOnMap(reopened.State)); // deleted again: nothing to carry
    }

    [Fact]
    public async Task TheExportCarriesThePlacedModelWithItsPlacementForClientAndServer()
    {
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        SyntheticLevels.WriteContent(content, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(content);
        using var project = Project.Create(temp.Combine("Models.ssproj"), "Models");
        var box = WriteModel(project.DirectoryPath, "SS_Box");
        var placed = new AddStaticMeshActorOp(LevelPath, "SM_SS_Box_Added", box.MeshObjectPath, TransformValue.At(0, 0, 0));
        var at = new TransformValue(new FVector(-900, 250, 10), new FRotator(0, 45, 0), new FVector(2, 2, 2));
        project.Apply(placed);
        project.Apply(new SetTransformOp(placed.Created, placed.Transform, at));
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_SS_Gone_Added", UeCook.ImportsRoot + "/SS_Gone/SM_SS_Gone.SM_SS_Gone", TransformValue.At(100, 0, 0)));

        var options = new ExportOptions { OutputDirectory = temp.Combine("out"), BendMeshes = _ => null, Validate = false }; // the import's stand-in files are no packages to read back
        var result = await new ProjectExporter().ExportAsync(project, catalog, options);

        // The model's cooked files are in the pak, at its own folder.
        var entries = PakInspector.ReadIndex(result.PakPath!).Entries.Select(e => e.Path).ToList();
        foreach (var file in new[] { "SM_SS_Box.uasset", "SM_SS_Box.uexp", "T_SS_Box_D.uasset", "T_SS_Box_D.uexp" })
        {
            Assert.Contains(entries, e => e.EndsWith("ScumStudio/Imports/SS_Box/" + file, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(result.Assets, a => a.PackagePath == box.MeshPackage && a.VirtualPath == "SCUM/Content/ScumStudio/Imports/SS_Box/SM_SS_Box.uasset");
        Assert.DoesNotContain(entries, e => e.Contains("SS_Gone", StringComparison.OrdinalIgnoreCase));

        // The placement is in the level, where the gizmo left it; the one whose model is gone is left out with a warning.
        Assert.Equal(new[] { "SM_SS_Box_Added" }, Assert.Single(result.Levels).Report.AddedActors);
        Assert.Contains(result.Warnings, w => w.Contains("SM_SS_Gone_Added draws the imported 3D model SS_Gone", StringComparison.Ordinal));
        using (var overlay = AssetCatalog.OpenLoose(content, new AssetCatalogOptions { LooseOverlays = [result.StagingDirectory] }))
        {
            var document = LevelDocument.Load(new Cue4ParseLevelReader(overlay), LevelPath);
            var actor = document.FindActor("SM_SS_Box_Added")!;
            Assert.Equal("StaticMeshActor", actor.ClassName);
            Assert.Equal(box.MeshObjectPath, actor.StaticMeshPath);
            Assert.True(actor.Root!.Relative.IsNearlyEqual(at), actor.Root.Relative.ToString());
            Assert.Null(document.FindActor("SM_SS_Gone_Added"));
        }

        // The server's pak carries it too: its level draws the model.
        var server = await new ProjectExporter().ExportAsync(project, catalog, options, ProjectSourceRole.Server);
        Assert.Contains(server.Assets, a => a.PackagePath == box.MeshPackage);
        Assert.Contains(PakInspector.ReadIndex(server.PakPath!).Entries, e => e.Path.EndsWith("ScumStudio/Imports/SS_Box/SM_SS_Box.uasset", StringComparison.OrdinalIgnoreCase));

        // A model in the library that nothing on the map draws is not shipped.
        WriteModel(project.DirectoryPath, "SS_Unused");
        var again = await new ProjectExporter().ExportAsync(project, catalog, options);
        Assert.DoesNotContain(again.Assets, a => a.PackagePath.Contains("SS_Unused", StringComparison.Ordinal));
    }
}
