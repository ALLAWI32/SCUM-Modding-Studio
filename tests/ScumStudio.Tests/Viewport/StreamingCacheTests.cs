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

    [Fact]
    public void ALevelThatStaysKeepsItsSlotAndItsActorsTheirIds()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var level = LevelDocument.Load(new Cue4ParseLevelReader(catalog), SyntheticLevels.LevelPath);
        static LevelDocument Empty(string name) => LevelDocument.FromData(new LevelData { PackagePath = "/Game/Maps/" + name, Exports = [], ActorIndices = [] });
        var (a, b, d) = (Empty("A"), Empty("B"), Empty("D"));
        var preparer = new LevelScenePreparer(catalog);

        // A first load numbers its levels like their indices.
        var slots = LevelScenePreparer.SlotsAfter([a, b, level], null);
        Assert.Equal([0, 1, 2], slots);
        var first = preparer.Prepare([a, b, level], new LevelSceneOptions { DocumentSlots = slots });

        // A goes and D comes: B and the level keep 1 and 2 although they move up the list, D takes 3 (0 stays A's while the
        // first scene is still on screen).
        slots = LevelScenePreparer.SlotsAfter([b, level, d], first);
        Assert.Equal([1, 2, 3], slots);
        var second = preparer.Prepare([b, level, d], new LevelSceneOptions { DocumentSlots = slots });
        var actor = level.Actors[0];
        Assert.Equal(first.IdOf(2, actor), second.IdOf(1, actor));
        Assert.Equal(LevelScenePreparer.SelectableIdOf(2, actor), second.IdOf(1, actor));
        Assert.All(second.Placements, p => Assert.Equal(2, p.DocumentIndex)); // only the level draws anything: its placements carry its slot
        Assert.Throws<ArgumentException>(() => preparer.Prepare([b, level], new LevelSceneOptions { DocumentSlots = [0] }));
    }

    [Fact]
    public void ANewLevelNeverTakesASlotTheShownSceneUses()
    {
        // F010: the view draws and picks the shown scene until the next one is uploaded while the page already reads ids in
        // the next one's numbering; a level arriving in the slot of one that leaves made a click on the leaving level edit it.
        var shown = SceneOf([NamedLevel("A")], 0);
        Assert.Equal([1], LevelScenePreparer.SlotsAfter([NamedLevel("B")], shown));
    }

    [Fact]
    public void SlotsStayLowOverALongStream()
    {
        var pool = Enumerable.Range(0, 24).Select(i => NamedLevel("L" + i)).ToArray();
        var random = new Random(7);
        PreparedLevelScene? shown = null;
        var start = 0;
        for (var round = 0; round < 40; round++)
        {
            start = Math.Clamp(start + random.Next(-2, 4), 0, pool.Length - 8);
            var documents = pool.Skip(start).Take(random.Next(3, 9)).OrderBy(_ => random.Next()).ToArray();
            var slots = LevelScenePreparer.SlotsAfter(documents, shown);
            Assert.Equal(slots.Length, slots.Distinct().Count());
            var before = Enumerable.Range(0, shown?.Documents.Count ?? 0).ToDictionary(i => shown!.Documents[i], i => shown!.SlotOf(i));
            for (var i = 0; i < documents.Length; i++)
            {
                if (before.TryGetValue(documents[i], out var kept))
                {
                    Assert.Equal(kept, slots[i]);
                }
                else
                {
                    // An arriving level takes none of the shown slots and stays below |shown| + |new|: no climbing.
                    Assert.DoesNotContain(slots[i], before.Values);
                    Assert.True(slots[i] < before.Count + documents.Length, $"round {round}: slot {slots[i]}");
                }
            }

            shown = SceneOf(documents, slots);
        }
    }

    [Fact]
    public void AnIdNamesTheSameActorOnlyWhileItsSlotHoldsTheSameLevel()
    {
        var (a, b, d) = (NamedLevel("A"), NamedLevel("B"), NamedLevel("D"));
        var first = SceneOf([a, b], 0, 1);
        var next = SceneOf([b, d], LevelScenePreparer.SlotsAfter([b, d], first)); // B keeps 1, D takes 2
        var taken = SceneOf([d, b], 0, 1); // D in A's slot, as the overflow fallback or an older scene can have it
        const uint ofA = (0u << 20) | 5u, ofB = (1u << 20) | 5u, pinOfB = ofB | (1u << 19);

        Assert.True(LevelScenePreparer.SameActorIn(first, next, ofB));
        Assert.True(LevelScenePreparer.SameActorIn(first, taken, pinOfB));
        Assert.False(LevelScenePreparer.SameActorIn(first, next, ofA)); // A left
        Assert.False(LevelScenePreparer.SameActorIn(first, taken, ofA)); // and D took its slot
        Assert.True(LevelScenePreparer.SameActorIn(first, first, ofA));
        Assert.False(LevelScenePreparer.SameActorIn(first, SceneOf([a, NamedLevel("B")], 0, 1), ofB)); // B read again: its exports may differ

        // Nothing, added actors and previews (the page numbers them from 2^31) are the same in any scene.
        Assert.True(LevelScenePreparer.SameActorIn(first, taken, 0));
        Assert.True(LevelScenePreparer.SameActorIn(first, taken, 0x8000_0000u));
        Assert.True(LevelScenePreparer.SameActorIn(null, taken, 0xC000_0003u));

        Assert.False(LevelScenePreparer.SameActorIn(null, taken, ofB));
        Assert.False(LevelScenePreparer.SameActorIn(first, null, ofB));
        var shifted = new PreparedLevelScene([b, d], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [], [], [], TimeSpan.Zero)
        {
            Slots = [1, 2],
            IdShift = 16,
        };
        Assert.False(LevelScenePreparer.SameActorIn(first, shifted, ofB));
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

    internal static LevelDocument NamedLevel(string name) => LevelDocument.FromData(new LevelData { PackagePath = "/Game/Maps/" + name, Exports = [], ActorIndices = [] });

    /// <summary>A scene of <paramref name="documents"/> in <paramref name="slots"/> that draws nothing (the slots are all that matter here).</summary>
    internal static PreparedLevelScene SceneOf(IReadOnlyList<LevelDocument> documents, params int[] slots) =>
        new(documents, [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [], [], [], TimeSpan.Zero) { Slots = slots };
}
