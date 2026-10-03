using System.Diagnostics.CodeAnalysis;

namespace ScumStudio.Core.Security;

/// <summary>
/// Validation of a 256-bit AES key typed as hexadecimal text: optional surrounding whitespace, an optional
/// <c>0x</c>/<c>0X</c> prefix, then exactly 64 hex digits.
/// </summary>
/// <remarks>
/// The key is secret. Nothing here puts the key (or any part of it) into an exception, a log line or a
/// <see cref="object.ToString"/> result; callers must follow the same rule.
/// </remarks>
public static class AesKeyHex
{
    /// <summary>Number of hex digits in a 256-bit key.</summary>
    public const int HexDigits = 64;

    /// <summary>Number of bytes in a 256-bit key.</summary>
    public const int KeyBytes = 32;

    /// <summary>Message used for invalid input; it never contains the input.</summary>
    public const string InvalidKeyMessage =
        "The AES key must be 64 hexadecimal characters (256-bit), optionally prefixed with 0x.";

    /// <summary>True when <paramref name="text"/> is a well-formed 256-bit hex key.</summary>
    public static bool IsValid([NotNullWhen(true)] string? text) => TryNormalize(text, out _);

    /// <summary>
    /// Normalises a key to <c>0x</c> followed by 64 upper-case hex digits (the form CUE4Parse's <c>FAesKey</c> accepts).
    /// </summary>
    /// <returns>False when <paramref name="text"/> is not a 256-bit hex key; <paramref name="normalized"/> is then null.</returns>
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        if (span.Length != HexDigits)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        normalized = string.Create(HexDigits + 2, text, static (destination, source) =>
        {
            var digits = source.AsSpan().Trim();
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[2..];
            }

            destination[0] = '0';
            destination[1] = 'x';
            for (var i = 0; i < HexDigits; i++)
            {
                destination[i + 2] = char.ToUpperInvariant(digits[i]);
            }
        });
        return true;
    }

    /// <summary>Like <see cref="TryNormalize"/>, but throws <see cref="ArgumentException"/> (without echoing the key).</summary>
    public static string Normalize(string text) =>
        TryNormalize(text, out var normalized) ? normalized : throw new ArgumentException(InvalidKeyMessage, nameof(text));

    /// <summary>Decodes a normalised key (<c>0x</c> + 64 digits) to its 32 bytes. The caller should zero the result.</summary>
    internal static byte[] ToBytes(string normalized) => Convert.FromHexString(normalized.AsSpan(2));

    /// <summary>Encodes 32 key bytes in the normalised form.</summary>
    internal static string FromBytes(ReadOnlySpan<byte> key) => "0x" + Convert.ToHexString(key);
}
