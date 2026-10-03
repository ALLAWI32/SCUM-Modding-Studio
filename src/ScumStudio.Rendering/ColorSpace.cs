namespace ScumStudio.Rendering;

/// <summary>sRGB transfer functions (IEC 61966-2-1), matching the shaders.</summary>
public static class ColorSpace
{
    /// <summary>Encodes a linear value in [0, 1] to sRGB.</summary>
    public static float LinearToSrgb(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
    }

    /// <summary>Decodes an sRGB value in [0, 1] to linear.</summary>
    public static float SrgbToLinear(float srgb)
    {
        var c = Math.Clamp(srgb, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Encodes a linear value to an 8-bit sRGB byte.</summary>
    public static byte LinearToSrgbByte(float linear) => (byte)MathF.Round(LinearToSrgb(linear) * 255f);
}
