using ScumStudio.Tests.Fixtures;
using Xunit.Sdk;

namespace ScumStudio.Tests.App;

/// <summary>
/// Like <c>[AvaloniaFact]</c> (runs on the headless Avalonia UI thread, same discoverer) but skipped when the SCUM
/// fixture archive is unavailable. <c>AvaloniaFactAttribute</c> is sealed, so this re-declares its discoverer
/// (Avalonia.Headless.XUnit 11.3.x).
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AvaloniaFixturesFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; sets <see cref="FactAttribute.Skip"/> when fixtures are missing.</summary>
    public AvaloniaFixturesFactAttribute()
    {
        if (FixturePaths.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
