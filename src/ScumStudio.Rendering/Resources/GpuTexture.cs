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

    private GpuTexture(GL gl, uint handle, uint sampler, int width, int height, bool srgb, TextureWrap wrap)
    {
        _gl = gl;
        Handle = handle;
        Sampler = sampler;
        Width = width;
        Height = height;
        IsSrgb = srgb;
        Wrap = wrap;
    }

    /// <summary>Texture object name.</summary>
    public uint Handle { get; }

    /// <summary>Sampler object name (bound to the same unit by <see cref="Bind"/>).</summary>
    public uint Sampler { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>True when stored as <c>GL_SRGB8_ALPHA8</c> (colour); false for linear data (masks, normals).</summary>
    public bool IsSrgb { get; }

    /// <summary>Wrap mode used on both axes.</summary>
    public TextureWrap Wrap { get; }

    /// <summary>Approximate GPU memory of the texture in bytes (RGBA8 plus a third for the mip chain).</summary>
    public long EstimatedBytes => (long)Width * Height * 4 * 4 / 3;

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
