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

    /// <summary>
    /// Igor (Discord): "picking the hangar selects everything in it; let me edit what is fixed to the main model". In part
    /// mode one part of a Blueprint (the lamp's bulb) is selected, moved and deleted alone; the lamp stays where it is.
    /// </summary>
    [Fact]
    public async Task OnePartOfABlueprintIsMovedAndDeletedAlone()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Parts");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        var lamp = map.AllActors.Single(a => a.Name == "BP_Lamp_C_1");
        var key = InstanceKey.Of(lamp.SelectableId, "Bulb", InstanceKey.Part);
        map.PickParts = true;
        map.SelectedInstanceKey = key;
        map.SelectedActorId = lamp.SelectableId;
        Assert.True(map.HasSelectedInstance);
        Assert.Contains(map.ActorProperties, r => r.Name == "Part" && r.Value.StartsWith("Bulb of BP_Lamp", StringComparison.Ordinal));

        var world = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(lamp.SelectableId, world with { Translation = new FVector(world.Translation.X + 120f, world.Translation.Y, world.Translation.Z) });
        var move = Assert.IsType<SetTransformOp>(Assert.Single(ctx.Services.Projects.Current!.Journal.Applied).Op);
        Assert.Equal("Bulb", move.Component);
        Assert.Equal(world.Translation.X + 120f, map.InstanceTransforms[key].Translation.X, 0.5f);
        Assert.Empty(map.ActorTransforms); // the lamp itself did not move

        map.DeleteSelectedCommand.Execute(null);
        var delete = Assert.IsType<SetTransformOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal(new FVector(0, 0, 0), delete.New.Scale); // gone: drawn and collided as nothing
        Assert.DoesNotContain(lamp.SelectableId, map.HiddenActorIds);
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
