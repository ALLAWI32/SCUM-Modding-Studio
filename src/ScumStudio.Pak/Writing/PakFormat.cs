namespace ScumStudio.Pak.Writing;

/// <summary>Constants of the UE4 pak container format (FPakInfo / FPakEntry).</summary>
public static class PakFormat
{
    /// <summary>FPakInfo::PakFile_Magic.</summary>
    public const uint Magic = 0x5A6F12E1;

    /// <summary>PakFile_Version_Fnv64BugFix: the version UE 4.26/4.27 (and SCUM) write.</summary>
    public const int VersionFnv64BugFix = 11;

    /// <summary>First version with a path-hash index and encoded entries (PakFile_Version_PathHashIndex).</summary>
    public const int VersionPathHashIndex = 10;

    /// <summary>Footer size for versions 8B, 10 and 11: guid(16) + encrypted(1) + magic(4) + version(4) + offset(8) + size(8) + sha1(20) + 5 x 32 compression names.</summary>
    public const int FooterSizeV11 = 16 + 1 + 4 + 4 + 8 + 8 + 20 + (CompressionMethodSlots * CompressionMethodNameLength);

    /// <summary>Number of compression method name slots in the footer (v8B+).</summary>
    public const int CompressionMethodSlots = 5;

    /// <summary>Length of each compression method name slot.</summary>
    public const int CompressionMethodNameLength = 32;

    /// <summary>UE's default compression block size (64 KiB).</summary>
    public const int DefaultCompressionBlockSize = 64 * 1024;

    /// <summary>Serialized size of an FPakEntry without compression blocks (v8B+): 3 x int64 + uint32 method + sha1 + flags + block size.</summary>
    public const int EntryHeaderSize = 8 + 8 + 8 + 4 + 20 + 1 + 4;

    /// <summary>Serialized size of an FPakEntry header with <paramref name="blockCount"/> compression blocks.</summary>
    public static int GetEntryHeaderSize(bool compressed, int blockCount) =>
        compressed ? EntryHeaderSize + 4 + (16 * blockCount) : EntryHeaderSize;
}
