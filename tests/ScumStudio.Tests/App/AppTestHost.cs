using Avalonia;
using Avalonia.Headless;
using ScumStudio.Tests.App;

[assembly: AvaloniaTestApplication(typeof(AppTestHost))]

namespace ScumStudio.Tests.App;

/// <summary>
/// Avalonia configuration for <c>[AvaloniaFact]</c> tests: the real <see cref="ScumStudio.App.App"/> (theme, palette,
/// view locator) on the headless platform with Skia rendering, so frames can be captured as PNG screenshots.
/// </summary>
public static class AppTestHost
{
    /// <summary>Builds the headless application.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ScumStudio.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
