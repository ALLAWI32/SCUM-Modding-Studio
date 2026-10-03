using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Context;

/// <summary>
/// Adapts a context owned by someone else (e.g. Avalonia's <c>OpenGlControlBase</c>, which exposes
/// <c>GlInterface.GetProcAddress</c>) to <see cref="IGlContext"/>. The host is responsible for making the context
/// current before it calls into the renderer; <see cref="MakeCurrent"/> only invokes the optional callback.
/// </summary>
public sealed class ProcAddressGlContext : IGlContext
{
    private readonly Func<(int W, int H)> _size;
    private readonly Action? _makeCurrent;

    /// <summary>Creates the adapter.</summary>
    /// <param name="getProcAddress">Resolves GL entry points by name (e.g. <c>GlInterface.GetProcAddress</c>).</param>
    /// <param name="size">Returns the current drawable size in pixels.</param>
    /// <param name="makeCurrent">Optional callback that makes the host context current.</param>
    public ProcAddressGlContext(Func<string, nint> getProcAddress, Func<(int W, int H)> size, Action? makeCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        _size = size ?? throw new ArgumentNullException(nameof(size));
        _makeCurrent = makeCurrent;
        Gl = GL.GetApi(getProcAddress);
    }

    /// <inheritdoc />
    public GL Gl { get; }

    /// <inheritdoc />
    public (int W, int H) Size => _size();

    /// <inheritdoc />
    public void MakeCurrent() => _makeCurrent?.Invoke();
}
