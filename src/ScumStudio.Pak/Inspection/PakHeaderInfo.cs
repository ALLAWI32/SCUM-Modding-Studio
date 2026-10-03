namespace ScumStudio.Pak.Inspection;

/// <summary>Footer and (when readable) primary-index summary of a pak file.</summary>
/// <param name="FilePath">Full path of the pak.</param>
/// <param name="FileLength">Size in bytes.</param>
/// <param name="Version">Pak version (11 = UE 4.26/4.27 Fnv64BugFix).</param>
/// <param name="EncryptedIndex">True when the index is AES-encrypted (then the index fields are null).</param>
/// <param name="EncryptionKeyGuid">Key GUID (all zero for SCUM).</param>
/// <param name="IndexOffset">Offset of the primary index.</param>
/// <param name="IndexSize">Size of the primary index.</param>
/// <param name="IndexSha1">SHA-1 of the primary index as stored in the footer (hex).</param>
/// <param name="CompressionMethods">Compression method names declared in the footer (entry method index = position + 1).</param>
/// <param name="MountPoint">Raw mount point (e.g. <c>../../../</c>); null when the index is encrypted.</param>
/// <param name="EntryCount">Number of entries; null when the index is encrypted.</param>
/// <param name="PathHashSeed">Path hash seed (v10+); null when unknown.</param>
/// <param name="HasPathHashIndex">True when a path-hash index is present (v10+).</param>
/// <param name="HasFullDirectoryIndex">True when a full directory index is present (v10+).</param>
public sealed record PakHeaderInfo(
    string FilePath,
    long FileLength,
    int Version,
    bool EncryptedIndex,
    Guid EncryptionKeyGuid,
    long IndexOffset,
    long IndexSize,
    string IndexSha1,
    IReadOnlyList<string> CompressionMethods,
    string? MountPoint,
    int? EntryCount,
    ulong? PathHashSeed,
    bool? HasPathHashIndex,
    bool? HasFullDirectoryIndex);

/// <summary>An entry decoded from a v10/v11 pak index.</summary>
/// <param name="Path">Path relative to the mount point.</param>
/// <param name="Offset">Offset of the entry record (header followed by data).</param>
/// <param name="UncompressedSize">Original size.</param>
/// <param name="CompressedSize">Stored size.</param>
/// <param name="CompressionMethodIndex">0 = stored, otherwise 1-based index into <see cref="PakHeaderInfo.CompressionMethods"/>.</param>
/// <param name="IsEncrypted">True when the data is encrypted.</param>
/// <param name="CompressionBlockSize">Block size (0 when stored).</param>
/// <param name="BlockSizes">Compressed size of each block (empty when stored).</param>
public sealed record PakIndexEntry(
    string Path,
    long Offset,
    long UncompressedSize,
    long CompressedSize,
    int CompressionMethodIndex,
    bool IsEncrypted,
    int CompressionBlockSize,
    IReadOnlyList<int> BlockSizes);

/// <summary>A fully decoded, unencrypted v10/v11 pak index.</summary>
/// <param name="Header">Footer and primary index summary.</param>
/// <param name="Entries">Entries in directory-index order.</param>
/// <param name="PathHashIndex">Raw path-hash index bytes (null when absent).</param>
/// <param name="FullDirectoryIndex">Raw full directory index bytes.</param>
/// <param name="PathHashes">Path hash to encoded-entry offset, as stored.</param>
public sealed record PakIndex(
    PakHeaderInfo Header,
    IReadOnlyList<PakIndexEntry> Entries,
    byte[]? PathHashIndex,
    byte[] FullDirectoryIndex,
    IReadOnlyDictionary<ulong, int> PathHashes);
