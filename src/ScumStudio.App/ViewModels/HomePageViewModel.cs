using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>One line of the Home checklist: a part of the setup, how it stands and what to press to fix it.</summary>
public sealed partial class HomeRow : ObservableObject
{
    private readonly Action? _action;

    /// <summary>Creates a row.</summary>
    /// <param name="title">The part (game files, key, server ...).</param>
    /// <param name="detail">How it stands, or what is missing.</param>
    /// <param name="state">Done, to do, or not applicable.</param>
    /// <param name="actionLabel">The button's text, or null for none.</param>
    /// <param name="action">What the button does.</param>
    public HomeRow(string title, string detail, WelcomeCheckState state, string? actionLabel = null, Action? action = null)
    {
        Title = title;
        Detail = detail;
        State = state;
        ActionLabel = actionLabel;
        _action = action;
    }

    /// <summary>The part.</summary>
    public string Title { get; }

    /// <summary>How it stands.</summary>
    public string Detail { get; }

    /// <summary>Done, to do or failed.</summary>
    public WelcomeCheckState State { get; }

    /// <summary>A short mark for the state.</summary>
    public string Mark => State switch
    {
        WelcomeCheckState.Ok => "✔",
        WelcomeCheckState.Warn => "!",
        WelcomeCheckState.Fail => "✖",
        _ => "…",
    };

    /// <summary>The button's text.</summary>
    public string? ActionLabel { get; }

    /// <summary>The row has a button.</summary>
    public bool HasAction => ActionLabel is not null && _action is not null;

    /// <summary>Runs the row's action.</summary>
    [RelayCommand]
    private void Run() => _action?.Invoke();
}

/// <summary>
/// The Home page: what is set up and what is left, in one list, with a button on each open item. It is what a new user sees after the
/// first-run wizard: the game's files and key, the server and its UE4SS, the class dump, the loot presets and the open project.
/// </summary>
public sealed partial class HomePageViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly Action<string> _navigate;
    private readonly Action _openSetup;
    private readonly Action? _openWelcome;

    /// <summary>Creates the page.</summary>
    /// <param name="services">Services.</param>
    /// <param name="navigate">Opens a page by its key.</param>
    /// <param name="openSetup">Opens the detailed setup dialog.</param>
    /// <param name="openWelcome">Opens the first-run wizard again (null: the button is hidden).</param>
    public HomePageViewModel(AppServices services, Action<string> navigate, Action openSetup, Action? openWelcome = null)
        : base("home", "Home", "What is set up and what is left, in one place.")
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _openSetup = openSetup ?? throw new ArgumentNullException(nameof(openSetup));
        _openWelcome = openWelcome;
        services.SettingsChanged += (_, _) => services.Dispatcher.Invoke(Refresh);
        services.KeyChanged += (_, _) => services.Dispatcher.Invoke(Refresh);
        Refresh();
    }

    /// <summary>The checklist.</summary>
    public ObservableCollection<HomeRow> Rows { get; } = [];

    /// <summary>One line over the list: all set, or how many items are left.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>The wizard can be started again from here.</summary>
    public bool CanOpenWizard => _openWelcome is not null;

    /// <summary>Starts the first-run wizard again (language, game, server, automatic setup).</summary>
    [RelayCommand]
    private void OpenWizard() => _openWelcome?.Invoke();

    /// <inheritdoc />
    public override string IconKey => "Icon.Home";

    /// <inheritdoc />
    public override void OnNavigatedTo() => Refresh();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Refresh();
    }

    /// <summary>Looks at everything again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        var settings = _services.Settings.Load();
        var workspace = _services.Workspace;
        var setup = Loc.T("Home.Action.Setup");

        Rows.Add(string.IsNullOrWhiteSpace(settings.GamePaksFolder)
            ? new HomeRow(Loc.T("Home.Game"), Loc.T("Home.Game.None"), WelcomeCheckState.Warn, setup, _openSetup)
            : workspace.State == WorkspaceState.Connected
                ? new HomeRow(Loc.T("Home.Game"), Loc.F("Home.Game.Ok", workspace.StatusText), WelcomeCheckState.Ok)
                : new HomeRow(Loc.T("Home.Game"), Loc.F("Home.Game.NotConnected", workspace.StatusText), WelcomeCheckState.Warn, setup, _openSetup));

        Rows.Add(_services.Keys.HasKey
            ? new HomeRow(Loc.T("Home.Key"), Loc.T("Home.Key.Ok"), WelcomeCheckState.Ok)
            : new HomeRow(Loc.T("Home.Key"), Loc.T("Home.Key.None"), WelcomeCheckState.Warn, setup, _openSetup));

        var server = ServerInstall.Find(settings.ServerPaksFolder);
        if (server is null)
        {
            Rows.Add(new HomeRow(Loc.T("Home.Server"), Loc.T("Home.Server.None"), WelcomeCheckState.Warn, setup, _openSetup));
        }
        else
        {
            Rows.Add(new HomeRow(Loc.T("Home.Server"), Loc.F("Home.Server.Ok", server.Win64), WelcomeCheckState.Ok));
            var openServer = Loc.T("Home.Action.OpenServer");
            Rows.Add(server.IsUe4ssActive
                ? new HomeRow(Loc.T("Home.Ue4ss"), Loc.T("Home.Ue4ss.Ok"), WelcomeCheckState.Ok)
                : new HomeRow(Loc.T("Home.Ue4ss"), Loc.T("Home.Ue4ss.None"), WelcomeCheckState.Warn, openServer, () => _navigate("server")));
            Rows.Add(File.Exists(Path.Combine(server.Ue4ssFolder, "scum_dump.txt"))
                ? new HomeRow(Loc.T("Home.Dump"), Loc.T("Home.Dump.Ok"), WelcomeCheckState.Ok)
                : new HomeRow(Loc.T("Home.Dump"), Loc.T("Home.Dump.None"), WelcomeCheckState.Warn, server.HasUe4ss ? openServer : null, () => _navigate("server")));
            Rows.Add(new LootOverrideStore(server).HasDefaults
                ? new HomeRow(Loc.T("Home.Loot"), Loc.T("Home.Loot.Ok"), WelcomeCheckState.Ok)
                : new HomeRow(Loc.T("Home.Loot"), Loc.F("Home.Loot.None", LootOverrideStore.ExportCommand), WelcomeCheckState.Warn));
        }

        Rows.Add(_services.Projects.Current is { } project
            ? new HomeRow(Loc.T("Home.Project"), Loc.F("Home.Project.Ok", project.Manifest.Name), WelcomeCheckState.Ok, Loc.T("Home.Action.OpenProjects"), () => _navigate("projects"))
            : new HomeRow(Loc.T("Home.Project"), Loc.T("Home.Project.None"), WelcomeCheckState.Warn, Loc.T("Home.Action.OpenProjects"), () => _navigate("projects")));

        var left = Rows.Count(r => r.State != WelcomeCheckState.Ok);
        Summary = left == 0 ? Loc.T("Home.Summary.Ready") : Loc.F("Home.Summary.Left", left);
    }
}
