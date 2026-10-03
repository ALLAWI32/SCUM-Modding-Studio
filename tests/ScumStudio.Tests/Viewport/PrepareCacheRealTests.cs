using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner: "the church has no textures, only white, like San Andreas". Streaming away dropped the church's decoded textures
/// from the prepare cache but kept its materials' looks, so coming back drew it with no texture. Real game files only.
/// </summary>
public sealed class PrepareCacheRealTests
{
    [Fact]
    public void ABuildingStreamedInAgainKeepsItsTextures()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = ScumStudio.Pak.AesKeyText.FromEnvironmentOrStore() });
        var church = LevelDocument.Load(new Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/C_3_Church");
        var preparer = new LevelScenePreparer(catalog);
        var cache = new LevelPrepareCache();
        var options = new LevelSceneOptions { IncludeInstances = false, TextureSize = 64 };

        var first = preparer.Prepare([church], options, cache: cache);
        for (var away = 0; away < LevelPrepareCache.KeepGenerations; away++)
        {
            preparer.Prepare([], options, cache: cache); // flown elsewhere: the church's meshes and images are dropped
        }

        var again = preparer.Prepare([church], options, cache: cache);
        var drawn = again.Meshes.Values.SelectMany(m => m.MaterialTextures.Values).Distinct().ToList();
        Assert.Equal(first.Textures.Count, again.Textures.Count);
        Assert.NotEmpty(drawn);
        Assert.All(drawn, t => Assert.True(again.Textures.ContainsKey(t), t));
    }
}
