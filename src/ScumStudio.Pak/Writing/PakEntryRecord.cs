namespace ScumStudio.Pak.Writing;

/// <summary>One compression block, offsets relative to the start of the entry record (pak v5+ RelativeChunkOffsets).</summary>
/// <param name="Start">First byte of the compressed block.</param>
/// <param name="End">One past the last byte.</param>
public readonly record struct PakCompressionBlock(long Start, long End)
{
    /// <summary>Compressed size of the block.</summary>
    public long Size => End - Start;
}

/// <summary>
/// An FPakEntry as written by <see cref="PakWriter"/>: serialised in full before the file data (with offset 0),
/// and as an encoded entry (FPakFile::EncodePakEntry) in the index.
/// </summary>
internal sealed record PakEntryRecord
{
    /// <summary>Absolute offset of the entry record (header + data) in the pak.</summary>
    public required long Offset { get; init; }

    /// <summary>Stored (possibly compressed) size of the data.</summary>
    public required long CompressedSize { get; init; }

    /// <summary>Original size of the file.</summary>
    public required long UncompressedSize { get; init; }

    /// <summary>1-based index into the footer's compression method names; 0 = stored.</summary>
    public required int CompressionMethodIndex { get; init; }

    /// <summary>SHA-1 of the stored bytes (compressed bytes for compressed entries).</summary>
    public required byte[] Sha1 { get; init; }

    /// <summary>Compression blocks (empty when stored).</summary>
    public IReadOnlyList<PakCompressionBlock> Blocks { get; init; } = [];

    /// <summary>Compression block size (0 when stored).</summary>
    public int CompressionBlockSize { get; init; }

    /// <summary>Size of the serialised header that precedes the data.</summary>
    public int HeaderSize => PakFormat.GetEntryHeaderSize(CompressionMethodIndex != 0, Blocks.Count);

    /// <summary>Writes the full FPakEntry (FPakEntry::Serialize, version 11).</summary>
    /// <param name="writer">Target.</param>
    /// <param name="inData">True for the copy in front of the data, whose offset field is 0 (as UnrealPak/repak write it).</param>
    public void WriteFull(BinaryWriter writer, bool inData)
    {
        writer.Write(inData ? 0L : Offset);
        writer.Write(CompressedSize);
        writer.Write(UncompressedSize);
        writer.Write((uint)CompressionMethodIndex);
        writer.Write(Sha1);
        if (CompressionMethodIndex != 0)
        {
            writer.Write(Blocks.Count);
            foreach (var block in Blocks)
            {
                writer.Write(block.Start);
                writer.Write(block.End);
            }
        }

        writer.Write((byte)0); // flags: not encrypted, not deleted
        writer.Write((uint)CompressionBlockSize);
    }

    /// <summary>Writes the bit-packed index form (FPakFile::EncodePakEntry).</summary>
    public void WriteEncoded(BinaryWriter writer)
    {
        if (Blocks.Count > 0xFFFF)
        {
            throw new NotSupportedException($"An entry with {Blocks.Count} compression blocks cannot be encoded (max 65535).");
        }

        var blockSizeCode = (uint)(CompressionBlockSize >> 11);
        if (blockSizeCode > 0x3F || (blockSizeCode << 11) != (uint)CompressionBlockSize)
        {
            blockSizeCode = 0x3F;
        }

        var offset32 = Offset <= uint.MaxValue;
        var uncompressed32 = UncompressedSize <= uint.MaxValue;
        var compressed32 = CompressedSize <= uint.MaxValue;
        var flags = (offset32 ? 1u << 31 : 0)
                    | (uncompressed32 ? 1u << 30 : 0)
                    | (compressed32 ? 1u << 29 : 0)
                    | ((uint)CompressionMethodIndex << 23)
                    | ((uint)Blocks.Count << 6)
                    | blockSizeCode;
        writer.Write(flags);
        if (blockSizeCode == 0x3F)
        {
            writer.Write((uint)CompressionBlockSize);
        }

        WriteVar(writer, Offset, offset32);
        WriteVar(writer, UncompressedSize, uncompressed32);
        if (CompressionMethodIndex != 0)
        {
            WriteVar(writer, CompressedSize, compressed32);
            if (Blocks.Count > 1)
            {
                foreach (var block in Blocks)
                {
                    writer.Write((uint)block.Size);
                }
            }
        }
    }

    private static void WriteVar(BinaryWriter writer, long value, bool as32)
    {
        if (as32)
        {
            writer.Write((uint)value);
        }
        else
        {
            writer.Write(value);
        }
    }
}
