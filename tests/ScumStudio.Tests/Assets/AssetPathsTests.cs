using ScumStudio.Assets.Catalog;

namespace ScumStudio.Tests.Assets;

public sealed class AssetPathsTests
{
    [Theory]
    [InlineData("SCUM/Content/ConZ_Files/A/SM_B.uasset", "/Game/ConZ_Files/A/SM_B")]
    [InlineData("SCUM/Content/Maps/X.umap", "/Game/Maps/X")]
    [InlineData("scum\\Content\\A\\B.uexp", "/Game/A/B")]
    [InlineData("Engine/Content/BasicShapes/Cube.uasset", "/Engine/BasicShapes/Cube")]
    [InlineData("Other/Thing.uasset", "/Other/Thing")]
    public void ToPackagePath_MapsProviderPaths(string file, string expected) =>
        Assert.Equal(expected, AssetPaths.ToPackagePath(file));

    [Theory]
    [InlineData("/Game/A/B.B", "SCUM/Content/A/B")]
    [InlineData("/Game/A/B", "SCUM/Content/A/B")]
    [InlineData("Game/A/B.B:Sub", "SCUM/Content/A/B")]
    [InlineData("SCUM/Content/A/B.uasset", "SCUM/Content/A/B")]
    [InlineData("/Engine/BasicShapes/Cube.Cube", "Engine/Content/BasicShapes/Cube")]
    [InlineData("StaticMesh'/Game/A/B.B'", "SCUM/Content/A/B")]
    public void ToFilePathWithoutExtension_AcceptsAllSpellings(string path, string expected) =>
        Assert.Equal(expected, AssetPaths.ToFilePathWithoutExtension(path));

    [Theory]
    [InlineData("/Game/A/B.B", "/Game/A/B", "B")]
    [InlineData("/Game/A/B", "/Game/A/B", "B")]
    [InlineData("/Game/A/B.B:PersistentLevel", "/Game/A/B", "PersistentLevel")]
    [InlineData("/Game/A/B.C", "/Game/A/B", "C")]
    [InlineData("SCUM/Content/A/B.uasset", "SCUM/Content/A/B", "B")]
    [InlineData("Texture2D'/Game/T/T_X.T_X'", "/Game/T/T_X", "T_X")]
    public void SplitObjectPath_SplitsPackageAndObject(string path, string package, string name)
    {
        var (p, n) = AssetPaths.SplitObjectPath(path);
        Assert.Equal(package, p);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("SCUM/Content/A/MI_X.MI_X", "/Game/A/MI_X.MI_X")]
    [InlineData("/Game/A/MI_X.MI_X", "/Game/A/MI_X.MI_X")]
    [InlineData("SCUM/Content/Maps/L.L:PersistentLevel", "/Game/Maps/L.L:PersistentLevel")]
    public void NormalizeObjectPath_UsesGameRoot(string path, string expected) =>
        Assert.Equal(expected, AssetPaths.NormalizeObjectPath(path));

    [Fact]
    public void ToObjectPath_AddsMainExportName()
    {
        Assert.Equal("/Game/A/B.B", AssetPaths.ToObjectPath("SCUM/Content/A/B.uasset"));
        Assert.Equal("/Game/A/B.Other", AssetPaths.ToObjectPath("/Game/A/B", "Other"));
    }

    [Fact]
    public void ParseAesKey_AcceptsHexAndRejectsWithoutEchoing()
    {
        // Built at runtime: the repository never contains key-like literals.
        var hex = string.Concat(Enumerable.Repeat("a5", 32));
        var key = AssetCatalog.ParseAesKey("0x" + hex);
        Assert.Equal(32, key.Length);
        Assert.All(key, b => Assert.Equal(0xA5, b));

        var spaced = string.Join(' ', Enumerable.Repeat("A5A5A5A5", 8));
        Assert.Equal(key, AssetCatalog.ParseAesKey(spaced));

        var bad = hex[..^1] + "z";
        var ex = Assert.Throws<ArgumentException>(() => AssetCatalog.ParseAesKey(bad));
        Assert.DoesNotContain(hex[..16], ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => AssetCatalog.ParseAesKey(hex[..62]));
    }

    [Fact]
    public void FindLooseProjectRoot_AcceptsParentProjectAndContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "scumstudio-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = Path.Combine(root, "extracted", "SCUM", "Content", "ConZ_Files");
            Directory.CreateDirectory(content);
            var project = Path.Combine(root, "extracted", "SCUM");
            Assert.Equal(project, AssetCatalog.FindLooseProjectRoot(Path.Combine(root, "extracted")));
            Assert.Equal(project, AssetCatalog.FindLooseProjectRoot(project));
            Assert.Equal(project, AssetCatalog.FindLooseProjectRoot(Path.Combine(project, "Content")));
            Assert.Null(AssetCatalog.FindLooseProjectRoot(content));
            Assert.Null(AssetCatalog.FindLooseProjectRoot(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenLoose_EmptyProject_HasNoPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "scumstudio-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "MyExtract", "Content", "Maps"));
            File.WriteAllText(Path.Combine(root, "MyExtract", "Content", "Maps", "readme.txt"), "not an asset");
            using var catalog = AssetCatalog.Open(Path.Combine(root, "MyExtract"));
            Assert.Equal(AssetSourceKind.Loose, catalog.SourceKind);
            Assert.Empty(catalog.PackageFiles);
            Assert.False(catalog.PackageExists("/Game/Maps/Nothing"));
            Assert.False(catalog.TryLoadPackage("/Game/Maps/Nothing", out _));
            Assert.Throws<FileNotFoundException>(() => catalog.LoadPackage("/Game/Maps/Nothing"));
            Assert.Equal(0, catalog.BuildIndex().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
