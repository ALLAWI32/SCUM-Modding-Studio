using System.Buffers.Binary;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Formats.Packages;

/// <summary>
/// A cooked UE 4.27 texture stores, before each pixel format's platform data, the <em>absolute</em> file offset just past
/// it (<c>UTexture::SerializeCookedPlatformData</c>'s <c>SkipOffset</c>; UE5 made it relative). The loader seeks there for a
/// format it cannot use. When a rewritten package moves a texture (a landscape tile whose grass was cleared grows its
/// components ahead of the tile's heightmap and weightmap textures), the stored offsets still name the old place: the
/// texture loads only while nothing seeks, and a strict reader (CUE4Parse, a server without that format) lands in the
/// middle of other data. <see cref="Relocate"/> moves them with the texture (defolt_0999's real Z_4 project, 2026-10-16:
/// 31 heightmaps/weightmaps of Landscape_Z_4_1b and _1d).
/// </summary>
public static class TextureSkipOffsets
{
    private static readonly HashSet<string> TextureClasses = new(StringComparer.Ordinal)
    {
        "Texture2D", "TextureCube", "Texture2DArray", "VolumeTexture", "LightMapTexture2D", "ShadowMapTexture2D", "LightMapVirtualTexture2D",
    };

    /// <summary>True for the classes whose cooked data carries the offsets (<see cref="TextureClasses"/>).</summary>
    public static bool IsTextureClass(string className) => TextureClasses.Contains(className);

    /// <summary>
    /// The payload of a texture export now laid out at <paramref name="newStart"/> (absolute), its skip offsets moved there
    /// from wherever they were written; <paramref name="previousStart"/> is where the bytes were read from, when known. The
    /// payload comes back unchanged when it already fits, is no cooked texture, or its offsets cannot be followed.
    /// </summary>
    public static ReadOnlyMemory<byte> Relocate(ReadOnlyMemory<byte> payload, IReadOnlyList<string> names, long previousStart, long newStart)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (FirstFormatName(payload, names) is not { } first)
        {
            return payload;
        }

        var span = payload.Span;
        var stored = BinaryPrimitives.ReadInt64LittleEndian(span[(first + 8)..]);
        foreach (var start in new[] { previousStart, stored - (span.Length - 8), stored - (span.Length - 12) })
        {
            if (Chain(span, names, first, start) is not { } offsets)
            {
                continue;
            }

            if (start == newStart)
            {
                return payload;
            }

            var copy = payload.ToArray();
            foreach (var at in offsets)
            {
                var value = BinaryPrimitives.ReadInt64LittleEndian(copy.AsSpan(at));
                BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(at), value - start + newStart);
            }

            return copy;
        }

        return payload;
    }

    /// <summary>Offset of the first pixel format name in a cooked texture payload, or null when it is none.</summary>
    private static int? FirstFormatName(ReadOnlyMemory<byte> payload, IReadOnlyList<string> names)
    {
        try
        {
            var bytes = payload.ToArray();
            var r = new ByteReader(bytes);
            new PropertyReader(names, _ => string.Empty, bytes, 0).ReadTaggedList(r);
            if (r.I32() != 0)
            {
                r.Position += 16; // the object's guid
            }

            r.Position += 4; // FStripDataFlags of UTexture and of the texture class
            if (r.I32() != 1)
            {
                return null; // not cooked
            }

            return r.Position + 16 <= bytes.Length && IsFormatName(bytes.AsSpan(r.Position), names) ? r.Position : null;
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// With the payload's bytes read from <paramref name="start"/>, the positions of the skip offsets: each one must land on
    /// the next format name after its platform data, the last on the closing <c>None</c>; null when they do not.
    /// </summary>
    private static List<int>? Chain(ReadOnlySpan<byte> span, IReadOnlyList<string> names, int first, long start)
    {
        var offsets = new List<int>();
        var at = first;
        for (var guard = 0; guard < 16; guard++)
        {
            if (at + 8 > span.Length)
            {
                return null;
            }

            if (IsNone(span[at..], names))
            {
                return offsets.Count > 0 ? offsets : null;
            }

            if (at + 16 > span.Length || !IsFormatName(span[at..], names))
            {
                return null;
            }

            var next = BinaryPrimitives.ReadInt64LittleEndian(span[(at + 8)..]) - start;
            if (next <= at + 16 || next > span.Length - 8)
            {
                return null;
            }

            offsets.Add(at + 8);
            at = (int)next;
        }

        return null;
    }

    private static bool IsFormatName(ReadOnlySpan<byte> at, IReadOnlyList<string> names)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(at);
        return index >= 0 && index < names.Count && BinaryPrimitives.ReadInt32LittleEndian(at[4..]) == 0
               && names[index].StartsWith("PF_", StringComparison.Ordinal);
    }

    private static bool IsNone(ReadOnlySpan<byte> at, IReadOnlyList<string> names)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(at);
        return index >= 0 && index < names.Count && BinaryPrimitives.ReadInt32LittleEndian(at[4..]) == 0
               && string.Equals(names[index], "None", StringComparison.Ordinal);
    }
}
