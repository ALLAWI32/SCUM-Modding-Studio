using ScumStudio.Pak.Inspection;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Pak;

/// <summary>
/// Tests against the user's real mod paks (SCUM_FIXTURES) with repak as the oracle.
/// pakchunk96-F16_P.pak was built by the user's make_pak.py (repak) from build_sep_F16/.
/// </summary>
public sealed class PakFixtureTests
{
    private const string F16Pak = "pakchunk96-F16_P.pak";

    [RepakFixturesFact]
    public void PakFileSource_OverArchiveRoot_ListsExactlyWhatRepakLists()
    {
        var pak = FixturePaths.Pak(F16Pak);
        var expected = RepakTool.List(pak);
        Assert.NotEmpty(expected);

        using var root = PakFileSource.OpenDirectory(FixturePaths.RequireRoot());
        Assert.Contains(root.Archives, a => a.Name == F16Pak && a.IsMounted && a.Version == 11 && !a.IsEncrypted);
        Assert.Equal(expected, root.EnumerateArchiveFiles(F16Pak));

        using var single = PakFileSource.OpenFile(pak);
        Assert.Equal(expected, single.EnumerateFiles());
        Assert.All(single.EnumerateFiles(), p => Assert.True(p.StartsWith("SCUM/Content/", StringComparison.Ordinal) || p == "SCUM/AssetRegistry.bin", p));
    }

    [FixturesFact]
    public void PakFileSource_TryGetBytes_MatchesUnpackedBuildTree()
    {
        var tree = FixturePaths.BuildSepTree("F16");
        using var source = PakFileSource.OpenFile(FixturePaths.Pak(F16Pak));
        var paths = source.EnumerateFiles().ToList();
        var samples = new[]
            {
                paths.First(p => p.EndsWith(".uasset", StringComparison.Ordinal)),
                paths.First(p => p.EndsWith(".uexp", StringComparison.Ordinal)),
                paths.FirstOrDefault(p => p.EndsWith(".ubulk", StringComparison.Ordinal)),
                "SCUM/AssetRegistry.bin",
                paths[paths.Count / 2],
                paths[^1],
            }
            .OfType<string>()
            .Distinct()
            .ToList();

        foreach (var path in samples)
        {
            Assert.True(source.TryGetBytes(path, out var data), path);
            var file = Path.Combine([tree, .. path.Split('/')]);
            Assert.Equal(File.ReadAllBytes(file), data);
        }

        Assert.False(source.TryGetBytes("SCUM/Content/Does/Not/Exist.uasset", out _));
    }

    [FixturesFact]
    public void UsersPaks_PathHashesAreReproduced()
    {
        foreach (var pak in FixturePaths.ClientPaks())
        {
            var index = PakInspector.ReadIndex(pak);
            Assert.Equal(11, index.Header.Version);
            Assert.Equal("../../../", index.Header.MountPoint);
            var seed = index.Header.PathHashSeed ?? 0;
            Assert.Equal(index.Entries.Count, index.PathHashes.Count);
            Assert.All(index.Entries, e => Assert.True(index.PathHashes.ContainsKey(PakPathHash.Compute(e.Path, seed)), e.Path));
        }
    }

    [RepakFixturesFact]
    public async Task PakWriter_PacksBuildTree_RepakAndCue4ParseAgree()
    {
        var staging = FixturePaths.BuildSepTree("F16");
        // Only the packable part of the tree: the owner's working folder may hold extra files (e.g. registry backups).
        var stagedFiles = PakTestData.ListTree(staging).Where(p => ScumStudio.Pak.PakPaths.IsModPakEntry(p)).ToList();
        using var temp = new TempDirectory();

        foreach (var compression in new[] { PakCompression.None, PakCompression.Zlib })
        {
            var pak = temp.Combine($"pakchunk96-F16_{compression}_P.pak");
            var result = await new PakWriter(new PakWriterOptions { Compression = compression }).WriteFromDirectoryAsync(staging, pak);
            Assert.Equal(stagedFiles.Count, result.EntryCount);

            var info = RepakTool.Info(pak);
            Assert.Equal("V11", info["version"]);
            Assert.Equal("../../../", info["mount point"]);
            Assert.Equal("false", info["encrypted index"]);
            Assert.Equal(stagedFiles, RepakTool.List(pak));

            var unpacked = temp.Combine($"unpacked_{compression}");
            RepakTool.Run("unpack", "-q", "-o", unpacked, pak);
            PakTestData.AssertTreesEqual(staging, unpacked, stagedFiles);
            Directory.Delete(unpacked, recursive: true);

            using (var source = PakFileSource.OpenFile(pak))
            {
                Assert.Equal(stagedFiles, source.EnumerateFiles());
                foreach (var path in stagedFiles)
                {
                    var data = await source.ReadBytesAsync(path);
                    Assert.NotNull(data);
                    Assert.True(PakTestData.FilesEqual(Path.Combine([staging, .. path.Split('/')]), WriteTemp(temp, data!)), path);
                }
            }

            if (compression == PakCompression.None)
            {
                // Stored paks from the same tree have the same size as the user's repak-built pak and identical indexes.
                var original = PakInspector.ReadIndex(FixturePaths.Pak(F16Pak));
                var ours = PakInspector.ReadIndex(pak);
                Assert.Equal(new FileInfo(FixturePaths.Pak(F16Pak)).Length, new FileInfo(pak).Length);
                Assert.Equal(original.PathHashIndex, ours.PathHashIndex);
                Assert.Equal(original.FullDirectoryIndex, ours.FullDirectoryIndex);
            }
            else
            {
                Assert.True(result.CompressedEntryCount > 0);
                Assert.True(result.PakSize < new FileInfo(FixturePaths.Pak(F16Pak)).Length);
            }

            File.Delete(pak);
        }
    }

    [FixturesFact]
    public async Task CompositeFileSource_ModPakOverridesStockTree()
    {
        using var mod = PakFileSource.OpenFile(FixturePaths.Pak(F16Pak));
        var stock = new LooseFileSource(FixturePaths.OrigRoot);
        using var overlay = new CompositeFileSource(mod, stock);

        var modOnly = mod.EnumerateFiles().First(p => !stock.Exists(p));
        Assert.Same(mod, overlay.FindSource(modOnly));
        var stockOnly = stock.EnumerateFiles().First(p => !mod.Exists(p));
        Assert.Same(stock, overlay.FindSource(stockOnly));
        Assert.Equal(await File.ReadAllBytesAsync(FixturePaths.OrigFile(stockOnly)), await overlay.ReadBytesAsync(stockOnly));

        var union = overlay.EnumerateFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Superset(mod.EnumerateFiles().ToHashSet(StringComparer.OrdinalIgnoreCase), union);
        Assert.Superset(stock.EnumerateFiles().ToHashSet(StringComparer.OrdinalIgnoreCase), union);
    }

    private static string WriteTemp(TempDirectory temp, byte[] data)
    {
        var file = temp.Combine("read.bin");
        File.WriteAllBytes(file, data);
        return file;
    }
}
