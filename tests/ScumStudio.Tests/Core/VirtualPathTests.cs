using ScumStudio.Core.Abstractions;

namespace ScumStudio.Tests.Core;

public sealed class VirtualPathTests
{
    [Theory]
    [InlineData(@"SCUM\Content\Maps\A.umap", "SCUM/Content/Maps/A.umap")]
    [InlineData("/SCUM//Content/./Maps/", "SCUM/Content/Maps")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) =>
        Assert.Equal(expected, VirtualPath.Normalize(input));

    [Fact]
    public void IsUnderIsCaseInsensitiveAndSegmentAware()
    {
        Assert.True(VirtualPath.IsUnder("SCUM/Content/Maps/A.umap", "scum/content"));
        Assert.True(VirtualPath.IsUnder("SCUM/Content", "SCUM/Content/"));
        Assert.False(VirtualPath.IsUnder("SCUM/ContentX/A.umap", "SCUM/Content"));
        Assert.True(VirtualPath.IsUnder("anything", ""));
    }

    [Fact]
    public void FileNameParts()
    {
        const string path = "SCUM/Content/Maps/A_0_Outpost.umap";
        Assert.Equal(".umap", VirtualPath.GetExtension(path));
        Assert.Equal("A_0_Outpost.umap", VirtualPath.GetFileName(path));
        Assert.Equal("SCUM/Content/Maps", VirtualPath.GetDirectory(path));
        Assert.Equal("SCUM/Content/Maps/A_0_Outpost.uexp", VirtualPath.ChangeExtension(path, ".uexp"));
        Assert.Equal("SCUM/Content/Maps/A_0_Outpost", VirtualPath.ChangeExtension(path, null));
        Assert.Equal("SCUM/Content/Maps/A.umap", VirtualPath.Combine("SCUM", "Content/", @"\Maps", "A.umap"));
    }
}
