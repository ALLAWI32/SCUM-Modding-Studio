using ScumStudio.Pak;
using ScumStudio.Pak.Inspection;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Pak;

public sealed class PakWriterTests
{
    [Fact]
    public void PathHash_MatchesRepakAndUnrealVectors()
    {
        // Values read from a repak 0.2.3 V11 pak (seed 0) and from the user's SCUM mod paks.
        Assert.Equal(0x750a701a49186ee4UL, PakPathHash.Compute("SCUM/AssetRegistry.bin"));
        Assert.Equal(0x0072ebe2db361ac6UL, PakPathHash.Compute("SCUM/Content/C/empty.ubulk"));
        Assert.Equal(PakPathHash.Compute("scum/assetregistry.bin"), PakPathHash.Compute("SCUM/AssetRegistry.bin"));
        Assert.NotEqual(PakPathHash.Compute("SCUM/AssetRegistry.bin", 1), PakPathHash.Compute("SCUM/AssetRegistry.bin"));
    }

    [Theory]
    [InlineData(PakCompression.None)]
    [InlineData(PakCompression.Zlib)]
    public async Task WrittenPak_IsReadByCue4ParseAndInspector(PakCompression compression)
    {
        using var temp = new TempDirectory();
        var files = PakTestData.WriteSampleStaging(temp);
        var pakPath = temp.Combine("out", "pakchunk90-Test_P.pak");
        var writer = new PakWriter(new PakWriterOptions { Compression = compression });

        var result = await writer.WriteFromDirectoryAsync(temp.Combine("stage"), pakPath);

        Assert.Equal(files.Count, result.EntryCount);
        Assert.Empty(result.SkippedFiles);
        Assert.Equal(new FileInfo(pakPath).Length, result.PakSize);
        Assert.False(File.Exists(pakPath + ".tmp"));

        var header = PakInspector.ReadHeader(pakPath);
        Assert.Equal(11, header.Version);
        Assert.False(header.EncryptedIndex);
        Assert.Equal(Guid.Empty, header.EncryptionKeyGuid);
        Assert.Equal("../../../", header.MountPoint);
        Assert.Equal(files.Count, header.EntryCount);
        Assert.Equal(0UL, header.PathHashSeed);
        Assert.True(header.HasPathHashIndex);
        Assert.True(header.HasFullDirectoryIndex);
        Assert.Equal(compression == PakCompression.Zlib ? ["Zlib"] : Array.Empty<string>(), header.CompressionMethods);
        Assert.Empty(await PakInspector.VerifyAsync(pakPath));

        var index = PakInspector.ReadIndex(pakPath);
        Assert.Equal(files.Keys.OrderBy(k => k, StringComparer.Ordinal), index.Entries.Select(e => e.Path).OrderBy(k => k, StringComparer.Ordinal));
        if (compression == PakCompression.Zlib)
        {
            var big = index.Entries.Single(e => e.Path.EndsWith("A_0_Test.uexp", StringComparison.Ordinal));
            Assert.Equal(1, big.CompressionMethodIndex);
            Assert.Equal(4, big.BlockSizes.Count);
            Assert.Equal(PakFormat.DefaultCompressionBlockSize, big.CompressionBlockSize);
            // Incompressible noise and the empty file are stored.
            Assert.Equal(0, index.Entries.Single(e => e.Path.EndsWith("Box.uasset", StringComparison.Ordinal)).CompressionMethodIndex);
            Assert.Equal(0, index.Entries.Single(e => e.Path.EndsWith("Box.ubulk", StringComparison.Ordinal)).CompressionMethodIndex);
            Assert.True(result.CompressedEntryCount >= 3);
        }
        else
        {
            Assert.All(index.Entries, e => Assert.Equal(0, e.CompressionMethodIndex));
        }

        using var source = PakFileSource.OpenFile(pakPath);
        Assert.Equal(files.Keys.OrderBy(k => k, StringComparer.Ordinal), source.EnumerateFiles());
        foreach (var (path, data) in files)
        {
            Assert.True(source.TryGetBytes(path, out var read), path);
            Assert.Equal(data, read);
            Assert.Equal(data, await source.ReadBytesAsync(path.ToLowerInvariant()));
        }

        var archive = Assert.Single(source.Archives);
        Assert.Equal("pakchunk90-Test_P.pak", archive.Name);
        Assert.True(archive.IsMounted);
        Assert.False(archive.IsEncrypted);
        Assert.Equal(11, archive.Version);
    }

    [Fact]
    public async Task Output_IsDeterministic()
    {
        using var temp = new TempDirectory();
        PakTestData.WriteSampleStaging(temp);
        var writer = new PakWriter(new PakWriterOptions { Compression = PakCompression.Zlib });
        await writer.WriteFromDirectoryAsync(temp.Combine("stage"), temp.Combine("a.pak"));
        await writer.WriteFromDirectoryAsync(temp.Combine("stage"), temp.Combine("b.pak"));
        Assert.Equal(await File.ReadAllBytesAsync(temp.Combine("a.pak")), await File.ReadAllBytesAsync(temp.Combine("b.pak")));
    }

    [Fact]
    public async Task StagingFilesOutsideGameContent_AreSkipped_AndRegistryCanBeExcluded()
    {
        using var temp = new TempDirectory();
        PakTestData.WriteSampleStaging(temp);
        temp.Write("stage/report.txt", "notes"u8.ToArray());
        temp.Write("stage/SCUM/Config/Foo.ini", "x"u8.ToArray());
        var writer = new PakWriter(new PakWriterOptions { IncludeAssetRegistry = false });

        var result = await writer.WriteFromDirectoryAsync(temp.Combine("stage"), temp.Combine("p.pak"));

        Assert.Equal(["SCUM/AssetRegistry.bin", "SCUM/Config/Foo.ini", "report.txt"], result.SkippedFiles);
        using var source = PakFileSource.OpenFile(temp.Combine("p.pak"));
        Assert.All(source.EnumerateFiles(), p => Assert.StartsWith("SCUM/Content/", p, StringComparison.Ordinal));
        Assert.False(source.Exists("SCUM/AssetRegistry.bin"));
    }

    [Fact]
    public async Task InMemoryEntries_CustomMountPointAndSeed_RoundTrip()
    {
        using var temp = new TempDirectory();
        var entries = new[]
        {
            PakWriterEntry.FromBytes("SCUM/Content/B.uasset", new byte[] { 1, 2, 3 }),
            PakWriterEntry.FromBytes("SCUM/Content/Sub/Ünïcode.uexp", new byte[] { 4 }),
        };
        var writer = new PakWriter(new PakWriterOptions { MountPoint = "../../../", PathHashSeed = 0xDEADBEEF });
        await writer.WriteAsync(entries, temp.Combine("m.pak"));

        var index = PakInspector.ReadIndex(temp.Combine("m.pak"));
        Assert.Equal(0xDEADBEEFUL, index.Header.PathHashSeed);
        Assert.Empty(await PakInspector.VerifyAsync(temp.Combine("m.pak")));
        using var source = PakFileSource.OpenFile(temp.Combine("m.pak"));
        Assert.True(source.TryGetBytes("SCUM/Content/Sub/Ünïcode.uexp", out var data));
        Assert.Equal(new byte[] { 4 }, data);
    }

    [Fact]
    public async Task DuplicatePathsDifferingOnlyByCase_AreRejected()
    {
        using var temp = new TempDirectory();
        var entries = new[]
        {
            PakWriterEntry.FromBytes("SCUM/Content/A.uasset", new byte[] { 1 }),
            PakWriterEntry.FromBytes("SCUM/content/a.uasset", new byte[] { 2 }),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => new PakWriter().WriteAsync(entries, temp.Combine("d.pak")));
        Assert.False(File.Exists(temp.Combine("d.pak")));
        Assert.False(File.Exists(temp.Combine("d.pak.tmp")));
    }

    [Fact]
    public async Task EmptyStaging_Throws()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("stage", "SCUM", "Content"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PakWriter().WriteFromDirectoryAsync(temp.Combine("stage"), temp.Combine("e.pak")));
    }

    [Fact]
    public void Inspector_RejectsNonPak()
    {
        using var temp = new TempDirectory();
        var file = temp.Write("x.pak", new byte[500]);
        Assert.Throws<InvalidDataException>(() => PakInspector.ReadHeader(file));
    }
}
