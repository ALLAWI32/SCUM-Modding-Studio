using ScumStudio.Pak;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Tests.Pak;

public sealed class PakPathsAndKeyTests
{
    [Theory]
    [InlineData("SCUM/Content/A.uasset", "SCUM/Content/A.uasset")]
    [InlineData("../../../SCUM/Content/A.uasset", "SCUM/Content/A.uasset")]
    [InlineData("/SCUM/Content/A.uasset", "SCUM/Content/A.uasset")]
    [InlineData("SCUM/SCUM/Content/A.uasset", "SCUM/Content/A.uasset")] // pakchunk0_s51 mounts at ../../../SCUM/
    [InlineData("scum/SCUM/Content/A.uasset", "SCUM/Content/A.uasset")]
    [InlineData("SCUM\\Content\\A.uasset", "SCUM/Content/A.uasset")]
    [InlineData("Engine/Content/B.uasset", "Engine/Content/B.uasset")]
    public void NormalizeEntryPath(string input, string expected) =>
        Assert.Equal(expected, PakPaths.NormalizeEntryPath(input));

    [Theory]
    [InlineData("SCUM/Content/A.uasset", true)]
    [InlineData("SCUM/AssetRegistry.bin", true)]
    [InlineData("scum/assetregistry.bin", true)]
    [InlineData("SCUM/Content", false)]
    [InlineData("SCUM/Config/DefaultGame.ini", false)]
    [InlineData("readme.txt", false)]
    public void IsModPakEntry(string path, bool expected) => Assert.Equal(expected, PakPaths.IsModPakEntry(path));

    [Fact]
    public void ToSafeFilePath_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "safe-root");
        Assert.Equal(Path.Combine(root, "SCUM", "Content", "A.uasset"), PakPaths.ToSafeFilePath(root, "SCUM/Content/A.uasset"));
        Assert.Throws<InvalidDataException>(() => PakPaths.ToSafeFilePath(root, "../evil.txt"));
        Assert.Throws<InvalidDataException>(() => PakPaths.ToSafeFilePath(root, "SCUM/../../evil.txt"));
        Assert.Throws<InvalidDataException>(() => PakPaths.ToSafeFilePath(root, ""));
    }

    [Fact]
    public void AesKeyText_NormalizesAndValidates()
    {
        // A synthetic 256-bit pattern, not a real key.
        var digits = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
        Assert.True(AesKeyText.TryNormalize(digits, out var a));
        Assert.Equal("0x" + digits.ToUpperInvariant(), a);
        Assert.True(AesKeyText.TryNormalize("  0X" + digits + " ", out var b));
        Assert.Equal(a, b);
        Assert.False(AesKeyText.TryNormalize(digits[..62], out _));
        Assert.False(AesKeyText.TryNormalize(digits + "00", out _));
        Assert.False(AesKeyText.TryNormalize(digits[..63] + "g", out _));
        Assert.False(AesKeyText.TryNormalize(null, out _));
        var ex = Assert.Throws<ArgumentException>(() => AesKeyText.Normalize(digits[..60]));
        Assert.DoesNotContain(digits[..20], ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SigCopier_CopiesNextToPakWithMatchingName()
    {
        using var temp = new TempDirectory();
        var stock = temp.Write("Paks/pakchunk44-WindowsNoEditor.sig", [9, 8, 7]);
        File.SetLastWriteTimeUtc(stock, new DateTime(2026, 9, 9, 16, 35, 0, DateTimeKind.Utc)); // the game's last update
        var pak = temp.Write("out/pakchunk96-F16_P.pak", [1]);

        var sig = SigCopier.Copy(stock, pak);

        Assert.Equal(temp.Combine("out", "pakchunk96-F16_P.sig"), sig);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(sig));
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(sig) < TimeSpan.FromMinutes(1), "dated like the pak it sits next to");
        Assert.Throws<FileNotFoundException>(() => SigCopier.Copy(temp.Combine("nope.sig"), pak));
        Assert.Throws<ArgumentException>(() => SigCopier.Copy(pak, pak));
        Assert.Throws<ArgumentException>(() => SigCopier.Copy(sig, pak));
    }
}
