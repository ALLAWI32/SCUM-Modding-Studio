using System.Buffers.Binary;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// What follows an ISM/HISM component's <c>PerInstanceSMData</c> in a cooked level (UE 4.27): the per-instance custom data,
/// the cooked render copy of the instances (<c>FStaticMeshInstanceData</c> in draw order, after its size as an int64) and,
/// for a HISM (foliage), its cluster tree. The game draws the render copy but builds collision from the array, so a tree
/// moved or deleted only in the array was still drawn where it was, with no collision there (owner: "I walk through the
/// trees"). <see cref="Refresh"/> writes each edited instance into its draw slot of the copy (found through
/// <c>InstanceReorderTable</c>; its random, light map and custom data stay as cooked) and refits the clusters holding it the
/// way UE's BuildTree measures them, so the component loads exactly like a stock one. Dropping the copy instead (3.2.0 and
/// before) sent the component down the engine's rebuild path and made the foliage around it blink (defolt_0999). Only a
/// component that also got instances appended, or whose data is not laid out as expected, still has its copy dropped.
/// </summary>
public static class InstanceRenderData
{
    /// <summary>FClusterNode: BoundMin, FirstChild, BoundMax, LastChild, FirstInstance, LastInstance, Min/MaxInstanceScale.</summary>
    private const int ClusterNodeSize = 64;

    /// <summary>An instance smaller than this on every axis is collapsed ("deleted"): it does not count in a cluster's scale range.</summary>
    private const float CollapsedScale = 0.01f;

    /// <summary>
    /// The component payload with its edited instances written into the render copy and the clusters holding them refitted;
    /// when instances were appended (or the copy or tree is not as expected) the render copy dropped and the clusters grown
    /// over the moves instead; the payload unchanged when nothing applies.
    /// </summary>
    /// <param name="payload">Component export payload (the instance array already patched).</param>
    /// <param name="block">The component's tagged properties (offsets as in <paramref name="payload"/>).</param>
    /// <param name="arrayEnd">Offset just after the instance array's last element.</param>
    /// <param name="count">Instances stored before the edit (the render copy's count).</param>
    /// <param name="elementSize">Bytes per instance in the array (64, or 80 with the old light map biases).</param>
    /// <param name="appended">Instances appended behind the stored ones.</param>
    /// <param name="edits">Moved and collapsed instances: index, pristine and new transform (component space).</param>
    public static byte[] Refresh(byte[] payload, PropertyBlock block, int arrayEnd, int count, int elementSize, int appended, IReadOnlyList<(int Index, FTransform From, FTransform To)> edits)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(edits);
        if (appended == 0 && Fit(payload, block, arrayEnd, count, elementSize, edits) is { } fitted)
        {
            return fitted;
        }

        var moves = edits.Where(e => !IsCollapsed(e.To)).ToList();
        if (Layout(payload, arrayEnd, count) is not { } layout)
        {
            // Already without a render copy (a level this studio wrote, e.g. the project's own pak imported back into it):
            // the cluster tree after the empty copy still has to reach where instances went, or the game culls them by
            // their old place (only a grown instance array makes it rebuild the tree).
            if (CopySizeAt(payload, arrayEnd) is { } emptyAt && BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(emptyAt)) == 0)
            {
                var grown = payload.ToArray();
                GrowClusters(grown, block, emptyAt + 8, moves);
                return grown;
            }

            return payload;
        }

        // Appended instances: the stored tree and copy no longer cover the array, so the game rebuilds both at load
        // (NumBuiltInstances and the reorder table differ from the array); the copy goes, the clusters only grow.
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
        if (CopySizeAt(payload, arrayEnd) is not { } sizeAt)
        {
            return null;
        }

        var bytes = BinaryPrimitives.ReadInt64LittleEndian(span[sizeAt..]);
        var at = sizeAt + 8;
        // FStaticMeshInstanceData starts with bUseHalfFloat (0 or 1) and the instance count.
        if (bytes < 8 || at + bytes > span.Length
            || BinaryPrimitives.ReadInt32LittleEndian(span[at..]) is not (0 or 1) || BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]) != count)
        {
            return null;
        }

        return (sizeAt, (int)bytes);
    }

    /// <summary>
    /// The arrays of a render copy at <paramref name="copyAt"/> (<c>FStaticMeshInstanceData</c>: bUseHalfFloat, NumInstances,
    /// NumCustomDataFloats, then origins (float4, w = per-instance random), light map biases (4 x int16), transform rows
    /// (3 x 4 halves or floats) and custom floats, each as element size + count + elements), or null when not exactly so.
    /// </summary>
    internal static (int Origins, int Transforms, bool Half)? CopyArrays(byte[] payload, int copyAt, int bytes, int count)
    {
        var span = payload.AsSpan();
        if (bytes < 12 || copyAt + bytes > span.Length)
        {
            return null;
        }

        var half = BinaryPrimitives.ReadInt32LittleEndian(span[copyAt..]);
        var floats = BinaryPrimitives.ReadInt32LittleEndian(span[(copyAt + 8)..]);
        if (half is not (0 or 1) || BinaryPrimitives.ReadInt32LittleEndian(span[(copyAt + 4)..]) != count || floats < 0)
        {
            return null;
        }

        var at = copyAt + 12;
        var starts = new int[4];
        ReadOnlySpan<(int Size, long Count)> expected = [(16, count), (8, count), (half == 1 ? 24 : 48, count), (4, (long)count * floats)];
        for (var k = 0; k < 4; k++)
        {
            if (at + 8 > copyAt + bytes
                || BinaryPrimitives.ReadInt32LittleEndian(span[at..]) != expected[k].Size || BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]) != expected[k].Count)
            {
                return null;
            }

            starts[k] = at + 8;
            at += 8 + (int)(expected[k].Size * expected[k].Count);
        }

        return at == copyAt + bytes ? (starts[0], starts[2], half == 1) : null;
    }

    /// <summary>Where the render copy's int64 size is (after the per-instance custom data), or null when the payload is too short.</summary>
    private static int? CopySizeAt(byte[] payload, int arrayEnd)
    {
        var span = payload.AsSpan();
        if (arrayEnd < 0 || arrayEnd + 8 > span.Length)
        {
            return null;
        }

        var customSize = BinaryPrimitives.ReadInt32LittleEndian(span[arrayEnd..]);
        var customCount = BinaryPrimitives.ReadInt32LittleEndian(span[(arrayEnd + 4)..]);
        var sizeAt = arrayEnd + 8 + ((long)customSize * customCount);
        return customSize < 0 || customCount < 0 || sizeAt + 16 > span.Length ? null : (int)sizeAt;
    }

    /// <summary>
    /// The payload with the edits written into the render copy (when there is one) and the cluster tree refitted, or null
    /// when the component is not a HISM laid out as expected (no tree, no reorder table, no cached mesh bounds).
    /// </summary>
    private static byte[]? Fit(byte[] payload, PropertyBlock block, int arrayEnd, int count, int elementSize, IReadOnlyList<(int Index, FTransform From, FTransform To)> edits)
    {
        if (edits.Count == 0)
        {
            return payload;
        }

        int copyAt, copyBytes;
        if (Layout(payload, arrayEnd, count) is { } layout)
        {
            (copyAt, copyBytes) = (layout.SizeAt + 8, layout.Bytes);
        }
        else if (CopySizeAt(payload, arrayEnd) is { } emptyAt && BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(emptyAt)) == 0)
        {
            (copyAt, copyBytes) = (emptyAt + 8, 0);
        }
        else
        {
            return null;
        }

        if (Reorder(block, count) is not { } reorder || MeshBox(block) is not { } mesh || edits.Any(e => e.Index < 0 || e.Index >= count))
        {
            return null;
        }

        var result = payload.ToArray();
        if (copyBytes > 0)
        {
            if (CopyArrays(result, copyAt, copyBytes, count) is not { } arrays)
            {
                return null;
            }

            foreach (var (index, _, to) in edits)
            {
                WriteCopy(result, arrays, reorder[index], to);
            }
        }

        return Refit(result, block, copyAt + copyBytes, arrayEnd - (elementSize * count), elementSize, count, reorder, mesh, edits) ? result : null;
    }

    /// <summary>InstanceReorderTable (array index to draw index) when it is a permutation of the <paramref name="count"/> instances.</summary>
    private static int[]? Reorder(PropertyBlock block, int count)
    {
        if (block.Find("InstanceReorderTable")?.Value is not ArrayValue table || table.Items.Count != count)
        {
            return null;
        }

        var reorder = table.Items.Select(v => v is IntValue i ? i.Value : -1).ToArray();
        var seen = new bool[count];
        foreach (var d in reorder)
        {
            if (d < 0 || d >= count || seen[d])
            {
                return null;
            }

            seen[d] = true;
        }

        return reorder;
    }

    /// <summary>The box BuildTree measures every instance with: the static mesh's bounds the tree was built for (<c>CacheMeshExtendedBounds</c>).</summary>
    private static (FVector Min, FVector Max)? MeshBox(PropertyBlock block)
    {
        var (origin, extent) = block.Find("CacheMeshExtendedBounds")?.Value switch
        {
            BoxSphereBoundsValue b => (b.Origin, b.BoxExtent),
            StructValue s => (s.Properties.FirstOrDefault(p => p.Name == "Origin")?.Value as VectorValue, s.Properties.FirstOrDefault(p => p.Name == "BoxExtent")?.Value as VectorValue),
            _ => (null, null),
        };
        if (origin is null || extent is null || extent.X < 0 || extent.Y < 0 || extent.Z < 0)
        {
            return null;
        }

        var o = new FVector(origin.X, origin.Y, origin.Z);
        var e = new FVector(extent.X, extent.Y, extent.Z);
        return (o - e, o + e);
    }

    /// <summary>Writes <paramref name="to"/> into draw slot <paramref name="slot"/> of the copy: origin and the three transform rows (FStaticMeshInstanceData::SetInstance), keeping the random in origin.w and each row's w.</summary>
    private static void WriteCopy(byte[] payload, (int Origins, int Transforms, bool Half) arrays, int slot, FTransform to)
    {
        var m = to.ToMatrixWithScale();
        var span = payload.AsSpan();
        WriteVector(span[(arrays.Origins + (slot * 16))..], new FVector(m.M41, m.M42, m.M43));
        ReadOnlySpan<float> rows = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                if (arrays.Half)
                {
                    BinaryPrimitives.WriteHalfLittleEndian(span[(arrays.Transforms + (slot * 24) + (r * 8) + (c * 2))..], (Half)rows[(r * 3) + c]);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(span[(arrays.Transforms + (slot * 48) + (r * 16) + (c * 4))..], rows[(r * 3) + c]);
                }
            }
        }
    }

    /// <summary>
    /// Refits the leaves holding an edited instance as UE's BuildTree does (the union of the mesh box placed by each of the
    /// leaf's instances; the scale range of the ones not collapsed), then their ancestors (union of the children) and the
    /// component's BuiltInstanceBounds (the root). False when the tree is not as BuildTree lays it out.
    /// </summary>
    private static bool Refit(byte[] payload, PropertyBlock block, int at, int dataOffset, int elementSize, int count, int[] reorder,
        (FVector Min, FVector Max) mesh, IReadOnlyList<(int Index, FTransform From, FTransform To)> edits)
    {
        var span = payload.AsSpan();
        if (at + 8 > span.Length || dataOffset < 0)
        {
            return false;
        }

        var nodeSize = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
        var nodes = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]);
        if (nodeSize != ClusterNodeSize || nodes <= 0 || at + 8 + ((long)nodes * nodeSize) > span.Length)
        {
            return false; // an ISM (no cluster tree)
        }

        Span<byte> Node(int n) => payload.AsSpan(at + 8 + (n * nodeSize), nodeSize);
        int Int(int n, int offset) => BinaryPrimitives.ReadInt32LittleEndian(Node(n)[offset..]);
        if (Int(0, 32) != 0 || Int(0, 36) != count - 1)
        {
            return false;
        }

        // Every edited instance's leaf, and the path down to it (children come after their parent, ranges nest).
        var dirty = new bool[nodes];
        foreach (var (index, _, _) in edits)
        {
            var drawn = reorder[index];
            var n = 0;
            dirty[0] = true;
            while (Int(n, 12) >= 0)
            {
                var (first, last) = (Int(n, 12), Int(n, 28));
                var child = -1;
                for (var c = first; c <= last && c > n && c < nodes; c++)
                {
                    if (Int(c, 32) <= drawn && Int(c, 36) >= drawn)
                    {
                        child = c;
                        break;
                    }
                }

                if (child < 0)
                {
                    return false;
                }

                n = child;
                dirty[n] = true;
            }
        }

        var drawToArray = new int[count];
        for (var i = 0; i < count; i++)
        {
            drawToArray[reorder[i]] = i;
        }

        for (var n = nodes - 1; n >= 0; n--)
        {
            if (!dirty[n])
            {
                continue;
            }

            var node = Node(n);
            var min = new FVector(float.MaxValue);
            var max = new FVector(float.MinValue);
            var minScale = new FVector(float.MaxValue);
            var maxScale = new FVector(float.MinValue);
            if (Int(n, 12) < 0)
            {
                if (Int(n, 32) < 0 || Int(n, 36) >= count || Int(n, 32) > Int(n, 36))
                {
                    return false;
                }

                for (var d = Int(n, 32); d <= Int(n, 36); d++)
                {
                    var matrix = span.Slice(dataOffset + (drawToArray[d] * elementSize), 64);
                    var (lo, hi) = TransformBox(mesh, matrix);
                    (min, max) = (FVector.Min(min, lo), FVector.Max(max, hi));
                    var scale = new FVector(Row(matrix, 0).Size(), Row(matrix, 1).Size(), Row(matrix, 2).Size());
                    if (MaxAbs(scale) >= CollapsedScale)
                    {
                        (minScale, maxScale) = (FVector.Min(minScale, scale), FVector.Max(maxScale, scale));
                    }
                }
            }
            else
            {
                for (var c = Int(n, 12); c <= Int(n, 28); c++)
                {
                    var child = Node(c);
                    (min, max) = (FVector.Min(min, ReadVector(child)), FVector.Max(max, ReadVector(child[16..])));
                    (minScale, maxScale) = (FVector.Min(minScale, ReadVector(child[40..])), FVector.Max(maxScale, ReadVector(child[52..])));
                }
            }

            WriteVector(node, min);
            WriteVector(node[16..], max);
            if (minScale.X <= maxScale.X)
            {
                WriteVector(node[40..], minScale); // a leaf of collapsed instances keeps its cooked range
                WriteVector(node[52..], maxScale);
            }
        }

        // The component's bounds are the root cluster's (ApplyBuildTree).
        if (block.Find("BuiltInstanceBounds") is { Size: 25 } built)
        {
            var root = Node(0);
            root[..12].CopyTo(span[built.ValueOffset..]);
            root[16..28].CopyTo(span[(built.ValueOffset + 12)..]);
        }

        return true;
    }

    /// <summary>UE <c>FBox::TransformBy(FMatrix)</c>: the axis-aligned box around <paramref name="box"/> placed by the row-major instance matrix.</summary>
    private static (FVector Min, FVector Max) TransformBox((FVector Min, FVector Max) box, ReadOnlySpan<byte> matrix)
    {
        var center = (box.Min + box.Max) * 0.5f;
        var extent = (box.Max - box.Min) * 0.5f;
        var (x, y, z) = (Row(matrix, 0), Row(matrix, 1), Row(matrix, 2));
        var origin = (x * center.X) + (y * center.Y) + (z * center.Z) + Row(matrix, 3);
        var reach = Abs(x * extent.X) + Abs(y * extent.Y) + Abs(z * extent.Z);
        return (origin - reach, origin + reach);
    }

    private static FVector Row(ReadOnlySpan<byte> matrix, int row) => ReadVector(matrix[(row * 16)..]);

    private static FVector Abs(FVector v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    private static bool IsCollapsed(FTransform t) => MaxAbs(t.Scale3D) < CollapsedScale;

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
