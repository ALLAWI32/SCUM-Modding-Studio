using System.Buffers.Binary;

namespace ScumStudio.Formats;

/// <summary>
/// Unreal FGuid: four little-endian uint32 values (16 bytes on disk). <see cref="ToString"/> returns the
/// 32-char lower-case hex of the raw bytes, the representation used by <c>ue4pkg.py</c> (<c>R.guid()</c>).
/// </summary>
public readonly record struct FGuid(uint A, uint B, uint C, uint D)
{
    /// <summary>The all-zero guid.</summary>
    public static FGuid Zero => default;

    /// <summary>True when all components are zero.</summary>
    public bool IsZero => A == 0 && B == 0 && C == 0 && D == 0;

    /// <summary>Reads a guid from 16 bytes.</summary>
    public static FGuid FromBytes(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));

    /// <summary>Parses the 32-char raw-byte hex form produced by <see cref="ToString"/>.</summary>
    public static FGuid ParseHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length != 16)
        {
            throw new FormatException($"FGuid hex must be 32 characters, got {hex.Length}.");
        }

        return FromBytes(bytes);
    }

    /// <summary>Writes the 16 raw bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, A);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], B);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], C);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], D);
    }

    /// <summary>Raw bytes as a new array.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[16];
        WriteTo(bytes);
        return bytes;
    }

    /// <inheritdoc />
    public override string ToString() => Convert.ToHexString(ToBytes()).ToLowerInvariant();
}
