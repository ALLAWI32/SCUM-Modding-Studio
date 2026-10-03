using Avalonia.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using Xunit;
using Xunit.Sdk;

namespace ScumStudio.Tests.App;

/// <summary>
/// Runs on the Avalonia headless platform and only when <c>SCUM_MAP_SLICE</c> points at the extracted map slice
/// (the folder that contains <c>SCUM/Content/ConZ_Files/Maps/The_Island</c>).
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AvaloniaMapSliceFactAttribute : FactAttribute
{
    /// <summary>Environment variable naming the slice folder.</summary>
    public const string EnvironmentVariable = "SCUM_MAP_SLICE";

    /// <summary>Creates the attribute; skips when the slice is not available.</summary>
    public AvaloniaMapSliceFactAttribute()
    {
        if (Root is null)
        {
            Skip = $"{EnvironmentVariable} is not set or does not exist (extracted map slice).";
        }
    }

    /// <summary>The slice folder, or null.</summary>
    public static string? Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null;
        }
    }
}

/// <summary>The map page loading a real sublevel of the slice into the entity list and viewport view model.</summary>
public sealed class MapViewportSliceTests
{
    [AvaloniaMapSliceFact]
    public async Task MapPageLoadsARealSublevelAndDeletesAnActor()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.OpenLooseAsync(AvaloniaMapSliceFactAttribute.Root!, ProgressSink.Null);
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            HeadlessUi.Pump();
            Assert.True(map.HasWorld, "world index should be built from the slice");

            var cell = map.Nodes.First(n => n.Title == "Cell A_0");
            cell.IsExpanded = true;
            var leaf = cell.Children.SelectMany(c => c.Children).First(n => n.Package?.Name == "A_0_Outpost_Exterior");
            map.SelectedNode = leaf;
            await map.LevelLoadCompletion;
            HeadlessUi.Pump();

            Assert.NotNull(map.PreparedScene);
            Assert.Equal(510, map.AllActors.Count);
            Assert.Equal("A_0_Outpost_Exterior", map.LoadedLevelsCaption);
            Assert.True(map.PreparedScene!.Placements.Count > 50, "the outpost exterior places meshes");
            Assert.Contains(map.AllActors, a => a.ClassName == "StaticMeshActor" && a.HasMesh);

            // Entity filter and selection sync with the viewport id.
            map.EntityFilter = "Barricade";
            Assert.True(map.Actors.Count > 0 && map.Actors.Count < map.AllActors.Count);
            var picked = map.Actors[0];
            map.SelectedActorId = picked.SelectableId;
            Assert.Same(picked, map.SelectedActor);
            Assert.NotEmpty(map.ActorProperties);

            // Deleting needs a project; the deletion lands in the journal and hides the actor.
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Outpost edit");
            HeadlessUi.Pump();
            Assert.True(map.DeleteSelectedCommand.CanExecute(null));
            map.DeleteSelectedCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Contains(picked.SelectableId, map.HiddenActorIds);
            Assert.True(picked.IsDeleted);
            Assert.True(ctx.Services.Projects.CanUndo);

            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();
            Assert.DoesNotContain(picked.SelectableId, map.HiddenActorIds);

            // "Delete all with the same mesh" removes every barricade of that mesh in the level in one journal entry.
            Assert.True(map.DeleteAllOfMeshCommand.CanExecute(null));
            Assert.Contains(" actors with mesh ", map.DeleteAllOfMeshTip, StringComparison.Ordinal);
            map.DeleteAllOfMeshCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.True(map.HiddenActorIds.Count > 1, $"expected several barricades hidden, got {map.HiddenActorIds.Count}");
            Assert.Contains(picked.SelectableId, map.HiddenActorIds);
            Assert.StartsWith("Delete all of ", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
            Assert.Contains("level A_0_Outpost_Exterior", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal);
            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();
            Assert.Empty(map.HiddenActorIds);

            // Placing a real mesh: the actor is journaled, drawn as a mesh clone, and the mesh is loaded for the viewport.
            var meshPath = map.AllActors.First(a => a.Actor.StaticMeshPath is { } p && map.PreparedScene!.Meshes.ContainsKey(p)).Actor.StaticMeshPath!;
            map.AimPointProvider = () => new FVector(-614000, -549000, 2400);
            Assert.True(map.AddMeshActor(meshPath));
            HeadlessUi.Pump();
            var placed = Assert.Single(map.AllActors, a => a.IsAdded);
            Assert.Equal(meshPath, Assert.Single(map.Clones).MeshPath);
            Assert.True(map.PreparedScene!.Meshes.ContainsKey(meshPath), "a mesh already in the scene needs no extra load");
            Assert.Empty(map.ExtraMeshes);

            ctx.Services.Projects.Undo();
            HeadlessUi.Pump();
            Assert.DoesNotContain(map.AllActors, a => a.IsAdded);

            var list = HeadlessUi.FindNamed<ListBox>(window, "EntityList")!;
            Assert.True(list.ItemCount > 0);
            HeadlessUi.SaveScreenshot(window, "page-map-level");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
