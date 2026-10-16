using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Map's Add object › My models (Pheidian: "import custom models into the editor"), synthetic loose game files, no GL:
/// the project's models are listed, placed like any mesh through the journal, found by name, and the import says what
/// it needs when Unreal Engine 4.27 is not installed.
/// </summary>
public sealed class MapModelsAppTests
{
    [Fact]
    public async Task MyModelsListsTheProjectsModelsAndPlacesThemLikeAnyMesh()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        map.AimPointProvider = () => new FVector(300, 400, 50);

        map.RefreshMyModels();
        Assert.Empty(map.MyModels); // no project
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Models");
        var box = MapModelsTests.WriteModel(project.DirectoryPath, "SS_Box");
        map.RefreshMyModels();
        var row = Assert.Single(map.MyModels);
        Assert.Equal("Box", row.Name);
        Assert.Equal("SM_SS_Box", row.Caption);

        // Place: a new mesh actor drawing the model where the camera aims, one journal step.
        row.Place.Execute(null);
        var added = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Equal(box.MeshObjectPath, added.Actor.StaticMeshPath);
        Assert.Same(added, map.SelectedActor);
        Assert.Equal(300f, Assert.Single(map.Clones).RootWorld.Translation.X, 0.01f);
        Assert.IsType<AddStaticMeshActorOp>(project.Journal.Applied[^1].Op);
        Assert.Contains("1", Assert.Single(map.MyModels).Caption, StringComparison.Ordinal); // "on the map 1×"

        // Typed into the Add object box by its name, it is placed too; Ctrl+Z takes both away.
        Assert.True(map.AddObject("Box"));
        Assert.Equal(2, map.AllActors.Count(a => a.IsAdded && a.Actor.StaticMeshPath == box.MeshObjectPath));
        ctx.Services.Projects.Undo();
        ctx.Services.Projects.Undo();
        Assert.DoesNotContain(map.AllActors, a => a.IsAdded);

        // A model whose files were removed is not placed: the toast says so.
        Directory.Delete(Path.Combine(project.DirectoryPath, box.Folder), recursive: true);
        row.Place.Execute(null);
        Assert.DoesNotContain(map.AllActors, a => a.IsAdded);
        Assert.Empty(map.MyModels);

        // Without Unreal Engine 4.27 the import says what it needs and opens nothing.
        if (!ModelImports.HasEngine)
        {
            await map.ImportMapModelCommand.ExecuteAsync(null);
            Assert.True(map.ModelEngineMissing);
            Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message.Contains("Unreal Engine 4.27 was not found", StringComparison.Ordinal));
            Assert.False(map.IsImportingModel);
        }
    }
}
