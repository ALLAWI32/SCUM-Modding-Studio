using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ScumStudio.Core.IO;

namespace ScumStudio.Core.Sharing;

/// <summary>
/// The mod gallery's moderator key (owner: remove bad uploads "with a private key file that lives only on his PC"): an
/// ECDSA P-256 private key in <c>moderator.key</c> (PKCS#8 PEM). A removal is signed with it; the Worker holds only the
/// public key, so neither the app nor the Worker carries anything that can remove a mod. The key bytes are never logged,
/// printed or returned.
/// </summary>
/// <remarks>
/// A signature covers <see cref="Canonical"/> (method, URL path, Unix time, a random nonce) and is SHA-256 ECDSA in the
/// IEEE P1363 form (r‖s, 64 bytes), the form WebCrypto's <c>verify</c> takes. The Worker refuses a time more than five
/// minutes off and a nonce it has seen, so a captured removal cannot be sent again.
/// </remarks>
public sealed class ModeratorKey : IDisposable
{
    /// <summary>Environment variable with another key file path (tests, a second PC of the owner).</summary>
    public const string PathVariable = "SCUMSTUDIO_MODSHARE_KEY";

    /// <summary>The key file's name.</summary>
    public const string FileName = "moderator.key";

    private const int MaxFileBytes = 16 * 1024;
    private const int SignatureBytes = 64;
    private const int NonceBytes = 16;

    private static readonly byte[] P256SpkiPrefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    private readonly ECDsa _key;

    private ModeratorKey(ECDsa key)
    {
        _key = key;
        PublicSpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Where the key is looked for: <see cref="PathVariable"/> when set, else <c>Desktop\ScumStudio-ModShare\moderator.key</c>.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable)?.Trim() is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Path.Combine(Desktop(), "ScumStudio-ModShare", FileName);

    /// <summary>The public half (SubjectPublicKeyInfo, Base64), the value the Worker's <c>MODERATOR_PUBLIC_KEY</c> holds.</summary>
    public string PublicSpkiBase64 { get; }

    /// <summary>The key at <paramref name="path"/> (default <see cref="DefaultPath"/>), or null when there is none, it cannot be read, or it is not a P-256 private key.</summary>
    public static ModeratorKey? TryLoad(string? path = null)
    {
        try
        {
            var file = new FileInfo(path ?? DefaultPath);
            return file.Exists && file.Length <= MaxFileBytes ? FromPem(File.ReadAllText(file.FullName)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A new key pair: the private key as PKCS#8 PEM and the public key as SubjectPublicKeyInfo Base64.</summary>
    public static (string PrivatePem, string PublicSpkiBase64) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// Writes a new key to <paramref name="path"/> and returns its public key. An existing file is kept (IOException)
    /// unless <paramref name="overwrite"/>: replacing it locks the owner out until the Worker gets the new public key.
    /// </summary>
    public static string GenerateFile(string path, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path) && !overwrite)
        {
            throw new IOException($"{path} already exists.");
        }

        var (pem, publicKey) = Generate();
        AtomicFile.WriteAllBytes(path, Encoding.ASCII.GetBytes(pem), ownerOnly: true);
        return publicKey;
    }

    /// <summary>The signed text: <c>METHOD\nPATH\nUNIX-SECONDS\nNONCE</c>, with the method upper case and the URL path without a query.</summary>
    public static string Canonical(string method, string path, long unixSeconds, string nonce) =>
        $"{method}\n{path}\n{unixSeconds.ToString(CultureInfo.InvariantCulture)}\n{nonce}";

    /// <summary>Signs a request: the time, a fresh nonce (16 random bytes, Base64url) and the P1363 signature (Base64).</summary>
    public (long Time, string Nonce, string Signature) Sign(string method, string path, DateTimeOffset now)
    {
        var time = now.ToUnixTimeSeconds();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signature = _key.SignData(Encoding.UTF8.GetBytes(Canonical(method, path, time, nonce)), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (time, nonce, Convert.ToBase64String(signature));
    }

    /// <summary>True when <paramref name="signatureBase64"/> is a 64-byte P1363 signature of <paramref name="canonical"/> by the P-256 key <paramref name="publicSpkiBase64"/>.</summary>
    public static bool Verify(string publicSpkiBase64, string canonical, string signatureBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            if (signature.Length != SignatureBytes)
            {
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicSpkiBase64), out _);
            return IsP256(key) && key.VerifyData(Encoding.UTF8.GetBytes(canonical), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();

    /// <summary>A P-256 private key from PEM text, or null.</summary>
    internal static ModeratorKey? FromPem(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (IsP256(key))
            {
                key.SignData([0], HashAlgorithmName.SHA256); // a public key alone cannot sign: no moderator
                return new ModeratorKey(key);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
        }

        key.Dispose();
        return null;
    }

    /// <summary>A P-256 key exports the 91-byte SubjectPublicKeyInfo that starts with id-ecPublicKey and prime256v1.</summary>
    private static bool IsP256(ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        return spki.Length == 91 && spki.AsSpan(0, P256SpkiPrefix.Length).SequenceEqual(P256SpkiPrefix);
    }

    private static string Desktop() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop
            ? desktop
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
