using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Viewport.Decals;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-19): "the external stickers, the PNG files, sometimes do not work when we put them" (Vehicles, Decals,
/// Add sticker with his own picture). Every kind of PNG a user may have, and the JPEGs that trip readers, go through the
/// same Add sticker as the button: each is kept as an 8-bit RGBA PNG of at most 2048 pixels, upright, in its own colours,
/// read back as a sticker and put on a car; what cannot work says what is wrong and what to do.
/// </summary>
public sealed class StickerPicturesTests(ITestOutputHelper output)
{
    /// <summary>The kinds of picture: file name and what the kept sticker must be (width, height, red left half).</summary>
    public static IEnumerable<object[]> Kinds() =>
    [
        ["rgba8.png", 64, 32, true],
        ["rgb8-no-alpha.png", 64, 32, true],
        ["rgba16.png", 64, 32, true],
        ["palette.png", 64, 32, true],
        ["grey.png", 64, 32, false],
        ["grey-alpha.png", 64, 32, false],
        ["interlaced.png", 64, 32, true],
        ["gamma-and-profile.png", 64, 32, true],
        ["tiny.png", 1, 1, true],
        ["npot.png", 300, 200, true],
        ["odd.png", 333, 77, true],
        ["huge.png", 2048, 2048, true],
        ["cmyk.jpg", 64, 32, true],
        ["progressive.jpg", 8, 8, false],
        ["exif-turned.jpg", 32, 64, false],
        ["webp-named.png", 64, 32, true],
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryKindOfPictureBecomesAStickerOnTheCar(string file, int width, int height, bool redLeft)
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stickers");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var picture = Make(ctx.Combine("in"), file);

        Assert.True(page.ImportSticker(picture), string.Join(" | ", ctx.Services.Notifications.Toasts.Select(t => t.Message)));
        var kept = Path.Combine(ctx.Services.Projects.Current!.DirectoryPath, Assert.Single(page.StickerItems).StickerFile!);
        Assert.EndsWith(".png", kept, StringComparison.Ordinal);
        var info = Image.Identify(kept);
        var png = info.Metadata.GetPngMetadata();
        Assert.Equal((width, height, (PngColorType?)PngColorType.RgbWithAlpha, (PngBitDepth?)PngBitDepth.Bit8), (info.Width, info.Height, png.ColorType, png.BitDepth));
        output.WriteLine($"{file}: {new FileInfo(picture).Length} bytes -> {info.Width}x{info.Height}, {new FileInfo(kept).Length} bytes");

        // Its colours: the left half of the test picture is red (or grey), the right half clear or blue.
        using (var image = Image.Load<Rgba32>(kept))
        {
            var left = image[width / 4, height / 2];
            Assert.True(redLeft ? left.R > 200 && left.G < 60 && left.B < 60 && left.A == 255 : left.A == 255 && Math.Abs(left.R - left.G) < 8, $"{file}: left {left}");
        }

        // It is a sticker the car takes.
        var sticker = DecalLibrary.LoadSticker(kept);
        Assert.Equal((Math.Min(width, 1024), Math.Min(height, 1024)), (sticker.Width, sticker.Height));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        Assert.Equal(Path.GetFileName(kept), Path.GetFileName(page.Decals.Layers.Single().StickerFile));
    }

    [Fact]
    public async Task APictureThatCannotWorkSaysWhatIsWrongAndWhatToDo()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stickers");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var folder = Directory.CreateDirectory(ctx.Combine("bad")).FullName;

        var text = Path.Combine(folder, "notes.png");
        await File.WriteAllTextAsync(text, "not a picture");
        var cut = Path.Combine(folder, "cut.png");
        var whole = await File.ReadAllBytesAsync(Make(folder, "rgba8.png"));
        await File.WriteAllBytesAsync(cut, whole[..(whole.Length / 2)]);
        var clear = Path.Combine(folder, "clear.png");
        using (var image = new Image<Rgba32>(32, 32))
        {
            await image.SaveAsPngAsync(clear);
        }

        var wide = Path.Combine(folder, "wide.png");
        using (var image = new Image<L8>(20000, 4))
        {
            await image.SaveAsPngAsync(wide);
        }

        foreach (var (picture, words) in new[]
                 {
                     (text, "is not a picture the studio can read"),
                     (cut, "is damaged or cut short"),
                     (clear, "is fully transparent"),
                     (wide, "20000 x 4 pixels, more than the studio reads (8192 on a side)"),
                 })
        {
            Assert.False(page.ImportSticker(picture), picture);
            var toast = ctx.Services.Notifications.Toasts.Last();
            output.WriteLine(toast.Message);
            Assert.True(toast.IsError);
            Assert.Contains(Path.GetFileName(picture), toast.Message, StringComparison.Ordinal);
            Assert.Contains(words, toast.Message, StringComparison.Ordinal);
        }

        Assert.Empty(page.StickerItems);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(ctx.Services.Projects.Current!.DirectoryPath, "decals", "stickers")));
    }

    /// <summary>The Stickers grid shows each kept sticker (a CMYK photo or a 16-bit PNG too): its tile is the PNG the studio wrote.</summary>
    [AvaloniaFact]
    public async Task AKeptStickerShowsItsTileInTheGrid()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stickers");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        foreach (var file in new[] { "cmyk.jpg", "rgba16.png", "huge.png" })
        {
            Assert.True(page.ImportSticker(Make(ctx.Combine("in"), file)), file);
        }

        Assert.Equal(3, page.StickerItems.Count);
        foreach (var item in page.StickerItems)
        {
            _ = item.Thumbnail; // asks for it
        }

        Assert.True(HeadlessUi.PumpUntil(() => page.StickerItems.All(i => i.Thumbnail is not null), TimeSpan.FromSeconds(20)));
        Assert.All(page.StickerItems, i => Assert.True(i.Thumbnail!.PixelSize.Width <= 192 && i.Thumbnail.PixelSize.Height <= 192, $"{i.Thumbnail.PixelSize}"));
    }

    /// <summary>
    /// Writes the test picture <paramref name="file"/> into <paramref name="folder"/>: the left half red (grey for the grey
    /// kinds), the right half clear (blue where the format has no alpha).
    /// </summary>
    internal static string Make(string folder, string file)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        Image<Rgba32> Halves(int w, int h)
        {
            var image = new Image<Rgba32>(w, h);
            image.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = x < Math.Max(1, w / 2) ? new Rgba32(230, 20, 20, 255) : new Rgba32(20, 20, 230, 0);
                    }
                }
            });
            return image;
        }

        switch (file)
        {
            case "rgba8.png":
                using (var i = Halves(64, 32)) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha }); }
                break;
            case "rgb8-no-alpha.png":
                using (var i = Halves(64, 32)) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.Rgb }); }
                break;
            case "rgba16.png":
                using (var i = Halves(64, 32).CloneAs<Rgba64>()) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit16 }); }
                break;
            case "palette.png":
                using (var i = Halves(64, 32)) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit8 }); }
                break;
            case "grey.png":
                using (var i = Halves(64, 32).CloneAs<L8>()) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.Grayscale }); }
                break;
            case "grey-alpha.png":
                using (var i = Halves(64, 32).CloneAs<La16>()) { i.SaveAsPng(path, new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha }); }
                break;
            case "interlaced.png":
                using (var i = Halves(64, 32)) { i.SaveAsPng(path, new PngEncoder { InterlaceMethod = PngInterlaceMode.Adam7 }); }
                break;
            case "gamma-and-profile.png":
                using (var i = Halves(64, 32))
                {
                    i.Metadata.GetPngMetadata().Gamma = 0.45455f;
                    i.Metadata.IccProfile = new IccProfile(IccHeader());
                    i.SaveAsPng(path);
                }

                break;
            case "tiny.png":
                using (var i = Halves(1, 1)) { i.SaveAsPng(path); }
                break;
            case "npot.png":
                using (var i = Halves(300, 200)) { i.SaveAsPng(path); }
                break;
            case "odd.png":
                using (var i = Halves(333, 77)) { i.SaveAsPng(path); }
                break;
            case "huge.png":
                using (var i = Halves(8192, 8192)) { i.SaveAsPng(path, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed }); }
                break;
            case "cmyk.jpg":
                using (var i = Halves(64, 32)) { i.SaveAsJpeg(path, new JpegEncoder { ColorType = JpegEncodingColor.Cmyk, Quality = 95 }); }
                break;
            case "progressive.jpg":
                File.WriteAllBytes(path, ProgressiveGreyJpeg());
                break;
            case "exif-turned.jpg":
                // Stored 64 x 32 with a grey left half; EXIF orientation 6: the photo is shown turned a quarter, 32 x 64.
                using (var i = Halves(64, 32))
                {
                    i.Mutate(x => x.Grayscale());
                    i.Metadata.ExifProfile = new ExifProfile();
                    i.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                    i.SaveAsJpeg(path, new JpegEncoder { Quality = 95 });
                }

                break;
            case "webp-named.png":
                using (var i = Halves(64, 32)) { i.SaveAsWebp(path); }
                break;
            default:
                throw new ArgumentException(file, nameof(file));
        }

        return path;
    }

    /// <summary>A 132-byte ICC profile: an RGB display profile header with no tags.</summary>
    private static byte[] IccHeader()
    {
        var icc = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(icc, 132);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(8), 0x02100000);
        "mntrRGB XYZ "u8.CopyTo(icc.AsSpan(12));
        "acsp"u8.CopyTo(icc.AsSpan(36));
        return icc;
    }

    /// <summary>
    /// An 8 x 8 grey (200) progressive JPEG (SOF2), written by hand: ImageSharp only writes baseline. One DC scan (spectral
    /// selection 0..0, no successive approximation); the AC coefficients are zero, as a flat block's are.
    /// </summary>
    private static byte[] ProgressiveGreyJpeg()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        jpeg.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00, .. Enumerable.Repeat((byte)1, 64)]); // all quantizers 1
        jpeg.AddRange([0xFF, 0xC2, 0x00, 0x0B, 8, 0, 8, 0, 8, 1, 1, 0x11, 0]); // progressive DCT, 8 x 8, one component
        jpeg.AddRange([0xFF, 0xC4, 0x00, 0x14, 0x00, 1, .. new byte[15], 10]); // DC table: one code of length 1 for category 10
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x08, 1, 1, 0x00, 0, 0, 0x00]); // DC scan of component 1
        jpeg.AddRange([0x48, 0x1F]); // code 0, then 576 = (200 - 128) x 8 in 10 bits, padded with ones
        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }
}
