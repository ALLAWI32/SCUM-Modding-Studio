using Avalonia;
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
            AccentPalette.Apply(this, services.UiState.Current.Accent);
            Loc.Instance.Language = services.Settings.Load().Ui.Language;

            var viewModel = new MainWindowViewModel(services);
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
            desktop.Exit += (_, _) =>
            {
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
