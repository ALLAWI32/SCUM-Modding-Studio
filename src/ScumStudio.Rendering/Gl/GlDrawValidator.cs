using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Gl;

/// <summary>
/// Checks the GL state a draw is about to use the way a strict driver does (D03: AMD cards crash where NVIDIA lets things
/// through): every sampler the current program uses must find a complete texture of its kind on its unit, a shadow sampler
/// a depth texture compared against the reference, a plain sampler a texture that is not compared, and samplers of two
/// kinds never share a unit. Vendor-neutral (it reads the state back), so it proves the same on any GPU; slow, for tests.
/// </summary>
internal static class GlDrawValidator
{
    private const GLEnum ActiveUniforms = (GLEnum)0x8B86;
    private const GLEnum Sampler2D = (GLEnum)0x8B5E;
    private const GLEnum Sampler2DShadow = (GLEnum)0x8B62;
    private const GLEnum CompareRefToTexture = (GLEnum)0x884E;

    /// <summary>The problems of the current program's samplers with what is bound now (empty when the draw is sound).</summary>
    public static IReadOnlyList<string> Check(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        var problems = new List<string>();
        var program = (uint)gl.GetInteger((GetPName)0x8B8D); // GL_CURRENT_PROGRAM
        if (program == 0)
        {
            return ["no program bound"];
        }

        var activeUnit = gl.GetInteger((GetPName)0x84E0); // GL_ACTIVE_TEXTURE
        gl.GetProgram(program, ActiveUniforms, out var count);
        var kindOfUnit = new Dictionary<int, (GLEnum Kind, string Name)>();
        for (var i = 0u; i < (uint)count; i++)
        {
            var name = gl.GetActiveUniform(program, i, out var size, out UniformType type);
            var kind = (GLEnum)type;
            if (kind != Sampler2D && kind != Sampler2DShadow)
            {
                continue;
            }

            gl.GetUniform(program, gl.GetUniformLocation(program, name), out int unit);
            if (kindOfUnit.TryGetValue(unit, out var other) && other.Kind != kind)
            {
                problems.Add($"{name} and {other.Name} are samplers of different kinds on unit {unit}");
            }

            kindOfUnit[unit] = (kind, name);
            gl.ActiveTexture(TextureUnit.Texture0 + unit);
            var texture = gl.GetInteger((GetPName)0x8069); // GL_TEXTURE_BINDING_2D
            if (texture == 0)
            {
                problems.Add($"{name} (unit {unit}) has no texture bound");
                continue;
            }

            gl.GetTexLevelParameter(TextureTarget.Texture2D, 0, (GetTextureParameter)0x1000, out int width); // GL_TEXTURE_WIDTH
            gl.GetTexLevelParameter(TextureTarget.Texture2D, 0, (GetTextureParameter)0x1003, out int format); // GL_TEXTURE_INTERNAL_FORMAT
            if (width <= 0)
            {
                problems.Add($"{name} (unit {unit}) has texture {texture} without a level 0 (incomplete)");
                continue;
            }

            var sampler = (uint)gl.GetInteger((GetPName)0x8919); // GL_SAMPLER_BINDING
            int compare;
            if (sampler != 0)
            {
                gl.GetSamplerParameter(sampler, SamplerParameterI.CompareMode, out compare);
            }
            else
            {
                gl.GetTexParameter(TextureTarget.Texture2D, (GetTextureParameter)0x884C, out compare); // GL_TEXTURE_COMPARE_MODE
            }

            var depth = IsDepthFormat(format);
            var compared = (GLEnum)compare == CompareRefToTexture;
            if (kind == Sampler2DShadow && (!depth || !compared))
            {
                problems.Add($"{name} (unit {unit}) is a shadow sampler on texture {texture} ({(depth ? "depth" : $"format 0x{format:X}")}, {(compared ? "compared" : "not compared")})");
            }
            else if (kind == Sampler2D && depth && compared)
            {
                problems.Add($"{name} (unit {unit}) is a plain sampler on depth texture {texture} that is compared");
            }
        }

        gl.ActiveTexture((TextureUnit)activeUnit);
        return problems;
    }

    private static bool IsDepthFormat(int format) => format is 0x1902 or 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC or 0x88F0 or 0x8CAD;
}
