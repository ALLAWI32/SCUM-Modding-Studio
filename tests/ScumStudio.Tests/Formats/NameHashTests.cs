using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Formats;

public sealed class NameHashTests
{
    // Stored hashes taken from stock SCUM packages (WolfsWagen_ES name table).
    [Theory]
    [InlineData("/Script/CoreUObject", 0x49f8, 0x3e2d)]
    [InlineData("/Script/Engine", 0x4086, 0x4985)]
    [InlineData("/Script/SCUM", 0xa662, 0xd777)]
    [InlineData("/Game/ConZ_Files/Items/Item_Icons/ICO_WolfsWagen", 0x7a3c, 0x5dc8)]
    public void ComputeMatchesUnrealCook(string name, int nonCase, int casePreserving)
    {
        var (h1, h2) = NameHashes.Compute(name);
        Assert.Equal((ushort)nonCase, h1);
        Assert.Equal((ushort)casePreserving, h2);
    }

    [Fact]
    public void PythonToolchainHashDiffersOnlyInNonCaseHash()
    {
        // ue4write.py folds two bytes per char; the case-preserving CRC is the same.
        var unreal = NameHashes.Compute("/Script/SCUM");
        var python = NameHashes.ComputePythonToolchain("/Script/SCUM");
        Assert.Equal((ushort)0xf14b, python.NonCasePreserving);
        Assert.Equal(unreal.CasePreserving, python.CasePreserving);
    }

    [Fact]
    public void NonCasePreservingHashIgnoresAsciiCase()
    {
        Assert.Equal(NameHashes.Compute("RelativeLocation").NonCasePreserving, NameHashes.Compute("RELATIVElocation").NonCasePreserving);
        Assert.NotEqual(NameHashes.Compute("RelativeLocation").CasePreserving, NameHashes.Compute("RELATIVElocation").CasePreserving);
    }

    [Theory]
    [InlineData("Foo", 7, 0)]
    [InlineData("Foo_3", 7, 4)]
    [InlineData("Foo_0", 7, 1)]
    [InlineData("Bar_1", 9, 0)]
    public void ResolveNameHandlesNumberSuffix(string text, int index, int number)
    {
        var table = new Dictionary<string, int> { ["Foo"] = 7, ["Bar_1"] = 9 };
        Assert.Equal(new FNameRef(index, number), FNameRef.Resolve(table, text));
    }

    [Fact]
    public void ResolveNameRejectsLeadingZeroSuffixAndUnknownNames()
    {
        var table = new Dictionary<string, int> { ["Foo"] = 0 };
        Assert.Throws<KeyNotFoundException>(() => FNameRef.Resolve(table, "Foo_03"));
        Assert.Throws<KeyNotFoundException>(() => FNameRef.Resolve(table, "Nope"));
        Assert.Equal("Foo_2", new FNameRef(0, 3).Format(["Foo"]));
    }

    [Fact]
    public void FStringRoundTripsAsciiLatin1AndWide()
    {
        var w = new ByteWriter();
        w.FString("abc");
        w.FString("");
        w.FString("Größe");
        w.FString("Größe", wide: false);
        w.FString("中文");
        var r = new ByteReader(w.ToArray());
        Assert.Equal(("abc", false), r.FStringWithEncoding());
        Assert.Equal((string.Empty, false), r.FStringWithEncoding());
        Assert.Equal(("Größe", true), r.FStringWithEncoding());
        Assert.Equal(("Größe", false), r.FStringWithEncoding());
        Assert.Equal(("中文", true), r.FStringWithEncoding());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void GuidHexMatchesRawBytes()
    {
        var bytes = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var g = FGuid.FromBytes(bytes);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", g.ToString());
        Assert.Equal(g, FGuid.ParseHex(g.ToString()));
        Assert.Equal(bytes, g.ToBytes());
    }
}
