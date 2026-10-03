using System.Runtime.InteropServices;

namespace ScumStudio.Rendering.Context;

/// <summary>
/// Pre-loads the GLFW native library shipped by the <c>Ultz.Native.GLFW</c> package from
/// <c>runtimes/&lt;rid&gt;/native</c> next to the application.
/// </summary>
/// <remarks>
/// Silk.NET's own library probing only searches the system paths and the application folder, not the
/// <c>runtimes/&lt;rid&gt;/native</c> folders of a framework-dependent (RID-less) build, so <c>glfwInit</c> would fail with
/// "Couldn't find a suitable window platform" even though the library is on disk. Once the library is loaded into the
/// process, the loader's by-name lookup (<c>dlopen("libglfw.so.3")</c> / <c>LoadLibrary("glfw3.dll")</c>) returns the
/// already loaded module.
/// </remarks>
internal static class GlfwNativeLoader
{
    private static readonly object Gate = new();
    private static bool _attempted;

    /// <summary>Path of the loaded library, or null when it was not found (Silk then falls back to system GLFW).</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>Loads the library once per process; safe to call repeatedly.</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;
            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                {
                    LoadedPath = candidate;
                    return;
                }
            }

            // Single-file publish with IncludeNativeLibrariesForSelfExtract: the library is extracted to a
            // temporary bundle folder that only the runtime's own native probing knows about (AppContext.BaseDirectory
            // is the folder of the .exe there). Silk.NET's resolver does not use that probing, so load it by name here.
            if (LibraryFileName() is { } name
                && NativeLibrary.TryLoad(name, typeof(GlfwNativeLoader).Assembly, null, out _))
            {
                LoadedPath = name;
            }
        }
    }

    /// <summary>Candidate file paths, most specific first.</summary>
    internal static IEnumerable<string> Candidates()
    {
        var fileName = LibraryFileName();
        if (fileName is null)
        {
            yield break;
        }

        // Assembly.Location is empty inside a single-file publish; AppContext.BaseDirectory covers that case and empty
        // entries are filtered out below.
#pragma warning disable IL3000
        var bases = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(GlfwNativeLoader).Assembly.Location) }
#pragma warning restore IL3000
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rids = new[] { RuntimeInformation.RuntimeIdentifier, PortableRid() }
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var dir in bases)
        {
            foreach (var rid in rids)
            {
                yield return Path.Combine(dir, "runtimes", rid, "native", fileName);
            }

            yield return Path.Combine(dir, fileName);
        }
    }

    private static string? LibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "glfw3.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libglfw.3.dylib";
        }

        return OperatingSystem.IsLinux() ? "libglfw.so.3" : null;
    }

    private static string PortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }
}
