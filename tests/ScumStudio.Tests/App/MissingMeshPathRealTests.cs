using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// O Piqueno (3.4.0): the package check stopped his export because A_3_TV_Base_Entrance imported
/// <c>/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_4</c>, a mesh the game does not have (client or server). The Map
/// page's "Add object" box (and Ctrl+V with a copied path) takes any <c>/Game/...</c> path and places it without asking the
/// game files; the export then writes the import and the package check blocks the whole pak. A path the game lacks must be
/// refused at the box, with the "Object not found" toast. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class MissingMeshPathRealTests
{
    private const string TvBase = "/Game/ConZ_Files/Maps/The_Island/A_3_TV_Base_Entrance";
    private const string Missing = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_4";
    private const string RealLake = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_B_3_4";

    [PaksFact]
    public async Task APathTheGameDoesNotHaveIsRefusedAtTheAddObjectBox()
    {
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder! });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        Assert.False(ctx.Services.Workspace.Catalog!.PackageExists(Missing)); // the game has no such mesh
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "MyMapMod");
        await map.LoadLevelsAsync([TvBase]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(1000f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        var added = map.AddObject(Missing);
        var journaled = project.Journal.Applied.Select(e => e.Op).OfType<AddStaticMeshActorOp>().Select(o => o.NewName + " -> " + o.StaticMesh).ToList();
        Assert.False(added, $"AddObject accepted {Missing}; the journal now holds: {string.Join(", ", journaled)}");
        Assert.Empty(project.Journal.Applied);
        Assert.Equal(Loc.T("Map.ObjectNotFound"), ctx.Services.Notifications.Toasts[^1].Title);

        // A lake the game has still goes in, its path typed the same way.
        Assert.True(map.AddObject(RealLake));
        Assert.Equal(RealLake + ".LakeWaterSurface_B_3_4", Assert.Single(project.Journal.Applied.Select(e => e.Op).OfType<AddStaticMeshActorOp>()).StaticMesh);
    }
}
