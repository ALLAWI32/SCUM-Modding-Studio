using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.IO;
using ScumStudio.Core.Settings;

namespace ScumStudio.Core.Security;

/// <summary>
/// Stores the game's AES pak key for the current user, encrypted at rest, in <c>key.bin</c> next to
/// <c>settings.json</c> (see <see cref="StudioHome"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows</b>: DPAPI (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>) with a random
/// per-install entropy kept in <c>key.entropy</c>; only the same Windows user on the same machine can decrypt.
/// </para>
/// <para>
/// <b>Other OSes</b> (development machines): AES-256-GCM with a random per-user key in <c>key.local</c>, both files
/// created with mode 0600. This protects against other users, not against the same user, and a warning saying the
/// key is "not hardware-protected" is logged.
/// </para>
/// <para>
/// <c>key.bin</c> holds one line <c>&lt;scheme&gt;:&lt;base64&gt;</c>. The key itself never appears in logs,
/// exceptions or <see cref="object.ToString"/>. <see cref="TryGet"/> returns the normalised form <c>0x</c> + 64
/// upper-case hex digits.
/// </para>
/// </remarks>
public sealed class ProtectedKeyStore
{
    /// <summary>File holding the protected key.</summary>
    public const string KeyFileName = "key.bin";

    /// <summary>Windows: file holding the per-install DPAPI entropy.</summary>
    public const string EntropyFileName = "key.entropy";

    /// <summary>Non-Windows: file holding the per-user AES-GCM wrapping key.</summary>
    public const string LocalKeyFileName = "key.local";

    /// <summary>Text of the warning logged on non-Windows systems.</summary>
    public const string NotHardwareProtectedWarning =
        "The AES key is encrypted with a local key file readable only by your user account (0600); it is not hardware-protected. On Windows ScumStudio uses DPAPI instead.";

    private const string DpapiScheme = "dpapi-v1";
    private const string AesGcmScheme = "aesgcm-v1";
    private const int SecretFileBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] AssociatedData = "ScumStudio.AesKey.v1"u8.ToArray();

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private bool _warnedNotHardwareProtected;

    /// <summary>Creates a store in <paramref name="directory"/> (default: <see cref="StudioHome.GetDirectory"/>).</summary>
    public ProtectedKeyStore(string? directory = null, ILogger? logger = null)
    {
        Directory = Path.GetFullPath(directory ?? StudioHome.GetDirectory());
        KeyFilePath = Path.Combine(Directory, KeyFileName);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when the platform protects the key with the OS user credentials (Windows DPAPI).</summary>
    public static bool IsOsProtected => OperatingSystem.IsWindows();

    /// <summary>Folder holding the key files.</summary>
    public string Directory { get; }

    /// <summary>Full path of <c>key.bin</c>.</summary>
    public string KeyFilePath { get; }

    /// <summary>True when a key file exists (it may still fail to decrypt; use <see cref="TryGet"/>).</summary>
    public bool HasKey => File.Exists(KeyFilePath);

    /// <summary>
    /// Validates, encrypts and stores <paramref name="hex"/> (64 hex digits, optional <c>0x</c>), replacing any stored key.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not a 256-bit hex key (the message does not contain it).</exception>
    public void Set(string hex)
    {
        if (!AesKeyHex.TryNormalize(hex, out var normalized))
        {
            throw new ArgumentException(AesKeyHex.InvalidKeyMessage, nameof(hex));
        }

        var key = AesKeyHex.ToBytes(normalized);
        try
        {
            lock (_gate)
            {
                StudioHome.EnsureDirectory(Directory);
                string line;
                if (OperatingSystem.IsWindows())
                {
                    line = DpapiScheme + ":" + Convert.ToBase64String(ProtectWindows(key));
                }
                else
                {
                    WarnNotHardwareProtected();
                    line = AesGcmScheme + ":" + Convert.ToBase64String(ProtectLocal(key));
                }

                AtomicFile.WriteAllBytes(KeyFilePath, Encoding.ASCII.GetBytes(line + "\n"), ownerOnly: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _logger.LogInformation("AES key stored in {Path}.", KeyFilePath);
    }

    /// <summary>
    /// Reads and decrypts the stored key. Returns false when none is stored or it cannot be decrypted (a warning,
    /// without key material, is logged in the latter case).
    /// </summary>
    /// <param name="hex">The key as <c>0x</c> + 64 upper-case hex digits, or null.</param>
    public bool TryGet([NotNullWhen(true)] out string? hex)
    {
        hex = null;
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(KeyFilePath))
                {
                    return false;
                }

                text = File.ReadAllText(KeyFilePath, Encoding.ASCII).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The stored AES key could not be read ({Error}).", ex.GetType().Name);
                return false;
            }

            var separator = text.IndexOf(':');
            if (separator <= 0)
            {
                _logger.LogWarning("The stored AES key file {Path} is not in a recognised format; enter the key again.", KeyFilePath);
                return false;
            }

            var scheme = text[..separator];
            byte[]? key = null;
            try
            {
                var payload = Convert.FromBase64String(text[(separator + 1)..]);
                if (scheme == DpapiScheme && OperatingSystem.IsWindows())
                {
                    key = UnprotectWindows(payload);
                }
                else if (scheme == AesGcmScheme && !OperatingSystem.IsWindows())
                {
                    WarnNotHardwareProtected();
                    key = UnprotectLocal(payload);
                }
                else
                {
                    _logger.LogWarning(
                        "The stored AES key uses protection scheme '{Scheme}', which is not available on this operating system; enter the key again.",
                        scheme);
                    return false;
                }

                if (key is null || key.Length != AesKeyHex.KeyBytes)
                {
                    _logger.LogWarning("The stored AES key could not be decrypted (missing or changed protection file); enter the key again.");
                    return false;
                }

                hex = AesKeyHex.FromBytes(key);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "The stored AES key could not be decrypted ({Error}); it may belong to another user account or machine. Enter the key again.",
                    ex.GetType().Name);
                return false;
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
    }

    /// <summary>Deletes the stored key and its protection files. Does nothing when no key is stored.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var removed = DeleteIfExists(KeyFilePath);
            DeleteIfExists(Path.Combine(Directory, EntropyFileName));
            DeleteIfExists(Path.Combine(Directory, LocalKeyFileName));
            if (removed)
            {
                _logger.LogInformation("Stored AES key removed from {Path}.", Directory);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private byte[] ProtectWindows(byte[] key)
    {
        var entropy = LoadOrCreateSecret(Path.Combine(Directory, EntropyFileName));
        return ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    private byte[]? UnprotectWindows(byte[] payload)
    {
        var entropy = ReadSecret(Path.Combine(Directory, EntropyFileName));
        return entropy is null ? null : ProtectedData.Unprotect(payload, entropy, DataProtectionScope.CurrentUser);
    }

    private byte[] ProtectLocal(byte[] key)
    {
        EnsureAesGcm();
        var wrapKey = LoadOrCreateSecret(Path.Combine(Directory, LocalKeyFileName));
        try
        {
            var blob = new byte[NonceBytes + TagBytes + key.Length];
            var nonce = blob.AsSpan(0, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(wrapKey, TagBytes);
            aes.Encrypt(nonce, key, blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), AssociatedData);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private byte[]? UnprotectLocal(byte[] blob)
    {
        EnsureAesGcm();
        if (blob.Length != NonceBytes + TagBytes + AesKeyHex.KeyBytes)
        {
            return null;
        }

        var wrapKey = ReadSecret(Path.Combine(Directory, LocalKeyFileName));
        if (wrapKey is null)
        {
            return null;
        }

        try
        {
            var key = new byte[AesKeyHex.KeyBytes];
            using var aes = new AesGcm(wrapKey, TagBytes);
            aes.Decrypt(
                blob.AsSpan(0, NonceBytes),
                blob.AsSpan(NonceBytes + TagBytes),
                blob.AsSpan(NonceBytes, TagBytes),
                key,
                AssociatedData);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private static void EnsureAesGcm()
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform, so the AES key cannot be stored.");
        }
    }

    /// <summary>Reads a base64 secret file; null when missing or malformed. Tightens permissive Unix modes.</summary>
    private byte[]? ReadSecret(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnforceOwnerOnly(path);
        try
        {
            var bytes = Convert.FromBase64String(File.ReadAllText(path, Encoding.ASCII).Trim());
            return bytes.Length == SecretFileBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Returns the secret in <paramref name="path"/>, creating a random one (mode 0600) when missing or malformed.</summary>
    private byte[] LoadOrCreateSecret(string path)
    {
        if (ReadSecret(path) is { } existing)
        {
            return existing;
        }

        var secret = RandomNumberGenerator.GetBytes(SecretFileBytes);
        AtomicFile.WriteAllBytes(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(secret) + "\n"), ownerOnly: true);
        return secret;
    }

    private void EnforceOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var mode = File.GetUnixFileMode(path);
        if ((mode & ~ownerOnly) != 0)
        {
            _logger.LogWarning("{Path} was accessible to other users; restricting it to the owner (0600).", path);
            File.SetUnixFileMode(path, ownerOnly);
        }
    }

    private void WarnNotHardwareProtected()
    {
        if (_warnedNotHardwareProtected)
        {
            return;
        }

        _warnedNotHardwareProtected = true;
        _logger.LogWarning(NotHardwareProtectedWarning);
    }

    private static bool DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
