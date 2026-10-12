using ScumStudio.Pak;
using Xunit.Sdk;

namespace ScumStudio.Tests;

/// <summary>
/// The real game files for the tests that need them: the <c>SCUM_PAKS</c> folder and the AES key (environment or this PC's
/// store). <see cref="SkipReason"/> names what is missing and never contains the key.
/// </summary>
internal static class RealPaks
{
    /// <summary>The Paks folder, or null when <see cref="SkipReason"/> is set.</summary>
    public static string? Folder => SkipReason is null ? Environment.GetEnvironmentVariable("SCUM_PAKS") : null;

    /// <summary>Why the real-file tests cannot run here, or null when they can.</summary>
    public static string? SkipReason
    {
        get
        {
            var paks = Environment.GetEnvironmentVariable("SCUM_PAKS");
            if (string.IsNullOrWhiteSpace(paks))
            {
                return "SCUM_PAKS is not set (the game's Content\\Paks folder).";
            }

            if (!Directory.Exists(paks))
            {
                return $"The SCUM_PAKS folder does not exist: {paks}";
            }

            return AesKeyText.FromEnvironmentOrStore() is null
                ? $"No AES key: neither {AesKeyText.EnvironmentVariable} nor the studio's key store has one."
                : null;
        }
    }
}

/// <summary>A fact that is skipped (not passed) when the real game files or the AES key are missing.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PaksFactAttribute : FactAttribute
{
    public PaksFactAttribute()
    {
        if (RealPaks.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A theory that is skipped (not passed) when the real game files or the AES key are missing.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PaksTheoryAttribute : TheoryAttribute
{
    public PaksTheoryAttribute()
    {
        if (RealPaks.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// Like <c>[AvaloniaFact]</c> (headless Avalonia UI thread, same discoverer) but skipped when the real game files or the
/// AES key are missing (see <see cref="App.AvaloniaFixturesFactAttribute"/> for why the discoverer is re-declared).
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AvaloniaPaksFactAttribute : FactAttribute
{
    public AvaloniaPaksFactAttribute()
    {
        if (RealPaks.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
