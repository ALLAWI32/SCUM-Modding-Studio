using System.Buffers.Binary;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Textures;

/// <summary>A written texture package.</summary>
/// <param name="Bytes">Header and <c>.uexp</c>.</param>
/// <param name="UBulk">The streamed mips, or null when every mip is inline.</param>
/// <param name="PixelFormat">The template's pixel format.</param>
/// <param name="Width">Width of mip 0.</param>
/// <param name="Height">Height of mip 0.</param>
/// <param name="MipCount">Mips written.</param>
public sealed record TextureWriteResult(PackageBytes Bytes, byte[]? UBulk, string PixelFormat, int Width, int Height, int MipCount);

/// <summary>
/// Writes a cooked <c>Texture2D</c> package from RGBA pixels in the layout of a stock texture (port of <c>build_one</c> in the
/// owner's <c>build_textures.py</c>): the template's names, imports, export entry, tagged properties (sRGB, compression,
/// LOD group) and pixel format are kept, the package is renamed to its new path, the mips are rebuilt (<see cref="MipChain"/>)
/// and encoded (<see cref="BlockEncoder"/>) with the cook's split (64 px and less inline, 128 px and more in the
/// <c>.ubulk</c>), and the absolute offsets are written for the real header size (two passes), then read back.
/// </summary>
public static class TextureWriter
{
    /// <summary>Writes <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>) as <paramref name="newPackagePath"/>.</summary>
    /// <param name="template">The stock texture whose layout is copied.</param>
    /// <param name="templateObjectPath">Its object (or package) path.</param>
    /// <param name="newPackagePath">The package to write (the template's own path rewrites it in place).</param>
    /// <param name="rgba">Mip 0 pixels.</param>
    /// <param name="width">Width: a power of two from 4 to 4096.</param>
    /// <param name="height">Height: a power of two from 4 to 4096.</param>
    /// <param name="weightMask">Optional mip weight per texel (see <see cref="MipChain"/>).</param>
    /// <exception cref="ArgumentException">A size is not a power of two in range, or a new leaf ends in <c>_&lt;digits&gt;</c>.</exception>
    /// <exception cref="NotSupportedException">The template's pixel format cannot be encoded.</exception>
    /// <exception cref="InvalidOperationException">The template is not a texture this writer knows, or the read-back failed.</exception>
    public static TextureWriteResult Write(CookedPackage template, string templateObjectPath, string newPackagePath, byte[] rgba, int width, int height,
        byte[]? weightMask = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rgba);
        var templatePackage = PackageMap.Normalize(templateObjectPath);
        var newPackage = PackageMap.Normalize(newPackagePath);
        var renamed = !string.Equals(templatePackage, newPackage, StringComparison.OrdinalIgnoreCase);
        if (renamed && TextureWrites.EndsInNumber(newPackage))
        {
            throw new ArgumentException($"{PackageMap.Leaf(newPackage)} ends in _<number>: the game would read it as a numbered name. End it with a letter.", nameof(newPackagePath));
        }

        if (!ImageFiles.IsValidSize(width) || !ImageFiles.IsValidSize(height))
        {
            throw new ArgumentException($"{width}x{height}: a texture edge is a power of two from {ImageFiles.MinSize} to {ImageFiles.MaxSize}.", nameof(width));
        }

        var index = CookedTexture2D.FindExport(template);
        if (index < 0)
        {
            throw new InvalidOperationException($"{templatePackage} holds no Texture2D.");
        }

        CookedTexture2D texture;
        try
        {
            texture = CookedTexture2D.Read(template, index);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{templatePackage}: {ex.Message}", ex);
        }

        if (texture.Platforms.Count != 1)
        {
            throw new InvalidOperationException($"{templatePackage} has {texture.Platforms.Count} platform formats; one was expected.");
        }

        var platform = texture.Platforms[0];
        if (!BlockEncoder.IsSupported(platform.PixelFormat))
        {
            throw new NotSupportedException($"{templatePackage} is {platform.PixelFormat}: the studio writes {string.Join(", ", BlockEncoder.Formats)}.");
        }

        var srgb = texture.Properties.Find("SRGB")?.BoolValue ?? true;
        var normal = texture.Properties.Find("CompressionSettings")?.Value is EnumValue { Value: var compression } && compression.Contains("TC_Normalmap", StringComparison.Ordinal);
        var blockSize = platform.PixelFormat == "PF_B8G8R8A8" ? 1 : 4;
        var layout = Layout(platform, width, height, blockSize);
        var images = MipChain.Build(rgba, width, height, layout.Count, srgb && !normal, normal, blockSize, weightMask);
        var mips = layout.Select((m, i) =>
        {
            var image = images[i].Width == m.SizeX && images[i].Height == m.SizeY ? images[i] : MipChain.PadTo(images[i], m.SizeX, m.SizeY);
            return m with { Data = BlockEncoder.Encode(image.Rgba, image.Width, image.Height, platform.PixelFormat) };
        }).ToList();
        var written = new CookedTexture2D
        {
            Prefix = WithImportedSize(texture, width, height),
            Properties = texture.Properties,
            Platforms = [platform with { SizeX = width, SizeY = height, Mips = mips }],
            NoneName = texture.NoneName,
        };

        var input = renamed ? RenamedTables(template, templatePackage, newPackage) : PackageWriter.ToBuildInput(template, useStoredNameHashes: true);
        var payloads = Enumerable.Range(0, template.Exports.Count).Select(template.GetExportData).ToArray();
        // Two passes: the offsets are fixed-size, so the header the first pass gives is the real one.
        payloads[index] = written.Write(0).Payload;
        var first = PackageWriter.Build(input with { ExportData = payloads });
        var exportOffset = CookedPackage.Parse(first.UAsset, first.UExp).Exports[index].SerialOffset;
        var (payload, ubulk) = written.Write(exportOffset);
        payloads[index] = payload;
        var bytes = PackageWriter.Build(input with { ExportData = payloads });
        Verify(bytes, ubulk, index, mips, newPackage);
        return new TextureWriteResult(bytes, ubulk, platform.PixelFormat, width, height, mips.Count);
    }

    /// <summary><paramref name="picture"/> fitted to the template's size and written as <paramref name="newPackagePath"/> (see the other overload).</summary>
    public static TextureWriteResult Write(CookedPackage template, string templateObjectPath, string newPackagePath, TextureImage picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var (width, height) = Size(template);
        return Write(template, templateObjectPath, newPackagePath, ImageFiles.Fit(picture, width, height), width, height);
    }

    /// <summary>The size of a texture package's top mip.</summary>
    /// <exception cref="InvalidOperationException">The package holds no readable Texture2D.</exception>
    public static (int Width, int Height) Size(CookedPackage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var index = CookedTexture2D.FindExport(template);
        try
        {
            return index >= 0 && CookedTexture2D.Read(template, index).Platforms is [var platform, ..]
                ? (platform.SizeX, platform.SizeY)
                : throw new InvalidOperationException($"{template.BasePath} holds no Texture2D.");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{template.BasePath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The mips to write: the template's own when the size is the same (stock layout byte for byte), else a chain for the new
    /// size with the template's mip policy (a full chain or a single mip) and the cook's inline/streamed split.
    /// </summary>
    private static List<CookedTextureMip> Layout(CookedTexturePlatform platform, int width, int height, int blockSize)
    {
        if (platform.SizeX == width && platform.SizeY == height)
        {
            return [.. platform.Mips];
        }

        var full = MipChain.FullCount(width, height);
        var missing = MipChain.FullCount(platform.SizeX, platform.SizeY) - platform.Mips.Count;
        var count = platform.Mips.Count == 1 ? 1 : Math.Max(1, full - Math.Max(missing, 0));
        var streamed = platform.Mips.FirstOrDefault(m => !m.IsInline);
        var inline = platform.Mips.FirstOrDefault(m => m.IsInline);
        return Enumerable.Range(0, count).Select(i =>
        {
            var (w, h) = MipChain.MipSize(width, height, i);
            var big = streamed is not null && Math.Max(w, h) >= 128;
            var like = big ? streamed! : inline ?? new CookedTextureMip { Flags = CookedTextureMip.InlineFlags };
            return like with { SizeX = Math.Max(w, blockSize), SizeY = Math.Max(h, blockSize), SizeZ = 1 };
        }).ToList();
    }

    /// <summary>
    /// The prefix with <c>ImportedSize</c> set to a new cooked size (same-size patch). At the template's own size it stays as
    /// stored: the stock value is the source picture's size, often larger than the cooked one (T_Car_Body_1_D_v3).
    /// </summary>
    private static byte[] WithImportedSize(CookedTexture2D texture, int width, int height)
    {
        var prefix = (byte[])texture.Prefix.Clone();
        if (texture.Platforms[0] is var platform && platform.SizeX == width && platform.SizeY == height)
        {
            return prefix;
        }

        if (texture.Properties.Find("ImportedSize") is { Value: IntPointValue } size)
        {
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(size.ValueOffset), width);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(size.ValueOffset + 4), height);
        }

        return prefix;
    }

    /// <summary>The template's tables with its leaf and /Game path renamed (the rename-clone's name table; payloads replaced after).</summary>
    private static PackageBuildInput RenamedTables(CookedPackage template, string templatePackage, string newPackage)
    {
        var clone = PackageCloner.Clone(template, templatePackage, new PackageMap([new KeyValuePair<string, string>(templatePackage, newPackage)]));
        return PackageWriter.ToBuildInput(CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, null, newPackage));
    }

    /// <summary>The build_textures self-checks on the written bytes.</summary>
    private static void Verify(PackageBytes bytes, byte[]? ubulk, int index, IReadOnlyList<CookedTextureMip> mips, string packagePath)
    {
        var package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk, packagePath);
        var platform = CookedTexture2D.Read(package, index).Platforms[0];
        var at = package.Exports[index].SerialOffset;
        var problem = platform.SkipOffset != at + platform.EndPosition ? "skip offset"
            : platform.Mips.Count != mips.Count ? "mip count"
            : platform.Mips.Where(m => m.IsInline).Any(m => m.OffsetInFile != at + m.OffsetPosition + 8) ? "inline offsets"
            : platform.Mips.Where(m => !m.IsInline).Sum(m => (long)m.SizeOnDisk) != (ubulk?.Length ?? 0) ? ".ubulk length"
            : platform.Mips.Zip(mips).Any(p => p.First.Data is null || !p.First.Data.AsSpan().SequenceEqual(p.Second.Data)) ? "mip pixels"
            : null;
        if (problem is not null)
        {
            throw new InvalidOperationException($"{packagePath}: the written texture does not read back ({problem}).");
        }
    }
}
