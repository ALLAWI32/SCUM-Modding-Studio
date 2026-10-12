using Silk.NET.OpenGL;
using ScumStudio.Rendering.Gl;

namespace ScumStudio.Rendering.Targets;

/// <summary>
/// Resizable offscreen targets of one viewport: a colour framebuffer (RGBA8 colour holding sRGB-encoded values +
/// 32-bit float depth) and an ID framebuffer (R32UI pick codes + its own depth) for GPU picking. With more than one
/// sample the scene is drawn into multisampled buffers of the same formats and resolved into the colour texture when
/// it is read, copied or shown.
/// </summary>
/// <remarks>Create, use and dispose it on the thread where its GL context is current.</remarks>
public sealed class RenderTarget : IDisposable
{
    private readonly GL _gl;
    private uint _colorFbo;
    private uint _colorTexture;
    private uint _colorDepth;
    private uint _pickFbo;
    private uint _pickTexture;
    private uint _pickDepth;
    private uint _msaaFbo;
    private uint _msaaColor;
    private uint _msaaDepth;
    private int _requestedSamples;
    private bool _unresolved;
    private bool _disposed;

    /// <summary>
    /// Creates the targets at <paramref name="width"/> x <paramref name="height"/> pixels with <paramref name="samples"/>
    /// samples per pixel (rounded down to a power of two and to what the driver offers; 1 = no multisampling).
    /// </summary>
    public RenderTarget(GL gl, int width, int height, int samples = 1)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _requestedSamples = Math.Max(1, samples);
        Allocate(width, height);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Samples per pixel the scene is drawn with: 1, or the multisample count the driver accepted.</summary>
    public int Samples { get; private set; } = 1;

    /// <summary>Colour framebuffer object the scene is drawn into (the multisampled one when <see cref="Samples"/> &gt; 1).</summary>
    public uint ColorFramebuffer => _msaaFbo != 0 ? _msaaFbo : _colorFbo;

    /// <summary>Colour texture (RGBA8, sRGB-encoded values; can be sampled by a host UI); a multisampled frame is resolved into it first.</summary>
    public uint ColorTexture
    {
        get
        {
            Resolve();
            return _colorTexture;
        }
    }

    /// <summary>ID framebuffer object.</summary>
    public uint PickFramebuffer => _pickFbo;

    /// <summary>Width / height.</summary>
    public float AspectRatio => (float)Width / Height;

    /// <summary>
    /// Re-allocates the attachments when the size or the requested <paramref name="samples"/> (null keeps them) changed
    /// (sizes below 1 are clamped to 1, so a collapsed host control is harmless). Returns true when it did.
    /// </summary>
    public bool Resize(int width, int height, int? samples = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var requested = Math.Max(1, samples ?? _requestedSamples);
        if (width == Width && height == Height && requested == _requestedSamples)
        {
            return false;
        }

        Release();
        _requestedSamples = requested;
        Allocate(width, height);
        return true;
    }

    /// <summary>Binds the colour framebuffer and sets the viewport to the full target.</summary>
    public void BindColor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, ColorFramebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        _unresolved = _msaaFbo != 0;
    }

    /// <summary>
    /// Resolves what was drawn into the multisampled buffers since the last resolve into <see cref="ColorTexture"/> (each
    /// pixel the average of its samples). Nothing to do with one sample; leaves framebuffer 0 bound when it resolved.
    /// </summary>
    public void Resolve()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_unresolved)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msaaFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _colorFbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _unresolved = false;
    }

    /// <summary>Binds the ID framebuffer and sets the viewport to the full target.</summary>
    public void BindPick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pickFbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    /// <summary>
    /// Reads the colour attachment as tightly packed RGBA8 rows, top row first (ready for PNG encoding).
    /// </summary>
    public unsafe byte[] ReadColorRgba()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Resolve();
        var rowBytes = Width * 4;
        var bottomUp = new byte[rowBytes * Height];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _colorFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = bottomUp)
        {
            _gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        var topDown = new byte[bottomUp.Length];
        for (var y = 0; y < Height; y++)
        {
            System.Buffer.BlockCopy(bottomUp, (Height - 1 - y) * rowBytes, topDown, y * rowBytes, rowBytes);
        }

        return topDown;
    }

    /// <summary>
    /// Reads one pixel of the ID framebuffer. <paramref name="x"/>/<paramref name="y"/> are window coordinates with the
    /// origin at the top-left (as reported by UI toolkits).
    /// </summary>
    /// <returns>The pick code (0 = nothing) and the window-space depth in [0, 1].</returns>
    public unsafe (uint PickCode, float Depth) ReadPick(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Pixel ({x}, {y}) is outside the {Width}x{Height} target.");
        }

        var glY = Height - 1 - y;
        uint code = 0;
        float depth = 0;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _pickFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        _gl.ReadPixels(x, glY, 1, 1, PixelFormat.RedInteger, PixelType.UnsignedInt, &code);
        _gl.ReadPixels(x, glY, 1, 1, PixelFormat.DepthComponent, PixelType.Float, &depth);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        return (code, depth);
    }

    /// <summary>
    /// Reads pick codes and depths of a rectangle of the ID buffer (top-left origin): row by row from the top, left to right.
    /// </summary>
    public unsafe (uint[] PickCodes, float[] Depths) ReadPickRegion(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Rectangle ({x}, {y}, {width}x{height}) is outside the {Width}x{Height} target.");
        }

        var glCodes = new uint[width * height];
        var glDepths = new float[width * height];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _pickFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (uint* codes = glCodes)
        fixed (float* depths = glDepths)
        {
            _gl.ReadPixels(x, Height - y - height, (uint)width, (uint)height, PixelFormat.RedInteger, PixelType.UnsignedInt, codes);
            _gl.ReadPixels(x, Height - y - height, (uint)width, (uint)height, PixelFormat.DepthComponent, PixelType.Float, depths);
        }

        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);

        // GL rows run bottom-up; flip them to top-down.
        var outCodes = new uint[glCodes.Length];
        var outDepths = new float[glDepths.Length];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(glCodes, row * width, outCodes, (height - 1 - row) * width, width);
            Array.Copy(glDepths, row * width, outDepths, (height - 1 - row) * width, width);
        }

        return (outCodes, outDepths);
    }

    /// <summary>
    /// Copies the colour attachment to <paramref name="destinationFramebuffer"/> (e.g. the framebuffer an Avalonia
    /// <c>OpenGlControlBase</c> passes to <c>OnOpenGlRender</c>), scaling to <paramref name="width"/> x <paramref name="height"/>.
    /// </summary>
    public void BlitTo(uint destinationFramebuffer, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Resolve();
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _colorFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destinationFramebuffer);
        var filter = width == Width && height == Height ? BlitFramebufferFilter.Nearest : BlitFramebufferFilter.Linear;
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, width, height, ClearBufferMask.ColorBufferBit, filter);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, destinationFramebuffer);
    }

    /// <summary>Deletes all attachments and framebuffers.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Release();
        }
    }

    private unsafe void Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target size must be positive (got {width}x{height}).");
        }

        Width = width;
        Height = height;
        _colorTexture = CreateTexture(InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        _colorDepth = CreateTexture(InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float);
        _colorFbo = CreateFramebuffer(_colorTexture, _colorDepth, "colour");
        _pickTexture = CreateTexture(InternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt);
        _pickDepth = CreateTexture(InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float);
        _pickFbo = CreateFramebuffer(_pickTexture, _pickDepth, "pick");
        Samples = 1;
        var samples = _requestedSamples > 1 ? Math.Min(_requestedSamples, _gl.GetInteger((GetPName)0x8D57)) : 1; // GL_MAX_SAMPLES
        if (samples > 1)
        {
            AllocateMultisampled(1 << System.Numerics.BitOperations.Log2((uint)samples));
        }
    }

    /// <summary>
    /// The multisampled draw buffers: the same formats as the colour framebuffer, so the water's copy of colour and depth
    /// stays one blit. A driver that refuses them leaves the target at one sample.
    /// </summary>
    private void AllocateMultisampled(int samples)
    {
        GlErrors.Drain(_gl); // errors other code left on the shared context are not this allocation's
        _msaaColor = Renderbuffer(InternalFormat.Rgba8);
        _msaaDepth = Renderbuffer(InternalFormat.DepthComponent32f);
        _msaaFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msaaColor);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _msaaDepth);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        var complete = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var errors = GlErrors.Drain(_gl); // always, so a refused allocation leaves no error for the next pass's check
        if (complete && errors.Count == 0)
        {
            Samples = samples;
            return;
        }

        System.Diagnostics.Debug.WriteLine($"RenderTarget: {samples} samples refused ({(complete ? string.Join(", ", errors) : "incomplete framebuffer")}); drawing with one sample.");
        ReleaseMultisampled();

        uint Renderbuffer(InternalFormat format)
        {
            var buffer = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, buffer);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, format, (uint)Width, (uint)Height);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            return buffer;
        }
    }

    private void ReleaseMultisampled()
    {
        if (_msaaFbo != 0)
        {
            _gl.DeleteFramebuffer(_msaaFbo);
            _gl.DeleteRenderbuffer(_msaaColor);
            _gl.DeleteRenderbuffer(_msaaDepth);
        }

        _msaaFbo = _msaaColor = _msaaDepth = 0;
        _unresolved = false;
    }

    private unsafe uint CreateTexture(InternalFormat internalFormat, PixelFormat format, PixelType type)
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)Width, (uint)Height, 0, format, type, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private uint CreateFramebuffer(uint color, uint depth, string what)
    {
        var fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"The {what} framebuffer ({Width}x{Height}) is incomplete: {status}.");
        }

        return fbo;
    }

    private void Release()
    {
        _gl.DeleteFramebuffer(_colorFbo);
        _gl.DeleteFramebuffer(_pickFbo);
        _gl.DeleteTexture(_colorTexture);
        _gl.DeleteTexture(_colorDepth);
        _gl.DeleteTexture(_pickTexture);
        _gl.DeleteTexture(_pickDepth);
        _colorFbo = _pickFbo = _colorTexture = _colorDepth = _pickTexture = _pickDepth = 0;
        ReleaseMultisampled();
    }
}
