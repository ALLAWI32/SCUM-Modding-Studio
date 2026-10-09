using Silk.NET.OpenGL;
using ScumStudio.Rendering.Gl;

namespace ScumStudio.Rendering.Resources;

/// <summary>How a texture is sampled outside the 0..1 UV range.</summary>
public enum TextureWrap
{
    /// <summary>Tile the texture (meshes with tiling UVs; the default).</summary>
    Repeat,

    /// <summary>Clamp to the edge texels (per-tile textures such as terrain albedo: no bleeding from the opposite edge).</summary>
    ClampToEdge,

    /// <summary>Tile the texture, mirroring every second repeat.</summary>
    MirroredRepeat,
}

/// <summary>
/// A 2D RGBA8 texture (sRGB-decoded on sampling when created as colour data), with mipmaps, plus its own sampler object
/// (wrap mode, trilinear filtering and <see cref="MaxAnisotropy"/>) that <see cref="Bind"/> binds alongside it.
/// </summary>
/// <remarks>
/// The sampling state lives on a sampler object rather than on the texture: the NVIDIA driver (616.92, GL 4.3 core)
/// accepts <c>GL_TEXTURE_MAX_ANISOTROPY</c> on a texture without error but keeps it at 1, while a sampler honours it.
/// </remarks>
public sealed class GpuTexture : IDisposable
{
    /// <summary>Anisotropic filtering level asked for on mipmapped textures (clamped to what the driver allows).</summary>
    public const float MaxAnisotropy = 16f;

    private readonly GL _gl;
    private bool _disposed;

    private GpuTexture(GL gl, uint handle, uint sampler, int width, int height, bool srgb, TextureWrap wrap, long bytes = 0)
    {
        _gl = gl;
        EstimatedBytes = bytes > 0 ? bytes : (long)width * height * 4 * 4 / 3;
        Handle = handle;
        Sampler = sampler;
        Width = width;
        Height = height;
        IsSrgb = srgb;
        Wrap = wrap;
    }

    /// <summary>Texture object name.</summary>
    public uint Handle { get; private set; }

    /// <summary>Sampler object name (bound to the same unit by <see cref="Bind"/>).</summary>
    public uint Sampler { get; private set; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>True when stored as <c>GL_SRGB8_ALPHA8</c> (colour); false for linear data (masks, normals).</summary>
    public bool IsSrgb { get; private set; }

    /// <summary>Wrap mode used on both axes.</summary>
    public TextureWrap Wrap { get; private set; }

    /// <summary>Approximate GPU memory of the texture in bytes (RGBA8 plus a third for the mip chain, or the compressed mips as uploaded).</summary>
    public long EstimatedBytes { get; private set; }

    /// <summary>Bytes of one 4x4 block of <paramref name="format"/>.</summary>
    public static int BlockBytes(CompressedFormat format) => format == CompressedFormat.Bc1 ? 8 : 16;

    /// <summary>Bytes of a <paramref name="width"/> x <paramref name="height"/> mip of <paramref name="format"/>.</summary>
    public static int CompressedSize(CompressedFormat format, int width, int height) =>
        Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * BlockBytes(format);

    /// <summary>
    /// Uploads block-compressed mips as they are (no decode, a quarter to an eighth of RGBA8's memory): <paramref name="mips"/>
    /// from the largest (<paramref name="width"/> x <paramref name="height"/>) down, each halving, with trilinear and
    /// anisotropic filtering over them. BC1-BC3 need <see cref="Context.GlInfo.SupportsS3tc"/>.
    /// </summary>
    public static unsafe GpuTexture FromCompressed(GL gl, CompressedFormat format, int width, int height, IReadOnlyList<byte[]> mips, bool srgb)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(mips);
        if (width <= 0 || height <= 0 || mips.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture size must be positive and there must be a mip.");
        }

        srgb &= format is not CompressedFormat.Bc5; // two-channel data (normal maps) has no sRGB form
        var internalFormat = (InternalFormat)(format switch
        {
            CompressedFormat.Bc1 => srgb ? 0x8C4C : 0x83F0, // (S)RGB_S3TC_DXT1: no alpha, so a stray transparent block cannot cut holes
            CompressedFormat.Bc2 => srgb ? 0x8C4E : 0x83F2,
            CompressedFormat.Bc3 => srgb ? 0x8C4F : 0x83F3,
            CompressedFormat.Bc5 => 0x8DBD, // RG_RGTC2
            _ => srgb ? 0x8E8D : 0x8E8C, // (SRGB_ALPHA_)BPTC_UNORM
        });
        var handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);
        var levels = 0;
        long bytes = 0;
        for (var level = 0; level < mips.Count; level++)
        {
            var (w, h) = (Math.Max(1, width >> level), Math.Max(1, height >> level));
            var size = CompressedSize(format, w, h);
            if (mips[level].Length < size)
            {
                break; // a short mip ends the chain; the levels above it still draw
            }

            fixed (byte* p = mips[level])
            {
                gl.CompressedTexImage2D(TextureTarget.Texture2D, level, internalFormat, (uint)w, (uint)h, 0, (uint)size, p);
            }

            bytes += size;
            levels++;
            if (w == 1 && h == 1)
            {
                break;
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, Math.Max(0, levels - 1));
        gl.BindTexture(TextureTarget.Texture2D, 0);
        var minFilter = levels > 1 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
        var sampler = gl.GenSampler();
        gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
        gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);
        gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
        gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)minFilter);
        ApplyAnisotropy(gl, sampler);
        return new GpuTexture(gl, handle, sampler, width, height, srgb, TextureWrap.Repeat, bytes);
    }

    /// <summary>
    /// Uploads tightly packed RGBA8 pixels (row-major, first row = top of the image, i.e. UV v = 0 as in UE and glTF).
    /// </summary>
    /// <param name="gl">GL API.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="rgba">Pixel data, <c>width * height * 4</c> bytes.</param>
    /// <param name="srgb">Colour data (decoded from sRGB to linear by the sampler) or linear data.</param>
    /// <param name="mipmaps">Generate a full mip chain and use trilinear, anisotropic filtering.</param>
    /// <param name="wrap">Wrap mode on both axes (<see cref="TextureWrap.ClampToEdge"/> for per-tile textures such as terrain).</param>
    public static unsafe GpuTexture FromRgba8(GL gl, int width, int height, ReadOnlySpan<byte> rgba, bool srgb = true, bool mipmaps = true,
        TextureWrap wrap = TextureWrap.Repeat)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture size must be positive.");
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8, got {rgba.Length}.", nameof(rgba));
        }

        var handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        var mode = wrap switch
        {
            TextureWrap.ClampToEdge => TextureWrapMode.ClampToEdge,
            TextureWrap.MirroredRepeat => TextureWrapMode.MirroredRepeat,
            _ => TextureWrapMode.Repeat,
        };
        var minFilter = mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)mode);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)mode);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
        if (mipmaps)
        {
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        var sampler = gl.GenSampler();
        gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)mode);
        gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)mode);
        gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
        gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)minFilter);
        if (mipmaps)
        {
            ApplyAnisotropy(gl, sampler);
        }

        return new GpuTexture(gl, handle, sampler, width, height, srgb, wrap);
    }

    /// <summary>A 1x1 texture of one colour.</summary>
    public static GpuTexture Solid(GL gl, byte r, byte g, byte b, byte a = 255, bool srgb = true) =>
        FromRgba8(gl, 1, 1, [r, g, b, a], srgb, mipmaps: false);

    /// <summary>Binds the texture and its sampler to texture unit <paramref name="unit"/>.</summary>
    public void Bind(int unit = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.BindSampler((uint)unit, Sampler);
    }

    /// <summary>Unbinds any sampler from texture unit <paramref name="unit"/> (hosts binding their own textures expect none).</summary>
    public static void UnbindSampler(GL gl, int unit = 0)
    {
        ArgumentNullException.ThrowIfNull(gl);
        gl.BindSampler((uint)unit, 0);
    }

    /// <summary>
    /// Becomes <paramref name="sharper"/> in place (its GL texture, sampler and size) and frees its own old GL texture with
    /// <paramref name="sharper"/>, which is disposed. Every mesh, material and cache holding this object draws the sharper
    /// image at once and none is left with a deleted texture (a copy swapped and deleted threw "Cannot access a disposed
    /// object" from a list still holding it).
    /// </summary>
    public void TakeOver(GpuTexture sharper)
    {
        ArgumentNullException.ThrowIfNull(sharper);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(sharper._disposed, sharper);
        (Handle, sharper.Handle) = (sharper.Handle, Handle);
        (Sampler, sharper.Sampler) = (sharper.Sampler, Sampler);
        (Width, sharper.Width) = (sharper.Width, Width);
        (Height, sharper.Height) = (sharper.Height, Height);
        (IsSrgb, sharper.IsSrgb) = (sharper.IsSrgb, IsSrgb);
        (Wrap, sharper.Wrap) = (sharper.Wrap, Wrap);
        (EstimatedBytes, sharper.EstimatedBytes) = (sharper.EstimatedBytes, EstimatedBytes);
        sharper.Dispose();
    }

    /// <summary>Deletes the texture and its sampler.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gl.DeleteTexture(Handle);
            _gl.DeleteSampler(Sampler);
        }
    }

    /// <summary>
    /// Sets <see cref="MaxAnisotropy"/> on <paramref name="sampler"/> (<c>GL_EXT_texture_filter_anisotropic</c>, core since
    /// 4.6; silently skipped on drivers without it). Keeps ground and wall textures sharp at grazing angles.
    /// </summary>
    private static void ApplyAnisotropy(GL gl, uint sampler)
    {
        const GetPName maxTextureMaxAnisotropy = (GetPName)0x84FF;
        const SamplerParameterF textureMaxAnisotropy = (SamplerParameterF)0x84FE;
        GlErrors.Drain(gl);
        var supported = gl.GetFloat(maxTextureMaxAnisotropy);
        if (GlErrors.Drain(gl).Count == 0 && supported >= 2f)
        {
            gl.SamplerParameter(sampler, textureMaxAnisotropy, MathF.Min(supported, MaxAnisotropy));
            GlErrors.Drain(gl);
        }
    }
}
