using System.Buffers.Binary;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// What follows an ISM/HISM component's <c>PerInstanceSMData</c> in a cooked level (UE 4.27): the per-instance custom data,
/// the cooked render copy of the instances (<c>FStaticMeshInstanceData</c> in draw order, after its size as an int64) and,
/// for a HISM (foliage), its cluster tree. The game draws the render copy but builds collision from the array, so a tree
/// moved or deleted only in the array was still drawn where it was, with no collision there (owner: "I walk through the
/// trees"). <see cref="Refresh"/> drops the render copy (size 0: the engine builds it from the array when the level loads)
/// and grows the clusters holding a moved instance to where it went, so it is not culled when its old place is out of view.
/// </summary>
public static class InstanceRenderData
{
    /// <summary>FClusterNode: BoundMin, FirstChild, BoundMax, LastChild, FirstInstance, LastInstance, Min/MaxInstanceScale.</summary>
    private const int ClusterNodeSize = 64;

    /// <summary>
    /// The component payload with its render copy dropped and its clusters grown over <paramref name="moves"/> (instance
    /// index, pristine and new transform, component space); the payload unchanged when it has no render copy as expected.
    /// </summary>
    /// <param name="payload">Component export payload (the instance array already patched).</param>
    /// <param name="block">The component's tagged properties (offsets as in <paramref name="payload"/>).</param>
    /// <param name="arrayEnd">Offset just after the instance array's last element.</param>
    /// <param name="count">Instances in the array.</param>
    /// <param name="moves">Moved instances.</param>
    public static byte[] Refresh(byte[] payload, PropertyBlock block, int arrayEnd, int count, IReadOnlyList<(int Index, FTransform From, FTransform To)> moves)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(moves);
        if (Layout(payload, arrayEnd, count) is not { } layout)
        {
            return payload;
        }

        var (sizeAt, bytes) = layout;
        var result = new byte[payload.Length - bytes];
        payload.AsSpan(0, sizeAt).CopyTo(result);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(sizeAt), 0);
        payload.AsSpan(sizeAt + 8 + bytes).CopyTo(result.AsSpan(sizeAt + 8));
        GrowClusters(result, block, sizeAt + 8, moves);
        return result;
    }

    /// <summary>Where the render copy's size is and how many bytes the copy has, or null when there is none (or not as expected).</summary>
    internal static (int SizeAt, int Bytes)? Layout(byte[] payload, int arrayEnd, int count)
    {
        var span = payload.AsSpan();
        if (arrayEnd < 0 || arrayEnd + 8 > span.Length)
        {
            return null;
        }

        var customSize = BinaryPrimitives.ReadInt32LittleEndian(span[arrayEnd..]);
        var customCount = BinaryPrimitives.ReadInt32LittleEndian(span[(arrayEnd + 4)..]);
        var sizeAt = arrayEnd + 8 + ((long)customSize * customCount);
        if (customSize < 0 || customCount < 0 || sizeAt + 16 > span.Length)
        {
            return null;
        }

        var bytes = BinaryPrimitives.ReadInt64LittleEndian(span[(int)sizeAt..]);
        var at = (int)sizeAt + 8;
        // FStaticMeshInstanceData starts with bUseHalfFloat (0 or 1) and the instance count.
        if (bytes < 8 || at + bytes > span.Length
            || BinaryPrimitives.ReadInt32LittleEndian(span[at..]) is not (0 or 1) || BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]) != count)
        {
            return null;
        }

        return ((int)sizeAt, (int)bytes);
    }

    private static void GrowClusters(byte[] payload, PropertyBlock block, int at, IReadOnlyList<(int Index, FTransform From, FTransform To)> moves)
    {
        var span = payload.AsSpan();
        if (moves.Count == 0 || at + 8 > span.Length)
        {
            return;
        }

        var nodeSize = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
        var nodes = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]);
        if (nodeSize != ClusterNodeSize || nodes <= 0 || at + 8 + ((long)nodes * nodeSize) > span.Length)
        {
            return; // an ISM (no cluster tree)
        }

        // Clusters hold instances in draw order: InstanceReorderTable maps an array index to its draw index.
        var reorder = block.Find("InstanceReorderTable")?.Value is ArrayValue table ? table.Items.OfType<IntValue>().Select(v => v.Value).ToList() : null;
        foreach (var (index, from, to) in moves)
        {
            var drawn = reorder is not null && index < reorder.Count ? reorder[index] : index;
            // ponytail: the cluster's box moved (and grown) with the instance; a turned instance is not re-measured.
            var grow = MathF.Max(1f, MaxAbs(to.Scale3D) / MathF.Max(MaxAbs(from.Scale3D), 1e-4f));
            for (var n = 0; n < nodes; n++)
            {
                var node = span.Slice(at + 8 + (n * nodeSize), nodeSize);
                if (BinaryPrimitives.ReadInt32LittleEndian(node[32..]) > drawn || BinaryPrimitives.ReadInt32LittleEndian(node[36..]) < drawn)
                {
                    continue;
                }

                var min = ReadVector(node);
                var max = ReadVector(node[16..]);
                WriteVector(node, FVector.Min(min, to.Translation + ((min - from.Translation) * grow)));
                WriteVector(node[16..], FVector.Max(max, to.Translation + ((max - from.Translation) * grow)));
            }
        }

        // The component's bounds cover the root cluster.
        if (block.Find("BuiltInstanceBounds") is { Size: 25 } built)
        {
            var root = span.Slice(at + 8, nodeSize);
            var bounds = span[built.ValueOffset..];
            WriteVector(bounds, FVector.Min(ReadVector(bounds), ReadVector(root)));
            WriteVector(bounds[12..], FVector.Max(ReadVector(bounds[12..]), ReadVector(root[16..])));
        }
    }

    private static float MaxAbs(FVector v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    private static FVector ReadVector(ReadOnlySpan<byte> b) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]), BinaryPrimitives.ReadSingleLittleEndian(b[8..]));

    private static void WriteVector(Span<byte> b, FVector v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b[8..], v.Z);
    }
}
