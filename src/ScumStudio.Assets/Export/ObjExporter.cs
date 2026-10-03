using System.Globalization;
using System.Text;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Assets.Export;

/// <summary>
/// Writes a <see cref="MeshData"/> as Wavefront OBJ (positions, normals, UVs, one <c>usemtl</c> group per section) with an
/// optional companion <c>.mtl</c> listing the materials by name.
/// </summary>
/// <remarks>OBJ's texture origin is bottom-left, so V is written as <c>1 - v</c>. Face indices are 1-based.</remarks>
public static class ObjExporter
{
    /// <summary>Writes OBJ text to <paramref name="writer"/>; <paramref name="mtlFileName"/> adds a <c>mtllib</c> line.</summary>
    public static void Write(MeshData mesh, TextWriter writer, MeshExportOptions? options = null, string? mtlFileName = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= new MeshExportOptions();
        var inv = CultureInfo.InvariantCulture;
        var (positions, normals, flip) = MeshTransform.Apply(mesh, options);
        var hasUv = options.IncludeUvs && mesh.Uv0.Length == mesh.VertexCount * 2;
        var hasN = normals.Length > 0;

        writer.WriteLine("# ScumStudio OBJ export");
        writer.WriteLine($"# {mesh.Name}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, scale {options.Scale.ToString(inv)}, "
                         + (options.ConvertToYUp ? "Y up (x, z, y of UE)" : "UE axes (Z up)"));
        if (!string.IsNullOrEmpty(mtlFileName))
        {
            writer.WriteLine("mtllib " + mtlFileName);
        }

        writer.WriteLine("o " + Sanitize(mesh.Name));
        var sb = new StringBuilder(64);
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            sb.Clear();
            sb.Append("v ").Append(F(positions[i * 3])).Append(' ').Append(F(positions[i * 3 + 1])).Append(' ').Append(F(positions[i * 3 + 2]));
            writer.WriteLine(sb);
        }

        if (hasUv)
        {
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                writer.WriteLine("vt " + F(mesh.Uv0[i * 2]) + " " + F(1f - mesh.Uv0[i * 2 + 1]));
            }
        }

        if (hasN)
        {
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                writer.WriteLine("vn " + F(normals[i * 3]) + " " + F(normals[i * 3 + 1]) + " " + F(normals[i * 3 + 2]));
            }
        }

        for (var s = 0; s < mesh.Sections.Length; s++)
        {
            var section = mesh.Sections[s];
            writer.WriteLine("g " + MeshTransform.MaterialLabel(section.MaterialName, s));
            writer.WriteLine("usemtl " + MeshTransform.MaterialLabel(section.MaterialName, s));
            var end = section.FirstIndex + section.IndexCount - section.IndexCount % 3;
            for (var t = section.FirstIndex; t < end; t += 3)
            {
                sb.Clear();
                sb.Append('f');
                for (var c = 0; c < 3; c++)
                {
                    var idx = MeshTransform.Corner(mesh.Indices, t, c, flip) + 1;
                    sb.Append(' ').Append(idx.ToString(inv));
                    if (hasUv || hasN)
                    {
                        sb.Append('/');
                        if (hasUv)
                        {
                            sb.Append(idx.ToString(inv));
                        }

                        if (hasN)
                        {
                            sb.Append('/').Append(idx.ToString(inv));
                        }
                    }
                }

                writer.WriteLine(sb);
            }
        }

        static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the companion MTL (one grey Lambert material per section, named after the section's material).</summary>
    public static void WriteMtl(MeshData mesh, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("# ScumStudio MTL export");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < mesh.Sections.Length; s++)
        {
            var label = MeshTransform.MaterialLabel(mesh.Sections[s].MaterialName, s);
            if (!seen.Add(label))
            {
                continue;
            }

            writer.WriteLine("newmtl " + label);
            if (mesh.Sections[s].MaterialName.Length > 0)
            {
                writer.WriteLine("# " + mesh.Sections[s].MaterialName);
            }

            writer.WriteLine("Kd 0.8 0.8 0.8");
            writer.WriteLine("Ka 0 0 0");
            writer.WriteLine("illum 1");
        }
    }

    /// <summary>Writes <c>path</c> (.obj) and a <c>.mtl</c> next to it. Returns the files written.</summary>
    public static async Task<IReadOnlyList<string>> SaveAsync(MeshData mesh, string path, MeshExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var mtl = Path.ChangeExtension(full, ".mtl");
        var obj = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        Write(mesh, obj, options, Path.GetFileName(mtl));
        var mtlText = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        WriteMtl(mesh, mtlText);
        await File.WriteAllTextAsync(full, obj.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(mtl, mtlText.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return [full, mtl];
    }

    private static string Sanitize(string name) => string.IsNullOrWhiteSpace(name) ? "mesh" : name.Replace(' ', '_');
}
