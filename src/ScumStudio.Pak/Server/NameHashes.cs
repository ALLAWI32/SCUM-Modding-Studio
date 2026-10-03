namespace ScumStudio.Pak.Server;

/// <summary>
/// The two 16-bit hashes stored with every FNameEntrySerialized in a cooked package's name table
/// (after ue4write.py <c>strihash_deprecated</c>, <c>strcrc32</c>, <c>name_hashes</c>).
/// </summary>
/// <remarks>
/// Deviation from ue4write.py, verified against every name in the stock cooked packages: UE's
/// <c>FCrc::Strihash_DEPRECATED</c> has an ANSICHAR overload that hashes ONE byte per character, and names that are
/// pure 7-bit ASCII are stored and hashed as ANSI. ue4write.py always hashes two bytes per character (the WIDECHAR
/// overload), so its non-case-preserving hash differs from the engine's for ASCII names. UE 4.27 ignores these hashes
/// when loading, which is why the Python output works in game; this port writes the engine's values.
/// </remarks>
internal static class NameHashes
{
    private static readonly uint[] DeprecatedTable = BuildDeprecatedTable();
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>(non-case-preserving hash &amp; 0xFFFF, case-preserving hash &amp; 0xFFFF).</summary>
    public static (ushort NonCasePreserving, ushort CasePreserving) Compute(string name) =>
        ((ushort)(StriHashDeprecated(name) & 0xFFFF), (ushort)(StrCrc32(name) & 0xFFFF));

    /// <summary>
    /// FCrc::Strihash_DEPRECATED: the ANSICHAR overload (one upper-cased byte per character) for pure 7-bit names,
    /// otherwise the WIDECHAR overload (low byte then high byte of each upper-cased UTF-16 code unit).
    /// </summary>
    public static uint StriHashDeprecated(string text)
    {
        var wide = text.Any(c => c >= 0x80);
        uint hash = 0;
        foreach (var ch in text)
        {
            if (!wide)
            {
                var a = ch is >= 'a' and <= 'z' ? (uint)(ch - 32) : ch;
                hash = ((hash >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(hash ^ a) & 0xFF];
                continue;
            }

            var c = (uint)char.ToUpperInvariant(ch);
            hash = ((hash >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(hash ^ c) & 0xFF];
            hash = ((hash >> 8) & 0x00FFFFFF) ^ DeprecatedTable[(hash ^ (c >> 8)) & 0xFF];
        }

        return hash;
    }

    /// <summary>zlib CRC-32 over each character as a little-endian uint32 (FCrc::StrCrc32).</summary>
    public static uint StrCrc32(string text)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var ch in text)
        {
            uint c = ch;
            for (var i = 0; i < 4; i++)
            {
                crc = Crc32Table[(crc ^ (c & 0xFF)) & 0xFF] ^ (crc >> 8);
                c >>= 8;
            }
        }

        return ~crc;
    }

    private static uint[] BuildDeprecatedTable()
    {
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
}
