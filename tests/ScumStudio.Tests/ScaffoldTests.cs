using ScumStudio.Core;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void AllAssembliesLoad()
    {
        Assert.Equal("ScumStudio.Core", CoreInfo.Name);
        Assert.Equal("ScumStudio.Formats", ScumStudio.Formats.FormatsInfo.Name);
        Assert.Equal("ScumStudio.Pak", ScumStudio.Pak.PakInfo.Name);
        Assert.Equal("ScumStudio.Assets", ScumStudio.Assets.AssetsInfo.Name);
        Assert.Equal("ScumStudio.Level", ScumStudio.Level.LevelInfo.Name);
        Assert.Equal("ScumStudio.Rendering", ScumStudio.Rendering.RenderingInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(CoreInfo.Version));
    }

    [FixturesFact]
    public void FixtureArchiveHasStockContent()
    {
        Assert.True(Directory.Exists(FixturePaths.OrigContent), FixturePaths.OrigContent);
        Assert.NotEmpty(FixturePaths.OrigFiles(".uasset"));
        Assert.NotEmpty(FixturePaths.ClientPaks());
        Assert.NotEmpty(FixturePaths.BuildSepTrees());
        Assert.True(File.Exists(FixturePaths.StockAssetRegistry), FixturePaths.StockAssetRegistry);
    }
}
