using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Rendering.Context;

namespace ScumStudio.App.Services;

/// <summary>What Settings › Graphics card says about the GPU in use (see <see cref="GraphicsStatus.Advice"/>).</summary>
[Flags]
public enum GraphicsAdvice
{
    /// <summary>Nothing to say.</summary>
    None = 0,

    /// <summary>A graphics chip built into the processor draws the 3D views.</summary>
    Integrated = 1,

    /// <summary>No GPU driver: Windows draws in software.</summary>
    Software = 2,

    /// <summary>A feature was turned off because the driver refused it (or a frame failed).</summary>
    FallbackUsed = 4,
}

/// <summary>
/// What the 3D views learned about the graphics card (D03, AMD cards crashed in 3.2.0): the GPU in use, the features turned
/// off for it and why, and the log all of that goes to with GL_VENDOR, GL_RENDERER and GL_VERSION. One per process: every
/// viewport and preview draws with <see cref="Fallbacks"/>, so a feature a driver refused in one is not tried again in
/// another or after the GL context is re-created. Thread-safe; <see cref="Changed"/> may be raised on the render thread.
/// </summary>
public static class GraphicsStatus
{
    /// <summary>Set this environment variable (to anything but 0) to start with every <see cref="GraphicsFeature"/> turned off.</summary>
    public const string SafeVariable = "SCUMSTUDIO_SAFE_GRAPHICS";

    private static ILogger _logger = NullLogger.Instance;
    private static string? _loggedGpu;

    static GraphicsStatus()
    {
        Fallbacks.TurnedOff += OnTurnedOff;
        if (Environment.GetEnvironmentVariable(SafeVariable) is { Length: > 0 } safe && safe != "0")
        {
            foreach (var feature in Enum.GetValues<GraphicsFeature>())
            {
                Fallbacks.TurnOff(feature, $"{SafeVariable} is set");
            }
        }
    }

    /// <summary>The features every renderer of the app does without.</summary>
    public static GraphicsFallbacks Fallbacks { get; } = new();

    /// <summary>The GPU the last 3D view started on (null until one did).</summary>
    public static GlInfo? Gpu { get; private set; }

    /// <summary>Why the last 3D view could not start OpenGL (null when it did).</summary>
    public static string? StartError { get; private set; }

    /// <summary>Raised when <see cref="Gpu"/>, <see cref="StartError"/> or <see cref="Fallbacks"/> changed (any thread).</summary>
    public static event Action? Changed;

    /// <summary>Shows that a feature was turned off (the feature, the reason); set by the app, null in tests.</summary>
    public static Action<GraphicsFeature, string>? Warn { get; set; }

    /// <summary>Sends what follows to <paramref name="logger"/> (the app's log).</summary>
    public static void Attach(ILogger logger) => _logger = logger ?? NullLogger.Instance;

    /// <summary>The GPU for the log: renderer, vendor and version string.</summary>
    public static string Describe(GlInfo? gpu = null) =>
        (gpu ?? Gpu) is { } g ? $"{g.Renderer} ({g.Vendor}, OpenGL {g.Version})" : "GPU not known yet";

    /// <summary>A 3D view started OpenGL on <paramref name="info"/>; <paramref name="errors"/>: GL errors found right after.</summary>
    public static void Started(string view, GlInfo info, IReadOnlyCollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(info);
        Gpu = info;
        StartError = null;
        var described = Describe(info);
        if (!string.Equals(_loggedGpu, described, StringComparison.Ordinal))
        {
            _loggedGpu = described;
            _logger.LogInformation(
                "Graphics: {Gpu}; context {Major}.{Minor}, up to {Samples} samples, BC1-3 uploads {S3tc}, {Kind}{Fallbacks}",
                described, info.Major, info.Minor, info.MaxSamples, info.SupportsS3tc ? "yes" : "no",
                GpuClassifier.IsIntegrated(info.Vendor, info.Renderer) ? "integrated GPU" : GpuClassifier.VendorOf(info.Vendor, info.Renderer).ToString(),
                Fallbacks.Snapshot() is { Count: > 0 } off ? "; off: " + string.Join(", ", off.Keys) : string.Empty);
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Graphics: the {View} found GL errors right after starting: {Errors} [{Gpu}]", view, string.Join(", ", errors), described);
        }

        Changed?.Invoke();
    }

    /// <summary>A 3D view could not start OpenGL (a shader the driver rejected, no GL 4.3).</summary>
    public static void StartFailed(string view, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        StartError = error.Message;
        _logger.LogError(error, "Graphics: the {View} could not start OpenGL: {Message} [{Gpu}]", view, error.Message, Describe());
        Changed?.Invoke();
    }

    /// <summary>A frame of a 3D view threw (<paramref name="failures"/> in a row); the first few are logged in full.</summary>
    public static void FrameFailed(string view, Exception error, int failures)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (failures <= 3 || failures % 50 == 0)
        {
            _logger.LogError(error, "Graphics: a frame of the {View} failed ({Failures} in a row): {Message} [{Gpu}]", view, failures, error.Message, Describe());
        }
    }

    /// <summary>A message of the driver's debug output (see <see cref="GlDebugOutput"/>).</summary>
    public static void DriverMessage(string message) => _logger.LogWarning("Graphics driver: {Message} [{Gpu}]", message, Describe());

    /// <summary>
    /// The features to do without after <paramref name="failures"/> failed frames in a row ending with <paramref name="error"/>:
    /// none for a failure outside the renderer and the driver (a bug of the page's state); else the 3.2 features first
    /// (multisampling and alpha to coverage, tested on one RTX only), then from the second failure compressed uploads too.
    /// </summary>
    public static IReadOnlyList<GraphicsFeature> FeaturesToDrop(Exception error, int failures)
    {
        var graphics = error.Message.Contains("OpenGL", StringComparison.Ordinal)
                       || (error.StackTrace ?? string.Empty).Contains("ScumStudio.Rendering.", StringComparison.Ordinal);
        return !graphics ? []
            : failures >= 2 ? [GraphicsFeature.Multisampling, GraphicsFeature.AlphaToCoverage, GraphicsFeature.TemporalStabilization, GraphicsFeature.CompressedTextures]
            : [GraphicsFeature.Multisampling, GraphicsFeature.AlphaToCoverage, GraphicsFeature.TemporalStabilization];
    }

    /// <summary>
    /// A 3D view switched to newly streamed areas: what the studio holds on the graphics card now, so the last lines of a log
    /// written before a crash inside the driver show whether it was filling up (D03; a there-and-back flight holds as much
    /// at its end as at its start).
    /// </summary>
    public static void Streamed(string view)
    {
        var (meshes, meshBytes) = Rendering.Resources.GpuMesh.Live;
        var (textures, textureBytes) = Rendering.Resources.GpuTexture.Live;
        _logger.LogInformation("Graphics: {View} shows new areas; the studio holds {Meshes} meshes ({MeshMb} MB) and {Textures} textures ({TextureMb} MB) on the graphics card",
            view, meshes, meshBytes >> 20, textures, textureBytes >> 20);
    }

    /// <summary>The name of <paramref name="feature"/> in the UI language.</summary>
    public static string FeatureName(GraphicsFeature feature) => feature switch
    {
        GraphicsFeature.Multisampling => Localization.Loc.T("Settings.Graphics.Feature.Multisampling"),
        GraphicsFeature.AlphaToCoverage => Localization.Loc.T("Settings.Graphics.Feature.AlphaToCoverage"),
        GraphicsFeature.CompressedTextures => Localization.Loc.T("Settings.Graphics.Feature.CompressedTextures"),
        GraphicsFeature.TemporalStabilization => Localization.Loc.T("Settings.Graphics.Feature.TemporalStabilization"),
        _ => feature.ToString(),
    };

    /// <summary>What Settings › Graphics card warns about for <paramref name="gpu"/> with <paramref name="off"/> turned off.</summary>
    public static GraphicsAdvice Advice(GlInfo? gpu, IReadOnlyCollection<GraphicsFeature> off)
    {
        var advice = off.Count > 0 ? GraphicsAdvice.FallbackUsed : GraphicsAdvice.None;
        if (gpu is null)
        {
            return advice;
        }

        if (GpuClassifier.VendorOf(gpu.Vendor, gpu.Renderer) == GpuVendor.Software)
        {
            advice |= GraphicsAdvice.Software;
        }
        else if (GpuClassifier.IsIntegrated(gpu.Vendor, gpu.Renderer))
        {
            advice |= GraphicsAdvice.Integrated;
        }

        return advice;
    }

    private static void OnTurnedOff(GraphicsFeature feature, string reason)
    {
        _logger.LogWarning("Graphics: {Feature} turned off: {Reason} [{Gpu}]", feature, reason, Describe());
        Warn?.Invoke(feature, reason);
        Changed?.Invoke();
    }
}
