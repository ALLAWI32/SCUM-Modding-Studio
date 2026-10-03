using ScumStudio.Assets.Catalog;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary><see cref="WorldIndex.WithTileInfo"/>: World Composition tile info per sublevel, from the package headers.</summary>
public sealed class WorldTileIndexTests
{
    [Fact]
    public void LeavesPackagesWithoutTileInfoAlone()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: false);
        SyntheticLevels.WritePersistentLevel(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var index = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        var level = index.Find("A_0_TestLevel")!;
        Assert.Null(level.Tile);
        Assert.Null(level.ParentPackagePath);
        Assert.Null(index.Parent(level));
        Assert.Empty(index.Children(level));
        Assert.Empty(index.RootTiles);
    }

    [MapSliceFact]
    public void ReadsTheOutpostHierarchyFromTheSlice()
    {
        using var catalog = MapSlice.Open();
        var progress = new List<int>();
        var index = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog, progress: new SynchronousProgress(progress));
        Assert.Equal(index.Packages.Count, progress.Count);

        var outpost = index.Find("A_0_Outpost")!;
        Assert.NotNull(outpost.Tile);
        Assert.True(outpost.Tile.BoundsValid);
        Assert.True(outpost.Tile.HasParent);
        Assert.EndsWith("/Landscape_A_0_1", outpost.ParentPackagePath, StringComparison.Ordinal);

        var landscape = index.Parent(outpost)!;
        Assert.Equal(WorldPackageKind.Landscape, landscape.Kind);
        Assert.NotNull(landscape.Tile);
        Assert.False(landscape.Tile.HasParent);
        Assert.Contains(landscape, index.RootTiles);
        Assert.Contains(index.Children(landscape), p => p.Name == "A_0_Outpost");

        var exterior = index.Find("A_0_Outpost_Exterior")!;
        Assert.Same(outpost, index.Parent(exterior));
        var saloon = index.Find("A_0_Outpost_Ext_Saloon")!;
        Assert.Same(outpost, index.Parent(saloon)); // building exteriors hang off the outpost tile, not off _Exterior
        Assert.Equal(2, saloon.Tile!.ZOrder);
        Assert.Contains(index.Children(saloon), p => p.Name == "A_0_Outpost_Saloon");
        Assert.Contains("Outpost", saloon.Tile!.Layer.Name, StringComparison.Ordinal);
        Assert.All(index.Sublevels.Where(p => p.Tile is not null), p => Assert.True(p.Tile!.Layer.StreamingDistance > 0));
    }

    private sealed class SynchronousProgress(List<int> sink) : IProgress<int>
    {
        public void Report(int value) => sink.Add(value);
    }
}

/// <summary>World mode: the cell under the camera is found from the landscape tiles' bounds.</summary>
public sealed class WorldCellAtTests
{
    [MapSliceFact]
    public void TheOutpostIsInA0AndTheOpenSeaIsInNoCell()
    {
        using var catalog = MapSlice.Open();
        var index = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        Assert.Equal(MapCell.Parse("A_0"), ScumStudio.App.ViewModels.MapPageViewModel.CellAt(index, -622000f, -556000f));
        Assert.Null(ScumStudio.App.ViewModels.MapPageViewModel.CellAt(index, 5_000_000f, 5_000_000f));
    }
}
