using Microsoft.Extensions.Logging;
using ScumStudio.Core.Logging;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Tests.Core;

/// <summary>A unique temporary folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string? prefix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), (prefix ?? "scumstudio-core-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a sub-folder (and parents) and returns its full path.</summary>
    public string Dir(params string[] segments)
    {
        var full = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file (and parent folders) with the given text and returns its full path.</summary>
    public string File(string text, params string[] segments)
    {
        var full = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
        return full;
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

/// <summary>Captures log output of a <see cref="TextWriterLoggerFactory"/> in memory.</summary>
internal sealed class CapturedLog : IDisposable
{
    private readonly StringWriter _writer = new();
    private readonly TextWriterLoggerFactory _factory;

    public CapturedLog(LogLevel minimumLevel = LogLevel.Trace)
    {
        _factory = new TextWriterLoggerFactory(_writer, minimumLevel);
        Logger = _factory.CreateLogger("Test");
    }

    public ILogger Logger { get; }

    public string Text => _writer.ToString();

    public void Dispose() => _factory.Dispose();
}

internal static class MathAssert
{
    public const float Tolerance = 1e-4f;

    public static void Near(float expected, float actual, float tolerance = Tolerance) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual} (tolerance {tolerance}).");

    public static void Near(FVector expected, FVector actual, float tolerance = Tolerance) =>
        Assert.True(expected.Equals(actual, tolerance), $"Expected ({expected}), got ({actual}).");

    public static void Near(System.Numerics.Vector3 expected, System.Numerics.Vector3 actual, float tolerance = Tolerance) =>
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance &&
            MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"Expected {expected}, got {actual}.");

    /// <summary>Same rotation (q and -q are equal).</summary>
    public static void SameRotation(FQuat expected, FQuat actual, float tolerance = Tolerance) =>
        Assert.True(expected.Equals(actual, tolerance), $"Expected ({expected}), got ({actual}).");

    public static void Near(FRotator expected, FRotator actual, float tolerance = 1e-2f) =>
        Assert.True(expected.Equals(actual, tolerance), $"Expected ({expected}), got ({actual}).");

    public static void Near(FTransform expected, FTransform actual, float tolerance = 1e-3f) =>
        Assert.True(expected.Equals(actual, tolerance), $"Expected {expected}, got {actual}.");
}
