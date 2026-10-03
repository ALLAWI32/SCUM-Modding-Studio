using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Formats.Packages;

/// <summary>
/// Same-size in-place edits of a <c>.uexp</c> (the "same-size patch" mode of the toolchain: floats, vectors,
/// int32 object indices, FName indices, bools, enum values). All offsets are <c>.uexp</c> file offsets.
/// </summary>
public static class PackagePatcher
{
    /// <summary>
    /// Byte-search for every tag <paramref name="name"/>:<paramref name="type"/> in one export and return
    /// (uexp offset of the value, value size) (port of <c>clone_vehicle.py tag_offsets</c>).
    /// </summary>
    /// <remarks>
    /// Faithful to the Python helper, including its pitfalls: only numberless FNames are matched; for
    /// ArrayProperty/SetProperty the returned offset points at the inner-type FName (the value starts 8 bytes later);
    /// for BoolProperty it points after the tag (the bool byte is 2 bytes earlier); tags with a property guid are
    /// misplaced. Prefer <see cref="PropertyBlock.FindAll"/> + <see cref="PropertyBlock.ToUExpOffset"/>.
    /// </remarks>
    public static IReadOnlyList<(int Offset, int Size)> FindTagValueOffsets(CookedPackage package, int exportIndex, string name, string type)
    {
        ArgumentNullException.ThrowIfNull(package);
        var index = package.NameIndex;
        if (!index.TryGetValue(name, out var ni) || !index.TryGetValue(type, out var ti))
        {
            return [];
        }

        var data = package.GetExportData(exportIndex).Span;
        var baseOffset = package.GetExportUExpOffset(exportIndex);
        Span<byte> head = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(head, ni);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], ti);
        BinaryPrimitives.WriteInt32LittleEndian(head[12..], 0);
        var extra = type == "StructProperty" ? 24 : type is "ByteProperty" or "EnumProperty" ? 8 : 0;
        var result = new List<(int, int)>();
        var from = 0;
        while (from < data.Length)
        {
            var k = data[from..].IndexOf(head);
            if (k < 0)
            {
                break;
            }

            k += from;
            var size = k + 20 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data[(k + 16)..]) : 0;
            result.Add((baseOffset + k + 24 + extra + 1, size));
            from = k + 1;
        }

        return result;
    }

    /// <summary>Writes a float32 at a .uexp offset.</summary>
    public static void WriteFloat(Span<byte> uexp, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(uexp.Slice(offset, 4), value);

    /// <summary>Writes an int32 at a .uexp offset.</summary>
    public static void WriteInt32(Span<byte> uexp, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(uexp.Slice(offset, 4), value);

    /// <summary>Writes three floats (Vector / Rotator pitch-yaw-roll) at a .uexp offset.</summary>
    public static void WriteVector(Span<byte> uexp, int offset, Vector3 value)
    {
        WriteFloat(uexp, offset, value.X);
        WriteFloat(uexp, offset + 4, value.Y);
        WriteFloat(uexp, offset + 8, value.Z);
    }

    /// <summary>Writes four floats (Quat / LinearColor / Vector4) at a .uexp offset.</summary>
    public static void WriteVector4(Span<byte> uexp, int offset, Vector4 value)
    {
        WriteVector(uexp, offset, new Vector3(value.X, value.Y, value.Z));
        WriteFloat(uexp, offset + 12, value.W);
    }

    /// <summary>Writes an FName reference (index, number) at a .uexp offset.</summary>
    public static void WriteFName(Span<byte> uexp, int offset, FNameRef name)
    {
        WriteInt32(uexp, offset, name.Index);
        WriteInt32(uexp, offset + 4, name.Number);
    }

    /// <summary>Sets a top-level or nested FloatProperty in place.</summary>
    /// <exception cref="InvalidOperationException">The tag is not a 4-byte FloatProperty.</exception>
    public static void SetFloat(Span<byte> uexp, PropertyBlock block, PropertyTag tag, float value)
    {
        Require(tag, "FloatProperty", 4);
        WriteFloat(uexp, block.ToUExpOffset(tag.ValueOffset), value);
    }

    /// <summary>Sets an IntProperty in place.</summary>
    public static void SetInt(Span<byte> uexp, PropertyBlock block, PropertyTag tag, int value)
    {
        Require(tag, "IntProperty", 4);
        WriteInt32(uexp, block.ToUExpOffset(tag.ValueOffset), value);
    }

    /// <summary>Sets an ObjectProperty / ClassProperty package index in place.</summary>
    public static void SetObjectIndex(Span<byte> uexp, PropertyBlock block, PropertyTag tag, int packageIndex)
    {
        if (tag.Type is not ("ObjectProperty" or "ClassProperty") || tag.Size != 4)
        {
            throw new InvalidOperationException($"'{tag.Name}' is {tag.Type} (size {tag.Size}), not an object reference.");
        }

        WriteInt32(uexp, block.ToUExpOffset(tag.ValueOffset), packageIndex);
    }

    /// <summary>Sets a BoolProperty in place (the value byte lives inside the tag).</summary>
    public static void SetBool(Span<byte> uexp, PropertyBlock block, PropertyTag tag, bool value)
    {
        if (tag.Type != "BoolProperty" || tag.BoolValueOffset < 0)
        {
            throw new InvalidOperationException($"'{tag.Name}' is {tag.Type}, not BoolProperty.");
        }

        uexp[block.ToUExpOffset(tag.BoolValueOffset)] = value ? (byte)1 : (byte)0;
    }

    /// <summary>Sets a NameProperty, EnumProperty or enum ByteProperty value (an FName) in place.</summary>
    public static void SetName(Span<byte> uexp, PropertyBlock block, PropertyTag tag, FNameRef name)
    {
        if (tag.Size != 8 || tag.Type is not ("NameProperty" or "EnumProperty" or "ByteProperty"))
        {
            throw new InvalidOperationException($"'{tag.Name}' is {tag.Type} (size {tag.Size}), not an FName value.");
        }

        WriteFName(uexp, block.ToUExpOffset(tag.ValueOffset), name);
    }

    /// <summary>Sets a native Vector (or Rotator: X=pitch, Y=yaw, Z=roll) struct in place.</summary>
    public static void SetVector(Span<byte> uexp, PropertyBlock block, PropertyTag tag, Vector3 value)
    {
        if (tag.Type != "StructProperty" || tag.Size != 12 || tag.Value is not (VectorValue or RotatorValue))
        {
            throw new InvalidOperationException($"'{tag.Name}' is {tag.TypeLabel} (size {tag.Size}), not a native Vector/Rotator.");
        }

        WriteVector(uexp, block.ToUExpOffset(tag.ValueOffset), value);
    }

    /// <summary>Overwrites any decoded 12-byte vector value (e.g. an array item) in place.</summary>
    public static void SetVector(Span<byte> uexp, PropertyBlock block, VectorValue target, Vector3 value)
    {
        ArgumentNullException.ThrowIfNull(target);
        WriteVector(uexp, block.ToUExpOffset(target.Offset), value);
    }

    private static void Require(PropertyTag tag, string type, int size)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Type != type || tag.Size != size)
        {
            throw new InvalidOperationException($"'{tag.Name}' is {tag.Type} (size {tag.Size}), expected {type} (size {size}).");
        }
    }
}
