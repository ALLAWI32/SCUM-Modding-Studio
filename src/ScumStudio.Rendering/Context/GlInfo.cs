using System.Globalization;
using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Context;

/// <summary>Driver strings and the capabilities the renderer cares about.</summary>
/// <param name="Vendor">GL_VENDOR.</param>
/// <param name="Renderer">GL_RENDERER (e.g. <c>llvmpipe</c>, a GPU name).</param>
/// <param name="Version">GL_VERSION string.</param>
/// <param name="Major">Major version (GL_MAJOR_VERSION).</param>
/// <param name="Minor">Minor version (GL_MINOR_VERSION).</param>
/// <param name="SupportsClipControl">True when <c>glClipControl</c> is available (GL 4.5 or ARB_clip_control), which enables reverse-Z depth.</param>
public sealed record GlInfo(string Vendor, string Renderer, string Version, int Major, int Minor, bool SupportsClipControl)
{
    /// <summary>True when the context version is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    public bool IsAtLeast(int major, int minor) => Major > major || (Major == major && Minor >= minor);

    /// <summary>Queries the current context.</summary>
    public static GlInfo Query(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        var vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
        var renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
        var version = gl.GetStringS(StringName.Version) ?? string.Empty;
        var major = gl.GetInteger(GetPName.MajorVersion);
        var minor = gl.GetInteger(GetPName.MinorVersion);
        if (major == 0)
        {
            // Pre-3.0 drivers do not know GL_MAJOR_VERSION; parse "M.m ..." instead.
            var parts = version.Split(new[] { '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                _ = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
                _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
            }
        }

        var clipControl = major > 4 || (major == 4 && minor >= 5) || HasExtension(gl, "GL_ARB_clip_control");
        return new GlInfo(vendor, renderer, version, major, minor, clipControl);
    }

    /// <summary>True when the current context advertises <paramref name="name"/>.</summary>
    public static bool HasExtension(GL gl, string name)
    {
        var count = gl.GetInteger(GetPName.NumExtensions);
        for (var i = 0u; i < (uint)count; i++)
        {
            if (string.Equals(gl.GetStringS(StringName.Extensions, i), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Renderer} ({Vendor}), OpenGL {Major}.{Minor}: {Version}";
}
