using System.Numerics;
using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Gl;

/// <summary>A linked vertex + fragment GLSL program with a uniform location cache.</summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Compiles and links <paramref name="vertexSource"/> and <paramref name="fragmentSource"/>.</summary>
    /// <exception cref="InvalidOperationException">Compilation or linking failed (the message contains the driver log).</exception>
    public ShaderProgram(GL gl, string name, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Name = name;
        var vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs;
        try
        {
            fs = Compile(ShaderType.FragmentShader, fragmentSource);
        }
        catch
        {
            _gl.DeleteShader(vs);
            throw;
        }

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);
        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(Handle);
            _gl.DeleteProgram(Handle);
            throw new InvalidOperationException($"Linking shader program '{name}' failed: {log}");
        }
    }

    /// <summary>Program object name.</summary>
    public uint Handle { get; }

    /// <summary>Debug name.</summary>
    public string Name { get; }

    /// <summary>Binds the program.</summary>
    public void Use() => _gl.UseProgram(Handle);

    /// <summary>Uniform location (cached; -1 when the uniform is inactive or missing).</summary>
    public int Location(string uniform)
    {
        if (!_locations.TryGetValue(uniform, out var location))
        {
            location = _gl.GetUniformLocation(Handle, uniform);
            _locations[uniform] = location;
        }

        return location;
    }

    /// <summary>
    /// Sets a mat4 uniform from a row-vector <see cref="Matrix4x4"/> (uploaded untransposed, which is the column-vector
    /// matrix GLSL expects; see <c>UeToGl</c>).
    /// </summary>
    public unsafe void Set(string uniform, in Matrix4x4 value)
    {
        var location = Location(uniform);
        if (location < 0)
        {
            return;
        }

        fixed (Matrix4x4* p = &value)
        {
            _gl.UniformMatrix4(location, 1, false, (float*)p);
        }
    }

    /// <summary>Sets a vec4 uniform.</summary>
    public void Set(string uniform, Vector4 value)
    {
        var location = Location(uniform);
        if (location >= 0)
        {
            _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);
        }
    }

    /// <summary>Sets a vec2 uniform.</summary>
    public void Set(string uniform, Vector2 value)
    {
        var location = Location(uniform);
        if (location >= 0)
        {
            _gl.Uniform2(location, value.X, value.Y);
        }
    }

    /// <summary>Sets a vec3 uniform.</summary>
    public void Set(string uniform, Vector3 value)
    {
        var location = Location(uniform);
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    /// <summary>Sets a float uniform.</summary>
    public void Set(string uniform, float value)
    {
        var location = Location(uniform);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    /// <summary>Sets an int / bool / sampler uniform.</summary>
    public void Set(string uniform, int value)
    {
        var location = Location(uniform);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    /// <summary>Deletes the program.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gl.DeleteProgram(Handle);
        }
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"Compiling {type} of '{Name}' failed: {log}");
        }

        return shader;
    }
}
