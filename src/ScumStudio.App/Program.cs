using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using Avalonia.OpenGL;
using ScumStudio.App.Services;

namespace ScumStudio.App;

/// <summary>Desktop entry point.</summary>
internal static class Program
{
    /// <summary>Set this environment variable to any value to echo Avalonia's platform/OpenGL warnings to the console.</summary>
    public const string AvaloniaLogVariable = "SCUMSTUDIO_AVALONIA_LOG";

    /// <summary>Set this environment variable to let Avalonia use a software OpenGL renderer (llvmpipe) for the viewport.</summary>
    public const string AllowSoftwareGlVariable = "SCUMSTUDIO_ALLOW_SOFTWARE_GL";

    /// <summary>Name of the one-window mutex and its events (per user session).</summary>
    internal const string InstanceName = "ScumStudio.App";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        if (options.AfterUpdatePid is { } pid)
        {
            WaitForExit(pid, TimeSpan.FromSeconds(30)); // the old version still closes its project and its files
        }

        App.Instance = TakeWindow(InstanceName, options, out var answered);
        if (answered)
        {
            return;
        }

        App.Instance?.Listen(App.ComeForward); // from now on: a start while this one still opens is answered too
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// One window (zarkos6175: two copies open, the project not saved): the hold on <paramref name="name"/>, or with
    /// <paramref name="answered"/> the window that has it came forward and this start quits. Automation and screenshot runs
    /// keep their own window (null). A window that does not answer in 2 s (closing, hung) lets this one start, owning the
    /// window when it lets go within 3 s more; the project lock protects the project.
    /// </summary>
    internal static SingleInstance? TakeWindow(string name, StartupOptions options, out bool answered)
    {
        answered = false;
        if (options.HasWork || options.McpPort is not null)
        {
            return null;
        }

        if (SingleInstance.Acquire(name, options.AfterUpdatePid is null ? TimeSpan.Zero : TimeSpan.FromSeconds(10)) is { } owned)
        {
            return owned;
        }

        answered = SingleInstance.SignalOwner(name, TimeSpan.FromSeconds(2));
        return answered ? null : SingleInstance.Acquire(name, TimeSpan.FromSeconds(3));
    }

    private static void WaitForExit(int pid, TimeSpan limit)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(limit);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

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
