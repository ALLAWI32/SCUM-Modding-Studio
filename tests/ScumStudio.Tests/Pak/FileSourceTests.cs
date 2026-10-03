using ScumStudio.Core.Abstractions;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Pak;

public sealed class FileSourceTests
{
    [Fact]
    public async Task LooseFileSource_IsCaseInsensitive_AndEnumerates()
    {
        using var temp = new TempDirectory();
        temp.Write("root/SCUM/Content/Maps/A.umap", [1]);
        temp.Write("root/SCUM/Content/Maps/Sub/B.uexp", [2]);
        temp.Write("root/SCUM/AssetRegistry.bin", [3]);
        var source = new LooseFileSource(temp.Combine("root"));

        Assert.Equal(3, source.Count);
        Assert.True(source.Exists("scum/content/maps/a.UMAP"));
        Assert.True(source.Exists("/SCUM\\Content//Maps/A.umap"));
        Assert.True(source.TryGetBytes("SCUM/Content/Maps/Sub/B.uexp", out var b));
        Assert.Equal(new byte[] { 2 }, b);
        Assert.Null(await source.ReadBytesAsync("SCUM/Content/Nope.uasset"));
        Assert.False(source.TryGetBytes("SCUM/Content/Nope.uasset", out _));
        Assert.Equal(["SCUM/AssetRegistry.bin", "SCUM/Content/Maps/A.umap", "SCUM/Content/Maps/Sub/B.uexp"], source.EnumerateFiles());
        Assert.Equal(["SCUM/Content/Maps/A.umap", "SCUM/Content/Maps/Sub/B.uexp"], source.EnumerateFiles("SCUM/Content/Maps"));
        Assert.Equal(["SCUM/Content/Maps/A.umap"], source.EnumerateFiles("scum/content/maps", recursive: false));
        Assert.Equal(["SCUM/AssetRegistry.bin"], source.EnumerateFiles("SCUM", recursive: false));
        Assert.Empty(source.EnumerateFiles("SCUM/Content/Map"));

        temp.Write("root/SCUM/Content/New.uasset", [4]);
        Assert.False(source.Exists("SCUM/Content/New.uasset"));
        source.Refresh();
        Assert.True(source.Exists("SCUM/Content/New.uasset"));
    }

    [Fact]
    public void LooseFileSource_VirtualPrefix()
    {
        using var temp = new TempDirectory();
        temp.Write("Content/X.uasset", [1]);
        var source = new LooseFileSource(temp.Combine("Content"), "SCUM/Content");
        Assert.Equal(["SCUM/Content/X.uasset"], source.EnumerateFiles());
        Assert.Equal(temp.Combine("Content", "X.uasset"), source.GetFullPath("SCUM/Content/x.uasset"));
    }

    [Fact]
    public async Task CompositeFileSource_FirstSourceWins_AndUnionIsListed()
    {
        using var temp = new TempDirectory();
        temp.Write("mod/SCUM/Content/Shared.uasset", "mod"u8.ToArray());
        temp.Write("mod/SCUM/Content/OnlyMod.uasset", "m"u8.ToArray());
        temp.Write("stock/SCUM/Content/shared.uasset", "stock"u8.ToArray());
        temp.Write("stock/SCUM/Content/OnlyStock.uasset", "s"u8.ToArray());
        var mod = new LooseFileSource(temp.Combine("mod"));
        var stock = new LooseFileSource(temp.Combine("stock"));

        using var overlay = new CompositeFileSource(mod, stock);
        Assert.True(overlay.TryGetBytes("SCUM/Content/Shared.uasset", out var shared));
        Assert.Equal("mod"u8.ToArray(), shared);
        Assert.Equal("stock"u8.ToArray(), await new CompositeFileSource(stock, mod).ReadBytesAsync("SCUM/Content/Shared.uasset"));
        Assert.Same(mod, overlay.FindSource("scum/content/shared.uasset"));
        Assert.Same(stock, overlay.FindSource("SCUM/Content/OnlyStock.uasset"));
        Assert.Null(overlay.FindSource("SCUM/Content/Missing.uasset"));
        Assert.False(overlay.TryGetBytes("SCUM/Content/Missing.uasset", out _));
        Assert.Null(await overlay.ReadBytesAsync("SCUM/Content/Missing.uasset"));
        Assert.Equal(
            ["SCUM/Content/OnlyMod.uasset", "SCUM/Content/OnlyStock.uasset", "SCUM/Content/Shared.uasset"],
            overlay.EnumerateFiles("SCUM/Content"));
        Assert.Contains(">", overlay.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeFileSource_ModPakOverStockLooseTree()
    {
        using var temp = new TempDirectory();
        temp.Write("stock/SCUM/Content/Items/Knife.uasset", "stock knife"u8.ToArray());
        temp.Write("stock/SCUM/Content/Items/Axe.uasset", "stock axe"u8.ToArray());
        await new PakWriter().WriteAsync(
            [PakWriterEntry.FromBytes("SCUM/Content/Items/Knife.uasset", "mod knife"u8.ToArray())],
            temp.Combine("paks", "pakchunk99-Knife_P.pak"));

        using var modPaks = PakFileSource.OpenDirectory(temp.Combine("paks"));
        using var overlay = new CompositeFileSource([modPaks, new LooseFileSource(temp.Combine("stock"))], ownsSources: true);

        Assert.Equal("mod knife"u8.ToArray(), await overlay.ReadBytesAsync("SCUM/Content/Items/Knife.uasset"));
        Assert.Equal("stock axe"u8.ToArray(), await overlay.ReadBytesAsync("SCUM/Content/Items/Axe.uasset"));
        Assert.Equal("pakchunk99-Knife_P.pak", modPaks.GetArchiveName("SCUM/Content/Items/Knife.uasset"));
        Assert.Equal(2, overlay.EnumerateFiles().Count());
    }

    [Fact]
    public async Task PakFileSource_OpenDirectory_FilterAndPriority()
    {
        using var temp = new TempDirectory();
        await new PakWriter().WriteAsync(
            [PakWriterEntry.FromBytes("SCUM/Content/A.uasset", "client"u8.ToArray())], temp.Combine("paks", "pakchunk96-X_P.pak"));
        await new PakWriter().WriteAsync(
            [PakWriterEntry.FromBytes("SCUM/Content/A.uasset", "server"u8.ToArray()), PakWriterEntry.FromBytes("SCUM/Content/S.uasset", new byte[] { 1 })],
            temp.Combine("paks", "server_pakchunk96-X_P.pak"));

        using (var clientOnly = PakFileSource.OpenDirectory(temp.Combine("paks"), new PakFileSourceOptions
               {
                   PakFileFilter = name => !name.StartsWith("server_", StringComparison.OrdinalIgnoreCase),
               }))
        {
            Assert.Single(clientOnly.Archives);
            Assert.Equal(["SCUM/Content/A.uasset"], clientOnly.EnumerateFiles());
            Assert.Equal("client"u8.ToArray(), await clientOnly.ReadBytesAsync("SCUM/Content/A.uasset"));
        }

        using var both = PakFileSource.OpenDirectory(temp.Combine("paks"));
        Assert.Equal(2, both.Archives.Count);
        Assert.Equal(["SCUM/Content/A.uasset", "SCUM/Content/S.uasset"], both.EnumerateFiles());
        Assert.Equal(["SCUM/Content/A.uasset", "SCUM/Content/S.uasset"], both.EnumerateArchiveFiles("server_pakchunk96-X_P.pak"));
        Assert.Throws<ArgumentException>(() => both.EnumerateArchiveFiles("nope.pak"));
    }

    [Fact]
    public void PakFileSource_RejectsMalformedKeyWithoutEchoingIt()
    {
        using var temp = new TempDirectory();
        const string notAKey = "0123456789abcdefXYZ";
        var ex = Assert.Throws<ArgumentException>(() =>
            PakFileSource.OpenDirectory(temp.Path, new PakFileSourceOptions { AesKey = notAKey }));
        Assert.DoesNotContain(notAKey, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PakFileSource_MissingInputs_Throw()
    {
        using var temp = new TempDirectory();
        Assert.Throws<DirectoryNotFoundException>(() => PakFileSource.OpenDirectory(temp.Combine("missing")));
        Assert.Throws<FileNotFoundException>(() => PakFileSource.OpenFile(temp.Combine("missing.pak")));
        Assert.Throws<ArgumentException>(() => PakFileSource.OpenFiles([]));
    }

    [Theory]
    [InlineData(PakCompression.None)]
    [InlineData(PakCompression.Zlib)]
    public async Task PakFileSource_ConcurrentReads_AreConsistent(PakCompression compression)
    {
        using var temp = new TempDirectory();
        var entries = Enumerable.Range(0, 40)
            .Select(i => PakWriterEntry.FromBytes($"SCUM/Content/F{i:D2}.uexp", Enumerable.Repeat((byte)i, 50_000 + i).ToArray()))
            .ToArray();
        await new PakWriter(new PakWriterOptions { Compression = compression }).WriteAsync(entries, temp.Combine("c.pak"));
        using var source = PakFileSource.OpenFile(temp.Combine("c.pak"));

        var results = await Task.WhenAll(Enumerable.Range(0, 160).Select(n => Task.Run(async () =>
        {
            var i = n % 40;
            var data = await source.ReadBytesAsync($"SCUM/Content/F{i:D2}.uexp");
            return data is not null && data.Length == 50_000 + i && data.All(b => b == i);
        })));
        Assert.All(results, Assert.True);
    }

    [Fact]
    public void VirtualPathsFromSources_AreNormalized()
    {
        using var temp = new TempDirectory();
        temp.Write("r/SCUM/Content/A.uasset", [1]);
        var source = new LooseFileSource(temp.Combine("r"));
        Assert.All(source.EnumerateFiles(), p => Assert.Equal(VirtualPath.Normalize(p), p));
    }
}
