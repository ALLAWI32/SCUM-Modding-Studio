using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord (2026-10-07): "the spawner's gizmo rotated on its own, and with each clone it came out a little more rotated
/// (crooked)"; "cloning and moving zombie spawn points gave five errors". Island zombie spawn places are cloned and moved
/// over and over: the turn stays exactly what it was and nothing throws. (Turns are compared component-wise: an angle from acos
/// near 1 in single precision reads 0.09° between two equal quaternions.) Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class SpawnCloneDriftRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    [Fact]
    public async Task ZombieSpawnPlacesKeepTheirTurnThroughCopiesAndMoves()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Drift");
        await map.LoadLevelsAsync([Farm]);
        var project = ctx.Services.Projects.Current!;
        var toasts = ctx.Services.Notifications.Toasts;

        var zombies = map.AllActors.Where(a => a.Level.PackagePath == SpawnPlaces.StaticDataPath && a.Name.StartsWith("ZombieSpawn_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(zombies);
        var source = zombies[0];
        map.SelectedActor = source;
        var start = map.SelectedRootWorld!.Value;

        // Five copies, each a copy of the last (as a user does with Ctrl+D), then a move of each: the turn never changes.
        var current = source;
        for (var i = 0; i < 5; i++)
        {
            map.SelectedActor = current;
            map.DuplicateSelectedCommand.Execute(null);
            current = map.SelectedActor!;
            Assert.NotEqual(source.Name, current.Name);
            var world = map.SelectedRootWorld!.Value;
            Assert.True(world.Rotation.Equals(start.Rotation, 1e-5f), $"copy {i + 1} turned by {world.Rotation.AngularDistance(start.Rotation) * 57.3f:0.###}°");

            map.ApplyDraggedTransform(current.SelectableId, world with { Translation = world.Translation + new FVector(150f, 0f, 0f) });
            var moved = map.SelectedRootWorld!.Value;
            Assert.True(moved.Rotation.Equals(start.Rotation, 1e-5f), $"move {i + 1} turned by {moved.Rotation.AngularDistance(start.Rotation) * 57.3f:0.###}°");
        }

        // Copy and paste, each paste copied again (Ctrl+C / Ctrl+V), the camera aiming a little further each time.
        var aim = start.Translation;
        map.AimPointProvider = () => aim;
        current = source;
        for (var i = 0; i < 5; i++)
        {
            map.SelectedActor = current;
            map.CopySelectedCommand.Execute(null);
            aim += new FVector(0f, 200f, 0f);
            map.SelectedActor = null;
            map.PasteCommand.Execute(null);
            current = map.SelectedActor!;
            var world = map.SelectedRootWorld!.Value;
            Assert.True(world.Rotation.Equals(start.Rotation, 1e-5f), $"paste {i + 1} turned by {world.Rotation.AngularDistance(start.Rotation) * 57.3f:0.###}°");
        }

        // Every zombie place of the area (some stand tilted): a copy and a move keep the turn.
        var tilted = 0;
        foreach (var place in zombies.Take(60))
        {
            map.SelectedActor = place;
            var before = map.SelectedRootWorld!.Value;
            var r = before.Rotator();
            tilted += MathF.Abs(r.Pitch) > 0.5f || MathF.Abs(r.Roll) > 0.5f ? 1 : 0;
            map.DuplicateSelectedCommand.Execute(null);
            var copy = map.SelectedRootWorld!.Value;

            Assert.True(copy.Rotation.Equals(before.Rotation, 1e-5f), $"{place.Name} ({r}) copy turned by {copy.Rotation.AngularDistance(before.Rotation) * 57.3f:0.###}°");
            map.ApplyDraggedTransform(map.SelectedActor!.SelectableId, copy with { Translation = copy.Translation + new FVector(0f, 0f, 50f) });
            Assert.True(map.SelectedRootWorld!.Value.Rotation.Equals(before.Rotation, 1e-5f), $"{place.Name} move turned");
        }

        Assert.DoesNotContain(toasts, t => t.Severity >= ScumStudio.App.Services.ToastSeverity.Warning);
        Assert.True(project.Journal.Applied.Count >= 10);
    }
}
