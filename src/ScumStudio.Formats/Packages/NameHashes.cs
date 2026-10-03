namespace ScumStudio.Formats.Packages;

/// <summary>
/// The two 16-bit hashes stored with every package name entry
/// (port of <c>ue4write.py</c> <c>strihash_deprecated</c>, <c>strcrc32</c>, <c>name_hashes</c>).
/// Strings are processed per UTF-16 code unit, like Unreal's TCHAR on Windows.
/// </summary>
public static class NameHashes
{
    private static readonly uint[] DeprecatedTable = BuildDeprecatedTable();
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildDeprecatedTable()
    {
        // port of ue4write.py _DEPR (MSB-first CRC table, polynomial 0x04C11DB7)
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint[] BuildCrc32Table()
    {
        // standard reflected CRC-32 (zlib) table, polynomial 0xEDB88320
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>
    /// Port of <c>strihash_deprecated</c> (Unreal <c>FCrc::Strihash_DEPRECATED</c>): upper-cases each char (ASCII
    /// only, as <c>TChar::ToUpper</c> does in UE 4.27) and folds it through the deprecated CRC table.
    /// </summary>
    /// <remarks>
    /// Correction over the Python port: Unreal hashes ANSI names with the <c>ANSICHAR</c> overload, i.e. ONE byte per
    /// char; only wide (UTF-16) names fold two bytes per char. <c>ue4write.py</c> always folds two bytes, which is why
    /// its rebuilt names differed in the non-case-preserving hash (FORMAT_NOTES: "uasset identical except possibly the
    /// 16-bit non-case-preserving name hash"). With the ANSI rule every stock SCUM package rebuilds byte-exact.
    /// </remarks>
    /// <param name="value">The name.</param>
    /// <param name="narrow">True when the name is stored as ANSI (default: when it is pure ASCII, as the writer stores it).</param>
    public static uint StriHashDeprecated(string value, bool? narrow = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var ansi = narrow ?? IO.ByteWriter.IsAscii(value);
        uint h = 0;
        foreach (var ch in value)
        {
            uint c = ch is >= 'a' and <= 'z' ? (uint)(ch - 32) : ch;
            h = ((h >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(h ^ c) & 0xFF];
            if (!ansi)
            {
                h = ((h >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(h ^ (c >> 8)) & 0xFF];
            }
        }

        return h;
    }

    /// <summary>
    /// Port of <c>strcrc32</c> (Unreal <c>FCrc::StrCrc32</c>): zlib CRC-32 over each char widened to 4 little-endian bytes.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <param name="narrow">
    /// True when the name is stored as ANSI: chars 0x80..0xFF are then sign-extended like Unreal's signed
    /// <c>ANSICHAR</c> (default: when the name is pure ASCII).
    /// </param>
    public static uint StrCrc32(string value, bool? narrow = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var ansi = narrow ?? IO.ByteWriter.IsAscii(value);
        var crc = 0xFFFFFFFFu;
        foreach (var ch in value)
        {
            uint c = ch;
            if (ansi && c >= 0x80 && c <= 0xFF)
            {
                c |= 0xFFFFFF00;
            }

            for (var k = 0; k < 4; k++)
            {
                crc = Crc32Table[(crc ^ c) & 0xFF] ^ (crc >> 8);
                c >>= 8;
            }
        }

        return ~crc;
    }

    /// <summary>Port of <c>name_hashes</c>: (NonCasePreservingHash, CasePreservingHash), low 16 bits of each.</summary>
    /// <param name="value">The name.</param>
    /// <param name="isWide">
    /// Storage of the name: true = UTF-16, false = ANSI. Null = what <see cref="IO.ByteWriter.FString"/> would choose
    /// (ANSI when pure ASCII).
    /// </param>
    public static (ushort NonCasePreserving, ushort CasePreserving) Compute(string value, bool? isWide = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var narrow = isWide.HasValue ? !isWide.Value : IO.ByteWriter.IsAscii(value);
        return ((ushort)(StriHashDeprecated(value, narrow) & 0xFFFF), (ushort)(StrCrc32(value, narrow) & 0xFFFF));
    }

    /// <summary>
    /// Exact port of <c>ue4write.py name_hashes</c> (two bytes per char for the non-case-preserving hash, Unicode
    /// upper-casing). Packages written by the Python toolchain carry these hashes; use it to recognise them.
    /// </summary>
    public static (ushort NonCasePreserving, ushort CasePreserving) ComputePythonToolchain(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        uint h = 0;
        foreach (var ch in value)
        {
            uint c = char.ToUpperInvariant(ch);
            h = ((h >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(h ^ (c & 0xFFFF)) & 0xFF];
            h = ((h >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(h ^ (c >> 8)) & 0xFF];
        }

        return ((ushort)(h & 0xFFFF), (ushort)(StrCrc32(value, narrow: false) & 0xFFFF));
    }
}
