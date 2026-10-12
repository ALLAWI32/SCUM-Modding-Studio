using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScumStudio.Core.Sharing;

namespace ScumStudio.Tests.Core;

/// <summary>
/// The mod gallery's moderator key (owner: moderate "with a private key file that lives only on his PC"): ECDSA P-256
/// signatures in the P1363 form WebCrypto verifies, checked with the public key alone; a tampered request, a public key
/// file or garbage is no moderator. Every key here is a throw-away test key.
/// </summary>
public sealed class ModeratorKeyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASignatureVerifiesWithThePublicKeyOnly()
    {
        using var temp = new TempFolder();
        var (pem, publicKey) = ModeratorKey.Generate();
        using var key = ModeratorKey.TryLoad(temp.File(pem, ModeratorKey.FileName))!;
        Assert.Equal(publicKey, key.PublicSpkiBase64);

        var (time, nonce, signature) = key.Sign("DELETE", "/mods/abcdefghijkl", At);
        Assert.Equal(At.ToUnixTimeSeconds(), time);
        Assert.True(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, nonce), signature));

        // Another key's public half does not accept it, and every signature has its own nonce.
        Assert.False(ModeratorKey.Verify(ModeratorKey.Generate().PublicSpkiBase64, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, nonce), signature));
        Assert.NotEqual(nonce, key.Sign("DELETE", "/mods/abcdefghijkl", At).Nonce);
        Assert.Equal(16, Convert.FromBase64String(nonce.Replace('-', '+').Replace('_', '/') + "==").Length);
    }

    [Fact]
    public void ATamperedMethodPathTimeOrNonceFails()
    {
        using var temp = new TempFolder();
        var (pem, publicKey) = ModeratorKey.Generate();
        using var key = ModeratorKey.TryLoad(temp.File(pem, ModeratorKey.FileName))!;
        var (time, nonce, signature) = key.Sign("DELETE", "/mods/abcdefghijkl", At);

        Assert.False(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("GET", "/mods/abcdefghijkl", time, nonce), signature));
        Assert.False(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkm", time, nonce), signature));
        Assert.False(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time + 1, nonce), signature));
        Assert.False(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, (nonce[0] == 'A' ? "B" : "A") + nonce[1..]), signature));

        var flipped = Convert.FromBase64String(signature);
        flipped[10] ^= 1;
        var canonical = ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, nonce);
        Assert.False(ModeratorKey.Verify(publicKey, canonical, Convert.ToBase64String(flipped)));

        // Garbage never throws.
        Assert.False(ModeratorKey.Verify(publicKey, canonical, "not base64!"));
        Assert.False(ModeratorKey.Verify(publicKey, canonical, string.Empty));
        Assert.False(ModeratorKey.Verify("AAAA", canonical, signature));
        Assert.False(ModeratorKey.Verify("not base64!", canonical, signature));
    }

    [Fact]
    public void TheSignatureIsSixtyFourBytesP1363()
    {
        using var temp = new TempFolder();
        var (pem, publicKey) = ModeratorKey.Generate();
        using var key = ModeratorKey.TryLoad(temp.File(pem, ModeratorKey.FileName))!;
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        for (var i = 0; i < 20; i++)
        {
            var (time, nonce, signature) = key.Sign("DELETE", "/mods/abcdefghijkl", At);
            var bytes = Convert.FromBase64String(signature);
            Assert.Equal(64, bytes.Length); // r and s, 32 bytes each, never DER (which varies around 70)
            var data = Encoding.UTF8.GetBytes(ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, nonce));
            Assert.True(verifier.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        // A DER signature of the same text is refused: the Worker's WebCrypto takes P1363 only.
        using var signer = ECDsa.Create();
        signer.ImportFromPem(pem);
        var der = signer.SignData(Encoding.UTF8.GetBytes("x"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(ModeratorKey.Verify(publicKey, "x", Convert.ToBase64String(der)));
    }

    [Fact]
    public void NoKeyFileMeansNoModerator()
    {
        using var temp = new TempFolder();
        Assert.Null(ModeratorKey.TryLoad(Path.Combine(temp.Path, "missing", ModeratorKey.FileName)));
        Assert.Null(ModeratorKey.TryLoad(temp.File("not a key at all", "garbage.key")));
        Assert.Null(ModeratorKey.TryLoad(temp.File(string.Empty, "empty.key")));
        Assert.Null(ModeratorKey.TryLoad(temp.Path)); // a folder
        Assert.Null(ModeratorKey.TryLoad(temp.File(new string('A', 100_000), "huge.key")));

        // The public half alone cannot remove anything.
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(ModeratorKey.TryLoad(temp.File(p256.ExportSubjectPublicKeyInfoPem(), "public.key")));

        // Another curve or another algorithm is not the moderator key.
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(ModeratorKey.TryLoad(temp.File(p384.ExportPkcs8PrivateKeyPem(), "p384.key")));
        using var rsa = RSA.Create(2048);
        Assert.Null(ModeratorKey.TryLoad(temp.File(rsa.ExportPkcs8PrivateKeyPem(), "rsa.key")));
    }

    [Fact]
    public void TheVectorsFileVerifies()
    {
        // The same file the Worker's WebCrypto test reads: made once from a throw-away test key (only its public half is kept).
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ThisDirectory(), "..", "App", "ModShareVectors.json")));
        var v = document.RootElement;
        var canonical = ModeratorKey.Canonical(v.GetProperty("method").GetString()!, v.GetProperty("path").GetString()!, v.GetProperty("time").GetInt64(), v.GetProperty("nonce").GetString()!);
        Assert.Equal(v.GetProperty("canonical").GetString(), canonical);
        Assert.Equal(64, Convert.FromBase64String(v.GetProperty("signature").GetString()!).Length);
        Assert.Equal(91, Convert.FromBase64String(v.GetProperty("publicKeySpki").GetString()!).Length);
        Assert.True(ModeratorKey.Verify(v.GetProperty("publicKeySpki").GetString()!, canonical, v.GetProperty("signature").GetString()!));
        Assert.False(ModeratorKey.Verify(v.GetProperty("publicKeySpki").GetString()!, canonical.Replace("DELETE", "GET", StringComparison.Ordinal), v.GetProperty("signature").GetString()!));
    }

    [Fact]
    public void GenerateWritesPemThatLoadsBack()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "ScumStudio-ModShare", ModeratorKey.FileName);
        var publicKey = ModeratorKey.GenerateFile(path);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(path), StringComparison.Ordinal); // PKCS#8
        using (var key = ModeratorKey.TryLoad(path)!)
        {
            Assert.Equal(publicKey, key.PublicSpkiBase64);
            var (time, nonce, signature) = key.Sign("DELETE", "/mods/abcdefghijkl", At);
            Assert.True(ModeratorKey.Verify(publicKey, ModeratorKey.Canonical("DELETE", "/mods/abcdefghijkl", time, nonce), signature));
        }

        // An existing key is kept unless replacing it is asked for: a new key locks the owner out until the Worker has it.
        Assert.Throws<IOException>(() => ModeratorKey.GenerateFile(path));
        using (var kept = ModeratorKey.TryLoad(path)!)
        {
            Assert.Equal(publicKey, kept.PublicSpkiBase64);
        }

        var replaced = ModeratorKey.GenerateFile(path, overwrite: true);
        Assert.NotEqual(publicKey, replaced);
        using var again = ModeratorKey.TryLoad(path)!;
        Assert.Equal(replaced, again.PublicSpkiBase64);
    }

    private static string ThisDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
