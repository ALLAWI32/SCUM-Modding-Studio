using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// Minimal glTF 2.0 reader for tests (System.Text.Json only): enough to read positions, normals and indices of every primitive
/// from a <c>.gltf</c> + single <c>.bin</c> (our exporter's output and UE Viewer/umodel exports).
/// </summary>
internal sealed class GltfReader
{
    private readonly JsonElement _root;
    private readonly byte[] _bin;

    private GltfReader(JsonElement root, byte[] bin)
    {
        _root = root;
        _bin = bin;
        Version = root.GetProperty("asset").GetProperty("version").GetString() ?? string.Empty;
        BufferLength = root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32();
        var prims = new List<Primitive>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var p in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attrs = p.GetProperty("attributes");
                prims.Add(new Primitive(
                    attrs.GetProperty("POSITION").GetInt32(),
                    attrs.TryGetProperty("NORMAL", out var n) ? n.GetInt32() : null,
                    p.GetProperty("indices").GetInt32(),
                    p.TryGetProperty("material", out var m) ? m.GetInt32() : null));
            }
        }

        Primitives = prims;
        MaterialNames = root.TryGetProperty("materials", out var mats)
            ? mats.EnumerateArray().Select(x => x.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty).ToList()
            : [];
    }

    public string Version { get; }

    public int BufferLength { get; }

    public IReadOnlyList<Primitive> Primitives { get; }

    public IReadOnlyList<string> MaterialNames { get; }

    public static GltfReader Parse(byte[] json, byte[] bin)
    {
        using var doc = JsonDocument.Parse(json);
        return new GltfReader(doc.RootElement.Clone(), bin);
    }

    public static GltfReader Load(string gltfPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(gltfPath));
        var uri = doc.RootElement.GetProperty("buffers")[0].GetProperty("uri").GetString()!;
        var bin = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(gltfPath)!, Uri.UnescapeDataString(uri)));
        return new GltfReader(doc.RootElement.Clone(), bin);
    }

    public int Count(int accessor) => Accessor(accessor).GetProperty("count").GetInt32();

    public int IndexComponentType(int accessor) => Accessor(accessor).GetProperty("componentType").GetInt32();

    public (Vector3 Min, Vector3 Max) AccessorBounds(int accessor)
    {
        var a = Accessor(accessor);
        return (Vec(a.GetProperty("min")), Vec(a.GetProperty("max")));

        static Vector3 Vec(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    }

    public Vector3[] ReadVec3(int accessor)
    {
        var (start, stride, count) = View(accessor, 12);
        var data = _bin.AsSpan(start);
        var result = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var s = data.Slice(i * stride, 12);
            result[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(s),
                BinaryPrimitives.ReadSingleLittleEndian(s[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[8..]));
        }

        return result;
    }

    public uint[] ReadIndices(int accessor)
    {
        var type = IndexComponentType(accessor);
        var size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException($"Index type {type}.") };
        var (start, stride, count) = View(accessor, size);
        var data = _bin.AsSpan(start);
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var s = data[(i * stride)..];
            result[i] = size switch
            {
                1 => s[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(s),
            };
        }

        return result;
    }

    private JsonElement Accessor(int index) => _root.GetProperty("accessors")[index];

    private (int Start, int Stride, int Count) View(int accessor, int elementSize)
    {
        var a = Accessor(accessor);
        var view = _root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        var viewLength = view.GetProperty("byteLength").GetInt32();
        var accessorOffset = a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : elementSize;
        var count = a.GetProperty("count").GetInt32();
        if (viewOffset + viewLength > _bin.Length || accessorOffset + (count - 1L) * stride + elementSize > viewLength)
        {
            throw new InvalidDataException($"Accessor {accessor} exceeds its buffer view.");
        }

        return (viewOffset + accessorOffset, stride, count);
    }

    internal readonly record struct Primitive(int Position, int? Normal, int Indices, int? Material);
}
