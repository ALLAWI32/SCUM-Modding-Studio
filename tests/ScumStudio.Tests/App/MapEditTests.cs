using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>Moving and duplicating actors from the map page (synthetic loose game files, no GL).</summary>
public sealed class MapEditTests
{
    [Fact]
    public async Task EditsTransformsAndDuplicatesThroughTheJournal()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Edits");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        Assert.Equal(3, map.AllActors.Count);
        Assert.Empty(map.Clones);

        // Like the entity ListBox: a selection missing from the new items is dropped and null written back.
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapPageViewModel.Actors) && map.SelectedActor is { } shown && !map.Actors.Contains(shown))
            {
                map.SelectedActor = null;
            }
        };
        Assert.Empty(map.ActorTransforms);

        // Selecting fills the editor with the pristine root transform.
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        Assert.Equal("1000, 0, 0", map.EditLocation);
        Assert.Equal("0, 90, 0", map.EditRotation);
        Assert.Equal("2, 2, 2", map.EditScale);
        Assert.True(map.ApplyTransformCommand.CanExecute(null));
        Assert.True(map.DuplicateSelectedCommand.CanExecute(null));

        // Apply a move: the journal gets a SetTransformOp and the viewport receives the new root world transform.
        map.EditLocation = "1500, 20, -5";
        map.ApplyTransformCommand.Execute(null);
        var moved = Assert.Single(map.ActorTransforms);
        Assert.Equal(house.SelectableId, moved.Key);
        Assert.Equal(1500f, moved.Value.Translation.X, 0.01f);
        Assert.Equal(-5f, moved.Value.Translation.Z, 0.01f);
        Assert.StartsWith("Transform A_0_TestLevel/StaticMeshActor_1", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        Assert.Contains(map.ActorProperties, r => r.Name == "Location" && r.Value.StartsWith("1500, 20, -5", StringComparison.Ordinal));

        // Nothing happens when the values did not change; garbage is a warning toast.
        var edits = ctx.Services.Projects.History.Count;
        map.ApplyTransformCommand.Execute(null);
        Assert.Equal(edits, ctx.Services.Projects.History.Count);
        map.EditScale = "big";
        map.ApplyTransformCommand.Execute(null);
        Assert.Equal(edits, ctx.Services.Projects.History.Count);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Invalid transform");
        map.EditScale = "2, 2, 2";

        // Duplicate: a synthetic entity 2 m along +X, drawn as a clone of the source, selected afterwards.
        map.DuplicateSelectedCommand.Execute(null);
        Assert.Equal(4, map.AllActors.Count);
        var copy = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Equal("StaticMeshActor_1_Copy", copy.Name);
        Assert.Equal(house.SelectableId, copy.SourceId);
        Assert.True(copy.SelectableId >= MapPageViewModel.AddedIdBase);
        Assert.Same(copy, map.SelectedActor);
        var clone = Assert.Single(map.Clones);
        Assert.Equal(copy.SelectableId, clone.Id);
        Assert.Equal(house.SelectableId, clone.SourceId);
        Assert.Equal(1700f, clone.RootWorld.Translation.X, 0.01f); // 1500 (moved) + 200
        Assert.Equal("1700, 20, -5", map.EditLocation);
        Assert.True(map.DuplicateSelectedCommand.CanExecute(null)); // a copy of a copy is a copy of the source
        Assert.True(map.ApplyTransformCommand.CanExecute(null));
        Assert.Contains(map.ActorProperties, r => r.Name == "State" && r.Value.Contains("added", StringComparison.Ordinal));

        // Moving the copy updates the clone; deleting it hides it; undo brings everything back.
        map.EditLocation = "900, 0, 0";
        map.ApplyTransformCommand.Execute(null);
        Assert.Equal(900f, Assert.Single(map.Clones).RootWorld.Translation.X, 0.01f);
        Assert.Single(map.ActorTransforms); // still only the pristine house

        // A gizmo drag arrives as the root's world transform and is journaled like Apply (copy and pristine actor).
        Assert.NotNull(map.SelectedRootWorld);
        Assert.Equal(900f, map.SelectedRootWorld.Value.Translation.X, 0.01f);
        map.ApplyDraggedTransform(copy.SelectableId, map.SelectedRootWorld.Value with { Translation = new FVector(950, 10, 0) });
        Assert.Equal(950f, Assert.Single(map.Clones).RootWorld.Translation.X, 0.01f);
        Assert.Equal(950f, map.SelectedRootWorld!.Value.Translation.X, 0.01f);
        Assert.Equal("950, 10, 0", map.EditLocation);
        Assert.Equal(copy.SelectableId, map.SelectedActor?.SelectableId); // owner: letting go of a drag must not drop the selection
        var houseWorld = map.ActorTransforms[house.SelectableId];
        map.ApplyDraggedTransform(house.SelectableId, houseWorld with { Translation = new FVector(1600, 20, -5) });
        Assert.Equal(1600f, map.ActorTransforms[house.SelectableId].Translation.X, 0.01f);
        Assert.Equal(90f, map.ActorTransforms[house.SelectableId].Rotation.Rotator().Yaw, 0.01f); // rotation and scale survive the drag
        Assert.Equal(10f, map.TranslationSnap); // the default UI preference

        map.DeleteSelectedCommand.Execute(null);
        Assert.Contains(copy.SelectableId, map.HiddenActorIds);
        Assert.Single(ctx.Services.Projects.Current!.PendingExportSet);

        while (ctx.Services.Projects.CanUndo)
        {
            ctx.Services.Projects.Undo();
        }

        Assert.Equal(3, map.AllActors.Count);
        Assert.Empty(map.Clones);
        Assert.Empty(map.ActorTransforms);
        Assert.Empty(map.HiddenActorIds);
    }

    [Fact]
    public async Task PlacesANewMeshActorWhereTheViewportAims()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        map.AimPointProvider = () => new FVector(1234, -56, 78);

        const string mesh = SyntheticLevels.RockPackage + ".SM_Rock";
        Assert.False(map.AddMeshActor(mesh)); // nothing loaded yet
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        Assert.False(map.AddMeshActor(mesh)); // no project yet
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "No project open");

        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Placing");
        Assert.True(map.AddMeshActor(mesh));

        var added = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Equal("SM_Rock_Added", added.Name);
        Assert.Equal("StaticMeshActor", added.ClassName);
        Assert.Equal(mesh, added.Actor.StaticMeshPath);
        Assert.Equal(0u, added.SourceId);
        Assert.Same(added, map.SelectedActor);
        Assert.Equal("1234, -56, 78", map.EditLocation);
        var clone = Assert.Single(map.Clones);
        Assert.Equal(mesh, clone.MeshPath);
        Assert.Equal(1234f, clone.RootWorld.Translation.X, 0.01f);
        Assert.StartsWith("Add SM_Rock as A_0_TestLevel/SM_Rock_Added", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        Assert.Single(ctx.Services.Projects.Current!.PendingExportSet);

        // A second one gets a unique name; the gizmo origin follows the placement; undo removes both.
        Assert.True(map.AddMeshActor(mesh));
        Assert.Equal(2, map.AllActors.Count(a => a.IsAdded));
        Assert.Equal("SM_Rock_Added2", map.SelectedActor!.Name);
        Assert.Equal(1234f, map.SelectedRootWorld!.Value.Translation.X, 0.01f);
        ctx.Services.Projects.Undo();
        ctx.Services.Projects.Undo();
        Assert.Empty(map.Clones);
        Assert.DoesNotContain(map.AllActors, a => a.IsAdded);
    }

    [Fact]
    public async Task CopiesAndPastesActorsAcrossLoadedLevels()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        const string other = "/Game/ConZ_Files/Maps/The_Island/A_0_Other";
        SyntheticLevels.WriteBlueprintOnlyLevel(game, "A_0_Other", SyntheticLevels.BlueprintPackage, "BP_Lamp_C_9", new FVector(10, 20, 30), new FRotator(0, 15, 0));
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Paste");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath, other]);
        Assert.Equal(4, map.AllActors.Count);
        Assert.False(map.PasteCommand.CanExecute(null));
        map.AimPointProvider = () => new FVector(500, 600, 700);

        // Copy the lamp of the first level, paste it into the second (the selected actor decides the target level).
        var lamp = map.AllActors.Single(a => a.Name == "BP_Lamp_C_1");
        map.SelectedActor = lamp;
        Assert.True(map.CopySelectedCommand.CanExecute(null));
        map.CopySelectedCommand.Execute(null);
        Assert.Same(lamp, map.CopiedActor);
        Assert.True(map.PasteCommand.CanExecute(null));

        map.SelectedActor = map.AllActors.Single(a => a.Name == "BP_Lamp_C_9");
        map.PasteCommand.Execute(null);
        var pasted = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Equal(other, pasted.Level.PackagePath);
        Assert.Equal("BP_Lamp_Added", pasted.Name);
        Assert.Equal("BP_Lamp_C", pasted.ClassName);
        Assert.Equal(lamp.SelectableId, pasted.SourceId); // drawn as a clone of the loaded source
        Assert.Same(pasted, map.SelectedActor);
        Assert.Equal(500f, Assert.Single(map.Clones).RootWorld.Translation.X, 0.01f);
        Assert.StartsWith("Add BP_Lamp as A_0_Other/BP_Lamp_Added (from A_0_TestLevel/BP_Lamp_C_1)", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);

        // Pasting into the source level itself is a duplicate; a StaticMeshActor becomes a new mesh actor elsewhere.
        map.SelectedActor = lamp;
        map.PasteCommand.Execute(null);
        Assert.StartsWith("Duplicate A_0_TestLevel/BP_Lamp_C_1 as BP_Lamp_C_1_Copy", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        map.CopySelectedCommand.Execute(null);
        map.SelectedActor = map.AllActors.Single(a => a.Name == "BP_Lamp_C_9");
        map.PasteCommand.Execute(null);
        Assert.StartsWith("Add SM_Rock as A_0_Other/SM_Rock_Added", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        Assert.Equal(3, map.AllActors.Count(a => a.IsAdded));
        Assert.Equal(2, ctx.Services.Projects.Current!.PendingExportSet.Count);

        // A copy of an added actor is a copy of what it was made from (owner: "duplicate it again").
        map.SelectedActor = pasted;
        Assert.True(map.CopySelectedCommand.CanExecute(null));
        map.CopySelectedCommand.Execute(null);
        map.PasteCommand.Execute(null);
        Assert.Equal(4, map.AllActors.Count(a => a.IsAdded));
        Assert.Contains("(from A_0_TestLevel/BP_Lamp_C_1)", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
        Assert.True(map.SelectedActor is { IsAdded: true } again && again.Name != pasted.Name); // the new copy is selected
    }

    [Fact]
    public async Task ShapeSlidersJournalScaleAndBendAsOneStep()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Shapes");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        Assert.True(map.HasShape);
        Assert.Equal(2, map.ShapeLength, 3); // the house is at scale 2
        Assert.False(map.CanBend); // its mesh is not in the synthetic game files

        // While a slider is held nothing is journaled; letting go journals scale and bend as one step.
        map.IsShapeDragging = true;
        map.ShapeLength = 3;
        map.ShapeBend = 45;
        Assert.Empty(ctx.Services.Projects.History);
        map.IsShapeDragging = false;
        map.CommitShape();
        var state = ctx.Services.Projects.Current!.State;
        Assert.Single(ctx.Services.Projects.History);
        Assert.Equal(45f, state.GetBend(house.Reference));
        Assert.Equal(new FVector(3, 2, 2), state.GetTransformOverride(house.Reference)!.Value.Scale);
        Assert.Same(house, map.SelectedActor);

        // Size scales all three; Straighten puts it back.
        map.ShapeSize *= 2;
        map.CommitShape();
        Assert.Equal(6f, state.GetTransformOverride(house.Reference)!.Value.Scale.X, 0.01f);
        map.ResetShapeCommand.Execute(null);
        Assert.Equal(0f, state.GetBend(house.Reference));
        Assert.Null(state.GetTransformOverride(house.Reference)); // back to the pristine scale 2
        Assert.Equal(2, map.ShapeLength, 3);
    }

    /// <summary>Owner: lean a house towards its front or side (up to 90°) instead of bending it; Straighten stands it up again.</summary>
    [Fact]
    public async Task TiltSlidersLeanTheHouseAndStraightenStandsItUp()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Tilt");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        var yaw = map.ShapeYaw;
        map.IsShapeDragging = true;
        map.ShapePitch = 30;
        map.ShapeRoll = -20;
        map.IsShapeDragging = false;
        map.CommitShape();
        var state = ctx.Services.Projects.Current!.State;
        var turned = state.GetTransformOverride(house.Reference)!.Value.Rotation.GetNormalized();
        Assert.Equal(30f, turned.Pitch, 0.1f);
        Assert.Equal(-20f, turned.Roll, 0.1f);
        Assert.Equal((float)yaw, turned.Yaw, 0.1f);
        Assert.Equal(2, map.ShapeLength, 3); // the size did not change

        map.ResetShapeCommand.Execute(null);
        Assert.Null(state.GetTransformOverride(house.Reference)); // upright as the level has it
        Assert.Equal(0, map.ShapePitch, 1);
    }

    [Fact]
    public async Task CtrlClickSelectsSeveralObjectsThatMoveCopyAndDeleteTogether()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Group");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        map.AimPointProvider = () => new FVector(5000, 0, 0);
        var history = ctx.Services.Projects.History;
        var state = ctx.Services.Projects.Current!.State;

        // Select the house, Ctrl+click the rocks: both are in the set.
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var rocks = map.AllActors.Single(a => a.Name == "Rocks_Actor");
        map.SelectedActor = house;
        map.ToggleGroup(rocks.SelectableId, null);
        Assert.True(map.HasGroup);
        Assert.Equal(2, map.KindSelectionIds.Count);
        Assert.Equal(2, map.GroupWorlds.Count);

        // Dragging the house moves the rocks the same way, in one step; the set stays.
        var houseWorld = map.GroupWorlds.Single(g => g.Id == house.SelectableId).World;
        var rocksWorld = map.GroupWorlds.Single(g => g.Id == rocks.SelectableId).World;
        map.ApplyDraggedTransform(house.SelectableId, houseWorld with { Translation = houseWorld.Translation + new FVector(100, 0, 0) });
        Assert.Single(history);
        Assert.Equal(rocksWorld.Translation.X + 100f, state.GetTransformOverride(rocks.Reference)!.Value.Location.X, 0.01f);
        Assert.True(map.HasGroup);

        // Copy + paste: both, laid out as they were, in one step, and the copies become the selection.
        map.CopySelectedCommand.Execute(null);
        map.PasteCommand.Execute(null);
        Assert.Equal(2, history.Count);
        var copies = map.AllActors.Where(a => a.IsAdded).ToList();
        Assert.Equal(2, copies.Count);
        Assert.True(map.HasGroup);
        var houseCopy = copies.Single(c => c.SourceId == house.SelectableId);
        var rocksCopy = copies.Single(c => c.SourceId == rocks.SelectableId);
        var gap = rocksWorld.Translation - houseWorld.Translation;
        var copyGap = state.GetAddedTransform(rocksCopy.Reference)!.Value.Location - state.GetAddedTransform(houseCopy.Reference)!.Value.Location;
        Assert.True(FVector.Distance(gap, copyGap) < 0.5f, $"{copyGap} instead of {gap}");

        // Delete removes exactly the selected copies, in one step.
        await map.DeleteKindSelectionAsync();
        Assert.Equal(3, history.Count);
        Assert.All(copies, c => Assert.True(state.IsDeleted(c.Reference)));
        Assert.False(state.IsDeleted(house.Reference));
        Assert.False(map.HasGroup);
    }

    [Fact]
    public async Task ExtendLaysACopyEndToEndAlongTheObject()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Extend");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);

        // The house stands at (1000, 0, 0) turned 90°: its length runs along +Y. Without mesh bounds a piece counts 2 m.
        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.Extend();
        var copy = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Same(copy, map.SelectedActor); // pressing again continues from the new piece
        var at = ctx.Services.Projects.Current!.State.GetAddedTransform(copy.Reference)!.Value.Location;
        Assert.True(FVector.Distance(at, new FVector(1000, 200, 0)) < 0.5f, at.ToString());
        map.Extend(backwards: true);
        Assert.Equal(2, map.AllActors.Count(a => a.IsAdded));
    }
}
