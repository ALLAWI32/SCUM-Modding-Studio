namespace ScumStudio.Assets.Textures;

/// <summary>A decoded texture mip as tightly packed 8-bit RGBA pixels (row-major, top row first).</summary>
/// <param name="Name">Texture asset name.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgba">Pixel data, <c>Width * Height * 4</c> bytes in R, G, B, A order.</param>
/// <param name="PixelFormat">Cooked pixel format the data was decoded from (e.g. <c>PF_DXT1</c>).</param>
/// <param name="MipIndex">Index of the decoded mip in the texture's mip chain (0 = largest).</param>
/// <param name="SourceWidth">Width of mip 0 (the full-size texture).</param>
/// <param name="SourceHeight">Height of mip 0.</param>
/// <param name="IsSrgb">True when the texture is sRGB (colour); false for linear data (normals, masks).</param>
/// <param name="IsNormalMap">True for <c>TC_Normalmap</c> textures (BC5: blue reconstructed from red/green).</param>
public sealed record TextureImage(
    string Name,
    int Width,
    int Height,
    byte[] Rgba,
    string PixelFormat,
    int MipIndex,
    int SourceWidth,
    int SourceHeight,
    bool IsSrgb,
    bool IsNormalMap)
{
    /// <summary>
    /// The cooked block-compressed mips from <see cref="MipIndex"/> down (largest first), for an upload without decoding
    /// (<see cref="TextureDecoder.DecodeForGpu"/>); <see cref="Rgba"/> is then empty. Null for decoded images.
    /// </summary>
    public IReadOnlyList<byte[]>? CompressedMips { get; init; }
}

/// <summary>Mip chain entry of a texture (sizes only).</summary>
/// <param name="Index">Mip index (0 = largest).</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="HasData">True when the mip's data is available (inline or in a loaded <c>.ubulk</c>).</param>
public sealed record TextureMipInfo(int Index, int Width, int Height, bool HasData);

/// <summary>Summary of a <c>UTexture2D</c>.</summary>
/// <param name="Name">Asset name.</param>
/// <param name="Width">Width of the first mip.</param>
/// <param name="Height">Height of the first mip.</param>
/// <param name="PixelFormat">Cooked pixel format, e.g. <c>PF_DXT1</c>, <c>PF_BC5</c>.</param>
/// <param name="CompressionSettings">UE compression settings (e.g. <c>TC_Default</c>, <c>TC_Normalmap</c>, <c>TC_Masks</c>).</param>
/// <param name="IsSrgb">sRGB flag.</param>
/// <param name="Mips">Mip chain.</param>
public sealed record TextureInfo(string Name, int Width, int Height, string PixelFormat, string CompressionSettings, bool IsSrgb, IReadOnlyList<TextureMipInfo> Mips);
