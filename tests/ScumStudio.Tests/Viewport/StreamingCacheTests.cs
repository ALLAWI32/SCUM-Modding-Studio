using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Streaming the levels around a moving camera must not read, decode and upload everything again each time the set
/// changes: what an earlier preparation made is reused, and only what is new is loaded.
/// </summary>
public sealed class StreamingCacheTests
{
    [Fact]
    public void ASecondPreparationReusesTheMeshesOfTheFirst()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SyntheticLevels.LevelPath);
        var cache = new LevelPrepareCache();
        var preparer = new LevelScenePreparer(catalog);

        var first = preparer.Prepare([document], cache: cache);
        var kept = cache.MeshCount;
        var second = preparer.Prepare([document], cache: cache);

        // The synthetic meshes do not load: the cache remembers that too instead of trying again.
        Assert.True(kept > 0);
        Assert.Equal(kept, cache.MeshCount);
        Assert.Equal(first.MissingMeshes, second.MissingMeshes);
        Assert.Equal(first.Placements.Count, second.Placements.Count);

        // Unused for longer than KeepGenerations preparations: dropped.
        for (var i = 0; i < LevelPrepareCache.KeepGenerations; i++)
        {
            preparer.Prepare([], cache: cache);
        }

        Assert.Equal(0, cache.MeshCount);
    }

    [MapSliceFact]
    public void RealMeshesAreLoadedOncePerCache()
    {
        using var catalog = MapSlice.Open();
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), MapSlice.MapsPath + "A_0_Outpost_Exterior");
        var cache = new LevelPrepareCache();
        var preparer = new LevelScenePreparer(catalog);

        var first = preparer.Prepare([document], new LevelSceneOptions { TextureSize = 64 }, cache: cache);
        var second = preparer.Prepare([document], new LevelSceneOptions { TextureSize = 64 }, cache: cache);

        Assert.NotEmpty(first.Meshes);
        Assert.All(first.Meshes, m => Assert.Same(m.Value, second.Meshes[m.Key])); // spline copies keep their keys too
        Assert.Equal(first.Textures.Keys.Order(), second.Textures.Keys.Order());
    }

    [MapSliceFact]
    public void TheLevelsAroundTheCameraComeFromEverySectorWithinTheRadius()
    {
        using var catalog = MapSlice.Open();
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        var outpost = new FVector(-622000f, -556000f, 3000f);

        var near = ScumStudio.App.ViewModels.MapPageViewModel.LevelsAround(world, outpost, 30_000f);
        Assert.Contains(near, p => p.EndsWith("/A_0_Outpost_Exterior", StringComparison.Ordinal));
        Assert.Contains(near, p => p.Contains("/Landscape_A_0", StringComparison.Ordinal));
        Assert.True(near.FindIndex(p => p.Contains("/Landscape_", StringComparison.Ordinal)) > near.FindLastIndex(p => !p.Contains("/Landscape_", StringComparison.Ordinal)));

        // High above the island nothing is close enough to load.
        Assert.Empty(ScumStudio.App.ViewModels.MapPageViewModel.LevelsAround(world, outpost with { Z = 2_000_000f }, 30_000f));

        // A level the project built on far away (a 1.6 km new bridge) counts where those edits are.
        var far = outpost with { X = outpost.X + 500_000f };
        var someLevel = near[0];
        Assert.DoesNotContain(someLevel, ScumStudio.App.ViewModels.MapPageViewModel.LevelsAround(world, far, 30_000f));
        var edits = new Dictionary<string, (FVector Min, FVector Max)>(StringComparer.OrdinalIgnoreCase) { [someLevel] = (far, far) };
        Assert.Contains(someLevel, ScumStudio.App.ViewModels.MapPageViewModel.LevelsAround(world, far, 30_000f, edits));
    }
    [Fact]
    public void EditsFarFromALevelKeepItLoadedWhereTheyAre()
    {
        const string bridge = "/Game/ConZ_Files/Maps/The_Island/A_0_Dr_Tudman_Bridge";
        var state = new ScumStudio.Level.Editing.EditState();
        var piece = new ScumStudio.Level.Editing.DuplicateActorOp(new ScumStudio.Level.Editing.ActorRef(bridge, "SM_DrTudmanBridge_0"), "SM_DrTudmanBridge_0_Copy9",
            ScumStudio.Level.Model.TransformValue.At(-673179.9f, -563054f, 3442f));
        state.Apply(piece);
        state.Apply(new ScumStudio.Level.Editing.SetTransformOp(piece.Created, ScumStudio.Level.Model.TransformValue.At(-673179.9f, -563054f, 3442f),
            ScumStudio.Level.Model.TransformValue.At(-625562.9f, -583461.1f, 3442f)));
        // A fence copy placed relative to its bridge (small numbers) says nothing about where it is.
        state.Apply(new ScumStudio.Level.Editing.DuplicateActorOp(new ScumStudio.Level.Editing.ActorRef(bridge, "SM_DrTudmanBridge_fence1_A_1"), "Fence_Copy",
            ScumStudio.Level.Model.TransformValue.At(-2181f, 0f, -1f)));

        var box = Assert.Single(ScumStudio.App.ViewModels.MapPageViewModel.EditedBounds(state));
        Assert.Equal(bridge, box.Key);
        Assert.Equal(new FVector(-625562.9f, -583461.1f, 3442f), box.Value.Min);
        Assert.Equal(box.Value.Min, box.Value.Max);
    }
}
