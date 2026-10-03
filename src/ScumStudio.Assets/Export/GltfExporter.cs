using System.Buffers.Binary;
using System.Text.Json;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Assets.Export;

/// <summary>
/// Writes a <see cref="MeshData"/> as a minimal glTF 2.0 asset: a <c>.gltf</c> JSON file plus a <c>.bin</c> buffer.
/// </summary>
/// <remarks>
/// <para>One mesh with one primitive per section (triangles, shared POSITION/NORMAL/TEXCOORD_0 accessors, per-section index
/// accessors), one material per distinct section material (named after it, neutral grey PBR factors), one node, one scene.</para>
/// <para>With the default <see cref="MeshExportOptions"/> positions are written in metres and in glTF's right-handed Y-up frame as
/// <c>(x, z, y) * 0.01</c> of the UE values (the same convention as UE Viewer/umodel exports), and the triangle order is kept
/// (the axis swap already turns UE's clockwise front faces counter-clockwise). UVs need no flip (both use a top-left origin).</para>
/// <para>Indices are written as UNSIGNED_SHORT when the mesh has fewer than 65,536 vertices, otherwise UNSIGNED_INT.</para>
/// </remarks>
public static class GltfExporter
{
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;
    private const int Float = 5126;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    /// <summary>Writes <paramref name="gltfPath"/> and a <c>.bin</c> with the same base name. Returns both paths.</summary>
    public static async Task<IReadOnlyList<string>> SaveAsync(MeshData mesh, string gltfPath, MeshExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(gltfPath);
        var full = Path.GetFullPath(gltfPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var binPath = Path.ChangeExtension(full, ".bin");
        var (json, bin) = Build(mesh, Path.GetFileName(binPath), options);
        await File.WriteAllBytesAsync(full, json, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(binPath, bin, cancellationToken).ConfigureAwait(false);
        return [full, binPath];
    }

    /// <summary>Builds the glTF JSON (UTF-8) and the binary buffer; the JSON references the buffer as <paramref name="binUri"/>.</summary>
    public static (byte[] Json, byte[] Bin) Build(MeshData mesh, string binUri, MeshExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new MeshExportOptions();
        var (positions, normals, flip) = MeshTransform.Apply(mesh, options);
        var n = mesh.VertexCount;
        var hasUv = options.IncludeUvs && mesh.Uv0.Length == n * 2;
        var use16 = n < 65536;

        using var bin = new MemoryStream();
        var views = new List<(int Offset, int Length, int? Target, int? Stride)>();
        var accessors = new List<Action<Utf8JsonWriter>>();

        int AddView(ReadOnlySpan<byte> data, int? target)
        {
            Pad(bin, 4);
            var offset = (int)bin.Position;
            bin.Write(data);
            views.Add((offset, data.Length, target, null));
            return views.Count - 1;
        }

        // Positions (with min/max, required by the spec).
        var posView = AddView(FloatsToBytes(positions), ArrayBuffer);
        var box = BoundingBox.FromPositions(positions);
        var posAccessor = accessors.Count;
        accessors.Add(w =>
        {
            w.WriteNumber("bufferView", posView);
            w.WriteNumber("componentType", Float);
            w.WriteNumber("count", n);
            w.WriteString("type", "VEC3");
            WriteVec(w, "min", box.IsEmpty ? [0, 0, 0] : [box.Min.X, box.Min.Y, box.Min.Z]);
            WriteVec(w, "max", box.IsEmpty ? [0, 0, 0] : [box.Max.X, box.Max.Y, box.Max.Z]);
        });

        int? normalAccessor = null;
        if (normals.Length > 0)
        {
            var view = AddView(FloatsToBytes(normals), ArrayBuffer);
            normalAccessor = accessors.Count;
            accessors.Add(w =>
            {
                w.WriteNumber("bufferView", view);
                w.WriteNumber("componentType", Float);
                w.WriteNumber("count", n);
                w.WriteString("type", "VEC3");
            });
        }

        int? uvAccessor = null;
        if (hasUv)
        {
            var view = AddView(FloatsToBytes(mesh.Uv0), ArrayBuffer);
            uvAccessor = accessors.Count;
            accessors.Add(w =>
            {
                w.WriteNumber("bufferView", view);
                w.WriteNumber("componentType", Float);
                w.WriteNumber("count", n);
                w.WriteString("type", "VEC2");
            });
        }

        // Materials: one per distinct label, in first-use order.
        var materialLabels = new List<string>();
        var sectionMaterial = new int[mesh.Sections.Length];
        for (var s = 0; s < mesh.Sections.Length; s++)
        {
            var label = MeshTransform.MaterialLabel(mesh.Sections[s].MaterialName, s);
            var idx = materialLabels.IndexOf(label);
            if (idx < 0)
            {
                materialLabels.Add(label);
                idx = materialLabels.Count - 1;
            }

            sectionMaterial[s] = idx;
        }

        var primitives = new List<(int Indices, int Material)>();
        for (var s = 0; s < mesh.Sections.Length; s++)
        {
            var section = mesh.Sections[s];
            var count = section.IndexCount - section.IndexCount % 3;
            if (count <= 0)
            {
                continue;
            }

            var bytes = new byte[count * (use16 ? 2 : 4)];
            for (var i = 0; i < count; i += 3)
            {
                for (var c = 0; c < 3; c++)
                {
                    var v = MeshTransform.Corner(mesh.Indices, section.FirstIndex + i, c, flip);
                    if (use16)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((i + c) * 2), (ushort)v);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i + c) * 4), v);
                    }
                }
            }

            var view = AddView(bytes, ElementArrayBuffer);
            var accessor = accessors.Count;
            accessors.Add(w =>
            {
                w.WriteNumber("bufferView", view);
                w.WriteNumber("componentType", use16 ? UnsignedShort : UnsignedInt);
                w.WriteNumber("count", count);
                w.WriteString("type", "SCALAR");
            });
            primitives.Add((accessor, sectionMaterial[s]));
        }

        Pad(bin, 4);
        var binBytes = bin.ToArray();

        using var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", "ScumStudio");
            w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteStartArray("nodes");
            w.WriteNumberValue(0);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            w.WriteStartObject();
            w.WriteString("name", mesh.Name);
            w.WriteNumber("mesh", 0);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("meshes");
            w.WriteStartObject();
            w.WriteString("name", mesh.Name);
            w.WriteStartArray("primitives");
            foreach (var (indices, material) in primitives)
            {
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", posAccessor);
                if (normalAccessor is { } na)
                {
                    w.WriteNumber("NORMAL", na);
                }

                if (uvAccessor is { } ua)
                {
                    w.WriteNumber("TEXCOORD_0", ua);
                }

                w.WriteEndObject();
                w.WriteNumber("indices", indices);
                w.WriteNumber("material", material);
                w.WriteNumber("mode", 4);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("materials");
            foreach (var label in materialLabels)
            {
                w.WriteStartObject();
                w.WriteString("name", label);
                w.WriteStartObject("pbrMetallicRoughness");
                WriteVec(w, "baseColorFactor", [0.8f, 0.8f, 0.8f, 1f]);
                w.WriteNumber("metallicFactor", 0);
                w.WriteNumber("roughnessFactor", 0.8);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteString("uri", binUri);
            w.WriteNumber("byteLength", binBytes.Length);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var (offset, length, target, _) in views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", offset);
                w.WriteNumber("byteLength", length);
                if (target is { } t)
                {
                    w.WriteNumber("target", t);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("accessors");
            foreach (var accessor in accessors)
            {
                w.WriteStartObject();
                accessor(w);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return (json.ToArray(), binBytes);
    }

    private static void WriteVec(Utf8JsonWriter w, string name, float[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static void Pad(Stream stream, int alignment)
    {
        while (stream.Position % alignment != 0)
        {
            stream.WriteByte(0);
        }
    }
}
