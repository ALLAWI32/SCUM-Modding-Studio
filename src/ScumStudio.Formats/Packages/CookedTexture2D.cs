using System.Buffers.Binary;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Formats.Packages;

/// <summary>One mip of a cooked texture: its <c>FByteBulkData</c> header, its pixels and where the header sits in the export.</summary>
public sealed record CookedTextureMip
{
    /// <summary>Bulk flags of a mip stored in the export (<c>ForceInlinePayload | SingleUse</c>; mips of 64 px and less).</summary>
    public const uint InlineFlags = 0x48;

    /// <summary>Bulk flags of a mip in the <c>.ubulk</c> (<c>NoOffsetFixUp | Force_NOT_InlinePayload | PayloadInSeperateFile | PayloadAtEndOfFile</c>).</summary>
    public const uint StreamedFlags = 0x10501;

    /// <summary>The mip's <c>bCooked</c>.</summary>
    public int Cooked { get; init; } = 1;

    /// <summary>Bulk data flags.</summary>
    public uint Flags { get; init; }

    /// <summary>Stored element count (bytes).</summary>
    public int ElementCount { get; init; }

    /// <summary>Stored size on disk (bytes).</summary>
    public int SizeOnDisk { get; init; }

    /// <summary>Stored offset: absolute file offset of the inline payload, or the offset inside the <c>.ubulk</c>.</summary>
    public long OffsetInFile { get; init; }

    /// <summary>The pixels: the inline payload, or the <c>.ubulk</c> slice when it was available (else null).</summary>
    public byte[]? Data { get; init; }

    /// <summary>Width.</summary>
    public int SizeX { get; init; }

    /// <summary>Height.</summary>
    public int SizeY { get; init; }

    /// <summary>Depth (1).</summary>
    public int SizeZ { get; init; } = 1;

    /// <summary>Export-relative position of <see cref="OffsetInFile"/> as read (-1 for a mip made in memory).</summary>
    public int OffsetPosition { get; init; } = -1;

    /// <summary>True when the payload is stored in the export (port of <c>parse_tex</c>'s rule).</summary>
    public bool IsInline => IsInlineFlags(Flags);

    /// <summary>The inline rule: <c>ForceInlinePayload</c>, or not <c>PayloadInSeperateFile</c>.</summary>
    public static bool IsInlineFlags(uint flags) => (flags & 0x40) != 0 || (flags & 0x100) == 0;
}

/// <summary>One <c>FTexturePlatformData</c> of a cooked texture (SCUM cooks one: the PC format).</summary>
public sealed record CookedTexturePlatform
{
    /// <summary>The pixel format FName (e.g. <c>PF_DXT1</c>).</summary>
    public required FNameRef PixelFormatName { get; init; }

    /// <summary>The pixel format as text.</summary>
    public required string PixelFormat { get; init; }

    /// <summary>Stored skip offset: absolute file offset of the byte after this platform data.</summary>
    public long SkipOffset { get; init; }

    /// <summary>Export-relative position of <see cref="SkipOffset"/> as read (-1 for data made in memory).</summary>
    public int SkipOffsetPosition { get; init; } = -1;

    /// <summary>Export-relative end of the platform data as read.</summary>
    public int EndPosition { get; init; } = -1;

    /// <summary>Width of the top mip.</summary>
    public int SizeX { get; init; }

    /// <summary>Height of the top mip.</summary>
    public int SizeY { get; init; }

    /// <summary>NumSlices and the flag bits (bit 30: optional data follows, bit 31: cube map).</summary>
    public uint PackedData { get; init; } = 1;

    /// <summary>The optional data (8 bytes) when <see cref="PackedData"/> has bit 30.</summary>
    public byte[]? OptData { get; init; }

    /// <summary>The pixel format string.</summary>
    public required string PixelFormatString { get; init; }

    /// <summary>First mip to serialize.</summary>
    public int FirstMip { get; init; }

    /// <summary>The mips, largest first.</summary>
    public required IReadOnlyList<CookedTextureMip> Mips { get; init; }

    /// <summary><c>bIsVirtual</c> (always 0: virtual textures are refused).</summary>
    public int IsVirtual { get; init; }
}

/// <summary>
/// The cooked payload of a <c>Texture2D</c> export (port of <c>parse_tex</c> and <c>build_payload</c> in the owner's
/// <c>build_textures.py</c>, layout in FORMAT_NOTES "Textures"): the tagged properties with <c>bHasGuid</c>, two strip flags and
/// <c>bCooked</c> kept as a prefix, then per platform the pixel format, an absolute skip offset, the sizes, the format string
/// and the mips (inline ones with their absolute file offset, streamed ones with their <c>.ubulk</c> offset), then <c>None</c>.
/// </summary>
public sealed class CookedTexture2D
{
    private const uint OptDataBit = 1u << 30;

    /// <summary>The prefix: tagged properties, <c>bHasGuid</c>, strip flags and <c>bCooked</c>, verbatim.</summary>
    public required byte[] Prefix { get; init; }

    /// <summary>The tagged properties of the prefix.</summary>
    public required PropertyBlock Properties { get; init; }

    /// <summary>The platform data (one for SCUM).</summary>
    public required IReadOnlyList<CookedTexturePlatform> Platforms { get; init; }

    /// <summary>The <c>None</c> FName that ends the platform list.</summary>
    public FNameRef NoneName { get; init; }

    /// <summary>Index of the first <c>Texture2D</c> export, or -1.</summary>
    public static int FindExport(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i) == "Texture2D")
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Reads export <paramref name="exportIndex"/> of <paramref name="package"/>; streamed mips get their pixels from its <c>.ubulk</c> when loaded.</summary>
    /// <exception cref="FormatException">The payload is not a cooked texture this reader knows (virtual, truncated, trailing bytes).</exception>
    public static CookedTexture2D Read(CookedPackage package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);
        var payload = package.GetExportBytes(exportIndex);
        var props = PropertyReader.ReadPayload(package, payload, exportIndex);
        var r = new ByteReader(payload) { Position = props.EndOffset };
        try
        {
            if (r.I32() != 0)
            {
                r.Skip(16); // bHasGuid: the object guid
            }

            r.Skip(4); // StripFlags x2
            r.I32(); // bCooked
            var prefix = payload[..r.Position];
            var platforms = new List<CookedTexturePlatform>();
            while (true)
            {
                var pfName = r.FName();
                var pf = package.ResolveName(pfName);
                if (pf == "None")
                {
                    if (r.Remaining != 0)
                    {
                        throw new FormatException($"{package.BasePath}: {r.Remaining} bytes after the texture's platform data.");
                    }

                    return new CookedTexture2D { Prefix = prefix, Properties = props, Platforms = platforms, NoneName = pfName };
                }

                var skipPos = r.Position;
                var skip = r.I64();
                var sx = r.I32();
                var sy = r.I32();
                var packed = r.U32();
                var opt = (packed & OptDataBit) != 0 ? r.Raw(8) : null;
                var pfs = r.FString();
                var first = r.I32();
                var count = r.I32();
                if (count is < 0 or > 32)
                {
                    throw new FormatException($"{package.BasePath}: implausible mip count {count}.");
                }

                var mips = new List<CookedTextureMip>(count);
                for (var m = 0; m < count; m++)
                {
                    var cooked = r.I32();
                    var flags = r.U32();
                    var elements = r.I32();
                    var size = r.I32();
                    var offPos = r.Position;
                    var offset = r.I64();
                    byte[]? data = null;
                    if (CookedTextureMip.IsInlineFlags(flags))
                    {
                        data = r.Raw(size);
                    }
                    else if (package.UBulk is { } bulk && offset >= 0 && offset + size <= bulk.Length)
                    {
                        data = bulk.AsSpan((int)offset, size).ToArray();
                    }

                    mips.Add(new CookedTextureMip
                    {
                        Cooked = cooked, Flags = flags, ElementCount = elements, SizeOnDisk = size, OffsetInFile = offset, Data = data,
                        SizeX = r.I32(), SizeY = r.I32(), SizeZ = r.I32(), OffsetPosition = offPos,
                    });
                }

                var virtualTexture = r.I32();
                if (virtualTexture != 0)
                {
                    throw new FormatException($"{package.BasePath}: virtual textures are not supported.");
                }

                platforms.Add(new CookedTexturePlatform
                {
                    PixelFormatName = pfName, PixelFormat = pf, SkipOffset = skip, SkipOffsetPosition = skipPos, EndPosition = r.Position,
                    SizeX = sx, SizeY = sy, PackedData = packed, OptData = opt, PixelFormatString = pfs, FirstMip = first, Mips = mips,
                    IsVirtual = virtualTexture,
                });
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new FormatException($"{package.BasePath}: truncated texture payload ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Writes the payload as the cook lays it out (port of <c>build_payload</c>): inline mips get the absolute file offset of
    /// their pixels, streamed mips are concatenated into the returned <c>.ubulk</c> from the largest, and every skip offset
    /// points at the byte after its platform data.
    /// </summary>
    /// <param name="exportFileOffset">Absolute file offset of the export (TotalHeaderSize + its <c>.uexp</c> offset).</param>
    /// <exception cref="InvalidOperationException">A mip has no pixels.</exception>
    public (byte[] Payload, byte[]? UBulk) Write(long exportFileOffset)
    {
        var w = new ByteWriter(Prefix.Length + 256);
        w.Raw(Prefix);
        var ubulk = new ByteWriter(4096);
        var streamed = false;
        var skips = new List<(int Position, long End)>();
        foreach (var p in Platforms)
        {
            w.FName(p.PixelFormatName);
            var skipPos = w.Tell();
            w.I64(0);
            w.I32(p.SizeX);
            w.I32(p.SizeY);
            w.U32(p.PackedData);
            if (p.OptData is { } opt)
            {
                w.Raw(opt);
            }

            w.FString(p.PixelFormatString);
            w.I32(p.FirstMip);
            w.I32(p.Mips.Count);
            foreach (var m in p.Mips)
            {
                var data = m.Data ?? throw new InvalidOperationException($"Mip {m.SizeX}x{m.SizeY} has no pixels (missing .ubulk?).");
                w.I32(m.Cooked);
                w.U32(m.Flags);
                w.I32(data.Length);
                w.I32(data.Length);
                if (m.IsInline)
                {
                    w.I64(exportFileOffset + w.Tell() + 8);
                    w.Raw(data);
                }
                else
                {
                    w.I64(ubulk.Length);
                    ubulk.Raw(data);
                    streamed = true;
                }

                w.I32(m.SizeX);
                w.I32(m.SizeY);
                w.I32(m.SizeZ);
            }

            w.I32(p.IsVirtual);
            skips.Add((skipPos, exportFileOffset + w.Tell()));
        }

        w.FName(NoneName);
        var payload = w.ToArray();
        foreach (var (position, end) in skips)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(position), end);
        }

        return (payload, streamed ? ubulk.ToArray() : null);
    }

    /// <summary>
    /// Puts the absolute offsets of every <c>Texture2D</c> export of a package back where they belong: the skip offsets and
    /// the inline mips' <c>OffsetInFile</c> hold TotalHeaderSize + their position, and a rename-copy (a longer or shorter
    /// name table) moves the header end. Other exports and the <c>.ubulk</c> (its offsets are relative) are unchanged.
    /// </summary>
    /// <param name="bytes">The package (header + <c>.uexp</c>).</param>
    /// <param name="ubulk">Its <c>.ubulk</c>, checked against the streamed mips when given.</param>
    /// <exception cref="FormatException">A streamed mip lies outside <paramref name="ubulk"/>.</exception>
    public static PackageBytes Relayout(PackageBytes bytes, byte[]? ubulk)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk);
        byte[]? uexp = null;
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i) != "Texture2D")
            {
                continue;
            }

            var texture = Read(package, i);
            var exportFileOffset = package.Exports[i].SerialOffset;
            var start = package.GetExportUExpOffset(i);
            uexp ??= (byte[])bytes.UExp.Clone();
            foreach (var p in texture.Platforms)
            {
                BinaryPrimitives.WriteInt64LittleEndian(uexp.AsSpan(start + p.SkipOffsetPosition), exportFileOffset + p.EndPosition);
                foreach (var m in p.Mips)
                {
                    if (m.IsInline)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(uexp.AsSpan(start + m.OffsetPosition), exportFileOffset + m.OffsetPosition + 8);
                    }
                    else if (ubulk is not null && (m.OffsetInFile < 0 || m.OffsetInFile + m.SizeOnDisk > ubulk.Length))
                    {
                        throw new FormatException($"{package.ResolveName(package.Exports[i].ObjectName)}: mip {m.SizeX}x{m.SizeY} lies outside the .ubulk.");
                    }
                }
            }
        }

        return uexp is null ? bytes : bytes with { UExp = uexp };
    }
}
