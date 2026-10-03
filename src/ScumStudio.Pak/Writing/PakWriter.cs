using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Writing;

/// <summary>
/// Writes an unencrypted Unreal Engine 4.27 pak, version 11 (Fnv64BugFix), the format of SCUM's mod paks.
/// </summary>
/// <remarks>
/// <para>Layout (same as UnrealPak and repak): for every entry, a full FPakEntry record (offset field 0, block offsets
/// relative to the record) followed by the stored bytes; then the primary index (mount point, entry count, path hash
/// seed, locations + SHA-1 of the two secondary indexes, encoded entries, zero non-encoded entries); the path-hash index
/// (FNV-64 of each lower-cased path to its encoded-entry offset, then an empty pruned directory index); the full
/// directory index ("/" and every parent directory with its files); and the 221-byte footer (zero key GUID, not encrypted,
/// magic 0x5A6F12E1, version 11, index offset/size/SHA-1, five 32-byte compression method names).</para>
/// <para>Entries are written in ordinal path order, so identical inputs give identical paks. Validated against repak 0.2.3
/// (info/list/unpack) and CUE4Parse in the test suite. Replaces the repak call in make_pak.py.</para>
/// </remarks>
public sealed class PakWriter
{
    private readonly ILogger _logger;

    /// <summary>Creates a writer.</summary>
    public PakWriter(PakWriterOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? new PakWriterOptions();
        if (Options.CompressionBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CompressionBlockSize must be positive.");
        }

        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The options in use.</summary>
    public PakWriterOptions Options { get; }

    /// <summary>
    /// Collects the entries of a staging directory laid out as a project root (<c>&lt;dir&gt;/SCUM/Content/...</c>,
    /// optionally <c>&lt;dir&gt;/SCUM/AssetRegistry.bin</c>).
    /// </summary>
    /// <param name="stagingDirectory">Project-root staging folder.</param>
    /// <param name="skipped">Receives the relative paths of files that are not packed.</param>
    public IReadOnlyList<PakWriterEntry> CollectEntries(string stagingDirectory, out IReadOnlyList<string> skipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var root = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Staging directory not found: {root}");
        }

        var entries = new List<PakWriterEntry>();
        var skippedList = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = VirtualPath.Normalize(Path.GetRelativePath(root, file));
            var isRegistry = string.Equals(relative, Options.ProjectName + "/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase);
            if ((Options.OnlyGameContent && !PakPaths.IsModPakEntry(relative, Options.ProjectName))
                || (isRegistry && !Options.IncludeAssetRegistry))
            {
                skippedList.Add(relative);
                continue;
            }

            entries.Add(PakWriterEntry.FromFile(relative, file));
        }

        skippedList.Sort(StringComparer.Ordinal);
        skipped = skippedList;
        return entries;
    }

    /// <summary>Packs a staging directory (see <see cref="CollectEntries"/>) into <paramref name="outputPakPath"/>.</summary>
    public async Task<PakWriteResult> WriteFromDirectoryAsync(
        string stagingDirectory,
        string outputPakPath,
        IProgress<PakWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = CollectEntries(stagingDirectory, out var skipped);
        foreach (var file in skipped)
        {
            _logger.LogWarning("Not packed (outside {Project}/Content and not the asset registry): {File}", Options.ProjectName, file);
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"Nothing to pack: no files below {Path.Combine(stagingDirectory, Options.ProjectName, "Content")}.");
        }

        var result = await WriteAsync(entries, outputPakPath, progress, cancellationToken).ConfigureAwait(false);
        return result with { SkippedFiles = skipped };
    }

    /// <summary>
    /// Writes <paramref name="entries"/> to <paramref name="outputPakPath"/> (via a temporary file that replaces the
    /// target only on success).
    /// </summary>
    public async Task<PakWriteResult> WriteAsync(
        IReadOnlyList<PakWriterEntry> entries,
        string outputPakPath,
        IProgress<PakWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPakPath);
        var output = Path.GetFullPath(outputPakPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".tmp";
        try
        {
            PakWriteResult result;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                result = await WriteAsync(entries, stream, progress, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, output, overwrite: true);
            _logger.LogInformation("Wrote {Pak}: {Count} entries, {Size:N0} bytes.", output, result.EntryCount, result.PakSize);
            return result with { OutputPath = output };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Writes <paramref name="entries"/> to a seekable, readable/writable stream starting at its current position 0.</summary>
    public async Task<PakWriteResult> WriteAsync(
        IReadOnlyList<PakWriterEntry> entries,
        Stream output,
        IProgress<PakWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("The output stream must be seekable and writable.", nameof(output));
        }

        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var duplicate = ordered.GroupBy(e => e.Path, VirtualPath.Comparer).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate pak entry path (paths are case-insensitive): {duplicate.Key}", nameof(entries));
        }

        output.SetLength(0);
        output.Position = 0;
        var records = new List<PakEntryRecord>(ordered.Count);
        long uncompressed = 0, stored = 0;
        var compressedCount = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = ordered[i];
            var record = await WriteEntryAsync(entry, output, cancellationToken).ConfigureAwait(false);
            records.Add(record);
            uncompressed += record.UncompressedSize;
            stored += record.CompressedSize;
            compressedCount += record.CompressionMethodIndex != 0 ? 1 : 0;
            progress?.Report(new PakWriteProgress(i + 1, ordered.Count, entry.Path));
        }

        var indexOffset = output.Position;
        var (primaryIndex, secondary) = BuildIndex(ordered, records, indexOffset);
        await output.WriteAsync(primaryIndex, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(secondary, cancellationToken).ConfigureAwait(false);
        var footer = BuildFooter(indexOffset, primaryIndex);
        await output.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
        output.SetLength(output.Position);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new PakWriteResult(null, records.Count, uncompressed, stored, output.Length, indexOffset, compressedCount, []);
    }

    private string[] CompressionMethodNames =>
        Options.Compression == PakCompression.Zlib ? ["Zlib"] : [];

    private async Task<PakEntryRecord> WriteEntryAsync(PakWriterEntry entry, Stream output, CancellationToken cancellationToken)
    {
        var entryOffset = output.Position;
        await using var source = entry.OpenRead();
        var length = source.Length;
        if (Options.Compression == PakCompression.Zlib && length > 0)
        {
            var compressed = await TryWriteZlibAsync(source, length, output, entryOffset, cancellationToken).ConfigureAwait(false);
            if (compressed is not null)
            {
                return compressed;
            }

            // Not smaller: rewind and store it uncompressed (UnrealPak does the same).
            output.Position = entryOffset;
            source.Position = 0;
        }

        return await WriteStoredAsync(source, length, output, entryOffset, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PakEntryRecord> WriteStoredAsync(Stream source, long length, Stream output, long entryOffset, CancellationToken cancellationToken)
    {
        output.Position = entryOffset + PakFormat.EntryHeaderSize;
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha1.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }

        if (copied != length)
        {
            throw new IOException($"Source changed while packing (expected {length} bytes, read {copied}).");
        }

        var record = new PakEntryRecord
        {
            Offset = entryOffset,
            CompressedSize = length,
            UncompressedSize = length,
            CompressionMethodIndex = 0,
            Sha1 = sha1.GetHashAndReset(),
        };
        var end = output.Position;
        await WriteHeaderAsync(record, output, cancellationToken).ConfigureAwait(false);
        output.Position = end;
        return record;
    }

    private async Task<PakEntryRecord?> TryWriteZlibAsync(Stream source, long length, Stream output, long entryOffset, CancellationToken cancellationToken)
    {
        var blockSize = Options.CompressionBlockSize;
        var blockCount = checked((int)((length + blockSize - 1) / blockSize));
        if (blockCount > 0xFFFF)
        {
            return null; // cannot be encoded in the index; store instead
        }

        var headerSize = PakFormat.GetEntryHeaderSize(true, blockCount);
        output.Position = entryOffset + headerSize;
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var blocks = new List<PakCompressionBlock>(blockCount);
        var input = new byte[blockSize];
        using var compressed = new MemoryStream(blockSize + 1024);
        long relative = headerSize;
        long total = 0;
        for (var b = 0; b < blockCount; b++)
        {
            var want = (int)Math.Min(blockSize, length - ((long)b * blockSize));
            await source.ReadExactlyAsync(input.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            compressed.SetLength(0);
            using (var zlib = new ZLibStream(compressed, Options.ZlibLevel, leaveOpen: true))
            {
                zlib.Write(input, 0, want);
            }

            var size = (int)compressed.Length;
            total += size;
            if (total >= length)
            {
                return null;
            }

            var bytes = compressed.GetBuffer();
            sha1.AppendData(bytes, 0, size);
            await output.WriteAsync(bytes.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            blocks.Add(new PakCompressionBlock(relative, relative + size));
            relative += size;
        }

        var record = new PakEntryRecord
        {
            Offset = entryOffset,
            CompressedSize = total,
            UncompressedSize = length,
            CompressionMethodIndex = 1,
            Sha1 = sha1.GetHashAndReset(),
            Blocks = blocks,
            CompressionBlockSize = blockSize,
        };
        var end = output.Position;
        await WriteHeaderAsync(record, output, cancellationToken).ConfigureAwait(false);
        output.Position = end;
        return record;
    }

    private static async Task WriteHeaderAsync(PakEntryRecord record, Stream output, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream(record.HeaderSize);
        using (var writer = new BinaryWriter(header, Encoding.UTF8, leaveOpen: true))
        {
            record.WriteFull(writer, inData: true);
        }

        if (header.Length != record.HeaderSize)
        {
            throw new InvalidOperationException("Internal error: entry header size mismatch.");
        }

        output.Position = record.Offset;
        await output.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the primary index and the secondary indexes (path-hash index followed by full directory index).</summary>
    private (byte[] Primary, byte[] Secondary) BuildIndex(IReadOnlyList<PakWriterEntry> entries, IReadOnlyList<PakEntryRecord> records, long indexOffset)
    {
        // Encoded entries, remembering where each one starts.
        var encodedOffsets = new int[records.Count];
        using var encoded = new MemoryStream();
        using (var writer = new BinaryWriter(encoded, Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < records.Count; i++)
            {
                encodedOffsets[i] = checked((int)encoded.Position);
                records[i].WriteEncoded(writer);
            }
        }

        // Path hash index: TMap<uint64, FPakEntryLocation> + empty pruned directory index.
        byte[] pathHashIndex;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                writer.Write(PakPathHash.Compute(entries[i].Path, Options.PathHashSeed));
                writer.Write(encodedOffsets[i]);
            }

            writer.Write(0);
            writer.Flush();
            pathHashIndex = ms.ToArray();
        }

        // Full directory index: TMap<FString directory, TMap<FString file, FPakEntryLocation>>.
        var directories = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var path = entries[i].Path;
            var slash = path.LastIndexOf('/');
            var directory = slash < 0 ? "/" : path[..(slash + 1)];
            var fileName = slash < 0 ? path : path[(slash + 1)..];
            GetOrAdd(directories, directory).Add(fileName, encodedOffsets[i]);
            while (directory != "/")
            {
                var parentSlash = directory.LastIndexOf('/', directory.Length - 2);
                directory = parentSlash < 0 ? "/" : directory[..(parentSlash + 1)];
                GetOrAdd(directories, directory);
            }
        }

        byte[] directoryIndex;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(directories.Count);
            foreach (var (directory, files) in directories)
            {
                PakBinary.WriteFString(writer, directory);
                writer.Write(files.Count);
                foreach (var (fileName, location) in files)
                {
                    PakBinary.WriteFString(writer, fileName);
                    writer.Write(location);
                }
            }

            writer.Flush();
            directoryIndex = ms.ToArray();
        }

        // Primary index; its size is needed for the secondary offsets, and it does not depend on them.
        var primarySize = MeasurePrimary((int)encoded.Length);
        var pathHashIndexOffset = indexOffset + primarySize;
        var directoryIndexOffset = pathHashIndexOffset + pathHashIndex.Length;
        byte[] primary;
        using (var ms = new MemoryStream(primarySize))
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            PakBinary.WriteFString(writer, Options.MountPoint);
            writer.Write(entries.Count);
            writer.Write(Options.PathHashSeed);
            writer.Write(1); // bReaderHasPathHashIndex
            writer.Write(pathHashIndexOffset);
            writer.Write((long)pathHashIndex.Length);
            writer.Write(SHA1.HashData(pathHashIndex));
            writer.Write(1); // bReaderHasFullDirectoryIndex
            writer.Write(directoryIndexOffset);
            writer.Write((long)directoryIndex.Length);
            writer.Write(SHA1.HashData(directoryIndex));
            writer.Write((int)encoded.Length);
            writer.Write(encoded.GetBuffer(), 0, (int)encoded.Length);
            writer.Write(0); // no entries that could not be encoded
            writer.Flush();
            primary = ms.ToArray();
        }

        if (primary.Length != primarySize)
        {
            throw new InvalidOperationException("Internal error: primary index size mismatch.");
        }

        return (primary, [.. pathHashIndex, .. directoryIndex]);
    }

    private int MeasurePrimary(int encodedLength)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        PakBinary.WriteFString(writer, Options.MountPoint);
        writer.Flush();
        return (int)ms.Length + 4 + 8 + (4 + 8 + 8 + 20) * 2 + 4 + encodedLength + 4;
    }

    private byte[] BuildFooter(long indexOffset, byte[] primaryIndex)
    {
        using var ms = new MemoryStream(PakFormat.FooterSizeV11);
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new byte[16]); // encryption key GUID: none
            writer.Write((byte)0);      // bEncryptedIndex
            writer.Write(PakFormat.Magic);
            writer.Write(PakFormat.VersionFnv64BugFix);
            writer.Write(indexOffset);
            writer.Write((long)primaryIndex.Length);
            writer.Write(SHA1.HashData(primaryIndex));
            var names = CompressionMethodNames;
            for (var i = 0; i < PakFormat.CompressionMethodSlots; i++)
            {
                var slot = new byte[PakFormat.CompressionMethodNameLength];
                if (i < names.Length)
                {
                    Encoding.ASCII.GetBytes(names[i]).CopyTo(slot, 0);
                }

                writer.Write(slot);
            }
        }

        if (ms.Length != PakFormat.FooterSizeV11)
        {
            throw new InvalidOperationException("Internal error: footer size mismatch.");
        }

        return ms.ToArray();
    }

    private static SortedDictionary<string, int> GetOrAdd(SortedDictionary<string, SortedDictionary<string, int>> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = new SortedDictionary<string, int>(StringComparer.Ordinal);
            map.Add(key, value);
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
