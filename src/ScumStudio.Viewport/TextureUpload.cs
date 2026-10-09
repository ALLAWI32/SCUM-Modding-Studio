using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;

namespace ScumStudio.Viewport;

/// <summary>Puts a <see cref="TextureImage"/> on the GPU: its cooked blocks as they are when it has them, else RGBA8.</summary>
public static class TextureUpload
{
    /// <summary>
    /// Uploads <paramref name="image"/> (render thread). A <see cref="TextureImage.CompressedMips"/> image the driver cannot
    /// take as it is gets its largest mip decoded to RGBA8 here.
    /// </summary>
    public static GpuTexture Create(SceneRenderer renderer, TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(image);
        if (image.CompressedMips is not { Count: > 0 } mips)
        {
            return renderer.CreateTexture(image.Width, image.Height, image.Rgba, image.IsSrgb);
        }

        var pixelFormat = Enum.Parse<EPixelFormat>(image.PixelFormat);
        var format = pixelFormat switch
        {
            EPixelFormat.PF_DXT1 => CompressedFormat.Bc1,
            EPixelFormat.PF_DXT3 => CompressedFormat.Bc2,
            EPixelFormat.PF_DXT5 => CompressedFormat.Bc3,
            EPixelFormat.PF_BC5 => CompressedFormat.Bc5,
            _ => CompressedFormat.Bc7,
        };
        return renderer.CreateCompressedTexture(format, image.Width, image.Height, mips, image.IsSrgb)
               ?? renderer.CreateTexture(image.Width, image.Height, TextureDecoder.DecodeRaw(mips[0], pixelFormat, image.Width, image.Height, image.IsNormalMap), image.IsSrgb);
    }
}
