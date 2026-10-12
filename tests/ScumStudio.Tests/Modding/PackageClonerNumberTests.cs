using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Cloning;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Formats;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Clones keep the cooker's FName form: a trailing <c>_N</c> is the FName number, never part of the text. The raw
/// (text, number) pairs are asserted, since <see cref="FNameRef.Format"/> reads "X_2" the same either way.
/// </summary>
public sealed class PackageClonerNumberTests(ITestOutputHelper output)
{
    private static readonly byte[] Opaque = [0xEE, 0xEE, 0xEE, 0xEE];

    [Fact]
    public void NumberedTemplateLeafIsRenamed()
    {
        // CR_Improvised_Bow_20 as the cooker stores it: "CR_Improvised_Bow" number 21.
        var source = DataAsset("CR_X", 21, Opaque);
        var clone = Clone(source, "/Game/A/CR_X_20", ("/Game/A/CR_X_20", "/Game/A/CR_SS_Y"));

        Assert.Equal(("CR_SS_Y", 0), Raw(clone, clone.Exports[0].ObjectName));
    }

    [Fact]
    public void NumberedNewLeafIsWrittenSplit()
    {
        var clone = Clone(DataAsset("X", 0, Opaque), "/Game/A/X", ("/Game/A/X", "/Game/A/Y_2"));

        Assert.Equal(("Y", 3), Raw(clone, clone.Exports[0].ObjectName));
        Assert.DoesNotContain("Y_2", clone.Names);
    }

    [Fact]
    public void RedirectToNumberedMeshIsSplit()
    {
        var source = Blueprint("/Game/A/SM_Old", "SM_Old", 0, Opaque);
        var clone = Clone(source, "/Game/A/BP_T", ("/Game/A/BP_T", "/Game/A/BP_SS_Y"), ("/Game/A/SM_Old", "/Game/A/ConcreteBlock002_1"));

        Assert.Equal(("/Game/A/ConcreteBlock002", 2), Raw(clone, clone.Imports[0].ObjectName));
        Assert.Equal(("ConcreteBlock002", 2), Raw(clone, clone.Imports[1].ObjectName));
        Assert.DoesNotContain(clone.Names, n => n.EndsWith("_1", StringComparison.Ordinal));
        Assert.Equal(("BP_SS_Y_C", 0), Raw(clone, clone.Exports[0].ObjectName));
    }

    [Fact]
    public void NumberedMemberIsRewired()
    {
        // A paint mesh of the family (SK_Winchester_1897): "SK_W" number 1898 in the Blueprint's imports.
        var source = Blueprint("/Game/A/SK_W", "SK_W", 1898, Opaque);
        var clone = Clone(source, "/Game/A/BP_T", ("/Game/A/BP_T", "/Game/A/BP_T_Gold"), ("/Game/A/SK_W_1897", "/Game/A/SK_W_1897_Gold"));

        Assert.Equal(("/Game/A/SK_W_1897_Gold", 0), Raw(clone, clone.Imports[0].ObjectName));
        Assert.Equal(("SK_W_1897_Gold", 0), Raw(clone, clone.Imports[1].ObjectName));
    }

    [Fact]
    public void PayloadReferenceRefuses()
    {
        // A NameProperty value (CR_X, 21) inside the payload: name index 5, number 21.
        byte[] payload = [0xEE, 0xEE, 0xEE, 0xEE, 5, 0, 0, 0, 21, 0, 0, 0, 0xEE, 0xEE, 0xEE, 0xEE];
        var source = DataAsset("CR_X", 21, payload);

        var error = Assert.Throws<InvalidOperationException>(() => Clone(source, "/Game/A/CR_X_20", ("/Game/A/CR_X_20", "/Game/A/CR_SS_Y")));
        Assert.Contains("CR_X_20", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberlessPairInUndecodedBytesFollowsInPlaceWithAWarning()
    {
        // (7, 0) in bytes the reader cannot decode is "SM_Old", or an int 7 and a zero by chance: nothing tells.
        var source = Blueprint("/Game/A/SM_Old", "SM_Old", 0, [0xEE, 0xEE, 0xEE, 0xEE, 7, 0, 0, 0, 0, 0, 0, 0]);
        var (cloned, clone) = CloneWith(source, "/Game/A/BP_T", ("/Game/A/BP_T", "/Game/A/BP_SS_Y"), ("/Game/A/SM_Old", "/Game/A/ConcreteBlock002_1"));

        Assert.Equal(("ConcreteBlock002", 2), Raw(clone, clone.Imports[1].ObjectName));
        Assert.Equal("ConcreteBlock002_1", clone.Names[7]); // the payload's name follows the rename, as before the number rule
        Assert.Equal(source.GetExportData(0).ToArray(), clone.GetExportData(0).ToArray());
        Assert.Contains("SM_Old", Assert.Single(cloned.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ADecodedNamePropertyOfARedirectIsRewrittenSplit()
    {
        // MeshName (NameProperty) = "SM_Old" (7, 0): a real FName, rewritten the way the cooker stores ConcreteBlock002_1.
        var source = Blueprint("/Game/A/SM_Old", "SM_Old", 0, Tagged(Tag(10, 11, Pair(7, 0)), 9), "MeshName", "NameProperty");
        var (cloned, clone) = CloneWith(source, "/Game/A/BP_T", ("/Game/A/BP_T", "/Game/A/BP_SS_Y"), ("/Game/A/SM_Old", "/Game/A/ConcreteBlock002_1"));

        var value = Assert.IsType<NameValue>(PropertyReader.ReadExport(clone, 0).Find("MeshName")!.Value);
        Assert.Equal(("ConcreteBlock002", 2), Raw(clone, value.Ref));
        Assert.Equal(clone.Imports[1].ObjectName, value.Ref);
        Assert.DoesNotContain("ConcreteBlock002_1", clone.Names);
        Assert.Empty(cloned.Warnings);
    }

    [Fact]
    public void APairInsideADecodedNumberIsChanceAndLeftAlone()
    {
        // Count (Int64Property) = 7 holds the bytes of (7, 0): not a name, so "SM_Old" keeps its entry and no warning.
        var source = Blueprint("/Game/A/SM_Old", "SM_Old", 0, Tagged(Tag(10, 11, Pair(7, 0)), 9), "Count", "Int64Property");
        var (cloned, clone) = CloneWith(source, "/Game/A/BP_T", ("/Game/A/BP_T", "/Game/A/BP_SS_Y"), ("/Game/A/SM_Old", "/Game/A/ConcreteBlock002_1"));

        Assert.Equal("SM_Old", clone.Names[7]);
        Assert.Equal(("ConcreteBlock002", 2), Raw(clone, clone.Imports[1].ObjectName));
        Assert.Equal(source.GetExportData(0).ToArray(), clone.GetExportData(0).ToArray());
        Assert.Empty(cloned.Warnings);
    }

    [Fact]
    public void ADecodedNumberedNamePropertyIsRewrittenNotRefused()
    {
        // Template (NameProperty) = CR_X_20, stored (5, 21): the clone names itself, not the stock recipe.
        var source = DataAsset("CR_X", 21, Tagged(Tag(7, 8, Pair(5, 21)), 6), "Template", "NameProperty");
        var clone = Clone(source, "/Game/A/CR_X_20", ("/Game/A/CR_X_20", "/Game/A/CR_SS_Y"));

        var value = Assert.IsType<NameValue>(PropertyReader.ReadExport(clone, 0).Find("Template")!.Value);
        Assert.Equal(("CR_SS_Y", 0), Raw(clone, value.Ref));
        Assert.Equal(("CR_SS_Y", 0), Raw(clone, clone.Exports[0].ObjectName));
    }

    [FixturesFact]
    public void AnUnnumberedCloneOfEveryStockPackageIsTheTextOnlyClone()
    {
        // The clone before the number rule renamed name-table text only. With a new leaf that does not split, every stock
        // package must come out byte for byte the same; only a numbered stock leaf (MI_HitZone_WheelTire_0) may differ,
        // since the text-only clone left its export under the stock name.
        int same = 0, numbered = 0;
        var unexplained = new List<string>();
        foreach (var file in FixturePaths.OrigFiles(".uasset"))
        {
            var relative = Path.GetRelativePath(FixturePaths.OrigContent, file).Replace('\\', '/');
            var oldPath = "/Game/" + relative[..^".uasset".Length];
            CookedPackage source;
            try
            {
                source = CookedPackage.Load(file[..^".uasset".Length]);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or EndOfStreamException)
            {
                continue;
            }

            var map = new PackageMap([new(oldPath, oldPath + "_Gold")]);
            var expected = TextOnlyClone(source, map, out var expectedError);
            byte[]? actual = null;
            string? actualError = null;
            try
            {
                var cloned = PackageCloner.Clone(source, oldPath, map);
                actual = [.. cloned.Bytes.UAsset, .. cloned.Bytes.UExp];
            }
            catch (InvalidOperationException ex)
            {
                actualError = ex.Message;
            }

            if (expected is not null && actual is not null && expected.AsSpan().SequenceEqual(actual))
            {
                same++;
            }
            else if (expectedError is not null && actualError is not null)
            {
                same++; // both refuse (a name the rename would give twice)
            }
            else if (FNameRef.Split(PackageMap.Leaf(oldPath)).Number != 0)
            {
                numbered++;
                output.WriteLine($"numbered leaf, differs: {oldPath} {actualError}");
            }
            else
            {
                unexplained.Add($"{oldPath}: {actualError ?? expectedError ?? "bytes differ"}");
            }
        }

        output.WriteLine($"{same} identical, {numbered} numbered leaves differ, {unexplained.Count} unexplained");
        Assert.True(same > 100, $"only {same} packages compared");
        Assert.Empty(unexplained);
    }

    [Fact]
    public void APlainRenameStaysInPlaceSoPayloadNamesFollow()
    {
        // The payload names X (index 5, number 0): renaming the entry in place renames that reference too.
        var source = DataAsset("X", 0, [5, 0, 0, 0, 0, 0, 0, 0]);

        var clone = Clone(source, "/Game/A/X", ("/Game/A/X", "/Game/A/Y"));
        Assert.Equal(source.Names.Count, clone.Names.Count);
        Assert.Equal("Y", clone.Names[5]);
        Assert.Equal(("Y", 0), Raw(clone, clone.Exports[0].ObjectName));
    }

    [Fact]
    public void UnrenamedPrimaryExportFailsVerify()
    {
        var source = DataAsset("CR_X", 21, Opaque);
        var error = Assert.Throws<InvalidOperationException>(() =>
            PackageCloner.Verify(source, PackageWriter.Rebuild(source), "/Game/A/CR_SS_Y", "CR_X_20"));
        Assert.Contains("CR_X_20", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapRemapsAnFNameByItsDisplayForm()
    {
        var map = new PackageMap([new("/Game/A/CR_Improvised_Bow_20", "/Game/A/CR_SS_Y")]);
        Assert.Equal(("CR_SS_Y", 0), map.RemapFName("CR_Improvised_Bow", 21));
        Assert.Equal(("CR_Improvised_Bow", 6), map.RemapFName("CR_Improvised_Bow", 6));
        Assert.Equal(("Kept_2", 0), map.RemapFName("Kept_2", 0)); // not renamed: the stored form stays
    }

    private static (string Text, int Number) Raw(CookedPackage package, FNameRef name) => (package.Names[name.Index], name.Number);

    private static CookedPackage Clone(CookedPackage source, string oldPath, params (string Old, string New)[] pairs) =>
        CloneWith(source, oldPath, pairs).Parsed;

    private static (ClonedPackage Cloned, CookedPackage Parsed) CloneWith(CookedPackage source, string oldPath, params (string Old, string New)[] pairs)
    {
        var map = new PackageMap(pairs.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
        var clone = PackageCloner.Clone(source, oldPath, map);
        return (clone, CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, null, clone.NewPackagePath));
    }

    /// <summary>The clone as it was written before the number rule: name-table text remapped, tables and payloads kept.</summary>
    private static byte[]? TextOnlyClone(CookedPackage source, PackageMap map, out string? error)
    {
        var input = PackageWriter.ToBuildInput(source);
        var names = input.Names.Select(map.RemapName).ToArray();
        error = names.GroupBy(n => n, StringComparer.Ordinal).Any(g => g.Count() > 1) ? "a name twice" : null;
        if (error is not null)
        {
            return null;
        }

        var wide = names.Select((n, i) => input.NameIsWide is { } flags && i < flags.Count && flags[i] && n.Any(c => c > 127)).ToArray();
        var bytes = PackageWriter.Build(input with { Names = names, NameIsWide = wide, NameHashOverride = null });
        return [.. bytes.UAsset, .. bytes.UExp];
    }

    private static byte[] Pair(int index, int number)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), number);
        return bytes;
    }

    /// <summary>A property tag without type extras: name, type, size, array index 0, no guid, then the value.</summary>
    private static byte[] Tag(int name, int type, byte[] value) =>
        [.. Pair(name, 0), .. Pair(type, 0), .. BitConverter.GetBytes(value.Length), 0, 0, 0, 0, 0, .. value];

    /// <summary>Tagged properties, the None terminator and the 4-byte tail of a cooked object.</summary>
    private static byte[] Tagged(byte[] tags, int none) => [.. tags, .. Pair(none, 0), 0, 0, 0, 0];

    /// <summary>A data asset package whose one export is (<paramref name="leaf"/>, <paramref name="number"/>).</summary>
    private static CookedPackage DataAsset(string leaf, int number, byte[] payload, params string[] extraNames)
    {
        List<string> names = ["/Script/CoreUObject", "Package", "Class", "/Script/Engine", "DataAsset", leaf, "None", .. extraNames];
        List<ImportEntry> imports =
        [
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(3)),
            new(new FNameRef(0), new FNameRef(2), -1, new FNameRef(4)),
        ];
        return Build(names, imports, [Export(-2, new FNameRef(5, number))], payload);
    }

    /// <summary>A Blueprint class (BP_T_C) that imports the static mesh (<paramref name="meshPath"/>, number) first.</summary>
    private static CookedPackage Blueprint(string meshPath, string meshLeaf, int number, byte[] payload, params string[] extraNames)
    {
        List<string> names =
        [
            "/Script/CoreUObject", "Package", "Class", "/Script/Engine", "StaticMesh", "BlueprintGeneratedClass", meshPath, meshLeaf,
            "BP_T_C", "None", .. extraNames,
        ];
        List<ImportEntry> imports =
        [
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(6, number)),
            new(new FNameRef(3), new FNameRef(4), -1, new FNameRef(7, number)),
            new(new FNameRef(0), new FNameRef(1), 0, new FNameRef(3)),
            new(new FNameRef(0), new FNameRef(2), -3, new FNameRef(5)),
        ];
        return Build(names, imports, [Export(-4, new FNameRef(8))], payload);
    }

    private static ExportEntry Export(int classIndex, FNameRef name) =>
        new() { ClassIndex = classIndex, ObjectName = name, ObjectFlags = 0x1, IsAsset = 1, FirstExportDependency = -1 };

    private static CookedPackage Build(List<string> names, List<ImportEntry> imports, List<ExportEntry> exports, byte[] payload)
    {
        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports = exports,
            ExportData = [payload],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp);
    }
}
