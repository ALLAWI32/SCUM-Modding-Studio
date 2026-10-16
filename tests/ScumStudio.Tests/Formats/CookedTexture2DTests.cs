using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Formats;

/// <summary>
/// The cooked Texture2D layout (FORMAT_NOTES "Textures", the owner's <c>build_textures.py</c>): read and rewritten byte for
/// byte, and the absolute offsets a rename-copy leaves stale (the copy's header has another size) put back.
/// </summary>
public sealed class CookedTexture2DTests
{
    [Fact]
    public void ReadThenWriteIsByteExact()
    {
        var (bytes, ubulk) = SyntheticTexture.Build(SyntheticTexture.Path);
        var package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk);
        var texture = CookedTexture2D.Read(package, 0);

        var platform = Assert.Single(texture.Platforms);
        Assert.Equal(("PF_DXT1", 256, 256, 9), (platform.PixelFormat, platform.SizeX, platform.SizeY, platform.Mips.Count));
        Assert.Equal([256, 128], platform.Mips.Where(m => !m.IsInline).Select(m => m.SizeX));
        Assert.All(platform.Mips.Where(m => m.IsInline), m => Assert.Equal(CookedTextureMip.InlineFlags, m.Flags));
        Assert.Equal([64, 32, 16, 8, 4, 4, 4], platform.Mips.Where(m => m.IsInline).Select(m => m.SizeX)); // below 4x4 stored as 4x4
        Assert.False(texture.Properties.Find("SRGB")!.BoolValue);

        var (payload, written) = texture.Write(package.Exports[0].SerialOffset);
        Assert.Equal(package.GetExportBytes(0), payload);
        Assert.Equal(ubulk, written);
    }

    [Fact]
    public void ARenameCopyKeepsStaleOffsetsUntilRelayout()
    {
        var (bytes, ubulk) = SyntheticTexture.Build(SyntheticTexture.Path);
        var source = CookedPackage.Parse(bytes.UAsset, bytes.UExp, ubulk);
        var longer = "/Game/Tests/Textures/T_Ground_Snow_Much_Longer_Name_D";
        var copy = PackageCloner.Clone(source, SyntheticTexture.Path, new PackageMap([new KeyValuePair<string, string>(SyntheticTexture.Path, longer)]));

        var stale = CookedPackage.Parse(copy.Bytes.UAsset, copy.Bytes.UExp, copy.UBulk);
        Assert.NotEqual(source.Summary.TotalHeaderSize, stale.Summary.TotalHeaderSize);
        Assert.False(OffsetsAreAbsolute(stale)); // the template's offsets, copied verbatim

        var fixedBytes = CookedTexture2D.Relayout(copy.Bytes, copy.UBulk);
        var relaid = CookedPackage.Parse(fixedBytes.UAsset, fixedBytes.UExp, copy.UBulk);
        Assert.True(OffsetsAreAbsolute(relaid));
        Assert.Equal(copy.Bytes.UAsset, fixedBytes.UAsset);
        Assert.Equal(source.GetExportBytes(0).Length, relaid.GetExportBytes(0).Length);
        Assert.Equal(ubulk, copy.UBulk); // the .ubulk offsets are relative: unchanged
    }

    [Fact]
    public void RelayoutLeavesOtherPackagesAlone()
    {
        var p = new SyntheticLevelPackage();
        var export = p.Export("DA_Thing", p.ScriptClass("/Script/Engine", "DataAsset"), 0);
        p.SetPayload(export, p.Properties(t => t.Float("Weight", 2f)));
        var bytes = p.Build();
        Assert.Same(bytes, CookedTexture2D.Relayout(bytes, null));
    }

    [Fact]
    public void AReplacedTextureGetsTheOffsetsOfItsOwnHeader()
    {
        // Replacements (a ground texture for a ground texture) rename-copy the texture under the stock path. Before the fix the
        // copy kept the template's absolute SkipOffset and inline OffsetInFile: CUE4Parse reads inline mips from where it
        // stands, so only the stored values tell.
        using var temp = new LevelTempDirectory();
        const string target = "/Game/Tests/Textures/T_Ground_Grass_Meadow_Long_D";
        SyntheticTexture.WriteLoose(temp.Path, SyntheticTexture.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var request = new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>())
        {
            Replacements = new Dictionary<string, string> { [target] = SyntheticTexture.Path },
        };
        var result = AssetModBuilder.Build(catalog, request);

        var built = Assert.Single(result.Packages);
        Assert.Equal(target, built.PackagePath);
        Assert.True(OffsetsAreAbsolute(CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk)));
    }

    [Fact]
    public void ATextureCopiedWithACloneGetsTheOffsetsOfItsOwnHeader()
    {
        // 3.2's clone loop (copies built first, a clone that fails is left out whole) rename-copies every package of a family;
        // a texture in it (a weapon's or vehicle's own paint texture) took the template's absolute offsets before the fix.
        using var temp = new LevelTempDirectory();
        const string copy = "/Game/Tests/Clones/CarGold/Textures/T_Body_D_OfTheCarGoldCloneWithALongerName";
        SyntheticTexture.WriteLoose(temp.Path, SyntheticTexture.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        var plan = new ClonePlan(SyntheticTexture.Path, copy, [new KeyValuePair<string, string>(SyntheticTexture.Path, copy)]);
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()) { WriteRegistry = false });

        var built = Assert.Single(result.Packages, p => p.PackagePath == copy);
        Assert.True(built.IsClone);
        Assert.True(OffsetsAreAbsolute(CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk)));
    }

    /// <summary>The build_textures checks: skip offset = header + export end, inline offsets = their absolute position.</summary>
    internal static bool OffsetsAreAbsolute(CookedPackage package)
    {
        var index = CookedTexture2D.FindExport(package);
        var at = package.Exports[index].SerialOffset;
        var platform = CookedTexture2D.Read(package, index).Platforms[0];
        return platform.SkipOffset == at + platform.EndPosition
               && platform.Mips.Where(m => m.IsInline).All(m => m.OffsetInFile == at + m.OffsetPosition + 8);
    }
}

/// <summary>
/// A cooked Texture2D as SCUM ships it, written by hand from FORMAT_NOTES (not by <see cref="CookedTexture2D"/>): tagged
/// ImportedSize / LODGroup / SRGB, bHasGuid, strip flags, bCooked, one platform with the full mip chain (128 px and up in the
/// <c>.ubulk</c> with flags 0x10501, 64 px and down inline with 0x48 and absolute offsets, below 4 px stored as 4x4).
/// </summary>
internal static class SyntheticTexture
{
    public const string Path = "/Game/Tests/Textures/T_Body_D";

    public static (PackageBytes Bytes, byte[]? UBulk) Build(string packagePath, string pixelFormat = "PF_DXT1", int size = 256, bool srgb = false,
        string compression = "TC_Default")
    {
        var leaf = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        var p = new SyntheticLevelPackage();
        p.Name(packagePath); // a cooked texture's name table carries its own path
        var export = p.Export(leaf, p.ScriptClass("/Script/Engine", "Texture2D"), 0);
        var blockBytes = pixelFormat is "PF_DXT1" or "PF_BC4" ? 8 : pixelFormat == "PF_B8G8R8A8" ? 0 : 16;
        var count = (int)Math.Log2(size) + 1;
        var mips = Enumerable.Range(0, count).Select(i =>
        {
            var edge = Math.Max(size >> i, blockBytes == 0 ? 1 : 4);
            var data = new byte[blockBytes == 0 ? edge * edge * 4 : (edge / 4) * (edge / 4) * blockBytes];
            for (var b = 0; b < data.Length; b++)
            {
                data[b] = (byte)((b * 31) + i);
            }

            return (Edge: edge, Data: data, Streamed: (size >> i) >= 128);
        }).ToList();

        byte[] Payload(long exportOffset)
        {
            var w = new ByteWriter();
            Tag(w, p, "ImportedSize", "StructProperty", 8);
            w.FName(p.Name("IntPoint"));
            w.Guid(default);
            w.U8(0);
            w.I32(size);
            w.I32(size);
            Tag(w, p, "CompressionSettings", "ByteProperty", 8);
            w.FName(p.Name("TextureCompressionSettings"));
            w.U8(0);
            w.FName(p.Name(compression));
            Tag(w, p, "LODGroup", "ByteProperty", 8);
            w.FName(p.Name("TextureGroup"));
            w.U8(0);
            w.FName(p.Name("TEXTUREGROUP_Vehicle"));
            if (!srgb)
            {
                Tag(w, p, "SRGB", "BoolProperty", 0);
                w.U8(0);
                w.U8(0);
            }

            w.FName(p.Name("None"));
            w.I32(0); // bHasGuid
            w.Raw([1, 0, 1, 0]); // StripFlags x2
            w.I32(1); // bCooked
            w.FName(p.Name(pixelFormat));
            var skip = w.Tell();
            w.I64(0);
            w.I32(size);
            w.I32(size);
            w.U32(1);
            w.FString(pixelFormat);
            w.I32(0);
            w.I32(mips.Count);
            long bulk = 0;
            foreach (var m in mips)
            {
                w.I32(1);
                w.U32(m.Streamed ? 0x10501u : 0x48u);
                w.I32(m.Data.Length);
                w.I32(m.Data.Length);
                if (m.Streamed)
                {
                    w.I64(bulk);
                    bulk += m.Data.Length;
                }
                else
                {
                    w.I64(exportOffset + w.Tell() + 8);
                    w.Raw(m.Data);
                }

                w.I32(m.Edge);
                w.I32(m.Edge);
                w.I32(1);
            }

            w.I32(0); // bIsVirtual
            var end = exportOffset + w.Tell();
            w.FName(p.Name("None"));
            var bytes = w.ToArray();
            BitConverter.GetBytes(end).CopyTo(bytes, skip);
            return bytes;
        }

        p.SetPayload(export, Payload(0));
        var first = p.Build();
        p.SetPayload(export, Payload(CookedPackage.Parse(first.UAsset, first.UExp).Exports[0].SerialOffset));
        var ubulk = mips.Where(m => m.Streamed).SelectMany(m => m.Data).ToArray();
        return (p.Build(), ubulk.Length > 0 ? ubulk : null);
    }

    /// <summary>Writes the texture under <c>SCUM/Content</c> of a loose folder.</summary>
    public static string WriteLoose(string root, string packagePath, string pixelFormat = "PF_DXT1", int size = 256, bool srgb = false, string compression = "TC_Default")
    {
        var (bytes, ubulk) = Build(packagePath, pixelFormat, size, srgb, compression);
        return WriteLoose(root, packagePath, bytes, ubulk);
    }

    /// <summary>Writes a package under <c>SCUM/Content</c> of a loose folder; returns its path without extension.</summary>
    public static string WriteLoose(string root, string packagePath, PackageBytes bytes, byte[]? ubulk)
    {
        var file = System.IO.Path.Combine([root, "SCUM", "Content", .. packagePath["/Game/".Length..].Split('/')]);
        bytes.WriteAsync(file).GetAwaiter().GetResult();
        if (ubulk is not null)
        {
            File.WriteAllBytes(file + ".ubulk", ubulk);
        }

        return file;
    }

    private static void Tag(ByteWriter w, SyntheticLevelPackage p, string name, string type, int size)
    {
        w.FName(p.Name(name));
        w.FName(p.Name(type));
        w.I32(size);
        w.I32(0);
    }
}
