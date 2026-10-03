using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core;
using ScumStudio.Core.Games;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// View model of <see cref="Views.MainWindow"/>: navigation rail, top bar (project, global search, status pills),
/// status bar (progress, log toggle), log panel, toasts and the setup overlay.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>Navigation keys in rail order.</summary>
    public static IReadOnlyList<string> PageKeys { get; } = ["map", "vehicles", "weapons", "spawns", "assets", "projects", "settings"];

    private readonly List<IDisposable> _disposables = [];

    /// <summary>Creates the view model over <paramref name="services"/>.</summary>
    public MainWindowViewModel(AppServices services)
    {
        Services = services;
        NavItems =
        [
            new NavItemViewModel("map", "Map", "Icon.Map", null, () => Track(new MapPageViewModel(services, OpenSetup))),
            new NavItemViewModel("vehicles", "Vehicles", "Icon.Vehicle", null, () => Track(new VehiclesPageViewModel(services, OpenSetup))),
            new NavItemViewModel("weapons", "Weapons", "Icon.Weapon", null, () => Track(new WeaponsPageViewModel(services, OpenSetup))),
            new NavItemViewModel("spawns", "Spawns", "Icon.Pin", null, () => Track(new SpawnsPageViewModel(services, OpenSetup))),
            new NavItemViewModel("assets", "Assets", "Icon.Assets", null, () => Track(new AssetsPageViewModel(services, OpenSetup, PlaceMeshInMap))),
            new NavItemViewModel("projects", "Projects", "Icon.Projects", null, () => Track(new ProjectsPageViewModel(services))),
            new NavItemViewModel("settings", "Settings", "Icon.Settings", null, () => Track(new SettingsPageViewModel(services, OpenSetup))),
        ];

        _isLogOpen = services.UiState.Current.LogPanelOpen;
        services.Workspace.PropertyChanged += OnWorkspaceChanged;
        services.SettingsChanged += OnSettingsOrKeyChanged;
        services.KeyChanged += OnSettingsOrKeyChanged;
        services.Mcp.StateChanged += OnMcpStateChanged;
        services.Notifications.Toasts.CollectionChanged += OnToastsChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshPills();

        var last = services.UiState.Current.LastPage;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.Key == last) ?? NavItems[0];
    }

    /// <summary>Services.</summary>
    public AppServices Services { get; }

    /// <summary>Window title.</summary>
    public string Title { get; } = $"{CoreInfo.ProductName} {ShortVersion}";

    /// <summary>Product version (without the source-revision suffix).</summary>
    public string Version { get; } = "v" + ShortVersion;

    /// <summary><see cref="CoreInfo.Version"/> without the <c>+&lt;commit&gt;</c> build metadata.</summary>
    public static string ShortVersion => CoreInfo.Version.Split('+')[0];

    /// <summary>Navigation rail entries.</summary>
    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    /// <summary>Status bar / progress.</summary>
    public OperationRunner Operations => Services.Operations;

    /// <summary>Log panel.</summary>
    public LogPanelViewModel Log => Services.Log;

    /// <summary>Toasts.</summary>
    public ObservableCollection<ToastViewModel> Toasts => Services.Notifications.Toasts;

    /// <summary>True while the status bar's notification tray shows a toast (the last log line steps aside).</summary>
    public bool HasToasts => Toasts.Count > 0;

    /// <summary>Project session (name in the top bar).</summary>
    public ProjectSession Projects => Services.Projects;

    /// <summary>Pill: game paks.</summary>
    public StatusPillViewModel GamePill { get; } = new("Pill.Game", "Icon.Pak");

    /// <summary>Pill: server paks.</summary>
    public StatusPillViewModel ServerPill { get; } = new("Pill.Server", "Icon.Server");

    /// <summary>Pill: AES key.</summary>
    public StatusPillViewModel KeyPill { get; } = new("Pill.Key", "Icon.Key");

    /// <summary>Pill: AI control (the built-in MCP server).</summary>
    public StatusPillViewModel AiPill { get; } = new("Pill.Ai", "Icon.Ai");

    /// <summary>Selected rail entry.</summary>
    [ObservableProperty]
    private NavItemViewModel? _selectedNavItem;

    /// <summary>Page shown in the content area.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private PageViewModel? _currentPage;

    /// <summary>Global search text.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Log panel visibility.</summary>
    [ObservableProperty]
    private bool _isLogOpen;

    /// <summary>Setup overlay visibility.</summary>
    [ObservableProperty]
    private bool _isSetupOpen;

    /// <summary>Setup overlay content.</summary>
    [ObservableProperty]
    private SetupViewModel? _setup;

    /// <summary>Placeholder text of the search box for the current page.</summary>
    public string SearchWatermark => CurrentPage switch
    {
        AssetsPageViewModel => Loc.T("Search.Assets"),
        MapPageViewModel => Loc.T("Search.Map"),
        _ => Loc.T("Search.Default"),
    };

    /// <summary>
    /// Startup: opens the setup overlay on first run, connects to the configured paks and reopens the last project.
    /// Never throws.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            Services.Logger.LogInformation("{Product} {Version} started (data folder {Folder}).", CoreInfo.ProductName, CoreInfo.Version, Services.DataDirectory);
            var settings = Services.Settings.Load();
            if (string.IsNullOrWhiteSpace(settings.GamePaksFolder) && !Services.UiState.Current.SetupCompleted)
            {
                OpenSetup();
            }

            if (!string.IsNullOrWhiteSpace(settings.LastProjectPath) && Level.Projects.Project.Exists(settings.LastProjectPath))
            {
                var path = settings.LastProjectPath;
                await Services.Operations.RunAsync(Loc.T("Shell.OpeningLastProject"), (_, ct) => Services.Projects.OpenAsync(path, ct)).ConfigureAwait(true);
            }

            _ = StartUpdatesAsync();
            if (!string.IsNullOrWhiteSpace(settings.GamePaksFolder))
            {
                await ConnectAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Services.Logger.LogError(ex, "Startup failed: {Message}", ex.Message);
            Services.Notifications.Error(Loc.T("Shell.StartupProblem"), ex.Message);
        }
    }

    /// <summary>Selects the page with <paramref name="key"/>; returns it (null for unknown keys).</summary>
    public PageViewModel? NavigateTo(string key)
    {
        var item = NavItems.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        SelectedNavItem = item;
        return item.Page;
    }

    /// <summary>Switches to the Map page and adds a new actor drawing <paramref name="meshObjectPath"/> there.</summary>
    public void PlaceMeshInMap(string meshObjectPath)
    {
        if (NavigateTo("map") is MapPageViewModel map)
        {
            map.AddMeshActor(meshObjectPath);
        }
    }

    /// <summary>Shows the setup overlay.</summary>
    [RelayCommand]
    public void OpenSetup()
    {
        Setup = new SetupViewModel(Services, CloseSetup);
        IsSetupOpen = true;
    }

    /// <summary>Connects (or reconnects) to the configured game paks.</summary>
    [RelayCommand]
    public async Task ConnectAsync()
    {
        await Services.Operations.RunAsync(Loc.T("Shell.Connecting"), (p, ct) => Services.Workspace.ConnectAsync(p, ct)).ConfigureAwait(true);
        RefreshPills();
    }

    private void OnToastsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasToasts));

    /// <inheritdoc />
    public void Dispose()
    {
        Services.Workspace.PropertyChanged -= OnWorkspaceChanged;
        Services.SettingsChanged -= OnSettingsOrKeyChanged;
        Services.KeyChanged -= OnSettingsOrKeyChanged;
        Services.Mcp.StateChanged -= OnMcpStateChanged;
        Services.Notifications.Toasts.CollectionChanged -= OnToastsChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        _disposables.Clear();
    }

    /// <summary>Recomputes the three status pills.</summary>
    public void RefreshPills()
    {
        RefreshAiPill();
        var ws = Services.Workspace;
        switch (ws.State)
        {
            case WorkspaceState.Connecting:
                GamePill.Set(Loc.T("Pill.Connecting"), PillState.Busy, ws.StatusText);
                break;
            case WorkspaceState.Connected:
                GamePill.Set(ws.IsLoose ? Loc.T("Pill.LooseFolder") : Loc.T("Pill.Connected"), ws.LockedContainers > 0 ? PillState.Warn : PillState.Ok, ws.StatusText);
                break;
            case WorkspaceState.Error:
                GamePill.Set(Loc.T("Pill.Error"), PillState.Error, ws.StatusText);
                break;
            default:
                var configured = Services.Settings.Load().GamePaksFolder;
                if (string.IsNullOrWhiteSpace(configured))
                {
                    GamePill.Set(Loc.T("Pill.NotSet"), PillState.Off, Loc.T("Pill.Game.NotSetDetail"));
                }
                else
                {
                    GamePill.Set(Loc.T("Pill.NotConnected"), PillState.Warn, configured);
                }

                break;
        }

        var server = Services.Settings.Load().ServerPaksFolder;
        if (string.IsNullOrWhiteSpace(server))
        {
            ServerPill.Set(Loc.T("Pill.NotSet"), PillState.Off, Loc.T("Pill.Server.NotSetDetail"));
        }
        else if (GameLocator.ContainsPaks(server))
        {
            ServerPill.Set(Loc.T("Pill.Found"), PillState.Ok, server);
        }
        else
        {
            ServerPill.Set(Loc.T("Pill.Missing"), PillState.Warn, Loc.F("Pill.Server.NoPaks", server));
        }

        if (Services.Keys.HasKey)
        {
            KeyPill.Set(ws.LockedContainers > 0 ? Loc.T("Pill.Check") : Loc.T("Pill.Set"), ws.LockedContainers > 0 ? PillState.Warn : PillState.Ok,
                ws.LockedContainers > 0 ? Loc.T("Pill.Key.LockedDetail") : Loc.T("Pill.Key.StoredDetail"));
        }
        else
        {
            KeyPill.Set(Loc.T("Pill.NotSet"), PillState.Off, Loc.T("Pill.Key.NotSetDetail"));
        }
    }

    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            CurrentPage = value.Page;
            CurrentPage.OnNavigatedTo();
            if (CurrentPage is ISearchablePage searchable && !string.IsNullOrEmpty(SearchText))
            {
                searchable.ApplySearch(SearchText);
            }

            Services.UiState.Update(u => u with { LastPage = value.Key });
        }
        catch (Exception ex)
        {
            Services.Logger.LogError(ex, "Could not open the {Page} page: {Message}", value.Title, ex.Message);
            Services.Notifications.Error(Loc.F("Shell.CouldNotOpenPage", value.Title), ex.Message);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (CurrentPage is ISearchablePage searchable)
        {
            searchable.ApplySearch(value);
        }
    }

    partial void OnIsLogOpenChanged(bool value) => Services.UiState.Update(u => u with { LogPanelOpen = value });

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (key is not null)
        {
            NavigateTo(key);
        }
    }

    [RelayCommand]
    private void SubmitSearch()
    {
        if (CurrentPage is not ISearchablePage)
        {
            var text = SearchText;
            NavigateTo("assets");
            (CurrentPage as ISearchablePage)?.ApplySearch(text);
        }
    }

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void Undo()
    {
        try
        {
            Services.Projects.Undo();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Services.Notifications.Error(Loc.T("Shell.UndoFailed"), ex.Message);
        }
    }

    [RelayCommand]
    private void Redo()
    {
        try
        {
            Services.Projects.Redo();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Services.Notifications.Error(Loc.T("Shell.RedoFailed"), ex.Message);
        }
    }

    private void CloseSetup(bool saved)
    {
        IsSetupOpen = false;
        Setup = null;
        RefreshPills();
        if (saved)
        {
            Services.Notifications.Success(Loc.T("Shell.SetupSaved"), Loc.T("Shell.SetupSavedDetail"));
            _ = ConnectAsync();
        }
    }

    private T Track<T>(T page) where T : PageViewModel
    {
        if (page is IDisposable d)
        {
            _disposables.Add(d);
        }

        return page;
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameWorkspace.State) or nameof(GameWorkspace.StatusText) or nameof(GameWorkspace.LockedContainers))
        {
            Services.Dispatcher.Invoke(RefreshPills);
        }
    }

    private void OnSettingsOrKeyChanged(object? sender, EventArgs e) => Services.Dispatcher.Invoke(RefreshPills);

    private void OnMcpStateChanged(object? sender, EventArgs e) => Services.Dispatcher.Invoke(RefreshAiPill);

    /// <summary>Re-reads every text composed here after the UI language changed (XAML texts follow on their own).</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in NavItems)
        {
            item.OnLanguageChanged();
        }

        foreach (var pill in new[] { GamePill, ServerPill, KeyPill, AiPill })
        {
            pill.RefreshLabel();
        }

        RefreshPills();
        Operations.OnLanguageChanged();
        Services.Projects.OnLanguageChanged();
        OnPropertyChanged(nameof(SearchWatermark));
    }

    /// <summary>Updates the AI pill from <see cref="AppServices.Mcp"/>.</summary>
    public void RefreshAiPill()
    {
        var mcp = Services.Mcp;
        if (mcp.IsRunning)
        {
            var detail = mcp.StatusText
                         + (mcp.ClientName.Length > 0 ? Environment.NewLine + Loc.F("Pill.Ai.Client", mcp.ClientName) : string.Empty)
                         + (mcp.LastActivity.Length > 0 ? Environment.NewLine + Loc.F("Pill.Ai.Last", mcp.LastActivity) : string.Empty);
            AiPill.Set(mcp.CallCount > 0 ? Loc.F("Pill.Ai.Calls", mcp.CallCount) : Loc.T("Pill.On"), PillState.Ok, detail);
        }
        else if (mcp.Error is { } error)
        {
            AiPill.Set(Loc.T("Pill.Error"), PillState.Error, error);
        }
        else
        {
            AiPill.Set(Loc.T("Pill.Off"), PillState.Off, Loc.T("Pill.Ai.OffDetail"));
        }
    }
}
