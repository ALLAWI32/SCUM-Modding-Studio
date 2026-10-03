using System.Numerics;
using System.Runtime.InteropServices;

namespace ScumStudio.Rendering.Resources;

/// <summary>
/// Per-instance vertex attributes of an instanced draw (96 bytes, attribute locations 3..9).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public readonly struct InstanceData : IEquatable<InstanceData>
{
    /// <summary>Size of one instance record in bytes.</summary>
    public const int SizeInBytes = 96;

    /// <summary>Byte offset of <see cref="Model"/>.</summary>
    public const int ModelOffset = 0;

    /// <summary>Byte offset of <see cref="Tint"/>.</summary>
    public const int TintOffset = 64;

    /// <summary>Byte offset of <see cref="PickCode"/> (followed by <see cref="Flags"/>).</summary>
    public const int PickCodeOffset = 80;

    /// <summary>Byte offset of <see cref="Surface"/>.</summary>
    public const int SurfaceOffset = 88;

    /// <summary>Model-to-world matrix, row-vector convention (<c>world = local * Model</c>).</summary>
    [FieldOffset(ModelOffset)]
    public readonly Matrix4x4 Model;

    /// <summary>Linear RGBA multiplier of the albedo.</summary>
    [FieldOffset(TintOffset)]
    public readonly Vector4 Tint;

    /// <summary>Value written to the ID buffer (0 = not pickable).</summary>
    [FieldOffset(PickCodeOffset)]
    public readonly uint PickCode;

    /// <summary>Shading flags.</summary>
    [FieldOffset(PickCodeOffset + 4)]
    public readonly InstanceFlags Flags;

    /// <summary>Metal (x) and gloss (y), 0..1: shiny paint (0 = plain shading).</summary>
    [FieldOffset(SurfaceOffset)]
    public readonly Vector2 Surface;

    /// <summary>Creates an instance record.</summary>
    public InstanceData(Matrix4x4 model, Vector4 tint, uint pickCode, InstanceFlags flags, Vector2 surface = default)
    {
        Model = model;
        Tint = tint;
        PickCode = pickCode;
        Flags = flags;
        Surface = surface;
    }

    /// <inheritdoc />
    public bool Equals(InstanceData other) =>
        Model.Equals(other.Model) && Tint.Equals(other.Tint) && PickCode == other.PickCode && Flags == other.Flags && Surface == other.Surface;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InstanceData other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Model, Tint, PickCode, Flags, Surface);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InstanceData left, InstanceData right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InstanceData left, InstanceData right) => !left.Equals(right);
}

/// <summary>Per-instance shading flags.</summary>
[Flags]
public enum InstanceFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Draw with the selection highlight (tint + rim).</summary>
    Selected = 1,
}
