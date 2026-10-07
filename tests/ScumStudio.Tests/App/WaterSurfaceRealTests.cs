using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "the water surfaces I placed in A_4_Farm_04 never appear in the game". He had picked the lakes' <c>_FN</c>
/// twins: the undersides (flipped normals, underwater material) the game shows only from under the water. The Map
/// refuses them, says why and names the top surface; one already in a project is marked. Real game files only
/// (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class WaterSurfaceRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_4_Farm_04";
    private const string LakeUnderside = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1_FN.LakeWaterSurface_C_1_FN";
    private const string LakeTop = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1.LakeWaterSurface_C_1";

    [Fact]
    public async Task TheUndersideOfALakeIsRefusedWithTheTopSurfaceNamed()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Water");
        await map.LoadLevelsAsync([Farm]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(1000f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // Refused: nothing journaled, a toast that names the mesh and the surface to place instead.
        Assert.False(map.AddMeshActor(LakeUnderside));
        Assert.Empty(project.Journal.Applied);
        var toast = ctx.Services.Notifications.Toasts[^1];
        Assert.Equal(Loc.T("Map.Underside"), toast.Title);
        Assert.Contains("LakeWaterSurface_C_1_FN", toast.Message, StringComparison.Ordinal);
        Assert.Contains("LakeWaterSurface_C_1", toast.Message.Replace("LakeWaterSurface_C_1_FN", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The top surface goes in (whether the game draws a lake placed as a plain actor is for the owner's in-game test).
        Assert.True(map.AddMeshActor(LakeTop));
        Assert.Equal(LakeTop, Assert.IsType<AddStaticMeshActorOp>(project.Journal.Applied[^1].Op).StaticMesh);

        // An underside an older project already holds: listed, marked, and its properties say why it is not in the game.
        var old = new AddStaticMeshActorOp(Farm, "LakeWaterSurface_C_1_FN_Added", LakeUnderside, TransformValue.At(aim.X, aim.Y, aim.Z));
        ctx.Services.Projects.Apply(old);
        var item = map.AllActors.Single(a => a.Name == old.NewName);
        Assert.True(item.IsHiddenInGame);
        Assert.False(map.AllActors.Single(a => a.Name.StartsWith("LakeWaterSurface_C_1_Added", StringComparison.Ordinal)).IsHiddenInGame);
        map.SelectedActor = item;
        Assert.Contains(map.ActorProperties, r => r.Value == Loc.T("Map.State.HiddenInGame"));
    }
}
