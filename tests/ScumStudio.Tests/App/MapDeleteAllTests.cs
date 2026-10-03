using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>"Delete all of the same kind" on the map page, driven on synthetic loose game files (no GL, no slice).</summary>
public sealed class MapDeleteAllTests
{
    [Fact]
    public async Task DeleteAllOfKindJournalsOneBulkEntryAndUndoRestoresIt()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Bulk delete");

        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        Assert.NotNull(map.PreparedScene);
        Assert.Equal(3, map.AllActors.Count);
        Assert.False(map.DeleteAllOfMeshCommand.CanExecute(null));
        Assert.False(map.DeleteAllOfClassCommand.CanExecute(null));
        Assert.Contains("select an actor", map.DeleteAllOfMeshTip, StringComparison.Ordinal);

        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        Assert.True(map.DeleteAllOfMeshCommand.CanExecute(null));
        Assert.True(map.DeleteAllOfClassCommand.CanExecute(null));
        Assert.Contains("1 actors + 2 instances with mesh SM_Rock", map.DeleteAllOfMeshTip, StringComparison.Ordinal); // the rock ISM draws the same mesh
        Assert.Contains("of class StaticMeshActor", map.DeleteAllOfClassTip, StringComparison.Ordinal);

        map.DeleteAllOfMeshCommand.Execute(null);
        Assert.True(house.IsDeleted);
        Assert.Contains(house.SelectableId, map.HiddenActorIds);
        Assert.False(map.DeleteAllOfMeshCommand.CanExecute(null)); // the selected actor is deleted now
        var entry = ctx.Services.Projects.History[0];
        Assert.StartsWith("Delete all of SM_Rock (StaticMesh) in level A_0_TestLevel", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("1 actor(s), 2 instance(s)", entry.Summary, StringComparison.Ordinal);
        Assert.Single(ctx.Services.Projects.Current!.PendingExportSet);

        // The two rock instances are hidden individually; their actor stays.
        var rocks = map.AllActors.Single(a => a.Name == "Rocks_Actor");
        Assert.False(rocks.IsDeleted);
        Assert.Equal(2, map.HiddenInstanceKeys.Count);
        Assert.All(map.HiddenInstanceKeys, k => Assert.Equal(rocks.SelectableId, k.SelectableId));
        Assert.Contains(new ScumStudio.Viewport.InstanceKey(rocks.SelectableId, "rocks", 1), map.HiddenInstanceKeys);
        map.SelectedActor = rocks;
        Assert.False(map.DeleteAllOfMeshCommand.CanExecute(null)); // a plain Actor with a scene root has no "kind" mesh
        map.SelectedActor = house;

        ctx.Services.Projects.Undo();
        Assert.False(house.IsDeleted);
        Assert.Empty(map.HiddenActorIds);
        Assert.Empty(map.HiddenInstanceKeys);
        Assert.True(map.DeleteAllOfMeshCommand.CanExecute(null));

        var lamp = map.AllActors.Single(a => a.Name == "BP_Lamp_C_1");
        map.SelectedActor = lamp;
        Assert.False(map.DeleteAllOfMeshCommand.CanExecute(null)); // the Blueprint's root has no mesh
        Assert.Contains("of class BP_Lamp_C", map.DeleteAllOfClassTip, StringComparison.Ordinal);
        map.DeleteAllOfClassCommand.Execute(null);
        Assert.True(lamp.IsDeleted);
        Assert.False(house.IsDeleted);
        Assert.StartsWith("Delete all of BP_Lamp (Class)", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
    }
}
