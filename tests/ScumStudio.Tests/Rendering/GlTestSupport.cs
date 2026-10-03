using System.Numerics;
using ScumStudio.Rendering.Context;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Probes once per process whether an OpenGL 4.3 core context can be created (a display plus a capable driver: a real
/// GPU on Windows, <c>xvfb-run</c> + Mesa on Linux). Tests needing GL use <see cref="GlFactAttribute"/> and are skipped,
/// not failed, when the probe fails (e.g. headless CI without xvfb, Windows runners with only the GDI 1.1 driver).
/// Set <c>SCUMSTUDIO_SKIP_GL=1</c> to skip them explicitly.
/// </summary>
internal static class GlTestEnvironment
{
    public const string SkipVariable = "SCUMSTUDIO_SKIP_GL";

    private static readonly Lazy<string?> Probe = new(() =>
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) is "1" or "true")
        {
            return $"{SkipVariable} is set.";
        }

        if (OperatingSystem.IsLinux()
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return "No display (run the tests under xvfb-run -a -s \"-screen 0 1280x800x24\").";
        }

        if (!OffscreenGlContext.TryCreate(64, 64, out var context, out var reason))
        {
            return $"OpenGL 4.3 unavailable: {reason}";
        }

        context!.Dispose();
        return null;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when GL tests can run, otherwise the skip reason.</summary>
    public static string? SkipReason => Probe.Value;
}

/// <summary>A fact that is skipped when no OpenGL 4.3 core context can be created.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GlFactAttribute : FactAttribute
{
    public GlFactAttribute()
    {
        if (GlTestEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>GLFW is not thread-safe: all GL test classes share this non-parallel collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlCollection
{
    public const string Name = "OpenGL";
}

internal static class RenderAssert
{
    public static void Near(float expected, float actual, float tolerance = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual} (tolerance {tolerance}).");

    public static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual} (tolerance {tolerance}).");

    public static void Near(in Matrix4x4 expected, in Matrix4x4 actual, float tolerance = 1e-4f)
    {
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= tolerance * MathF.Max(1f, MathF.Abs(expected[r, c])),
                    $"M{r + 1}{c + 1}: expected {expected[r, c]}, got {actual[r, c]}.\nexpected {expected}\nactual   {actual}");
            }
        }
    }
}
