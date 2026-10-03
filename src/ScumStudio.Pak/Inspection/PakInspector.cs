using System.Security.Cryptography;
using System.Text;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Pak.Inspection;

/// <summary>
/// Independent, dependency-free reader for pak footers and unencrypted v10/v11 indexes. Used by <c>scumstudio pak info</c>
/// and to verify <see cref="PakWriter"/> output byte by byte (encoded entries, path-hash index, directory index, SHA-1s).
/// For reading file contents (and encrypted stock paks) use <see cref="Reading.PakFileSource"/> (CUE4Parse).
/// </summary>
public static class PakInspector
{
    /// <summary>Reads the footer and, when the index is not encrypted and the version is 10+, the primary index header.</summary>
    /// <exception cref="InvalidDataException">Not a pak file, or an unsupported pak version.</exception>
    public static PakHeaderInfo ReadHeader(string pakPath)
    {
        using var stream = File.OpenRead(pakPath);
        return ReadHeader(stream, Path.GetFullPath(pakPath));
    }

    /// <summary>Reads and fully decodes an unencrypted v10/v11 pak index (entries, path-hash and directory indexes).</summary>
    /// <exception cref="InvalidDataException">Encrypted index, pre-v10 pak, or corrupt index (including SHA-1 mismatches).</exception>
    public static PakIndex ReadIndex(string pakPath)
    {
        using var stream = File.OpenRead(pakPath);
        var header = ReadHeader(stream, Path.GetFullPath(pakPath));
        if (header.EncryptedIndex)
        {
            throw new InvalidDataException("The pak index is encrypted; use PakFileSource with the AES key instead.");
        }

        if (header.Version < PakFormat.VersionPathHashIndex)
        {
            throw new InvalidDataException($"Pak version {header.Version} has no path-hash index; only v10/v11 are decoded here.");
        }

        var primary = ReadAt(stream, header.IndexOffset, header.IndexSize);
        if (!Convert.ToHexString(SHA1.HashData(primary)).Equals(header.IndexSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Primary index SHA-1 mismatch.");
        }

        using var reader = new BinaryReader(new MemoryStream(primary), Encoding.UTF8);
        PakBinary.ReadFString(reader);
        var count = reader.ReadInt32();
        reader.ReadUInt64();
        byte[]? pathHashIndex = null;
        if (reader.ReadInt32() != 0)
        {
            pathHashIndex = ReadSecondary(stream, reader, "path-hash index");
        }

        if (reader.ReadInt32() == 0)
        {
            throw new InvalidDataException("The pak has no full directory index.");
        }

        var directoryIndex = ReadSecondary(stream, reader, "full directory index");
        var encoded = reader.ReadBytes(reader.ReadInt32());
        var unencodable = reader.ReadInt32();
        if (unencodable != 0)
        {
            throw new InvalidDataException($"{unencodable} non-encoded entries are not supported by this inspector.");
        }

        var entries = new List<PakIndexEntry>(count);
        using (var dir = new BinaryReader(new MemoryStream(directoryIndex), Encoding.UTF8))
        {
            var dirCount = dir.ReadInt32();
            for (var d = 0; d < dirCount; d++)
            {
                var directory = PakBinary.ReadFString(dir);
                var fileCount = dir.ReadInt32();
                for (var f = 0; f < fileCount; f++)
                {
                    var name = PakBinary.ReadFString(dir);
                    var location = dir.ReadInt32();
                    var path = directory == "/" ? name : directory + name;
                    entries.Add(DecodeEntry(path.TrimStart('/'), encoded, location));
                }
            }
        }

        if (entries.Count != count)
        {
            throw new InvalidDataException($"Directory index lists {entries.Count} files but the index declares {count}.");
        }

        var hashes = new Dictionary<ulong, int>();
        if (pathHashIndex is not null)
        {
            using var phi = new BinaryReader(new MemoryStream(pathHashIndex), Encoding.UTF8);
            var n = phi.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                hashes[phi.ReadUInt64()] = phi.ReadInt32();
            }
        }

        return new PakIndex(header, entries, pathHashIndex, directoryIndex, hashes);
    }

    /// <summary>
    /// Checks every entry of an unencrypted v10/v11 pak: the in-data record matches the index, the stored bytes hash to
    /// the recorded SHA-1, and compressed blocks inflate to the declared size. Returns a list of problems (empty = OK).
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(string pakPath, CancellationToken cancellationToken = default)
    {
        var index = ReadIndex(pakPath);
        var problems = new List<string>();
        if (index.PathHashIndex is not null)
        {
            foreach (var entry in index.Entries)
            {
                if (!index.PathHashes.ContainsKey(PakPathHash.Compute(entry.Path, index.Header.PathHashSeed ?? 0)))
                {
                    problems.Add($"{entry.Path}: path hash missing from the path-hash index");
                }
            }
        }

        await using var stream = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        foreach (var entry in index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var problem = await VerifyEntryAsync(stream, index.Header, entry, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                problems.Add($"{entry.Path}: {problem}");
            }
        }

        return problems;
    }

    private static async Task<string?> VerifyEntryAsync(Stream stream, PakHeaderInfo header, PakIndexEntry entry, CancellationToken cancellationToken)
    {
        var compressed = entry.CompressionMethodIndex != 0;
        var headerSize = PakFormat.GetEntryHeaderSize(compressed, entry.BlockSizes.Count);
        var raw = ReadAt(stream, entry.Offset, headerSize);
        using var reader = new BinaryReader(new MemoryStream(raw));
        reader.ReadInt64(); // offset (0 in the data copy)
        var storedSize = reader.ReadInt64();
        var size = reader.ReadInt64();
        var method = reader.ReadUInt32();
        var sha1 = reader.ReadBytes(20);
        if (storedSize != entry.CompressedSize || size != entry.UncompressedSize || method != entry.CompressionMethodIndex)
        {
            return "in-data record does not match the index";
        }

        var blocks = new List<(long Start, long End)>();
        if (compressed)
        {
            var n = reader.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                blocks.Add((reader.ReadInt64(), reader.ReadInt64()));
            }
        }

        reader.ReadByte();
        var blockSize = reader.ReadUInt32();
        if (compressed && blockSize != entry.CompressionBlockSize)
        {
            return "compression block size mismatch";
        }

        var data = ReadAt(stream, entry.Offset + headerSize, entry.CompressedSize);
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(sha1))
        {
            return "SHA-1 mismatch";
        }

        if (!compressed)
        {
            return entry.CompressedSize == entry.UncompressedSize ? null : "stored entry with differing sizes";
        }

        var methodName = header.CompressionMethods.ElementAtOrDefault(entry.CompressionMethodIndex - 1);
        if (!string.Equals(methodName, "Zlib", StringComparison.OrdinalIgnoreCase))
        {
            return null; // only zlib is inflated here
        }

        long total = 0;
        foreach (var (start, end) in blocks)
        {
            await using var zlib = new System.IO.Compression.ZLibStream(
                new MemoryStream(data, (int)(start - headerSize), (int)(end - start)), System.IO.Compression.CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = await zlib.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }
        }

        return total == entry.UncompressedSize ? null : $"inflated to {total} bytes, expected {entry.UncompressedSize}";
    }

    private static PakHeaderInfo ReadHeader(Stream stream, string path)
    {
        var length = stream.Length;
        if (length < 44)
        {
            throw new InvalidDataException($"{path} is too small to be a pak file.");
        }

        // v8B..v11 (except v9): 221 bytes; v9: 222 (bIndexIsFrozen); v8A: 189 (4 names); v4..v7: 61 (no names).
        foreach (var (footerSize, names, frozen) in new[] { (221, 5, false), (222, 5, true), (189, 4, false), (61, 0, false) })
        {
            if (length < footerSize)
            {
                continue;
            }

            var footer = ReadAt(stream, length - footerSize, footerSize);
            if (PakBinary.U32(footer, 17) != PakFormat.Magic)
            {
                continue;
            }

            var version = (int)PakBinary.U32(footer, 21);
            var indexOffset = PakBinary.I64(footer, 25);
            var indexSize = PakBinary.I64(footer, 33);
            var indexSha1 = Convert.ToHexString(footer, 41, 20);
            var methods = new List<string>();
            var namesOffset = 61 + (frozen ? 1 : 0);
            for (var i = 0; i < names; i++)
            {
                var slot = footer.AsSpan(namesOffset + (i * 32), 32);
                var end = slot.IndexOf((byte)0);
                var name = Encoding.ASCII.GetString(end < 0 ? slot : slot[..end]);
                if (name.Length > 0)
                {
                    methods.Add(name);
                }
            }

            var encrypted = footer[16] != 0;
            var guid = new Guid(footer.AsSpan(0, 16));
            if (indexOffset < 0 || indexSize < 0 || indexOffset + indexSize > length)
            {
                throw new InvalidDataException($"{path}: index location is out of range.");
            }

            string? mountPoint = null;
            int? entryCount = null;
            ulong? seed = null;
            bool? hasPhi = null, hasFdi = null;
            if (!encrypted)
            {
                var head = ReadAt(stream, indexOffset, Math.Min(indexSize, 64 * 1024));
                using var reader = new BinaryReader(new MemoryStream(head), Encoding.UTF8);
                mountPoint = PakBinary.ReadFString(reader);
                entryCount = reader.ReadInt32();
                if (version >= PakFormat.VersionPathHashIndex)
                {
                    seed = reader.ReadUInt64();
                    hasPhi = reader.ReadInt32() != 0;
                    if (hasPhi == true)
                    {
                        reader.ReadBytes(8 + 8 + 20);
                    }

                    hasFdi = reader.ReadInt32() != 0;
                }
            }

            return new PakHeaderInfo(path, length, version, encrypted, guid, indexOffset, indexSize, indexSha1, methods,
                mountPoint, entryCount, seed, hasPhi, hasFdi);
        }

        throw new InvalidDataException($"{path} is not a supported pak file (no pak footer magic found).");
    }

    private static byte[] ReadSecondary(Stream stream, BinaryReader primary, string what)
    {
        var offset = primary.ReadInt64();
        var size = primary.ReadInt64();
        var sha1 = primary.ReadBytes(20);
        if (offset < 0 || size < 0 || offset + size > stream.Length)
        {
            throw new InvalidDataException($"The {what} location is out of range.");
        }

        var data = ReadAt(stream, offset, size);
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(sha1))
        {
            throw new InvalidDataException($"The {what} SHA-1 does not match.");
        }

        return data;
    }

    /// <summary>Decodes one entry (port of FPakFile::DecodePakEntry).</summary>
    private static PakIndexEntry DecodeEntry(string path, byte[] encoded, int location)
    {
        if (location < 0 || location >= encoded.Length)
        {
            throw new InvalidDataException($"{path}: encoded entry location {location} is out of range.");
        }

        using var reader = new BinaryReader(new MemoryStream(encoded, location, encoded.Length - location));
        var flags = reader.ReadUInt32();
        var blockSizeCode = flags & 0x3F;
        var blockSize = blockSizeCode == 0x3F ? (int)reader.ReadUInt32() : (int)(blockSizeCode << 11);
        var blockCount = (int)((flags >> 6) & 0xFFFF);
        var encrypted = ((flags >> 22) & 1) != 0;
        var method = (int)((flags >> 23) & 0x3F);
        var offset = (flags & (1u << 31)) != 0 ? reader.ReadUInt32() : reader.ReadInt64();
        var uncompressed = (flags & (1u << 30)) != 0 ? reader.ReadUInt32() : reader.ReadInt64();
        long compressedSize = uncompressed;
        var blockSizes = new List<int>();
        if (method != 0)
        {
            compressedSize = (flags & (1u << 29)) != 0 ? reader.ReadUInt32() : reader.ReadInt64();
            if (blockCount == 1 && !encrypted)
            {
                blockSizes.Add((int)compressedSize);
            }
            else
            {
                for (var i = 0; i < blockCount; i++)
                {
                    blockSizes.Add((int)reader.ReadUInt32());
                }
            }
        }

        return new PakIndexEntry(path, offset, uncompressed, compressedSize, method, encrypted, method == 0 ? 0 : blockSize, blockSizes);
    }

    private static byte[] ReadAt(Stream stream, long offset, long size)
    {
        var buffer = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
