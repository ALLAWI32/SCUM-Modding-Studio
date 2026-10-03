using ScumStudio.Formats;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Formats;

public sealed class PackageWriterTests
{
    [Fact]
    public void BuiltPackageParsesBackWithConsistentOffsets()
    {
        var syn = new SyntheticPackage();
        var payload = syn.BuildPayload();
        var bytes = syn.BuildPackage(payload);
        var pkg = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var s = pkg.Summary;

        Assert.Equal(-7, s.LegacyFileVersion);
        Assert.Equal(0, s.FileVersionUE4);
        Assert.Equal(522, s.EffectiveVersionUE4);
        Assert.True(s.IsFilterEditorOnly);
        Assert.Equal(bytes.UAsset.Length, s.TotalHeaderSize);
        Assert.Equal(s.SummaryEnd, s.NameOffset);
        Assert.Equal(pkg.ImportEnd, s.ExportOffset);
        Assert.Equal(s.ExportOffset + ExportEntry.SerializedSize, s.DependsOffset);
        Assert.Equal(s.DependsOffset + 4, s.AssetRegistryDataOffset);
        Assert.Equal(s.AssetRegistryDataOffset + 4, s.PreloadDependencyOffset);
        Assert.Equal(s.TotalHeaderSize + payload.Length, s.BulkDataStartOffset);
        Assert.Equal([new GenerationInfo(1, syn.Names.Count)], s.Generations);
        Assert.Equal(syn.Names, pkg.Names);
        Assert.Equal(3, pkg.Imports.Count);
        Assert.Equal("IMP:Actor", pkg.ResolveIndex(pkg.Exports[0].ClassIndex));
        Assert.Equal("/Script/Engine.Actor", pkg.GetFullPath(-2));
        Assert.Equal(payload, pkg.GetExportBytes(0));
        Assert.Equal(new[] { -2, -3 }, pkg.ReadPreloadDependencies());
        Assert.Equal(new byte[4], pkg.ReadAssetRegistryData());
        Assert.Equal(PackageSummary.PackageMagic, BitConverter.ToUInt32(bytes.UExp, bytes.UExp.Length - 4));

        foreach (var n in pkg.NameEntries)
        {
            Assert.Equal(NameHashes.Compute(n.Value), (n.NonCasePreservingHash, n.CasePreservingHash));
        }
    }

    [Fact]
    public void RebuildIsByteExact()
    {
        var bytes = new SyntheticPackage().BuildPackage();
        var pkg = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var result = RoundTripVerifier.Verify(pkg);
        Assert.True(result.IsIdentical, result.ToString());
        Assert.Empty(result.NameHashMismatches);
    }

    [Fact]
    public void GrowingAnExportShiftsLaterOffsets()
    {
        var syn = new SyntheticPackage();
        var small = CookedPackage.Parse(syn.BuildPackage().UAsset, syn.BuildPackage().UExp);
        var input = PackageWriter.ToBuildInput(small);
        var bigger = small.GetExportBytes(0).Concat(new byte[16]).ToArray();
        var rebuilt = PackageWriter.Build(input with { ExportData = [bigger], Names = [.. small.Names, "Extra"] });
        var pkg = CookedPackage.Parse(rebuilt.UAsset, rebuilt.UExp);
        Assert.Equal(bigger.Length, pkg.Exports[0].SerialSize);
        Assert.Equal(pkg.Summary.TotalHeaderSize, pkg.Exports[0].SerialOffset);
        Assert.Equal(pkg.Summary.TotalHeaderSize + bigger.Length, pkg.Summary.BulkDataStartOffset);
        Assert.Equal("Extra", pkg.Names[^1]);
        Assert.Equal(new GenerationInfo(1, small.Names.Count + 1), pkg.Summary.Generations[0]);
    }

    [Fact]
    public void FirstDifferenceReportsOffsetOrPrefixLength()
    {
        Assert.Null(RoundTripVerifier.FirstDifference([1, 2, 3], [1, 2, 3]));
        Assert.Equal(1, RoundTripVerifier.FirstDifference([1, 2, 3], [1, 9, 3]));
        Assert.Equal(2, RoundTripVerifier.FirstDifference([1, 2], [1, 2, 3]));
    }

    [Fact]
    public void ParseRejectsNonPackages()
    {
        Assert.Throws<FormatException>(() => CookedPackage.Parse(new byte[64], []));
        Assert.Throws<FormatException>(() => CookedPackage.Parse(BitConverter.GetBytes(PackageSummary.PackageMagic), []));
    }

    [Fact]
    public void DescribeListsTablesLikeUe4Pkg()
    {
        var bytes = new SyntheticPackage().BuildPackage();
        var text = CookedPackage.Parse(bytes.UAsset, bytes.UExp, basePath: "Game/MyActor").Describe();
        Assert.StartsWith("== Game/MyActor\nver_ue4=522 lic=0 legacy=-7 flags=0x80000000", text, StringComparison.Ordinal);
        Assert.Contains("imports(3):", text, StringComparison.Ordinal);
        Assert.Contains("  -2: /Script/CoreUObject.Class Actor outer=IMP:/Script/Engine", text, StringComparison.Ordinal);
        Assert.Contains("  1: MyActor class=IMP:Actor outer=None super=None", text, StringComparison.Ordinal);
    }
}
