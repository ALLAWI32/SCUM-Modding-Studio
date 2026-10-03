namespace ScumStudio.Pak.Writing;

/// <summary>Outcome of <see cref="PakWriter"/>.</summary>
/// <param name="OutputPath">Written pak (null when written to a caller-supplied stream).</param>
/// <param name="EntryCount">Number of entries.</param>
/// <param name="UncompressedBytes">Sum of the original file sizes.</param>
/// <param name="StoredBytes">Sum of the stored (possibly compressed) data sizes.</param>
/// <param name="PakSize">Total size of the pak.</param>
/// <param name="IndexOffset">Offset of the primary index.</param>
/// <param name="CompressedEntryCount">Entries actually stored compressed.</param>
/// <param name="SkippedFiles">Staging files that were not packed (relative paths).</param>
public sealed record PakWriteResult(
    string? OutputPath,
    int EntryCount,
    long UncompressedBytes,
    long StoredBytes,
    long PakSize,
    long IndexOffset,
    int CompressedEntryCount,
    IReadOnlyList<string> SkippedFiles);

/// <summary>Progress report of <see cref="PakWriter"/>.</summary>
/// <param name="EntriesWritten">Entries completed so far.</param>
/// <param name="EntryCount">Total entries.</param>
/// <param name="CurrentPath">Entry just completed.</param>
public readonly record struct PakWriteProgress(int EntriesWritten, int EntryCount, string CurrentPath);
