using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Context;

/// <summary>
/// An OpenGL 4.3 core context the renderer draws with. The renderer never creates windows itself: the Avalonia
/// viewport wraps its <c>OpenGlControlBase</c> context (see <see cref="ProcAddressGlContext"/>), tests and the CLI use
/// <see cref="OffscreenGlContext"/>.
/// </summary>
/// <remarks>All GL calls must happen on the thread the context is current on.</remarks>
public interface IGlContext
{
    /// <summary>The Silk.NET function table bound to this context.</summary>
    GL Gl { get; }

    /// <summary>Current drawable size in pixels (the size the host wants the next frame rendered at).</summary>
    (int W, int H) Size { get; }

    /// <summary>Makes the context current on the calling thread.</summary>
    void MakeCurrent();
}
