using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// One tree/rock/plank is one ISM instance: picking it must select, move and delete that instance alone (owner report:
/// "I pick one tree and it selects all the trees", and dragging moved the whole InstancedFoliageActor).
/// </summary>
public sealed class MapInstanceTests
{
    [Fact]
    public async Task OneInstanceIsMovedAndDeletedAlone()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Instances");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        var rocks = map.AllActors.Single(a => a.Name == "Rocks_Actor");
        var key = InstanceKey.Of(rocks.SelectableId, "Rocks", 1);
        map.SelectedInstanceKey = key; // the viewport sets the instance first, then the actor id
        map.SelectedActorId = rocks.SelectableId;
        Assert.True(map.HasSelectedInstance, string.Join(";", rocks.Actor.InstanceTransforms.Select(i => i.ComponentName + "#" + i.InstanceIndex)) + " sel=" + map.SelectedActor?.Name + " key=" + map.SelectedInstanceKey);
        Assert.Contains(map.ActorProperties, r => r.Name == "Instance" && r.Value.StartsWith("#1 of Rocks", StringComparison.Ordinal));

        var world = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(rocks.SelectableId, world with { Translation = new FVector(world.Translation.X + 300f, world.Translation.Y, world.Translation.Z) });
        var move = Assert.IsType<SetInstanceTransformOp>(Assert.Single(ctx.Services.Projects.Current!.Journal.Applied).Op);
        Assert.Equal(1, move.Target.Index);
        Assert.Equal(world.Translation.X + 300f, map.InstanceTransforms[key].Translation.X, 0.5f);
        Assert.Empty(map.ActorTransforms); // the actor holding the instances did not move

        map.DeleteSelectedCommand.Execute(null);
        Assert.IsType<DeleteInstanceOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Contains(key, map.HiddenInstanceKeys);
        Assert.DoesNotContain(rocks.SelectableId, map.HiddenActorIds);
        Assert.DoesNotContain(InstanceKey.Of(rocks.SelectableId, "Rocks", 0), map.HiddenInstanceKeys);
    }

    [Fact]
    public async Task CtrlASelectsEveryInstanceOfTheKindAndDeleteRemovesThemAll()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Kind");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        var rocks = map.AllActors.Single(a => a.Name == "Rocks_Actor");
        map.SelectedInstanceKey = InstanceKey.Of(rocks.SelectableId, "Rocks", 1);
        map.SelectedActorId = rocks.SelectableId;
        map.SelectAllOfKind(wholeMap: false);

        Assert.True(map.HasKindSelection);
        Assert.Equal(rocks.Actor.InstanceTransforms.Count, map.KindSelectionInstances.Count);
        await map.DeleteKindSelectionAsync();

        var op = Assert.IsType<DeleteAllOfKindOp>(Assert.Single(ctx.Services.Projects.Current!.Journal.Applied).Op);
        Assert.Equal(rocks.Actor.InstanceTransforms.Count, op.Instances.Count);
        Assert.False(map.HasKindSelection);
        Assert.All(rocks.Actor.InstanceTransforms, i => Assert.Contains(InstanceKey.Of(rocks.SelectableId, i.ComponentName, i.InstanceIndex), map.HiddenInstanceKeys));
    }

    [Fact]
    public async Task AnObjectPathCopiedFromAssetsIsAddedInFrontOfTheCamera()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Add");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        map.AimPointProvider = () => new FVector(100f, 200f, 300f);

        map.AddObjectText = "  " + SyntheticLevels.RockPackage + "  ";
        map.AddObjectFromTextCommand.Execute(null);

        var add = Assert.IsType<AddStaticMeshActorOp>(Assert.Single(ctx.Services.Projects.Current!.Journal.Applied).Op);
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", add.StaticMesh);
        Assert.Equal(new FVector(100f, 200f, 300f), add.Transform.Location);
        Assert.Empty(map.AddObjectText);
    }
}
