using System.Security.Cryptography;
using System.Text;
using ScumStudio.Core.Security;

namespace ScumStudio.Tests.Core;

public sealed class ProtectedKeyStoreTests
{
    /// <summary>A throw-away random test key (never a real key, never a literal in source).</summary>
    private static string RandomKeyHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(AesKeyHex.KeyBytes));

    [Fact]
    public void RoundTripNormalisesTheKey()
    {
        using var temp = new TempFolder();
        var key = RandomKeyHex();
        var store = new ProtectedKeyStore(temp.Path);
        Assert.False(store.HasKey);
        Assert.False(store.TryGet(out _));

        store.Set("  0X" + key.ToLowerInvariant() + "\n");

        Assert.True(store.HasKey);
        Assert.True(store.TryGet(out var read));
        Assert.True(read == "0x" + key, "The key read back differs from the key stored.");

        // A new instance (e.g. the next app start) reads the same key.
        Assert.True(new ProtectedKeyStore(temp.Path).TryGet(out var again));
        Assert.True(again == read);
    }

    [Fact]
    public void KeyFileDoesNotContainThePlainKey()
    {
        using var temp = new TempFolder();
        var key = RandomKeyHex();
        var store = new ProtectedKeyStore(temp.Path);
        store.Set(key);

        var text = File.ReadAllText(store.KeyFilePath);
        Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
        var raw = Convert.FromHexString(key);
        Assert.False(ContainsSequence(File.ReadAllBytes(store.KeyFilePath), raw));
        Assert.StartsWith(OperatingSystem.IsWindows() ? "dpapi-v1:" : "aesgcm-v1:", text);
    }

    [Fact]
    public void FilesAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // DPAPI path: no Unix modes.
        }

        using var temp = new TempFolder();
        var store = new ProtectedKeyStore(Path.Combine(temp.Path, "home"));
        store.Set(RandomKeyHex());

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(store.KeyFilePath));
        var localKey = Path.Combine(store.Directory, ProtectedKeyStore.LocalKeyFileName);
        Assert.True(File.Exists(localKey));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(localKey));

        // A loosened wrapping-key file is tightened again on the next read.
        File.SetUnixFileMode(localKey, ownerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.True(store.TryGet(out _));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(localKey));
    }

    [Fact]
    public void WarnsNotHardwareProtectedWithoutLeakingTheKey()
    {
        using var temp = new TempFolder();
        using var log = new CapturedLog();
        var key = RandomKeyHex();
        var store = new ProtectedKeyStore(temp.Path, log.Logger);
        store.Set(key);
        Assert.True(store.TryGet(out _));
        store.Clear();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("not hardware-protected", log.Text);
        }

        Assert.DoesNotContain(key, log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key[..16], log.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMalformedKeysWithoutEchoingThem()
    {
        // Built at run time and checked in one fact so no key-like text shows up in test names or source.
        var digits = RandomKeyHex();
        var badKeys = new[]
        {
            string.Empty,
            "0x",
            "not a key",
            digits[..63],
            digits + "A",
            "0x" + digits[..62],
            digits[..63] + "G",
            new string('z', AesKeyHex.HexDigits),
            digits[..32] + " " + digits[32..],
            "0x0x" + digits[..62],
        };

        using var temp = new TempFolder();
        var store = new ProtectedKeyStore(temp.Path);
        for (var i = 0; i < badKeys.Length; i++)
        {
            var bad = badKeys[i];
            var error = Assert.Throws<ArgumentException>(() => store.Set(bad));
            Assert.Equal("hex", error.ParamName);
            Assert.True(bad.Trim().Length <= 3 || !error.Message.Contains(bad.Trim(), StringComparison.Ordinal), $"Case {i}: message echoes the input.");
            Assert.False(store.HasKey);
            Assert.False(AesKeyHex.IsValid(bad), $"Case {i} should be invalid.");
        }

        Assert.Throws<ArgumentException>(() => store.Set(null!));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void ClearRemovesTheKey()
    {
        using var temp = new TempFolder();
        var store = new ProtectedKeyStore(temp.Path);
        store.Clear(); // nothing stored: no-op
        store.Set(RandomKeyHex());
        store.Clear();

        Assert.False(store.HasKey);
        Assert.False(store.TryGet(out var hex));
        Assert.Null(hex);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void ReplacingTheKeyKeepsOnlyTheNewOne()
    {
        using var temp = new TempFolder();
        var store = new ProtectedKeyStore(temp.Path);
        store.Set(RandomKeyHex());
        var second = RandomKeyHex();
        store.Set(second);

        Assert.True(store.TryGet(out var read));
        Assert.True(read == "0x" + second);
    }

    [Fact]
    public void CorruptOrTamperedFilesAreRejected()
    {
        using var temp = new TempFolder();
        using var log = new CapturedLog();
        var store = new ProtectedKeyStore(temp.Path, log.Logger);

        File.WriteAllText(store.KeyFilePath, "garbage");
        Assert.False(store.TryGet(out _));

        File.WriteAllText(store.KeyFilePath, "aesgcm-v1:%%%not-base64%%%");
        Assert.False(store.TryGet(out _));

        File.WriteAllText(store.KeyFilePath, "unknown-scheme:AAAA");
        Assert.False(store.TryGet(out _));

        // Flip one ciphertext byte: authenticated decryption (or DPAPI) must fail.
        store.Set(RandomKeyHex());
        var line = File.ReadAllText(store.KeyFilePath).Trim();
        var separator = line.IndexOf(':');
        var payload = Convert.FromBase64String(line[(separator + 1)..]);
        payload[^1] ^= 0x5A;
        File.WriteAllText(store.KeyFilePath, line[..(separator + 1)] + Convert.ToBase64String(payload));
        Assert.False(store.TryGet(out var hex));
        Assert.Null(hex);
        Assert.Contains("WRN", log.Text);
    }

    [Fact]
    public void MissingProtectionFileMeansNoKey()
    {
        using var temp = new TempFolder();
        var store = new ProtectedKeyStore(temp.Path);
        store.Set(RandomKeyHex());
        var protectionFile = Path.Combine(
            temp.Path,
            OperatingSystem.IsWindows() ? ProtectedKeyStore.EntropyFileName : ProtectedKeyStore.LocalKeyFileName);
        Assert.True(File.Exists(protectionFile));
        File.Delete(protectionFile);

        Assert.False(store.TryGet(out _));
    }

    [Fact]
    public void AesKeyHexNormalises()
    {
        var key = RandomKeyHex();
        Assert.True(AesKeyHex.TryNormalize(" 0x" + key.ToLowerInvariant() + " ", out var normalized));
        Assert.True(normalized == "0x" + key);
        Assert.True(AesKeyHex.IsValid(key));
        Assert.False(AesKeyHex.IsValid(null));
        Assert.True(AesKeyHex.Normalize(key) == "0x" + key);
        var error = Assert.Throws<ArgumentException>(() => AesKeyHex.Normalize(key[1..]));
        Assert.DoesNotContain(key[1..], error.Message, StringComparison.Ordinal);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
