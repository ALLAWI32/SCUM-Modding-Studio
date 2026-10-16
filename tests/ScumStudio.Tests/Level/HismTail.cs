using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Core.Mathematics;
using BoundingBox = ScumStudio.Core.Geometry.BoundingBox;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Reads and checks what follows a HISM's instance array the way UE 4.27 loads it (independent of the studio's writer):
/// <c>PerInstanceSMCustomData</c>, the cooked draw copy (<c>FStaticMeshInstanceData</c>: bUseHalfFloat, count, then origin,
/// light map, transform and custom-data arrays, each element size + count + bytes, in draw order) and the cluster tree.
/// </summary>
internal static class HismTail
{
    public sealed record Report(string Summary, List<string> Problems, long CopyBytes);

    /// <param name="payload">Component payload.</param>
    /// <param name="arrayEnd">Offset just after the instance array.</param>
    /// <param name="instances">The instances as stored in the array (component space).</param>
    /// <param name="reorder">InstanceReorderTable (array index to draw index), or null for identity.</param>
    /// <param name="mesh">The static mesh's local bounds, or null to skip the bound checks.</param>
    /// <param name="requireCopy">The draw copy must be there.</param>
    public static Report Check(byte[] payload, int arrayEnd, IReadOnlyList<FTransform> instances, IReadOnlyList<int>? reorder, BoundingBox? mesh, bool requireCopy)
    {
        var problems = new List<string>();
        var count = instances.Count;
        var customSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd));
        var customCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd + 4));
        var sizeAt = arrayEnd + 8 + (customSize * customCount);
        var copyBytes = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(sizeAt));
        var copyAt = sizeAt + 8;
        var at = copyAt + (int)copyBytes;
        var nodeSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));
        var nodes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at + 4));
        var summary = $"custom {customSize}x{customCount}, copy {copyBytes} B, tree {nodes} x {nodeSize}, trailing {payload.Length - (at + 8 + (nodes * nodeSize))} B";

        if (reorder is not null && (reorder.Count != count || reorder.Distinct().Count() != count || reorder.Any(r => r < 0 || r >= count)))
        {
            problems.Add($"reorder table ({reorder.Count}) is not a permutation of {count}");
            reorder = null;
        }

        int Draw(int i) => reorder is null ? i : reorder[i];
        var drawToArray = new int[count];
        for (var i = 0; i < count; i++)
        {
            drawToArray[Draw(i)] = i;
        }

        if (copyBytes == 0)
        {
            if (requireCopy && count > 0)
            {
                problems.Add("no cooked draw copy (size 0): the game builds it at load");
            }
        }
        else
        {
            var half = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(copyAt));
            var num = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(copyAt + 4));
            var floats = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(copyAt + 8)); // NumCustomDataFloats
            var o = copyAt + 12;
            var arrays = new List<(int Size, int Count, int At)>();
            for (var k = 0; k < 4 && o + 8 <= at; k++)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(o));
                var n = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(o + 4));
                arrays.Add((size, n, o + 8));
                o += 8 + (int)Math.Clamp((long)size * n, 0, at - o);
            }

            var head = string.Join(", ", Enumerable.Range(0, 12).Where(k => copyAt + (k * 4) + 4 <= at).Select(k => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(copyAt + (k * 4)))));
            summary += $"; copy head [{head}], half {half}, num {num}, custom floats {floats}, arrays [{string.Join(", ", arrays.Select(a => $"{a.Size}x{a.Count}"))}], copy end {(o == at ? "ok" : $"off by {at - o}")}";
            if (num != count || arrays.Count != 4 || arrays[0] != (16, count, arrays[0].At) || arrays[1].Size != 8 || arrays[1].Count != count || arrays[2].Count != count || arrays[2].Size != (half == 1 ? 24 : 48) || arrays[3].Count != count * floats || o != at)
            {
                problems.Add($"draw copy layout does not hold {count} instances");
            }
            else
            {
                var wrong = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    var d = Draw(i);
                    var origin = Read3(payload, arrays[0].At + (d * 16));
                    var m = instances[i].ToMatrixWithScale();
                    var expected = new[] { new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33) };
                    var ok = Vector3.Distance(origin, new Vector3(m.M41, m.M42, m.M43)) < 1f;
                    for (var r = 0; r < 3 && ok; r++)
                    {
                        var row = half == 1
                            ? new Vector3((float)ReadHalf(payload, arrays[2].At + (d * 24) + (r * 8)), (float)ReadHalf(payload, arrays[2].At + (d * 24) + (r * 8) + 2), (float)ReadHalf(payload, arrays[2].At + (d * 24) + (r * 8) + 4))
                            : Read3(payload, arrays[2].At + (d * 48) + (r * 16));
                        ok = Vector3.Distance(row, expected[r]) <= 0.01f * MathF.Max(1f, expected[r].Length());
                    }

                    if (!ok)
                    {
                        wrong.Add(i);
                    }
                }

                summary += $", {count - wrong.Count}/{count} drawn where the array has them";
                if (wrong.Count > 0)
                {
                    problems.Add($"draw copy differs from the array for instance(s) {string.Join(", ", wrong.Take(10))} (draw {string.Join(", ", wrong.Take(10).Select(Draw))})");
                }
            }
        }

        if (nodeSize != 64 || nodes <= 0)
        {
            return new Report(summary + " (no tree)", problems, copyBytes);
        }

        (Vector3 Min, Vector3 Max, int FirstChild, int LastChild, int First, int Last) Node(int n)
        {
            var b = at + 8 + (n * 64);
            return (Read3(payload, b), Read3(payload, b + 16), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(b + 12)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(b + 28)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(b + 32)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(b + 36)));
        }

        var root = Node(0);
        summary += $"; root ({root.Min.X:0.#},{root.Min.Y:0.#},{root.Min.Z:0.#})..({root.Max.X:0.#},{root.Max.Y:0.#},{root.Max.Z:0.#}) scale {Read3(payload, at + 8 + 40)}..{Read3(payload, at + 8 + 52)}";
        if (root.First != 0 || root.Last != count - 1)
        {
            problems.Add($"root covers {root.First}..{root.Last}, not 0..{count - 1}");
        }

        var slack = 0f;
        for (var n = 0; n < nodes; n++)
        {
            var node = Node(n);
            if (node.FirstChild >= 0)
            {
                var next = node.First;
                for (var c = node.FirstChild; c <= node.LastChild; c++)
                {
                    var child = c < nodes ? Node(c) : default;
                    if (c >= nodes || !Inside(child.Min, child.Max, node.Min, node.Max, 0.5f) || child.First != next)
                    {
                        problems.Add($"node {n} does not hold child {c}");
                    }

                    next = child.Last + 1;
                }

                if (next != node.Last + 1)
                {
                    problems.Add($"node {n}'s children do not cover {node.First}..{node.Last}");
                }

                continue;
            }

            if (mesh is not { } box)
            {
                continue;
            }

            var tight = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
            for (var d = node.First; d <= node.Last && d >= 0 && d < count; d++)
            {
                var (lo, hi) = InstanceBox(instances[drawToArray[d]], box);
                tight = new BoundingBox(Vector3.Min(tight.Min, lo), Vector3.Max(tight.Max, hi));
                if (!Inside(lo, hi, node.Min, node.Max, 2f + (0.01f * (hi - lo).Length())))
                {
                    problems.Add($"leaf {n} misses instance {drawToArray[d]} (draw {d})");
                }
            }

            slack = MathF.Max(slack, MathF.Max(Vector3.Distance(tight.Min, node.Min), Vector3.Distance(tight.Max, node.Max)));
        }

        summary += $"; leaves at most {slack:0.#} cm from their instances";
        return new Report(summary, problems, copyBytes);
    }

    /// <summary>The axis-aligned box of <paramref name="mesh"/> placed by <paramref name="t"/> (UE <c>FBox::TransformBy</c>).</summary>
    public static (Vector3 Min, Vector3 Max) InstanceBox(FTransform t, BoundingBox mesh)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var p = t.TransformPosition(new FVector((k & 1) == 0 ? mesh.Min.X : mesh.Max.X, (k & 2) == 0 ? mesh.Min.Y : mesh.Max.Y, (k & 4) == 0 ? mesh.Min.Z : mesh.Max.Z));
            lo = Vector3.Min(lo, new Vector3(p.X, p.Y, p.Z));
            hi = Vector3.Max(hi, new Vector3(p.X, p.Y, p.Z));
        }

        return (lo, hi);
    }

    private static bool Inside(Vector3 min, Vector3 max, Vector3 boxMin, Vector3 boxMax, float slack) =>
        min.X >= boxMin.X - slack && min.Y >= boxMin.Y - slack && min.Z >= boxMin.Z - slack && max.X <= boxMax.X + slack && max.Y <= boxMax.Y + slack && max.Z <= boxMax.Z + slack;

    private static Vector3 Read3(byte[] b, int at) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)));

    private static Half ReadHalf(byte[] b, int at) => BinaryPrimitives.ReadHalfLittleEndian(b.AsSpan(at));
}
