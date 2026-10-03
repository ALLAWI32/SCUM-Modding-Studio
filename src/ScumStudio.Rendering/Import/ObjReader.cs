using System.Globalization;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.Import;

/// <summary>
/// Minimal Wavefront OBJ reader producing a <see cref="MeshData"/> (fallback input for <c>scumstudio render obj</c> and
/// for meshes exported with <c>scumstudio asset mesh-export</c>).
/// </summary>
/// <remarks>
/// Supports <c>v</c>, <c>vn</c>, <c>vt</c>, <c>f</c> (triangles and convex polygons, fan-triangulated; <c>v</c>,
/// <c>v/t</c>, <c>v//n</c>, <c>v/t/n</c>; negative relative indices), <c>usemtl</c> (one section per material run) and
/// <c>o</c>/<c>g</c> (the first name becomes the mesh name). Every distinct (v, vt, vn) triple becomes one vertex.
/// Coordinates are kept as written (OBJ files are normally right-handed Y-up, i.e. <c>MeshSpace.Gl</c>).
/// OBJ texture coordinates have V pointing up; they are flipped to the top-left origin used by UE/glTF
/// (<c>v' = 1 - v</c>) unless <c>flipV</c> is false.
/// </remarks>
public static class ObjReader
{
    /// <summary>Reads an OBJ file.</summary>
    public static async Task<MeshData> LoadAsync(string path, bool flipV = true, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(text, Path.GetFileNameWithoutExtension(path), flipV);
    }

    /// <summary>Parses OBJ text.</summary>
    /// <exception cref="InvalidDataException">Malformed numbers or indices.</exception>
    public static MeshData Parse(string text, string defaultName = "obj", bool flipV = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var v = new List<float>();
        var vt = new List<float>();
        var vn = new List<float>();
        var positions = new List<float>();
        var uvs = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();
        var sections = new List<MeshSection>();
        var vertexMap = new Dictionary<(int V, int T, int N), uint>();
        var anyUv = false;
        var anyNormal = false;
        string? name = null;
        var material = string.Empty;
        var sectionStart = 0;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash].TrimEnd();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v":
                    ReadFloats(parts, 3, v, lineNumber);
                    break;
                case "vt":
                    ReadFloats(parts, 2, vt, lineNumber);
                    break;
                case "vn":
                    ReadFloats(parts, 3, vn, lineNumber);
                    break;
                case "o":
                case "g":
                    if (name is null && parts.Length > 1)
                    {
                        name = string.Join(' ', parts.Skip(1));
                    }

                    break;
                case "usemtl":
                    var next = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;
                    if (indices.Count > sectionStart)
                    {
                        sections.Add(new MeshSection(material, sectionStart, indices.Count - sectionStart));
                        sectionStart = indices.Count;
                    }

                    material = next;
                    break;
                case "f":
                    if (parts.Length < 4)
                    {
                        throw new InvalidDataException($"OBJ line {lineNumber}: a face needs at least 3 vertices.");
                    }

                    var corner = new uint[parts.Length - 1];
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var key = ParseCorner(parts[i], v.Count / 3, vt.Count / 2, vn.Count / 3, lineNumber);
                        if (!vertexMap.TryGetValue(key, out var index))
                        {
                            index = (uint)(positions.Count / 3);
                            vertexMap[key] = index;
                            positions.AddRange([v[key.V * 3], v[(key.V * 3) + 1], v[(key.V * 3) + 2]]);
                            if (key.T >= 0)
                            {
                                anyUv = true;
                                uvs.AddRange([vt[key.T * 2], flipV ? 1f - vt[(key.T * 2) + 1] : vt[(key.T * 2) + 1]]);
                            }
                            else
                            {
                                uvs.AddRange([0f, 0f]);
                            }

                            if (key.N >= 0)
                            {
                                anyNormal = true;
                                normals.AddRange([vn[key.N * 3], vn[(key.N * 3) + 1], vn[(key.N * 3) + 2]]);
                            }
                            else
                            {
                                normals.AddRange([0f, 0f, 0f]);
                            }
                        }

                        corner[i - 1] = index;
                    }

                    for (var i = 1; i + 1 < corner.Length; i++)
                    {
                        indices.AddRange([corner[0], corner[i], corner[i + 1]]);
                    }

                    break;
            }
        }

        if (indices.Count > sectionStart || sections.Count == 0)
        {
            sections.Add(new MeshSection(material, sectionStart, indices.Count - sectionStart));
        }

        var positionArray = positions.ToArray();
        var indexArray = indices.ToArray();
        // Vertices without an explicit normal get computed ones; keep the file's normals when all vertices have them.
        var normalArray = anyNormal && !HasMissingNormals(normals) ? normals.ToArray() : null;
        return MeshData.Create(name ?? defaultName, positionArray, indexArray, normalArray, anyUv ? uvs.ToArray() : null, [.. sections]);
    }

    private static bool HasMissingNormals(List<float> normals)
    {
        for (var i = 0; i < normals.Count; i += 3)
        {
            if (normals[i] == 0f && normals[i + 1] == 0f && normals[i + 2] == 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadFloats(string[] parts, int count, List<float> target, int lineNumber)
    {
        if (parts.Length < count + 1)
        {
            throw new InvalidDataException($"OBJ line {lineNumber}: '{parts[0]}' needs {count} numbers.");
        }

        for (var i = 1; i <= count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidDataException($"OBJ line {lineNumber}: '{parts[i]}' is not a number.");
            }

            target.Add(value);
        }
    }

    private static (int V, int T, int N) ParseCorner(string token, int vCount, int tCount, int nCount, int lineNumber)
    {
        var fields = token.Split('/');
        var vi = Resolve(fields[0], vCount, lineNumber, required: true);
        var ti = fields.Length > 1 ? Resolve(fields[1], tCount, lineNumber, required: false) : -1;
        var ni = fields.Length > 2 ? Resolve(fields[2], nCount, lineNumber, required: false) : -1;
        return (vi, ti, ni);
    }

    private static int Resolve(string field, int count, int lineNumber, bool required)
    {
        if (field.Length == 0 && !required)
        {
            return -1;
        }

        if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
        {
            throw new InvalidDataException($"OBJ line {lineNumber}: invalid index '{field}'.");
        }

        var zeroBased = index > 0 ? index - 1 : count + index;
        if (zeroBased < 0 || zeroBased >= count)
        {
            throw new InvalidDataException($"OBJ line {lineNumber}: index {index} is out of range (1..{count}).");
        }

        return zeroBased;
    }
}
