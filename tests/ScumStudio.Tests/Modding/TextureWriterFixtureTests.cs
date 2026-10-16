using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Textures;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The texture writer on the stock WolfsWagen textures the owner's <c>build_textures.py</c> mirrored in his shipped mods
/// (loose cooked files of <c>SCUM_FIXTURES</c>): each one re-written from its own decoded pixels under its own path keeps
/// every header byte, every byte of the export outside the mip pixels, the <c>.ubulk</c> length and every offset (the
/// build_textures asserts); written under a new path, CUE4Parse decodes it.
/// </summary>
public sealed class TextureWriterFixtureTests(ITestOutputHelper output)
{
    private const string WolfsWagen = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Textures/";

    private static readonly (string Root, string Package, string Format)[] Stock =
    [
        ("orig", WolfsWagen + "T_Car_Body_1_D_v3", "PF_DXT1"),
        ("orig", WolfsWagen + "T_Car_Body_1_N_v2", "PF_BC5"),
        ("orig", WolfsWagen + "T_WWBody_1_RMA", "PF_DXT1"),
        ("orig", WolfsWagen + "T_Glass_D", "PF_DXT5"),
        ("orig_extra", "/Game/ConZ_Files/Items/Item_Icons/ICO_WolfsWagen", "PF_BC7"),
    ];

    [FixturesFact]
    public void StockTexturesRewrittenFromTheirOwnPixelsKeepTheirLayout()
    {
        foreach (var row in Stock)
        {
            var (root, packagePath, format) = row;
            var stock = CookedPackage.Load(File(root, packagePath));
            var index = CookedTexture2D.FindExport(stock);
            var before = CookedTexture2D.Read(stock, index).Platforms[0];
            Assert.Equal(format, before.PixelFormat);
            var pixels = Decode(before);

            var written = TextureWriter.Write(stock, packagePath, packagePath, pixels, before.SizeX, before.SizeY);

            Assert.Equal(stock.UAsset, written.Bytes.UAsset);
            Assert.Equal(stock.UExp.Length, written.Bytes.UExp.Length);
            Assert.Equal(stock.UBulk?.Length, written.UBulk?.Length);
            var after = CookedTexture2D.Read(CookedPackage.Parse(written.Bytes.UAsset, written.Bytes.UExp, written.UBulk), index).Platforms[0];
            Assert.Equal(before.SkipOffset, after.SkipOffset);
            Assert.Equal(
                before.Mips.Select(m => (m.Flags, m.ElementCount, m.SizeOnDisk, m.OffsetInFile, m.SizeX, m.SizeY)),
                after.Mips.Select(m => (m.Flags, m.ElementCount, m.SizeOnDisk, m.OffsetInFile, m.SizeX, m.SizeY)));
            var start = stock.GetExportUExpOffset(index);
            var pixelRanges = before.Mips.Where(m => m.IsInline).Select(m => (From: start + m.OffsetPosition + 8, To: start + m.OffsetPosition + 8 + m.SizeOnDisk)).ToList();
            var changedOutside = Enumerable.Range(0, stock.UExp.Length).Count(i => stock.UExp[i] != written.Bytes.UExp[i] && !pixelRanges.Any(r => i >= r.From && i < r.To));
            Assert.Equal(0, changedOutside);

            var top = after.Mips[0];
            var again = TextureDecoder.DecodeRaw(top.Data, Enum.Parse<EPixelFormat>(format), top.SizeX, top.SizeY, format == "PF_BC5");
            var channels = format == "PF_BC5" ? 2 : format == "PF_DXT1" ? 3 : 4;
            var error = Enumerable.Range(0, pixels.Length).Where(i => i % 4 < channels).Average(i => Math.Abs(pixels[i] - again[i]));
            output.WriteLine($"{packagePath[(packagePath.LastIndexOf('/') + 1)..]}: {format} {before.SizeX}x{before.SizeY} {before.Mips.Count} mips, re-encode mean abs error {error:F2}");
            Assert.True(error <= 6, $"{packagePath}: mean abs error {error:F2}");
        }
    }

    [FixturesFact]
    public void AStockTextureWrittenUnderANewPathIsReadByCue4Parse()
    {
        using var temp = new LevelTempDirectory();
        foreach (var row in Stock)
        {
            var (root, packagePath, _) = row;
            var stock = CookedPackage.Load(File(root, packagePath));
            var before = CookedTexture2D.Read(stock, CookedTexture2D.FindExport(stock)).Platforms[0];
            var leaf = "T_WolfsWagenGold_" + packagePath[(packagePath.LastIndexOf('/') + 1)..].Replace("_", string.Empty, StringComparison.Ordinal) + "_Tex";
            var target = "/Game/ConZ_Files/Vehicles/WolfsWagenGold/Textures/" + leaf;

            var written = TextureWriter.Write(stock, packagePath, target, Decode(before), before.SizeX, before.SizeY);
            SyntheticTexture.WriteLoose(temp.Path, target, written.Bytes, written.UBulk);
            Assert.True(CookedTexture2DTests.OffsetsAreAbsolute(CookedPackage.Parse(written.Bytes.UAsset, written.Bytes.UExp, written.UBulk)));

            using var catalog = AssetCatalog.OpenLoose(temp.Path);
            var texture = catalog.LoadObject<UTexture2D>(target + "." + leaf);
            var image = TextureDecoder.Decode(texture);
            Assert.Equal((before.SizeX, before.SizeY, before.Mips.Count), (image.Width, image.Height, texture.PlatformData.Mips.Length));
        }
    }

    private static string File(string root, string packagePath) =>
        Path.Combine([FixturePaths.Combine(root), "SCUM", "Content", .. packagePath["/Game/".Length..].Split('/')]);

    private static byte[] Decode(CookedTexturePlatform platform)
    {
        var top = platform.Mips[0];
        return TextureDecoder.DecodeRaw(top.Data, Enum.Parse<EPixelFormat>(platform.PixelFormat), top.SizeX, top.SizeY, platform.PixelFormat == "PF_BC5");
    }
}
