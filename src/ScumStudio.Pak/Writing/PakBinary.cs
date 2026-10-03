using System.Buffers.Binary;
using System.Text;

namespace ScumStudio.Pak.Writing;

/// <summary>Little-endian helpers for Unreal serialisation (FString etc.).</summary>
internal static class PakBinary
{
    /// <summary>Writes an FString: ANSI with length+1 when every char is 7-bit, otherwise UTF-16LE with negative length.</summary>
    public static void WriteFString(BinaryWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        if (value.All(c => c < 0x80))
        {
            writer.Write(value.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(value));
            writer.Write((byte)0);
        }
        else
        {
            writer.Write(-(value.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(value));
            writer.Write((ushort)0);
        }
    }

    /// <summary>Reads an FString written by <see cref="WriteFString"/> (or by Unreal).</summary>
    public static string ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > 0)
        {
            if (length > 1 << 20)
            {
                throw new InvalidDataException($"FString length {length} is implausible.");
            }

            var bytes = reader.ReadBytes(length);
            return Encoding.Latin1.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
        }

        if (length < -(1 << 20))
        {
            throw new InvalidDataException($"FString length {length} is implausible.");
        }

        var wide = reader.ReadBytes(-length * 2);
        return Encoding.Unicode.GetString(wide, 0, Math.Max(0, wide.Length - 2));
    }

    /// <summary>Reads a little-endian uint32 from a span.</summary>
    public static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    /// <summary>Reads a little-endian int64 from a span.</summary>
    public static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
}
