using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;

namespace ScumStudio.App;

/// <summary>The Avalonia application (dark Fluent theme with the ScumStudio palette).</summary>
public partial class App : Application
{
    private AppServices? _services;

    private static Window? s_window;
    private static volatile bool s_closed;

    /// <summary>This process's hold on the one app window (<see cref="Program.Main"/>); null for automation runs.</summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>
    /// Another start asked for the window (<see cref="SingleInstance.Listen"/>, on a pool thread): it comes forward, or is
    /// still opening and opens in front. Also while it downloads or installs an update: a start told no would run beside
    /// it, during the install as the old version from the file the update renames to <c>.old</c>. Once the window closed
    /// it says no and that start goes on (an update's new version waits for this process to end and never asks).
    /// </summary>
    internal static bool ComeForward()
    {
        if (s_closed)
        {
            return false;
        }

        if (Volatile.Read(ref s_window) is { } window)
        {
            Dispatcher.UIThread.Post(() => BringToFront(window));
        }

        return true;
    }

    /// <summary>The window <see cref="ComeForward"/> brings to the front until it closes (UI thread).</summary>
    internal static void Attach(Window window)
    {
        s_closed = false;
        Volatile.Write(ref s_window, window);
        window.Closed += (_, _) => s_closed = true;
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        TextDirection.Register();
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = CreateServices();
            _services = services;
            HookUnhandledExceptions(services);
            HookGraphics(services);
            AccentPalette.Apply(this, services.UiState.Current.Accent);
            Loc.Instance.Language = services.Settings.Load().Ui.Language;

            var viewModel = new MainWindowViewModel(services) { UpdateCheck = Updater.CheckAsync, RequestShutdown = () => desktop.Shutdown() };
            var window = new MainWindow { DataContext = viewModel };
            services.Dialogs = new AvaloniaDialogService(() => window);
            var startup = StartupOptions.Parse(desktop.Args ?? []);
            services.Mcp.AttachShell(viewModel, () => window);
            services.Mcp.PortOverride = startup.McpPort;
            services.Mcp.TokenOverride = startup.McpToken;
            window.Opened += async (_, _) =>
            {
                await viewModel.InitializeAsync();
                await services.Mcp.ApplySettingsAsync();
                if (startup.McpPort is not null && services.Mcp.IsRunning)
                {
                    Console.Out.WriteLine("MCP server: " + services.Mcp.Url);
                }

                if (startup.HasWork)
                {
                    await startup.RunAsync(desktop, window, viewModel, services);
                }
            };
            desktop.MainWindow = window;
            Attach(window);
            desktop.Exit += (_, _) =>
            {
                // The window first (a start now gets its own), then the project and its lock (AppServices.Dispose).
                Instance?.Dispose();
                viewModel.Dispose();
                services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Routes exceptions that would otherwise terminate the app to the log and a toast. UI-thread exceptions are marked
    /// handled so a bug in one page never takes the editor down.
    /// </summary>
    public static void HookUnhandledExceptions(AppServices services)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            services.Logger.LogError(e.Exception, "Unhandled UI exception: {Message}", e.Exception.Message);
            services.Notifications.Error(Loc.T("Shell.SomethingWentWrong"), e.Exception.Message);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            services.Logger.LogError(e.Exception, "Unobserved task exception: {Message}", e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                services.Logger.LogCritical(ex, "Fatal exception: {Message}", ex.Message);
            }
        };
    }

    /// <summary>
    /// The graphics card's log and warnings (D03): the GPU each 3D view starts on, the driver's messages, and a toast when a
    /// feature is turned off for it. A native crash inside the driver is not an exception, so these lines are what is left.
    /// </summary>
    public static void HookGraphics(AppServices services)
    {
        var logger = services.LoggerFactory.CreateLogger("Graphics");
        GraphicsStatus.Attach(logger);
        GraphicsStatus.Warn = (feature, reason) => services.Notifications.ShowCoalesced("graphics-fallback", ToastSeverity.Warning,
            Loc.T("Settings.Graphics.FallbackToast"), Loc.F("Settings.Graphics.FallbackToast.Message", GraphicsStatus.FeatureName(feature)));
        if (Program.Win32Options([]).ShouldRenderOnUIThread)
        {
            logger.LogInformation("Graphics: the window is composed on the UI thread ({Variable} is set)", Program.UiThreadCompositorVariable);
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true; // above the other windows even when Windows keeps the focus where it is
        window.Topmost = false;
    }

    private static AppServices CreateServices()
    {
        try
        {
            return AppServices.Create();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // No usable per-user folder: fall back to a temp folder so the editor still starts.
            var fallback = Path.Combine(Path.GetTempPath(), "ScumStudio");
            var services = AppServices.Create(new AppServicesOptions { DataDirectory = fallback });
            services.Logger.LogWarning("Using {Folder} for settings because the per-user data folder is unavailable: {Message}", fallback, ex.Message);
            return services;
        }
    }
}
