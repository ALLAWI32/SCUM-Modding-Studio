using System.Buffers.Binary;
using System.Text;

namespace ScumStudio.Formats.IO;

/// <summary>
/// Little-endian cursor over a byte array (port of <c>ue4pkg.py</c> class <c>R</c>).
/// <see cref="Position"/> is an absolute index into <see cref="Data"/>; reads never pass <see cref="End"/>.
/// </summary>
public sealed class ByteReader
{
    /// <summary>Upper bound for a single FString length, to fail fast on garbage.</summary>
    public const int MaxStringLength = 16 * 1024 * 1024;

    /// <summary>Creates a reader over the whole array.</summary>
    public ByteReader(byte[] data)
        : this(data, 0, data.Length)
    {
    }

    /// <summary>Creates a reader over <c>data[start .. start+length)</c>; positions stay absolute.</summary>
    public ByteReader(byte[] data, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (start < 0 || length < 0 || start + length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"Window {start}+{length} outside buffer of {data.Length} bytes.");
        }

        Data = data;
        Start = start;
        End = start + length;
        Position = start;
    }

    /// <summary>Underlying buffer.</summary>
    public byte[] Data { get; }

    /// <summary>First readable absolute offset.</summary>
    public int Start { get; }

    /// <summary>Exclusive absolute end offset.</summary>
    public int End { get; }

    /// <summary>Absolute read position.</summary>
    public int Position { get; set; }

    /// <summary>Bytes left before <see cref="End"/>.</summary>
    public int Remaining => End - Position;

    /// <summary>Position relative to <see cref="Start"/>.</summary>
    public int RelativePosition => Position - Start;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > End || Position < Start)
        {
            throw new EndOfStreamException($"Read of {count} bytes at {Position} passes the end ({End}).");
        }

        var span = new ReadOnlySpan<byte>(Data, Position, count);
        Position += count;
        return span;
    }

    /// <summary>Reads an unsigned byte.</summary>
    public byte U8() => Take(1)[0];

    /// <summary>Reads a signed byte.</summary>
    public sbyte I8() => (sbyte)Take(1)[0];

    /// <summary>Reads an int16.</summary>
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    /// <summary>Reads a uint16.</summary>
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads an int32.</summary>
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>Reads a uint32.</summary>
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>Reads an int64.</summary>
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    /// <summary>Reads a uint64.</summary>
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Reads a float32.</summary>
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>Reads a float64.</summary>
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Reads <paramref name="count"/> raw bytes into a new array.</summary>
    public byte[] Raw(int count) => Take(count).ToArray();

    /// <summary>Skips <paramref name="count"/> bytes.</summary>
    public void Skip(int count) => Take(count);

    /// <summary>Reads a 16-byte FGuid.</summary>
    public FGuid Guid() => FGuid.FromBytes(Take(16));

    /// <summary>Reads an FName reference (int32 index, int32 number).</summary>
    public FNameRef FName() => new(I32(), I32());

    /// <summary>
    /// Reads an FString: int32 length (0 = empty; &gt;0 = Latin-1 bytes incl. NUL; &lt;0 = UTF-16 chars incl. NUL).
    /// </summary>
    public string FString() => FStringWithEncoding().Value;

    /// <summary>Reads an FString and reports whether it was stored as UTF-16.</summary>
    public (string Value, bool IsWide) FStringWithEncoding()
    {
        var n = I32();
        if (n == 0)
        {
            return (string.Empty, false);
        }

        if (n == int.MinValue || Math.Abs(n) > MaxStringLength)
        {
            throw new FormatException($"Implausible FString length {n} at {Position - 4}.");
        }

        if (n < 0)
        {
            var bytes = Take(-2 * n);
            return (Encoding.Unicode.GetString(bytes[..^2]), true);
        }

        var narrow = Take(n);
        return (Encoding.Latin1.GetString(narrow[..^1]), false);
    }
}
