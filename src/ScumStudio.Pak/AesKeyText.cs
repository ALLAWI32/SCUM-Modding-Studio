using System.Globalization;

namespace ScumStudio.Pak;

/// <summary>
/// Validation and canonicalisation of a user-supplied 256-bit AES key given as hexadecimal text.
/// </summary>
/// <remarks>
/// The key is secret: nothing in this type ever includes the key (or any part of it) in an exception message,
/// log line or <see cref="object.ToString"/>. Callers must follow the same rule.
/// </remarks>
public static class AesKeyText
{
    /// <summary>Number of hexadecimal digits in a 256-bit key.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// Environment variable the CLI reads when <c>--aes</c> is not given (keeps the key out of shell history).
    /// </summary>
    public const string EnvironmentVariable = "SCUMSTUDIO_AES_KEY";

    /// <summary>
    /// The key from <see cref="EnvironmentVariable"/>, else the one the app stored (protected) in the studio data folder,
    /// else null. Lets the CLI and AI tools open the stock paks without the key ever being typed, shown or logged.
    /// </summary>
    public static string? FromEnvironmentOrStore()
    {
        var key = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(key) ? key
            : new Core.Security.ProtectedKeyStore().TryGet(out var stored) ? stored : null;
    }

    /// <summary>
    /// Normalises <paramref name="input"/> (optional <c>0x</c> prefix, surrounding whitespace, dashes/spaces between
    /// digit groups) to <c>0x</c> followed by 64 upper-case hex digits, the form CUE4Parse's <c>FAesKey</c> accepts.
    /// </summary>
    /// <returns>False when the text is not a 256-bit hex key; <paramref name="normalized"/> is then null.</returns>
    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        Span<char> digits = stackalloc char[HexLength];
        var count = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '_')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || count == HexLength)
            {
                return false;
            }

            digits[count++] = char.ToUpperInvariant(c);
        }

        if (count != HexLength)
        {
            return false;
        }

        normalized = "0x" + new string(digits);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryNormalize"/> but throws <see cref="ArgumentException"/> (without echoing the key) when invalid.
    /// </summary>
    public static string Normalize(string input)
    {
        if (!TryNormalize(input, out var normalized))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The AES key must be {HexLength} hexadecimal characters (256-bit), optionally prefixed with 0x."),
                nameof(input));
        }

        return normalized!;
    }
}
