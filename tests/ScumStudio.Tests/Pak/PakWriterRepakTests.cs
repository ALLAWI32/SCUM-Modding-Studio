using ScumStudio.Pak.Inspection;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Pak;

/// <summary>Validates <see cref="PakWriter"/> against repak on a synthetic staging tree (no SCUM fixtures needed).</summary>
public sealed class PakWriterRepakTests
{
    [RepakFact]
    public async Task RepakReadsOurPaks_AndOurIndexesMatchRepaksByteForByte()
    {
        using var temp = new TempDirectory();
        PakTestData.WriteSampleStaging(temp);
        var stage = temp.Combine("stage");

        foreach (var compression in new[] { PakCompression.None, PakCompression.Zlib })
        {
            var ours = temp.Combine($"ours_{compression}.pak");
            await new PakWriter(new PakWriterOptions { Compression = compression }).WriteFromDirectoryAsync(stage, ours);

            var info = RepakTool.Info(ours);
            Assert.Equal("V11", info["version"]);
            Assert.Equal("Fnv64BugFix", info["version major"]);
            Assert.Equal("../../../", info["mount point"]);
            Assert.Equal("false", info["encrypted index"]);
            Assert.Equal(compression == PakCompression.Zlib ? "Zlib" : "None", info["compression"]);
            Assert.Equal(PakTestData.ListTree(stage), RepakTool.List(ours));

            var unpacked = temp.Combine($"unpacked_{compression}");
            RepakTool.Run("unpack", "-q", "-o", unpacked, ours);
            PakTestData.AssertTreesEqual(stage, unpacked);
        }

        // With stored entries the only freedom is data order, which the index blobs do not depend on:
        // our path-hash index and full directory index must equal repak's exactly.
        var theirs = temp.Combine("repak.pak");
        RepakTool.Run("pack", "-q", "--version", "V11", "-m", "../../../", stage, theirs);
        var theirIndex = PakInspector.ReadIndex(theirs);
        var ourIndex = PakInspector.ReadIndex(temp.Combine("ours_None.pak"));
        Assert.Equal(theirIndex.PathHashIndex, ourIndex.PathHashIndex);
        Assert.Equal(theirIndex.FullDirectoryIndex, ourIndex.FullDirectoryIndex);
        Assert.Equal(theirIndex.Header.IndexSize, ourIndex.Header.IndexSize);
        Assert.Equal(new FileInfo(theirs).Length, new FileInfo(temp.Combine("ours_None.pak")).Length);
        Assert.Equal(
            theirIndex.Entries.Select(e => (e.Path, e.UncompressedSize, e.CompressedSize, e.CompressionMethodIndex)),
            ourIndex.Entries.Select(e => (e.Path, e.UncompressedSize, e.CompressedSize, e.CompressionMethodIndex)));
    }

    [RepakFact]
    public async Task OurInspector_VerifiesRepakPaks()
    {
        using var temp = new TempDirectory();
        PakTestData.WriteSampleStaging(temp);
        var theirs = temp.Combine("repak_zlib.pak");
        RepakTool.Run("pack", "-q", "--version", "V11", "--compression", "Zlib", "-m", "../../../", temp.Combine("stage"), theirs);
        Assert.Empty(await PakInspector.VerifyAsync(theirs));
    }
}
