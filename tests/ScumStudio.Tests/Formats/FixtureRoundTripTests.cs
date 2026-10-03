using System.Diagnostics;
using ScumStudio.Formats.Packages;
using ScumStudio.Tests.Fixtures;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Formats;

/// <summary>Byte-exact read → rebuild → compare over the real SCUM packages of the fixture archive.</summary>
public sealed class FixtureRoundTripTests
{
    /// <summary>
    /// Stock packages that are allowed to differ, with the reason. Empty: every stock package under orig/ rebuilds
    /// byte-exact once name hashes follow Unreal's ANSI rule (see <see cref="NameHashes.StriHashDeprecated"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownDifferent = new Dictionary<string, string>();

    private readonly ITestOutputHelper _output;

    public FixtureRoundTripTests(ITestOutputHelper output) => _output = output;

    [FixturesFact]
    public void EveryStockPackageRebuildsByteExact()
    {
        var sw = Stopwatch.StartNew();
        var results = RoundTripVerifier.VerifyDirectory(FixturePaths.OrigContent, recursive: true);
        _output.WriteLine($"{results.Count} packages in {sw.Elapsed.TotalSeconds:F1} s");

        Assert.True(results.Count >= 500, $"expected ~506 stock packages, found {results.Count}");
        var unexpected = results
            .Where(r => !r.IsIdentical && !KnownDifferent.ContainsKey(Relative(r.Path)))
            .Select(r => r.ToString())
            .ToList();
        Assert.True(unexpected.Count == 0, string.Join('\n', unexpected.Take(20)));
        Assert.Equal(KnownDifferent.Count, results.Count(r => !r.IsIdentical));
        Assert.All(results, r => Assert.Empty(r.NameHashMismatches));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"round trip took {sw.Elapsed}");
    }

    [FixturesFact]
    public void ModTreeF16RebuildsByteExactWithStoredHashes()
    {
        var content = Path.Combine(FixturePaths.BuildSepTree("F16"), "SCUM", "Content");
        var stored = RoundTripVerifier.VerifyDirectory(content, recursive: true, useStoredNameHashes: true);
        Assert.True(stored.Count >= 100, $"found {stored.Count} packages");
        Assert.All(stored, r => Assert.True(r.IsIdentical, r.ToString()));

        // Packages written by ue4write.py carry its (two-bytes-per-char) non-case-preserving hash; every stored hash
        // must be explained by either Unreal's or the Python toolchain's algorithm.
        var recomputed = RoundTripVerifier.VerifyDirectory(content, recursive: true);
        Assert.All(recomputed, r =>
        {
            Assert.True(r.UExpIdentical, r.ToString());
            Assert.True(r.NameHashesExplained, r.ToString());
            Assert.Equal(r.NameHashMismatches.Count == 0, r.UAssetIdentical);
            Assert.True(r.IsLayoutIdentical, r.ToString());
        });
        var pythonWritten = recomputed.Count(r => r.PythonToolchainHashNames > 0);
        _output.WriteLine($"{stored.Count} packages; {pythonWritten} written by the Python toolchain");
        Assert.True(pythonWritten > 0);
    }

    [FixturesFact]
    public void PatchedPackageStillRebuildsByteExact()
    {
        var pkg = CookedPackage.Load(FixturePaths.OrigFile("SCUM/Content/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Chassis"));
        var block = pkg.ReadProperties(0);
        var uexp = (byte[])pkg.UExp.Clone();
        var scalar = block.FindAll("ParameterValue", "FloatProperty").First();
        PackagePatcher.SetFloat(uexp, block, scalar, 0.75f);

        var patched = CookedPackage.Parse(pkg.UAsset, uexp);
        Assert.True(RoundTripVerifier.Verify(patched).IsIdentical);
        var reread = patched.ReadProperties(0).FindAll("ParameterValue", "FloatProperty").First();
        Assert.Equal(0.75f, ((ScumStudio.Formats.Properties.FloatValue)reread.Value).Value);
        var at = block.ToUExpOffset(scalar.ValueOffset);
        var changed = Enumerable.Range(0, uexp.Length).Where(i => uexp[i] != pkg.UExp[i]).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, i => Assert.InRange(i, at, at + 3));
    }

    [FixturesFact]
    public void BulkFilesAreLoadedWithTheirPackage()
    {
        var ubulk = Directory.EnumerateFiles(FixturePaths.OrigContent, "*.ubulk", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        var pkg = CookedPackage.Load(Path.ChangeExtension(ubulk, null));
        Assert.NotNull(pkg.UBulk);
        Assert.Equal(new FileInfo(ubulk).Length, pkg.UBulk!.Length);
        Assert.Equal(pkg.Summary.TotalHeaderSize + pkg.UExp.Length - 4, pkg.Summary.BulkDataStartOffset);
    }

    private static string Relative(string basePath) =>
        Path.GetRelativePath(FixturePaths.OrigContent, basePath).Replace('\\', '/');
}
