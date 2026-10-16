using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Textures;

/// <summary>
/// A material instance's <c>TextureStreamingData</c> names the textures it samples (<c>TextureName</c>) with their UV density.
/// A material that now uses a new texture must name it there too, or the streamer finds no entry for it and keeps a mip
/// that is too small (FORMAT_NOTES builds 7 and 8: the blurred SUV body). Edit path <c>#StreamingTexture[&lt;old leaf&gt;]</c>,
/// value the new leaf: each matching <c>TextureName</c> is pointed at the new name (added to the name table; same size).
/// </summary>
public static partial class MaterialTextureStreaming
{
    /// <summary>The edit path renaming <paramref name="oldLeaf"/>.</summary>
    public static string PathOf(string oldLeaf) => $"#StreamingTexture[{oldLeaf}]";

    /// <summary>True for a <see cref="PathOf"/> path.</summary>
    public static bool IsEdit(string path) => StreamingPath().IsMatch(path);

    /// <summary>Applies the renames; returns the bytes and how many entries were renamed.</summary>
    /// <exception cref="InvalidOperationException">An edit names an export the package does not have.</exception>
    public static (PackageBytes Bytes, int Renamed) Apply(CookedPackage package, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(edits);
        var names = package.Names.ToList();
        var wide = package.NameEntries.Select(n => n.IsWide).ToList();
        var keys = TunableReader.ExportKeys(package);
        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        var renamed = 0;
        foreach (var edit in edits)
        {
            var oldLeaf = StreamingPath().Match(edit.Path).Groups["old"].Value;
            if (oldLeaf.Length == 0 || edit.Value.Length == 0 || edit.Value == oldLeaf)
            {
                continue;
            }

            var exportIndex = Array.IndexOf(keys, edit.Export);
            if (exportIndex < 0)
            {
                throw new InvalidOperationException($"{package.BasePath}: no export '{edit.Export}'.");
            }

            var nameIndex = names.IndexOf(edit.Value);
            if (nameIndex < 0)
            {
                nameIndex = names.Count;
                names.Add(edit.Value);
                wide.Add(false);
            }

            var block = PropertyReader.ReadPayload(package, payloads[exportIndex], names, exportIndex);
            if (block.Find("TextureStreamingData")?.Value is not ArrayValue items)
            {
                continue;
            }

            foreach (var item in items.Items.OfType<StructValue>())
            {
                if (item.Properties.FirstOrDefault(t => t.Name == "TextureName") is { Value: NameValue name } tag && name.Value == oldLeaf)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payloads[exportIndex].AsSpan(tag.ValueOffset), nameIndex);
                    BinaryPrimitives.WriteInt32LittleEndian(payloads[exportIndex].AsSpan(tag.ValueOffset + 4), 0);
                    renamed++;
                }
            }
        }

        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = names,
            NameIsWide = wide,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });
        return (bytes, renamed);
    }

    [GeneratedRegex(@"^#StreamingTexture\[(?<old>[^\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamingPath();
}
