using System.Diagnostics;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Pak;

/// <summary>
/// Locates the repak command line tool (https://github.com/trumank/repak, used as an independent pak oracle).
/// Looked up in the <c>REPAK</c> environment variable, <c>~/.cargo/bin</c>, then PATH. Tests needing it are skipped when absent.
/// </summary>
internal static class RepakTool
{
    private static readonly Lazy<string?> Located = new(Locate);

    public static string? Path => Located.Value;

    public static string? SkipReason => Path is null ? "repak not found (set REPAK or install with 'cargo install repak_cli')." : null;

    /// <summary>Runs repak and returns stdout; throws on a non-zero exit code.</summary>
    public static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Path ?? throw new InvalidOperationException(SkipReason))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"repak {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Result}");
        }

        return stdout.Result;
    }

    /// <summary><c>repak list</c> as a sorted list.</summary>
    public static List<string> List(string pak) =>
        Run("list", pak).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary><c>repak info</c> as key/value pairs.</summary>
    public static Dictionary<string, string> Info(string pak) =>
        Run("info", pak).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains(':'))
            .Select(l => (Key: l[..l.IndexOf(':')].Trim(), Value: l[(l.IndexOf(':') + 1)..].Trim()))
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);

    private static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "repak.exe" : "repak";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("REPAK"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", exe),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d, exe)));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }
}

/// <summary>A fact that is skipped when repak is not installed.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RepakFactAttribute : FactAttribute
{
    public RepakFactAttribute()
    {
        if (RepakTool.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A fact that is skipped when the SCUM fixture archive or repak is unavailable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RepakFixturesFactAttribute : FactAttribute
{
    public RepakFixturesFactAttribute()
    {
        if ((FixturePaths.SkipReason ?? RepakTool.SkipReason) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A unique temporary directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "scumstudio-pak-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file below the directory given a virtual path (forward slashes).</summary>
    public string Write(string virtualPath, byte[] data)
    {
        var file = System.IO.Path.Combine([Path, .. virtualPath.Split('/')]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Helpers shared by the pak tests.</summary>
internal static class PakTestData
{
    /// <summary>Relative file paths (forward slashes) of every file below <paramref name="root"/>, sorted ordinally.</summary>
    public static List<string> ListTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Asserts that two directory trees contain the same relative paths with byte-identical contents.</summary>
    public static void AssertTreesEqual(string expectedRoot, string actualRoot, IReadOnlyList<string>? expected = null)
    {
        expected ??= ListTree(expectedRoot);
        Assert.Equal(expected, ListTree(actualRoot));
        foreach (var relative in expected)
        {
            var a = System.IO.Path.Combine([expectedRoot, .. relative.Split('/')]);
            var b = System.IO.Path.Combine([actualRoot, .. relative.Split('/')]);
            Assert.True(FilesEqual(a, b), $"Content differs: {relative}");
        }
    }

    public static bool FilesEqual(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length)
        {
            return false;
        }

        var ba = new byte[1 << 16];
        var bb = new byte[1 << 16];
        int read;
        while ((read = fa.Read(ba, 0, ba.Length)) > 0)
        {
            fb.ReadExactly(bb, 0, read);
            if (!ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A small staging tree with stored, compressible, multi-block, empty and incompressible files.</summary>
    public static Dictionary<string, byte[]> SampleStaging()
    {
        var random = new Random(1234);
        var noise = new byte[3000];
        random.NextBytes(noise);
        var big = new byte[200_000]; // 4 blocks of 64 KiB, compressible
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)((i * 7) % 251 ^ (i >> 10));
        }

        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["SCUM/AssetRegistry.bin"] = "fake registry"u8.ToArray(),
            ["SCUM/Content/ConZ_Files/Maps/Test/A_0_Test.umap"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("umap header ", 500))),
            ["SCUM/Content/ConZ_Files/Maps/Test/A_0_Test.uexp"] = big,
            ["SCUM/Content/ConZ_Files/Models/Box.uasset"] = noise,
            ["SCUM/Content/ConZ_Files/Models/Box.ubulk"] = [],
            ["SCUM/Content/ConZ_Files/Models/Zeta.uexp"] = new byte[70_000],
        };
    }

    /// <summary>Writes <see cref="SampleStaging"/> into <paramref name="dir"/>.</summary>
    public static Dictionary<string, byte[]> WriteSampleStaging(TempDirectory dir, string subfolder = "stage")
    {
        var files = SampleStaging();
        foreach (var (path, data) in files)
        {
            dir.Write(subfolder + "/" + path, data);
        }

        return files;
    }
}
