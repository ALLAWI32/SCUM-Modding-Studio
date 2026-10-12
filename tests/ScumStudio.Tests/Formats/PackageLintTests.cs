using ScumStudio.Formats;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Formats;

/// <summary>The FName checks a written package must pass: one FName per imported package, numbers split as the cooker does.</summary>
public sealed class PackageLintTests
{
    [Fact]
    public void APackageImportedUnderTwoFNamesIsFlagged()
    {
        // "/Game/X/BP_M_39" as the studio wrote it (whole, number 0) next to the cooker's ("/Game/X/BP_M", 40).
        var package = Package(["/Script/CoreUObject", "Package", "/Game/X/BP_M_39", "/Game/X/BP_M", "Thing"],
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(2)),
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(3, 40)));

        var finding = Assert.Single(PackageLint.Check(package), f => f.Contains("different FNames", StringComparison.Ordinal));
        Assert.Contains("/Game/X/BP_M_39", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameStoredWholeThatTheCookerSplitsIsFlagged()
    {
        var package = Package(["/Script/CoreUObject", "Package", "/Game/X/SM_Rock_2", "Thing"],
            new ImportEntry(new FNameRef(0), new FNameRef(1), 0, new FNameRef(2)));

        var finding = Assert.Single(PackageLint.Check(package));
        Assert.Contains("/Game/X/SM_Rock number 3", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanPackageHasNoFindings()
    {
        var package = Package(["/Script/CoreUObject", "Package", "/Game/X/SM_Rock", "Thing", "Rocket_04"],
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(2, 3)),
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(4)));

        Assert.Empty(PackageLint.Check(package));
        var synthetic = new SyntheticPackage().BuildPackage();
        Assert.Empty(PackageLint.Check(CookedPackage.Parse(synthetic.UAsset, synthetic.UExp)));
    }

    private static CookedPackage Package(List<string> names, params ImportEntry[] imports)
    {
        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports = [new ExportEntry { ObjectName = new FNameRef(names.IndexOf("Thing")), FirstExportDependency = -1 }],
            ExportData = [new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, "/Game/X/Test");
    }
}
