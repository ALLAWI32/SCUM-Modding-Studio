using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-07): "I copy an object that exists only in C_2, fly to B_4 (the island loads and unloads), press
/// Ctrl+V and nothing is placed." The copied object's level is gone from the scene by then: the paste must still create
/// the copy and draw it once the source level was read again. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class CrossLevelPasteRealTests
{
    private const string C2 = "/Game/ConZ_Files/Maps/The_Island/C_2_Outpost";
    private const string B4 = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";

    [Fact]
    public async Task ABlueprintAndAMeshCopiedInC2PasteInB4AfterC2Unloaded()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Paste");
        var project = ctx.Services.Projects.Current!;

        // Copy a Blueprint building of C_2 (one with drawn parts, not a child of something).
        await map.LoadLevelsAsync([C2]);
        var blueprint = map.AllActors.First(a => a.Actor.Kind == ActorKind.Blueprint && !a.IsAdded && a.Actor.ParentComponent is null
            && a.Actor.Components.Any(c => c.StaticMeshPath is not null && !c.IsSynthesized && c.IsVisible));
        map.SelectedActor = blueprint;
        Assert.True(map.CopySelectedCommand.CanExecute(null));
        map.CopySelectedCommand.Execute(null);
        Assert.True(map.HasCopiedActor);

        // Fly to B_4: C_2 is gone from the scene (as when the island streams it out).
        await map.LoadLevelsAsync([B4]);
        Assert.DoesNotContain(map.AllActors, a => a.Reference == blueprint.Reference);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(700f, 0f, 0f);
        map.AimPointProvider = () => aim;
        map.SelectedActor = null;
        Assert.True(map.PasteCommand.CanExecute(null));
        map.PasteCommand.Execute(null);
        var op = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(B4, op.Level);
        Assert.Equal(blueprint.Reference, op.Source);
        Assert.Equal(blueprint.Actor.ClassPath, op.ClassPath);
        var clone = await DrawnCloneAsync(map, op.NewName);
        Assert.True((clone.RootWorld.Translation - aim).Size() < 1f);
        Assert.NotEmpty(clone.Placements!);

        // The same with a plain mesh actor of C_2.
        await map.LoadLevelsAsync([C2]);
        var mesh = map.AllActors.First(a => a.Actor.Kind == ActorKind.StaticMeshActor && !a.IsAdded && a.Actor.StaticMeshPath is { } m
            && !m.Contains("Distant", StringComparison.OrdinalIgnoreCase));
        map.SelectedActor = mesh;
        map.CopySelectedCommand.Execute(null);
        await map.LoadLevelsAsync([B4]);
        map.SelectedActor = null;
        map.PasteCommand.Execute(null);
        var meshOp = Assert.IsType<AddStaticMeshActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(B4, meshOp.Level);
        Assert.Equal(mesh.Actor.StaticMeshPath, meshOp.StaticMesh);
        var meshClone = Assert.Single(map.Clones, c => c.Name == meshOp.NewName);
        Assert.Equal(mesh.Actor.StaticMeshPath, meshClone.MeshPath);
        Assert.True(await WaitAsync(() => map.PreparedScene!.Meshes.ContainsKey(meshOp.StaticMesh)
            || map.ExtraMeshes.Any(e => string.Equals(e.Asset.MeshPath, meshOp.StaticMesh, StringComparison.OrdinalIgnoreCase))), "the pasted mesh was never loaded for the viewport");

        // Both copies survive the next streaming step (a reload of B_4) and are still drawn.
        await map.LoadLevelsAsync([B4]);
        Assert.Contains(map.Clones, c => c.Name == meshOp.NewName);
        Assert.NotEmpty((await DrawnCloneAsync(map, op.NewName)).Placements!);

        // The island's own streaming: the levels around C_2 come up as the streamer loads them, the camera flies to B_4,
        // the streamer swaps the set (C_2 falls out), and Ctrl+V still pastes what was copied in C_2.
        var world = map.World!;
        var c2Camera = blueprint.Actor.WorldTransform.Translation + new FVector(0f, 0f, 1500f);
        await map.LoadLevelsAsync(MapPageViewModel.LevelsAround(world, c2Camera, map.Quality.StreamRadiusCm), landscapeStep: 4, seaPlane: false, streamed: true);
        var streamedSource = map.AllActors.First(a => a.Reference == blueprint.Reference);
        map.SelectedActor = streamedSource;
        map.CopySelectedCommand.Execute(null);
        var b4Camera = aim + new FVector(0f, 0f, 1500f);
        await map.LoadLevelsAsync(MapPageViewModel.LevelsAround(world, b4Camera, map.Quality.StreamRadiusCm), landscapeStep: 4, seaPlane: false, streamed: true);
        Assert.DoesNotContain(map.AllActors, a => !a.IsAdded && a.Reference == blueprint.Reference);
        map.SelectedActor = null;
        var rows = project.Journal.Applied.Count;
        map.PasteCommand.Execute(null);
        Assert.Equal(rows + 1, project.Journal.Applied.Count);
        var streamedOp = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(blueprint.Reference, streamedOp.Source);
        Assert.NotEmpty((await DrawnCloneAsync(map, streamedOp.NewName)).Placements!);
    }

    /// <summary>The clone with its drawn parts (the source level is read on a worker after the paste).</summary>
    private static async Task<ActorClone> DrawnCloneAsync(MapPageViewModel map, string name)
    {
        ActorClone? clone = null;
        Assert.True(await WaitAsync(() => (clone = map.Clones.FirstOrDefault(c => c.Name == name)) is { Placements.Count: > 0 }), $"{name} was never drawn with its parts");
        return clone!;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }
}
