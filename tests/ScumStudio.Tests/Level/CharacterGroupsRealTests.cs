using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The island's groups of zombie / NPC points (<c>EncounterGroupedCharacterSpawnPoints</c>: where the armed NPC encounters,
/// which spawn as a group, put their characters) are spawn places now: read with every post, moved and copied with their
/// posts turning about the first, and a new one written with its own posts. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class CharacterGroupsRealTests
{
    [Fact]
    public void GroupsAreReadMovedCopiedAndCreatedOnTheRealStaticData()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var package = ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
        var stock = SpawnPlaces.Read(package);
        var groups = stock.Where(p => p.Kind == SpawnPlaceKind.CharacterGroup).ToList();
        Assert.True(groups.Count > 1000, $"{groups.Count} groups");
        Assert.True(groups.Count(g => g.Group == "None") < 5); // one stock group (NpcGroup_00892) has no type
        Assert.All(groups, g => Assert.True(g.Points.Count >= 2 && g.Group is "Exterior" or "Interior" or "None", $"{g.ActorName}: {g.Points.Count} posts, {g.Group}"));

        var (moved, copied) = (groups[10], groups[11]);
        var to = new TransformValue(moved.Transform.Translation + new FVector(1000f, -500f, 20f), new FRotator(0f, moved.Transform.Rotator().Yaw + 90f, 0f), FVector.One);
        var at = new TransformValue(copied.Transform.Translation + new FVector(0f, 3000f, 0f), copied.Transform.Rotator(), FVector.One);
        var posts = new[] { new FVector(-600000f, -560000f, 5000f), new FVector(-599500f, -560000f, 5010f), new FVector(-599500f, -559400f, 5020f) };
        var request = new SpawnPlacesEditRequest
        {
            Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(moved.Kind, moved.Index)] = to },
            Added = [(copied.Kind, copied.Index, at)],
            Created = [new NewSpawnPlace("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, "Interior", TransformValue.At(posts[0].X, posts[0].Y, posts[0].Z), Points: posts)],
        };

        var (bytes, report) = SpawnPlacesEditor.Apply(package, request);

        Assert.Empty(report.Warnings);
        Assert.Equal((1, 1, 1), (report.Moved, report.Added, report.Created));
        var written = SpawnPlaces.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SpawnPlaces.StaticDataPath));
        var after = written.Where(p => p.Kind == SpawnPlaceKind.CharacterGroup).ToList();
        Assert.Equal(groups.Count + 2, after.Count);
        Assert.Equal(stock.Count + 2, written.Count);

        // Every other group as it was; the moved one turned about its first post; the copy beside its source; the new one on its posts.
        Assert.All(groups.Where(g => g.Index != moved.Index).Zip(after.Where(g => g.Index != moved.Index)), pair => Same(pair.First.Points, pair.Second.Points));
        Same(SpawnPlaces.MovePosts(moved.Points, moved.Transform, to.ToTransform()), after.First(g => g.Index == moved.Index).Points);
        Assert.True(FVector.Distance(after.First(g => g.Index == moved.Index).Points[1] - after.First(g => g.Index == moved.Index).Points[0], to.ToTransform().Rotation.RotateVector(moved.Transform.Rotation.Inverse().RotateVector(moved.Points[1] - moved.Points[0]))) < 1f);
        Same(SpawnPlaces.MovePosts(copied.Points, copied.Transform, at.ToTransform()), after[^2].Points);
        Same(posts, after[^1].Points);
        Assert.Equal(("Interior", copied.Group), (after[^1].Group, after[^2].Group));
    }

    private static void Same(IReadOnlyList<FVector> expected, IReadOnlyList<FVector> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.All(expected.Zip(actual), pair => Assert.True(FVector.Distance(pair.First, pair.Second) < 0.5f, $"{pair.First} / {pair.Second}"));
    }
}
