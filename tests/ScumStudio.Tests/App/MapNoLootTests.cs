using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// "No loot inside" on the Map (defolt_0999 on Discord: "disable loot in the objects you add"): a checkbox for placed
/// objects in the properties panel and a default in the Add object box, each change one journal step (synthetic loose game
/// files, no GL).
/// </summary>
public sealed class MapNoLootTests
{
    [Fact]
    public async Task PlacedObjectsGetNoLootInsideFromThePanelAndFromTheAddObjectBox()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var journal = ctx.Services.Projects.Current!.Journal;
        var state = ctx.Services.Projects.Current!.State;
        var stock = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        ctx.Services.Projects.Apply(new SetLootOp(stock.Reference, null, new LootSetting(false, []), new LootSetting(true, [SyntheticLevels.LockerPreset])));
        map.RefreshEdits();
        stock = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");

        // A stock object has no such option (its parts are changed one by one in the loot editor).
        map.SelectedActor = stock;
        Assert.False(map.HasNoLootOption);
        Assert.True(map.IsLootable);

        // A copy: the panel's checkbox, one undoable step.
        map.DuplicateSelectedCommand.Execute(null);
        var copy = map.SelectedActor!;
        Assert.True(copy.IsAdded);
        Assert.True(map.HasNoLootOption);
        Assert.False(map.IsNoLoot);
        map.IsNoLoot = true;
        Assert.True(state.IsNoLoot(copy.Reference));
        Assert.IsType<SetNoLootOp>(journal.Applied[^1].Op);
        Assert.False(map.IsLootable); // the loot editor shows what the export gives
        Assert.True(!map.HasLootEditor || map.IsLootEditorLocked);
        ctx.Services.Projects.Undo();
        map.SelectedActor = stock;
        map.SelectedActor = map.AllActors.Single(a => a.Name == copy.Name);
        Assert.False(map.IsNoLoot);
        Assert.False(state.IsNoLoot(copy.Reference));

        // The Add object box's default: kept in the settings, given to every object made from now on in the same step.
        map.NoLootForNewObjects = true;
        Assert.True(ctx.Services.Settings.Load().Ui.NoLootInNewObjects);
        map.SelectedActor = stock;
        var steps = journal.Applied.Count;
        map.DuplicateSelectedCommand.Execute(null);
        var second = map.SelectedActor!;
        Assert.Equal(steps + 1, journal.Applied.Count);
        Assert.True(state.IsNoLoot(second.Reference));
        Assert.True(map.IsNoLoot);
        ctx.Services.Projects.Undo(); // the copy and its setting go together
        Assert.Empty(state.NoLootActors);

        // A copy of an object without loot has none either, with the default off again.
        map.NoLootForNewObjects = false;
        ctx.Services.Projects.Redo();
        map.RefreshEdits();
        map.SelectedActor = map.AllActors.Single(a => a.Name == second.Name);
        map.DuplicateSelectedCommand.Execute(null);
        Assert.True(state.IsNoLoot(map.SelectedActor!.Reference));
        map.SelectedActor = stock;
        map.DuplicateSelectedCommand.Execute(null);
        Assert.False(state.IsNoLoot(map.SelectedActor!.Reference));

        // The export plans the copies' loot out (a plan without game files writes the components' entries only).
        var request = ProjectExporter.PlanLevel(state, SyntheticLevels.LevelPath, stock.Level, []);
        Assert.Contains(request.Loot, l => l.Actor == second.Name && !l.Loot.Lootable);
    }

    private static async Task<MapPageViewModel> OpenAsync(AppTestContext ctx)
    {
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "NoLoot");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        return map;
    }
}
