using System.Numerics;
using System.Text.RegularExpressions;

namespace ScumStudio.Rendering.Context;

/// <summary>A drawing feature the renderer can do without when a driver refuses or mishandles it.</summary>
public enum GraphicsFeature
{
    /// <summary>Multisampled render targets (anti-aliased edges and foliage).</summary>
    Multisampling,

    /// <summary>Alpha to coverage: masked leaves cut by the share of samples they cover instead of a hard step.</summary>
    AlphaToCoverage,

    /// <summary>Block-compressed (BC1-BC7) texture uploads; without them textures are decoded to RGBA8 first.</summary>
    CompressedTextures,
}

/// <summary>
/// The features the renderers of this process do without, and why. A renderer turns one off when the driver refuses it
/// (a GL error, an incomplete framebuffer) and draws on without it; the viewport turns them off after a frame fails. One
/// object can be shared by every renderer on the same GPU. Thread-safe.
/// </summary>
/// <remarks>
/// Added for D03 (AMD cards crashed in 3.2.0, whose multisampled foliage and blits were tested on an RTX only). Tests turn
/// features off up front to run the fallback paths on any GPU.
/// </remarks>
public sealed class GraphicsFallbacks
{
    private readonly object _gate = new();
    private readonly Dictionary<GraphicsFeature, string> _off = [];

    /// <summary>Raised (on the thread that turned it off) the first time a feature is turned off, with the reason.</summary>
    public event Action<GraphicsFeature, string>? TurnedOff;

    /// <summary>True when <paramref name="feature"/> is turned off.</summary>
    public bool IsOff(GraphicsFeature feature)
    {
        lock (_gate)
        {
            return _off.ContainsKey(feature);
        }
    }

    /// <summary>Turns <paramref name="feature"/> off for good; true the first time (the reason is kept and reported).</summary>
    public bool TurnOff(GraphicsFeature feature, string reason)
    {
        lock (_gate)
        {
            if (!_off.TryAdd(feature, reason))
            {
                return false;
            }
        }

        TurnedOff?.Invoke(feature, reason);
        return true;
    }

    /// <summary>The features turned off so far with their reasons.</summary>
    public IReadOnlyDictionary<GraphicsFeature, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<GraphicsFeature, string>(_off);
        }
    }
}

/// <summary>Who made the GPU (from GL_VENDOR and GL_RENDERER).</summary>
public enum GpuVendor
{
    /// <summary>Not recognised.</summary>
    Unknown,

    /// <summary>NVIDIA.</summary>
    Nvidia,

    /// <summary>AMD (ATI).</summary>
    Amd,

    /// <summary>Intel.</summary>
    Intel,

    /// <summary>No GPU driver: a software rasteriser (Windows' GDI Generic, Mesa llvmpipe, SwiftShader).</summary>
    Software,
}

/// <summary>Decisions about the GPU in use that need no GL context (unit-tested).</summary>
public static class GpuClassifier
{
    /// <summary>The maker of the GPU named by <paramref name="vendor"/> (GL_VENDOR) and <paramref name="renderer"/> (GL_RENDERER).</summary>
    public static GpuVendor VendorOf(string? vendor, string? renderer)
    {
        var text = $"{vendor} {renderer}";
        if (Regex.IsMatch(text, @"GDI Generic|llvmpipe|softpipe|SwiftShader|Basic Render Driver", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Software;
        }

        if (Regex.IsMatch(text, @"NVIDIA|GeForce|Quadro|RTX", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (Regex.IsMatch(text, @"\bATI\b|\bAMD\b|Advanced Micro Devices|Radeon", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Amd;
        }

        return Regex.IsMatch(text, @"\bIntel\b", RegexOptions.IgnoreCase) ? GpuVendor.Intel : GpuVendor.Unknown;
    }

    /// <summary>
    /// True for a graphics chip built into the processor: every Intel GPU but the Arc A/B cards, and AMD's APUs ("AMD
    /// Radeon(TM) Graphics", "Radeon 780M", "Radeon Vega 8 Graphics", "Radeon R7 Graphics"); never an RX or Pro card.
    /// </summary>
    public static bool IsIntegrated(string? vendor, string? renderer)
    {
        var name = Regex.Replace(renderer ?? string.Empty, @"\((TM|R|tm|r)\)", string.Empty);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        switch (VendorOf(vendor, renderer))
        {
            case GpuVendor.Intel:
                return !Regex.IsMatch(name, @"\bArc (Pro )?[AB]\d{2,3}", RegexOptions.IgnoreCase);
            case GpuVendor.Amd:
                if (Regex.IsMatch(name, @"\bRX\b|\bPro\b|Radeon VII|FirePro|\bR9\b", RegexOptions.IgnoreCase))
                {
                    return false;
                }

                return Regex.IsMatch(name, @"^(AMD )?Radeon Graphics$|Vega \d+ Graphics|Radeon \d{3,4}[MS]\b|Radeon R\d Graphics|Radeon Vega Mobile", RegexOptions.IgnoreCase);
            default:
                return false;
        }
    }

    /// <summary>
    /// Samples per pixel a multisampled target gets: the largest power of two up to <paramref name="requested"/> and
    /// <paramref name="maxSamples"/> (GL_MAX_SAMPLES) that the colour and the depth formats both list (the driver's
    /// GL_SAMPLES of each; null or empty when it did not answer); 1 when there is none.
    /// </summary>
    public static int ChooseSamples(int requested, int maxSamples, IReadOnlyCollection<int>? colourCounts, IReadOnlyCollection<int>? depthCounts)
    {
        var limit = Math.Min(requested, maxSamples);
        for (var samples = limit < 2 ? 1 : 1 << BitOperations.Log2((uint)limit); samples > 1; samples >>= 1)
        {
            if (Lists(colourCounts, samples) && Lists(depthCounts, samples))
            {
                return samples;
            }
        }

        return 1;

        static bool Lists(IReadOnlyCollection<int>? counts, int samples) => counts is not { Count: > 0 } || counts.Contains(samples);
    }
}
