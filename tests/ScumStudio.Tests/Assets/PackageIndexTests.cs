using ScumStudio.Assets.Catalog;

namespace ScumStudio.Tests.Assets;

public sealed class PackageIndexTests
{
    private static PackageIndex Sample() => new(
    [
        new PackageEntry("SCUM/Content/A/SM_Rock.uasset", "/Game/A/SM_Rock", "StaticMesh"),
        new PackageEntry("SCUM/Content/A/B/T_Rock_D.uasset", "/Game/A/B/T_Rock_D", "Texture2D"),
        new PackageEntry("SCUM/Content/A/B/SM_Tree.uasset", "/Game/A/B/SM_Tree", "StaticMesh"),
        new PackageEntry("SCUM/Content/Maps/L_Test.umap", "/Game/Maps/L_Test", "World"),
    ], hasClasses: true);

    [Fact]
    public void BuildsFolderTree()
    {
        var index = Sample();
        Assert.Equal(4, index.Count);
        var game = index.Root.GetFolder("Game");
        Assert.NotNull(game);
        Assert.Equal(4, game!.TotalPackageCount);
        var b = index.GetFolder("/Game/A/B");
        Assert.NotNull(b);
        Assert.Equal("/Game/A/B", b!.Path);
        Assert.Equal(["SM_Tree", "T_Rock_D"], b.Packages.Select(p => p.Name));
        Assert.Equal(["A", "Maps"], game.Folders.Select(f => f.Name));
        Assert.Null(index.GetFolder("/Game/Nope"));
    }

    [Fact]
    public void FindsByPathAndClass()
    {
        var index = Sample();
        Assert.Equal("/Game/A/SM_Rock", index.Find("/Game/A/SM_Rock.SM_Rock")!.PackagePath);
        Assert.Equal("/Game/A/SM_Rock", index.Find("SCUM/Content/A/SM_Rock.uasset")!.PackagePath);
        Assert.Null(index.Find("/Game/A/Missing"));
        Assert.Equal(2, index.WithClass("StaticMesh").Count());
        Assert.Equal(2, index.WithClass("UStaticMesh").Count());
        Assert.Single(index.WithClass("world"));
        Assert.Equal(("StaticMesh", 2), index.ClassHistogram()[0]);
        var map = index.Find("/Game/Maps/L_Test")!;
        Assert.True(map.IsMap);
        Assert.Equal("/Game/Maps/L_Test.L_Test", map.ObjectPath);
        Assert.Equal("/Game/Maps", map.Folder);
    }

    [Theory]
    [InlineData("StaticMesh", "StaticMesh", true)]
    [InlineData("StaticMesh", "UStaticMesh", true)]
    [InlineData("StaticMeshActor", "AStaticMeshActor", true)]
    [InlineData("StaticMesh", "Static", false)]
    [InlineData("Texture2D", "UTexture", false)]
    public void ClassNameMatches_HandlesPrefixes(string actual, string wanted, bool expected) =>
        Assert.Equal(expected, PackageIndex.ClassNameMatches(actual, wanted));
}
