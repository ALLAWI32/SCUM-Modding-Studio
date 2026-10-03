using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Gl;

/// <summary>glGetError helpers (the renderer checks errors at pass boundaries, not per call).</summary>
public static class GlErrors
{
    /// <summary>Drains the error queue and returns the errors (empty when none).</summary>
    public static IReadOnlyList<GLEnum> Drain(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        List<GLEnum>? errors = null;
        for (var i = 0; i < 32; i++)
        {
            var error = gl.GetError();
            if (error == GLEnum.NoError)
            {
                break;
            }

            (errors ??= []).Add(error);
        }

        return errors ?? (IReadOnlyList<GLEnum>)Array.Empty<GLEnum>();
    }

    /// <summary>Throws when the error queue is not empty.</summary>
    /// <exception cref="InvalidOperationException">A GL error was recorded.</exception>
    public static void Check(GL gl, string where)
    {
        var errors = Drain(gl);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"OpenGL error after {where}: {string.Join(", ", errors)}");
        }
    }
}
