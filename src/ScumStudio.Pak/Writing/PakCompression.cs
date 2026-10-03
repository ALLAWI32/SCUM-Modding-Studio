namespace ScumStudio.Pak.Writing;

/// <summary>Compression applied to pak entries by <see cref="PakWriter"/>.</summary>
public enum PakCompression
{
    /// <summary>Entries are stored as-is (what the user's existing SCUM mod paks use).</summary>
    None = 0,

    /// <summary>
    /// Zlib (RFC 1950) per compression block. An entry whose compressed form is not smaller is stored uncompressed,
    /// like UnrealPak does.
    /// </summary>
    Zlib = 1,
}
