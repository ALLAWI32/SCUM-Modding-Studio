using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using Avalonia.OpenGL;

namespace ScumStudio.App;

/// <summary>Desktop entry point.</summary>
internal static class Program
{
    /// <summary>Set this environment variable to any value to echo Avalonia's platform/OpenGL warnings to the console.</summary>
    public const string AvaloniaLogVariable = "SCUMSTUDIO_AVALONIA_LOG";

    /// <summary>Set this environment variable to let Avalonia use a software OpenGL renderer (llvmpipe) for the viewport.</summary>
    public const string AllowSoftwareGlVariable = "SCUMSTUDIO_ALLOW_SOFTWARE_GL";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Avalonia configuration; also used by the visual designer. The 3D viewport needs a desktop OpenGL 4.3+ context,
    /// so Windows is asked for WGL (not the default ANGLE/Direct3D-backed GLES) and X11 for GLX, both falling back to
    /// software rendering for the rest of the UI when no such context exists.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        if (Environment.GetEnvironmentVariable(AvaloniaLogVariable) is not null)
        {
            Trace.Listeners.Add(new ConsoleTraceListener());
        }

        GlVersion[] desktopGl = [new GlVersion(GlProfileType.OpenGL, 4, 5), new GlVersion(GlProfileType.OpenGL, 4, 3)];
        // Avalonia refuses Mesa's software renderer (llvmpipe) for GLX by default; allow it on request so the viewport can be
        // exercised on headless Linux machines (Xvfb) where that is the only OpenGL available.
        var allowSoftwareGl = Environment.GetEnvironmentVariable(AllowSoftwareGlVariable) is not null;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                WglProfiles = desktopGl.ToList(),
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Software],
                GlProfiles = desktopGl.ToList(),
                GlxRendererBlacklist = allowSoftwareGl ? new List<string>() : ["llvmpipe"],
            })
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning);
    }
}
