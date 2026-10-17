using System.Buffers.Binary;

namespace ScumStudio.Level.Export;

/// <summary>
/// The cooked collision of a landscape component (<c>ULandscapeHeightfieldCollisionComponent</c>): after its tagged
/// properties come the object guid flag (+ guid), <c>bCooked</c> and <c>CookedCollisionData</c> (<c>BulkSerialize</c>:
/// element size 1, count, bytes), which is a PhysX 3.4 heightfield stream (<c>NXS</c> + <c>HFHF</c> header, then one
/// 4-byte sample per grid point: int16 height, material byte 0, material byte 1).
/// </summary>
public static class LandscapeCollision
{
    /// <summary>PhysX's hole material (<c>PxHeightFieldMaterial::eHOLE</c>): a cell whose material bits are this has no collision.</summary>
    public const byte HoleMaterial = 0x7F;

    /// <summary>Where the heightfield of one collision component lies in its export.</summary>
    /// <param name="StreamOffset">Export-relative offset of the PhysX stream (<c>NXS</c>).</param>
    /// <param name="StreamLength">Bytes of the stream (<c>CookedCollisionData</c>'s count).</param>
    /// <param name="Version">PhysX heightfield stream version.</param>
    /// <param name="Rows">PhysX rows.</param>
    /// <param name="Columns">PhysX columns.</param>
    /// <param name="Flags">PhysX heightfield flags.</param>
    /// <param name="Format">PhysX sample format (1 = 16-bit height + two material bytes).</param>
    /// <param name="SampleStride">Bytes per sample.</param>
    /// <param name="SamplesOffset">Export-relative offset of the first sample.</param>
    /// <param name="MinHeight">Lowest sample height in the header.</param>
    /// <param name="MaxHeight">Highest sample height in the header.</param>
    public readonly record struct Heightfield(int StreamOffset, int StreamLength, int Version, int Rows, int Columns, int Flags, int Format,
        int SampleStride, int SamplesOffset, float MinHeight, float MaxHeight)
    {
        /// <summary>Export-relative offset of sample (<paramref name="row"/>, <paramref name="column"/>).</summary>
        public int SampleAt(int row, int column) => SamplesOffset + (((row * Columns) + column) * SampleStride);
    }

    /// <summary>
    /// The heightfield of a cooked collision component export (<paramref name="payload"/>), read from the end of its tagged
    /// properties (<paramref name="propertiesEnd"/>); null when the bytes do not have that shape.
    /// </summary>
    public static Heightfield? Read(ReadOnlySpan<byte> payload, int propertiesEnd)
    {
        var at = propertiesEnd;
        if (!TryInt(payload, ref at, out var hasGuid) || hasGuid is not (0 or 1))
        {
            return null;
        }

        at += hasGuid * 16;
        if (!TryInt(payload, ref at, out var cooked) || cooked != 1 || !TryInt(payload, ref at, out var elementSize) || elementSize != 1
            || !TryInt(payload, ref at, out var length) || length < 32 || at + length > payload.Length)
        {
            return null;
        }

        var s = at;
        var stream = payload.Slice(s, length);
        if (stream[0] != 'N' || stream[1] != 'X' || stream[2] != 'S' || stream[4] != 'H' || stream[5] != 'F' || stream[6] != 'H' || stream[7] != 'F')
        {
            return null;
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(stream[8..]);
        var rows = BinaryPrimitives.ReadInt32LittleEndian(stream[12..]);
        var columns = BinaryPrimitives.ReadInt32LittleEndian(stream[16..]);
        if (rows is < 2 or > 4096 || columns is < 2 or > 4096)
        {
            return null;
        }

        // PhysX 3.4 saveHeightField: rows, columns, rowLimit, colLimit, nbColumns, thickness, convexEdgeThreshold (f32),
        // flags (u16), format (u32), the bounds (6 x f32), sampleStride, nbSamples (u32), min and max height (f32).
        const int flagsAt = 40, formatAt = 42, strideAt = 70, countAt = 74, samplesAt = 86;
        if (length < samplesAt || BinaryPrimitives.ReadInt32LittleEndian(stream[strideAt..]) != 4
            || BinaryPrimitives.ReadInt32LittleEndian(stream[countAt..]) != rows * columns
            || samplesAt + (4 * rows * columns) > length)
        {
            return null;
        }

        return new Heightfield(s, length, version, rows, columns, BinaryPrimitives.ReadUInt16LittleEndian(stream[flagsAt..]),
            BinaryPrimitives.ReadInt32LittleEndian(stream[formatAt..]), 4, s + samplesAt,
            BinaryPrimitives.ReadSingleLittleEndian(stream[(countAt + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(stream[(countAt + 8)..]));
    }

    private static bool TryInt(ReadOnlySpan<byte> data, ref int at, out int value)
    {
        if (at < 0 || at + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += 4;
        return true;
    }
}
