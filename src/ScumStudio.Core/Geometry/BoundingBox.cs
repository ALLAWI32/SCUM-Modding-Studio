using System.Numerics;

namespace ScumStudio.Core.Geometry;

/// <summary>
/// Axis-aligned bounding box in Unreal Engine space (centimetres; X forward, Y right, Z up).
/// </summary>
/// <param name="Min">Minimum corner.</param>
/// <param name="Max">Maximum corner.</param>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    /// <summary>
    /// The empty box (Min = +inf, Max = -inf). Including any point in it yields a degenerate box at that point.
    /// </summary>
    public static BoundingBox Empty { get; } =
        new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>True when no point has been included (any Min component greater than Max).</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>Centre of the box (undefined for an empty box).</summary>
    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Full size along each axis (zero for an empty box).</summary>
    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    /// <summary>Half size along each axis (zero for an empty box).</summary>
    public Vector3 Extent => Size * 0.5f;

    /// <summary>Returns the smallest box containing this box and <paramref name="point"/>.</summary>
    public BoundingBox Include(Vector3 point) =>
        new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>Returns the smallest box containing this box and <paramref name="other"/>.</summary>
    public BoundingBox Union(BoundingBox other) =>
        other.IsEmpty ? this : IsEmpty ? other : new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>True when <paramref name="point"/> lies inside or on the box.</summary>
    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.Y >= Min.Y && point.Z >= Min.Z &&
        point.X <= Max.X && point.Y <= Max.Y && point.Z <= Max.Z;

    /// <summary>
    /// Computes the bounds of a flat xyz position array (length must be a multiple of 3).
    /// Returns <see cref="Empty"/> for an empty array.
    /// </summary>
    public static BoundingBox FromPositions(ReadOnlySpan<float> positionsXyz)
    {
        if (positionsXyz.Length % 3 != 0)
        {
            throw new ArgumentException("Position array length must be a multiple of 3.", nameof(positionsXyz));
        }

        var box = Empty;
        for (var i = 0; i < positionsXyz.Length; i += 3)
        {
            box = box.Include(new Vector3(positionsXyz[i], positionsXyz[i + 1], positionsXyz[i + 2]));
        }

        return box;
    }
}
