using System.Buffers.Binary;
using System.Text;

namespace ScumStudio.Formats.IO;

/// <summary>
/// Growable little-endian byte writer (port of <c>ue4write.py</c> class <c>W</c>).
/// </summary>
public sealed class ByteWriter
{
    private byte[] _buffer;

    /// <summary>Creates a writer with the given initial capacity.</summary>
    public ByteWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    /// <summary>Number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>The written bytes (a view; valid until the next write).</summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, Length);

    /// <summary>Same as <see cref="Length"/> (Python <c>tell()</c>).</summary>
    public int Tell() => Length;

    private Span<byte> Grow(int count)
    {
        var needed = Length + count;
        if (needed > _buffer.Length)
        {
            var size = Math.Max(needed, _buffer.Length * 2);
            Array.Resize(ref _buffer, size);
        }

        var span = new Span<byte>(_buffer, Length, count);
        Length = needed;
        return span;
    }

    /// <summary>Writes a byte.</summary>
    public void U8(byte value) => Grow(1)[0] = value;

    /// <summary>Writes a uint16.</summary>
    public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value);

    /// <summary>Writes an int16.</summary>
    public void I16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Grow(2), value);

    /// <summary>Writes an int32.</summary>
    public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value);

    /// <summary>Writes a uint32.</summary>
    public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);

    /// <summary>Writes an int64.</summary>
    public void I64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value);

    /// <summary>Writes a uint64.</summary>
    public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value);

    /// <summary>Writes a float32.</summary>
    public void F32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value);

    /// <summary>Writes a float64.</summary>
    public void F64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), value);

    /// <summary>Writes raw bytes.</summary>
    public void Raw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

    /// <summary>Writes a 16-byte FGuid.</summary>
    public void Guid(FGuid guid) => guid.WriteTo(Grow(16));

    /// <summary>Writes an FName reference (int32 index, int32 number).</summary>
    public void FName(FNameRef name)
    {
        I32(name.Index);
        I32(name.Number);
    }

    /// <summary>Writes a UE-style bool (int32 0/1).</summary>
    public void Bool32(bool value) => I32(value ? 1 : 0);

    /// <summary>
    /// Writes an FString like <c>W.fstr</c>: empty = length 0; pure ASCII = narrow with NUL; otherwise UTF-16 with a
    /// negative length. <paramref name="wide"/> overrides the choice: true = always UTF-16, false = narrow Latin-1
    /// whenever every char fits in a byte (to reproduce names the cooker stored narrow).
    /// </summary>
    public void FString(string value, bool? wide = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            I32(0);
            return;
        }

        var narrow = wide switch
        {
            null => IsAscii(value),
            true => false,
            false => IsLatin1(value),
        };
        if (narrow)
        {
            I32(value.Length + 1);
            var span = Grow(value.Length + 1);
            Encoding.Latin1.GetBytes(value, span);
            span[^1] = 0;
            return;
        }

        I32(-(value.Length + 1));
        var bytes = Grow(2 * (value.Length + 1));
        Encoding.Unicode.GetBytes(value, bytes);
        bytes[^2] = 0;
        bytes[^1] = 0;
    }

    /// <summary>True when every char is at most 0xFF.</summary>
    public static bool IsLatin1(string value)
    {
        foreach (var c in value)
        {
            if (c > 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when every char is 7-bit ASCII (the condition <c>str.encode('ascii')</c> tests).</summary>
    public static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Overwrites an int32 at an earlier position.</summary>
    public void PatchI32(int position, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(_buffer, position, 4), value);

    /// <summary>Copies the written bytes to a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();
}
