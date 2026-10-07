using Microsoft.Extensions.Logging;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Games;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;

namespace ScumStudio.App.Services;

/// <summary>Options of <see cref="AppServices.Create"/>.</summary>
public sealed record AppServicesOptions
{
    /// <summary>Data folder for settings, key and UI state (default: <see cref="StudioHome.GetDirectory"/>).</summary>
    public string? DataDirectory { get; init; }

    /// <summary>UI dispatcher (default: Avalonia's UI thread).</summary>
    public IUiDispatcher? Dispatcher { get; init; }

    /// <summary>Dialogs (default: none until the main window sets <see cref="AppServices.Dialogs"/>).</summary>
    public IDialogService? Dialogs { get; init; }

    /// <summary>Factory for the game locator (tests pass one with fake Steam roots).</summary>
    public Func<ILogger, GameLocator>? LocatorFactory { get; init; }

    /// <summary>Toast lifetime; null keeps toasts until closed.</summary>
    public TimeSpan? ToastLifetime { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Write the log to <c>logs/scumstudio.log</c> in the data folder.</summary>
    public bool LogToFile { get; init; } = true;

    /// <summary>Finds the game's key online (<see cref="OnlineKeyFinder.FindAsync"/>); tests replace it so they never go online.</summary>
    public Func<CancellationToken, Task<string?>>? KeyFinder { get; init; }

    /// <summary>Mirror the log to standard output.</summary>
    public bool LogToConsole { get; init; } = true;

    /// <summary>Where new projects go and are listed from (default: <c>Documents/ScumStudio Projects</c>; tests use a temporary one).</summary>
    public string? ProjectsFolder { get; init; }
}

/// <summary>
/// Composition root of the desktop app (no DI container): settings, the protected key store, logging, notifications,
/// long-operation runner, the asset workspace and the project session. One instance per main window.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly Func<ILogger, GameLocator> _locatorFactory;

    private AppServices(AppServicesOptions options)
    {
        DataDirectory = Path.GetFullPath(options.DataDirectory ?? StudioHome.GetDirectory());
        ProjectsFolder = options.ProjectsFolder ?? ViewModels.ProjectsPageViewModel.DefaultProjectsFolder();
        Dispatcher = options.Dispatcher ?? new AvaloniaUiDispatcher();
        Dialogs = options.Dialogs ?? new NullDialogService();
        _locatorFactory = options.LocatorFactory ?? (logger => new GameLocator(logger: logger));
        FindKeyOnline = options.KeyFinder ?? OnlineKeyFinder.FindAsync;

        Log = new LogPanelViewModel(Dispatcher);
        var logFile = options.LogToFile ? Path.Combine(DataDirectory, "logs", "scumstudio.log") : null;
        // SCUMSTUDIO_LOG_LEVEL=debug also records the per-asset details (why a material or texture gave no preview, ...).
        var minimum = string.Equals(Environment.GetEnvironmentVariable("SCUMSTUDIO_LOG_LEVEL"), "debug", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug : LogLevel.Information;
        LoggerFactory = new SingleProviderLoggerFactory(new UiLoggerProvider(Log.Add, logFile, options.LogToConsole, minimum));
        Logger = LoggerFactory.CreateLogger("ScumStudio.App");

        Settings = new SettingsStore(DataDirectory, LoggerFactory.CreateLogger<SettingsStore>());
        Keys = new ProtectedKeyStore(DataDirectory, LoggerFactory.CreateLogger<ProtectedKeyStore>());
        UiState = new UiStateStore(DataDirectory, LoggerFactory.CreateLogger<UiStateStore>());
        Notifications = new NotificationService(Dispatcher, options.ToastLifetime, LoggerFactory.CreateLogger<NotificationService>());
        Operations = new OperationRunner(Dispatcher, Notifications, LoggerFactory.CreateLogger<OperationRunner>());
        Workspace = new GameWorkspace(Settings, Keys, Dispatcher, LoggerFactory.CreateLogger<GameWorkspace>());
        Projects = new ProjectSession(this, LoggerFactory.CreateLogger<ProjectSession>());
        Mcp = new AppMcpService(this);
        Thumbnails = new ThumbnailService(Path.Combine(DataDirectory, "cache", "thumbnails"), LoggerFactory.CreateLogger<ThumbnailService>());
        Reports = new ReportService(LoggerFactory.CreateLogger<ReportService>());
        Prefabs = new PrefabLibrary(Path.Combine(DataDirectory, "Prefabs"));
        Economy = new ProjectEconomy(this);
    }

    /// <summary>Raised (on the calling thread) after settings were saved through <see cref="UpdateSettings"/>.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised when the stored AES key was set or removed.</summary>
    public event EventHandler? KeyChanged;

    /// <summary>Data folder.</summary>
    public string DataDirectory { get; }

    /// <summary>Where new projects go and are listed from (see <see cref="AppServicesOptions.ProjectsFolder"/>).</summary>
    public string ProjectsFolder { get; }

    /// <summary>Finds the game's AES key online, or null (see <see cref="OnlineKeyFinder"/>).</summary>
    public Func<CancellationToken, Task<string?>> FindKeyOnline { get; }

    /// <summary>UI dispatcher.</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>Dialogs; replaced by the main window once it exists.</summary>
    public IDialogService Dialogs { get; set; }

    /// <summary>The log panel (also the sink of <see cref="LoggerFactory"/>).</summary>
    public LogPanelViewModel Log { get; }

    /// <summary>Logger factory for the whole app.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>General app logger.</summary>
    public ILogger Logger { get; }

    /// <summary><c>settings.json</c>.</summary>
    public SettingsStore Settings { get; }

    /// <summary>The protected AES key.</summary>
    public ProtectedKeyStore Keys { get; }

    /// <summary><c>ui-state.json</c>.</summary>
    public UiStateStore UiState { get; }

    /// <summary>Toasts.</summary>
    public NotificationService Notifications { get; }

    /// <summary>Long-running operations with progress.</summary>
    public OperationRunner Operations { get; }

    /// <summary>The asset catalog in use.</summary>
    public GameWorkspace Workspace { get; }

    /// <summary>The open project.</summary>
    public ProjectSession Projects { get; }

    /// <summary>The built-in MCP server (AI control).</summary>
    public AppMcpService Mcp { get; }

    /// <summary>Tile and row pictures (disk cache under the data folder).</summary>
    public ThumbnailService Thumbnails { get; }

    /// <summary>"Report a problem": reports translated to English and posted to the owner's Discord.</summary>
    public ReportService Reports { get; }

    /// <summary>Saved selections (plain JSON files under the data folder, independent of any project).</summary>
    public PrefabLibrary Prefabs { get; }

    /// <summary>The open project's economy (<c>EconomyOverride.json</c>), kept in step with the traders on the map.</summary>
    public ProjectEconomy Economy { get; }

    /// <summary>Creates the services.</summary>
    public static AppServices Create(AppServicesOptions? options = null) => new(options ?? new AppServicesOptions());

    /// <summary>Creates a game locator.</summary>
    public GameLocator CreateLocator() => _locatorFactory(LoggerFactory.CreateLogger<GameLocator>());

    /// <summary>Loads, changes and saves the settings, then raises <see cref="SettingsChanged"/>.</summary>
    public AppSettings UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var updated = Settings.Update(change);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>Validates and stores the AES key. Returns false (nothing stored) when the text is not a 256-bit hex key.</summary>
    public bool TryStoreKey(string? text)
    {
        if (!AesKeyHex.TryNormalize(text, out var normalized))
        {
            return false;
        }

        Keys.Set(normalized);
        KeyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Deletes the stored key.</summary>
    public void ForgetKey()
    {
        Keys.Clear();
        Logger.LogInformation("The stored AES key was removed.");
        KeyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Economy.Dispose();
        Thumbnails.Dispose();
        Mcp.Dispose();
        Projects.Dispose();
        Workspace.Dispose();
        LoggerFactory.Dispose();
    }
}
