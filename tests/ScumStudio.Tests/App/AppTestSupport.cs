using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.Core.Games;

namespace ScumStudio.Tests.App;

/// <summary>
/// An isolated <see cref="AppServices"/> over a temporary data folder: no log file, no console output, toasts that
/// stay until dismissed and a game locator that looks at an empty Steam root (so tests never see a real install).
/// </summary>
internal sealed class AppTestContext : IDisposable
{
    private AppTestContext(string directory, AppServices services)
    {
        Directory = directory;
        Services = services;
    }

    /// <summary>Temporary data folder (deleted on dispose).</summary>
    public string Directory { get; }

    /// <summary>The services.</summary>
    public AppServices Services { get; }

    /// <summary>
    /// Creates a context. <paramref name="inline"/> uses <see cref="InlineUiDispatcher"/> (plain <c>[Fact]</c> tests);
    /// otherwise Avalonia's dispatcher (<c>[AvaloniaFact]</c> tests run on the UI thread).
    /// </summary>
    public static AppTestContext Create(bool inline = true, IDialogService? dialogs = null, Func<CancellationToken, Task<string?>>? keyFinder = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "scumstudio-app-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var emptySteam = Path.Combine(directory, "no-steam");
        var services = AppServices.Create(new AppServicesOptions
        {
            DataDirectory = Path.Combine(directory, "data"),
            Dispatcher = inline ? new InlineUiDispatcher() : new AvaloniaUiDispatcher(),
            Dialogs = dialogs,
            LogToFile = false,
            LogToConsole = false,
            ToastLifetime = null,
            LocatorFactory = logger => new GameLocator(new GameLocatorOptions { SteamRoots = [emptySteam], ServerSearchRoots = [] }, logger),
            KeyFinder = keyFinder ?? (_ => Task.FromResult<string?>(null)),
        });
        return new AppTestContext(directory, services);
    }

    /// <summary>Combines below the temporary folder.</summary>
    public string Combine(params string[] parts) => Path.Combine([Directory, .. parts]);

    /// <inheritdoc />
    public void Dispose()
    {
        Services.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A dialog service answering folder/save pickers with preset paths.</summary>
internal sealed class ScriptedDialogService : IDialogService
{
    public Queue<string?> Folders { get; } = new();

    public Queue<string?> SaveFiles { get; } = new();

    public List<string> Clipboard { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? startFolder = null) =>
        Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);

    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string filterName) =>
        Task.FromResult(SaveFiles.Count > 0 ? SaveFiles.Dequeue() : null);

    public Task SetClipboardTextAsync(string text)
    {
        Clipboard.Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>Helpers for headless UI tests.</summary>
internal static class HeadlessUi
{
    /// <summary>Environment variable: when set, page screenshots are written (value = folder, or 1/true for a temp folder).</summary>
    public const string ScreenshotsVariable = "SCUMSTUDIO_SCREENSHOTS";

    /// <summary>Creates and shows the main window over <paramref name="services"/> at a fixed size.</summary>
    public static (MainWindow Window, MainWindowViewModel ViewModel) ShowMainWindow(AppServices services, double width = 1600, double height = 900)
    {
        var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        services.Dialogs = services.Dialogs is NullDialogService ? new AvaloniaDialogService(() => window) : services.Dialogs;
        window.Show();
        Pump();
        return (window, vm);
    }

    /// <summary>Runs queued dispatcher jobs and a layout/render pass.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds or the timeout expires.</summary>
    public static bool PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Pump();
            Thread.Sleep(10);
        }

        Pump();
        return true;
    }

    /// <summary>Descendants of <paramref name="root"/> of type <typeparamref name="T"/>.</summary>
    public static IEnumerable<T> Find<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>();

    /// <summary>The descendant control named <paramref name="name"/>, or null.</summary>
    public static T? FindNamed<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>Screenshot folder, or null when screenshots are disabled.</summary>
    public static string? ScreenshotDirectory
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ScreenshotsVariable);
            if (string.IsNullOrWhiteSpace(value) || value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetTempPath(), "scumstudio-screens")
                : Path.GetFullPath(value);
        }
    }

    /// <summary>
    /// Renders <paramref name="window"/> and saves the frame as <c>&lt;name&gt;.png</c> in <see cref="ScreenshotDirectory"/>.
    /// Returns the file, or null when screenshots are disabled.
    /// </summary>
    public static string? SaveScreenshot(Window window, string name)
    {
        if (ScreenshotDirectory is not { } folder)
        {
            return null;
        }

        Pump();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        System.IO.Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        frame.Save(path);
        return path;
    }
}
