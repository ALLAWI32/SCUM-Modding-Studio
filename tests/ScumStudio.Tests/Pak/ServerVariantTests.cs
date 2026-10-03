using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Server;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Pak;

/// <summary>
/// The server variant port must reproduce what tools/server_variant.py produced for the F16:
/// build_sep_F16 (client) + orig_server stub = build_server_F16 (server), byte for byte except the 16-bit
/// non-case-preserving name hashes, which the port writes the way the engine does (see NameHashes remarks).
/// </summary>
public sealed class ServerVariantTests
{
    [FixturesFact]
    public void NameHashes_MatchStoredHashesOfStockPackages()
    {
        var checkedNames = 0;
        foreach (var uasset in FixturePaths.OrigFiles(".uasset").Take(60))
        {
            foreach (var (name, stored) in ReadStoredHashes(File.ReadAllBytes(uasset)))
            {
                Assert.Equal(stored, NameHashes.Compute(name));
                checkedNames++;
            }
        }

        Assert.True(checkedNames > 100);
    }

    [FixturesFact]
    public async Task StubBuilder_ReproducesUsersServerTree()
    {
        var builder = AkAudioEventStubBuilder.FromServerTree(new LooseFileSource(FixturePaths.Combine("orig_server")));
        var client = FixturePaths.BuildSepTree("F16");
        var server = FixturePaths.Combine("build_server_F16");
        using var temp = new TempDirectory();

        // Only the Wwise event folder is copied (the rest of the tree is passed through unchanged by definition).
        var events = Path.Combine("SCUM", "Content", "WwiseAudio", "Event");
        CopyTree(Path.Combine(client, events), temp.Combine("stage", events));

        var replaced = await builder.ApplyInPlaceAsync(temp.Combine("stage"));

        Assert.NotEmpty(replaced);
        Assert.All(replaced, p => Assert.StartsWith("/Game/WwiseAudio/Event/", p, StringComparison.Ordinal));
        Assert.Contains("/Game/WwiseAudio/Event/DefaultWorkUnit/Vehicles/F16/F16_Ignition_Start", replaced);
        AssertPackagesEqualIgnoringNameHashes(Path.Combine(server, events), temp.Combine("stage", events));

        // Idempotent: a stub is itself an AkAudioEvent and is rebuilt to the same bytes.
        var first = PakTestData.ListTree(temp.Combine("stage")).ToDictionary(p => p, p => File.ReadAllBytes(temp.Combine("stage", p)));
        await builder.ApplyInPlaceAsync(temp.Combine("stage"));
        Assert.All(first, kv => Assert.Equal(kv.Value, File.ReadAllBytes(temp.Combine("stage", kv.Key))));
    }

    [FixturesFact]
    public async Task StubBuilder_BuildAsync_CopiesTreeAndReplacesEvents()
    {
        var builder = AkAudioEventStubBuilder.FromServerTree(new LooseFileSource(FixturePaths.Combine("orig_server")));
        using var temp = new TempDirectory();
        var client = temp.Combine("client");
        var events = Path.Combine("SCUM", "Content", "WwiseAudio", "Event");
        CopyTree(Path.Combine(FixturePaths.BuildSepTree("F16"), events), Path.Combine(client, events));
        temp.Write("client/SCUM/Content/Other/Keep.uasset", [1, 2, 3]);
        temp.Write("client/notes.txt", [4]);

        var replaced = await builder.BuildAsync(client, temp.Combine("server"));

        Assert.NotEmpty(replaced);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(temp.Combine("server", "SCUM", "Content", "Other", "Keep.uasset")));
        Assert.False(File.Exists(temp.Combine("server", "notes.txt")));
        AssertPackagesEqualIgnoringNameHashes(Path.Combine(FixturePaths.Combine("build_server_F16"), events), temp.Combine("server", events));
    }

    [FixturesFact]
    public void CreateStub_WritesEngineNameHashes_AndKeeps12BytePayload()
    {
        var serverTree = new LooseFileSource(FixturePaths.Combine("orig_server"));
        var builder = AkAudioEventStubBuilder.FromServerTree(serverTree);
        var (uasset, uexp) = builder.CreateStub("/Game/WwiseAudio/Event/DefaultWorkUnit/Vehicles/Test/Test_Horn_Start");

        var package = CookedPackageLite.Parse(uasset, uexp);
        Assert.Contains("/Game/WwiseAudio/Event/DefaultWorkUnit/Vehicles/Test/Test_Horn_Start", package.Names);
        Assert.Contains("Test_Horn_Start", package.Names);
        Assert.Equal(AkAudioEventStubBuilder.StubExportSize, package.Exports[0].SerialSize);
        Assert.Equal(package.Uasset.Length + AkAudioEventStubBuilder.StubExportSize, package.Exports[0].SerialOffset + package.Exports[0].SerialSize);
        Assert.True(AkAudioEventStubBuilder.IsAkAudioEvent(uasset, uexp));
        foreach (var (name, stored) in ReadStoredHashes(uasset))
        {
            Assert.Equal(NameHashes.Compute(name), stored);
        }

        // The unchanged names carry exactly the hashes the engine stored in the stock stub.
        Assert.True(serverTree.TryGetBytes("SCUM/Content/WwiseAudio/Event/DefaultWorkUnit/Vehicles/Vehicle_Ignition_Start.uasset", out var stock));
        var stockHashes = ReadStoredHashes(stock).ToDictionary(x => x.Name, x => x.Stored);
        foreach (var (name, stored) in ReadStoredHashes(uasset).Where(x => stockHashes.ContainsKey(x.Name)))
        {
            Assert.Equal(stockHashes[name], stored);
        }
    }

    [FixturesFact]
    public void IsAkAudioEvent_DetectsEventsOnly()
    {
        var events = Path.Combine(FixturePaths.BuildSepTree("F16"), "SCUM", "Content", "WwiseAudio", "Event");
        var anyEvent = Directory.EnumerateFiles(events, "*.uasset", SearchOption.AllDirectories).First();
        Assert.True(AkAudioEventStubBuilder.IsAkAudioEvent(File.ReadAllBytes(anyEvent), File.ReadAllBytes(Path.ChangeExtension(anyEvent, ".uexp"))));

        var mesh = FixturePaths.OrigFiles(".uasset").First(p => !p.Contains("WwiseAudio", StringComparison.OrdinalIgnoreCase));
        var uexp = Path.ChangeExtension(mesh, ".uexp");
        Assert.False(AkAudioEventStubBuilder.IsAkAudioEvent(File.ReadAllBytes(mesh), File.Exists(uexp) ? File.ReadAllBytes(uexp) : []));
    }

    /// <summary>(name, stored hashes) of every name-table entry, plus the byte position of each hash pair.</summary>
    private static List<(string Name, (ushort, ushort) Stored)> ReadStoredHashes(byte[] uasset) =>
        NameHashPositions(uasset).Select(x => (x.Name, ((ushort)(uasset[x.Position] | (uasset[x.Position + 1] << 8)), (ushort)(uasset[x.Position + 2] | (uasset[x.Position + 3] << 8))))).ToList();

    private static List<(string Name, int Position)> NameHashPositions(byte[] uasset)
    {
        var package = CookedPackageLite.Parse(uasset, []);
        using var reader = new BinaryReader(new MemoryStream(uasset));
        reader.BaseStream.Position = package.S.NameOffset;
        var result = new List<(string, int)>();
        foreach (var name in package.Names)
        {
            var length = reader.ReadInt32();
            reader.ReadBytes(length < 0 ? -2 * length : length);
            result.Add((name, (int)reader.BaseStream.Position));
            reader.ReadUInt32();
        }

        return result;
    }

    /// <summary>
    /// Same file list; .uexp/.ubulk byte-identical; .uasset identical once the 4 hash bytes of every name entry are zeroed.
    /// </summary>
    private static void AssertPackagesEqualIgnoringNameHashes(string expectedRoot, string actualRoot)
    {
        var files = PakTestData.ListTree(expectedRoot);
        Assert.Equal(files, PakTestData.ListTree(actualRoot));
        foreach (var relative in files)
        {
            var expected = File.ReadAllBytes(Path.Combine([expectedRoot, .. relative.Split('/')]));
            var actual = File.ReadAllBytes(Path.Combine([actualRoot, .. relative.Split('/')]));
            if (relative.EndsWith(".uasset", StringComparison.Ordinal))
            {
                expected = ZeroNameHashes(expected);
                actual = ZeroNameHashes(actual);
            }

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Content differs: {relative}");
        }
    }

    private static byte[] ZeroNameHashes(byte[] uasset)
    {
        var copy = (byte[])uasset.Clone();
        foreach (var (_, position) in NameHashPositions(uasset))
        {
            copy.AsSpan(position, 4).Clear();
        }

        return copy;
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
