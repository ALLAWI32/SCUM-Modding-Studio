using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Games;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>The steps of the first-run wizard.</summary>
public enum WelcomeStep
{
    /// <summary>Pick the interface language.</summary>
    Language,

    /// <summary>Pick the game folder.</summary>
    Game,

    /// <summary>Pick the dedicated server folder (optional).</summary>
    Server,

    /// <summary>Everything else is set up by itself, with the result per part.</summary>
    Setup,
}

/// <summary>How far one part of the automatic setup is.</summary>
public enum WelcomeCheckState
{
    /// <summary>Working on it.</summary>
    Wait,

    /// <summary>Done.</summary>
    Ok,

    /// <summary>Done, but something is left for the user.</summary>
    Warn,

    /// <summary>Could not be done.</summary>
    Fail,
}

/// <summary>One line of the automatic setup's result.</summary>
public sealed partial class WelcomeRow : ObservableObject
{
    /// <summary>Creates a row.</summary>
    public WelcomeRow(string text, WelcomeCheckState state)
    {
        _text = text;
        _state = state;
    }

    /// <summary>What was done or what is left.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>How far it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private WelcomeCheckState _state;

    /// <summary>A short mark for the state.</summary>
    public string Mark => State switch
    {
        WelcomeCheckState.Ok => "✔",
        WelcomeCheckState.Warn => "!",
        WelcomeCheckState.Fail => "✖",
        _ => "…",
    };
}

/// <summary>
/// The first-run wizard: the language, the game folder and (optionally) the server folder, then everything else by itself: the
/// game's key from the net and checked on the game's files, the mod folders (<c>~mods</c> of the game and of the server), a look
/// at the server's UE4SS. It replaces the long setup dialog for a new user; the dialog stays in Settings for details.
/// </summary>
public sealed partial class WelcomeViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<bool> _close;
    private bool _detected;

    /// <summary>Creates the wizard.</summary>
    /// <param name="services">Services.</param>
    /// <param name="close">Called with true when the user finished, false when they skipped.</param>
    public WelcomeViewModel(AppServices services, Action<bool> close)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _close = close ?? throw new ArgumentNullException(nameof(close));
        var settings = services.Settings.Load();
        _gamePath = settings.GamePaksFolder ?? string.Empty;
        _serverPath = settings.ServerPaksFolder ?? string.Empty;
        // First run: the system's language; run again from Home: the language the user already uses.
        var wanted = services.UiState.Current.SetupCompleted
            ? settings.Ui.Language
            : SystemLanguage(services.SystemCulture, Loc.Languages.Select(x => x.Code));
        _selectedLanguage = Loc.Languages.FirstOrDefault(l => l.Code == wanted) ?? Loc.Languages[0];
        Loc.Instance.Language = _selectedLanguage.Code;
    }

    /// <summary>The languages to pick from, each written in its own language.</summary>
    public IReadOnlyList<LanguageOption> Languages => Loc.Languages;

    /// <summary>The result of the automatic setup, line by line.</summary>
    public ObservableCollection<WelcomeRow> Rows { get; } = [];

    /// <summary>The step shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanguageStep), nameof(IsGameStep), nameof(IsServerStep), nameof(IsSetupStep), nameof(StepText), nameof(CanBack), nameof(CanNext), nameof(ShowNext))]
    private WelcomeStep _step = WelcomeStep.Language;

    /// <summary>The picked language.</summary>
    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    /// <summary>The game's <c>SCUM\Content\Paks</c> folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNext), nameof(GameProblem))]
    private string _gamePath;

    /// <summary>What was found for the game, or what to do.</summary>
    [ObservableProperty]
    private string _gameStatus = string.Empty;

    /// <summary>The server's <c>SCUM\Content\Paks</c> folder.</summary>
    [ObservableProperty]
    private string _serverPath;

    /// <summary>What was found for the server, or what to do.</summary>
    [ObservableProperty]
    private string _serverStatus = string.Empty;

    /// <summary>The user has no server.</summary>
    [ObservableProperty]
    private bool _noServer;

    /// <summary>The automatic setup is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBack), nameof(CanNext))]
    private bool _isBusy;

    /// <summary>The automatic setup has finished.</summary>
    [ObservableProperty]
    private bool _isDone;

    /// <summary>The language step is shown.</summary>
    public bool IsLanguageStep => Step == WelcomeStep.Language;

    /// <summary>The game step is shown.</summary>
    public bool IsGameStep => Step == WelcomeStep.Game;

    /// <summary>The server step is shown.</summary>
    public bool IsServerStep => Step == WelcomeStep.Server;

    /// <summary>The setup step is shown.</summary>
    public bool IsSetupStep => Step == WelcomeStep.Setup;

    /// <summary>"Step 2 of 4".</summary>
    public string StepText => Loc.F("Welcome.StepOf", (int)Step + 1, 4);

    /// <summary>The "next" button is shown (the last step has "start" instead).</summary>
    public bool ShowNext => Step != WelcomeStep.Setup;

    /// <summary>The "back" button works (not on the first step, not while the setup runs).</summary>
    public bool CanBack => Step is not WelcomeStep.Language and not WelcomeStep.Setup && !IsBusy;

    /// <summary>The "next" button works (a game folder is needed to leave the game step).</summary>
    public bool CanNext => !IsBusy && (Step != WelcomeStep.Game || (!string.IsNullOrWhiteSpace(GamePath) && GameProblem is null));

    /// <summary>Why the game folder cannot be used (it is the server's, for instance), or null.</summary>
    public string? GameProblem => SetupViewModel.GameFolderProblem(GamePath);

    /// <summary>
    /// The interface language that fits a system culture: its language when the app has it, Croatian, Bosnian and Serbian
    /// share one Latin-script table (<c>sh</c>), anything else English.
    /// </summary>
    public static string SystemLanguage(CultureInfo culture, IEnumerable<string> available)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var have = available.ToList();
        var code = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        if (code is "hr" or "bs" or "sr")
        {
            code = "sh";
        }

        return have.Contains(code) ? code : Loc.DefaultLanguage;
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null)
        {
            return;
        }

        // Saved first: switching the language makes every page read the settings again.
        _services.UpdateSettings(s => s with { Ui = s.Ui with { Language = value.Code } });
        Loc.Instance.Language = value.Code;
        OnPropertyChanged(nameof(StepText));
        RefreshStatuses();
    }

    /// <summary>Looks for the game and the dedicated server in the Steam libraries and fills the empty fields.</summary>
    [RelayCommand]
    private async Task DetectAsync()
    {
        IsBusy = true;
        try
        {
            var locator = _services.CreateLocator();
            var (game, server) = await Task.Run(() => (locator.FindGame(), locator.FindDedicatedServer())).ConfigureAwait(true);
            if (game is not null && string.IsNullOrWhiteSpace(GamePath))
            {
                GamePath = game.PaksDirectory;
            }

            if (server is not null && string.IsNullOrWhiteSpace(ServerPath))
            {
                ServerPath = server.PaksDirectory;
            }

            _detected = true;
            RefreshStatuses();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Welcome: detection failed ({Message}).", ex.Message);
            GameStatus = Loc.T("Welcome.NotFound");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Picks the game folder.</summary>
    [RelayCommand]
    private async Task BrowseGameAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickGame"), GamePath).ConfigureAwait(true) is { } path)
        {
            GamePath = GameLocator.ResolvePaksFolder(path) ?? path;
            RefreshStatuses();
        }
    }

    /// <summary>Picks the server folder.</summary>
    [RelayCommand]
    private async Task BrowseServerAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickServer"), ServerPath).ConfigureAwait(true) is { } path)
        {
            ServerPath = GameLocator.ResolvePaksFolder(path) ?? path;
            NoServer = false;
            RefreshStatuses();
        }
    }

    /// <summary>Goes to the next step; leaving the server step starts the automatic setup.</summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case WelcomeStep.Language:
                Step = WelcomeStep.Game;
                if (!_detected)
                {
                    await DetectAsync().ConfigureAwait(true);
                }

                break;
            case WelcomeStep.Game:
                Step = WelcomeStep.Server;
                break;
            case WelcomeStep.Server:
                Step = WelcomeStep.Setup;
                await RunSetupAsync().ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Goes back one step.</summary>
    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() => Step = Step == WelcomeStep.Server ? WelcomeStep.Game : WelcomeStep.Language;

    /// <summary>Closes the wizard without setting anything up (the language stays).</summary>
    [RelayCommand]
    private void Skip()
    {
        _services.UiState.Update(u => u with { SetupCompleted = true });
        _close(false);
    }

    /// <summary>Closes the wizard after the setup.</summary>
    [RelayCommand]
    private void Finish() => _close(true);

    partial void OnStepChanged(WelcomeStep value)
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    partial void OnGamePathChanged(string value)
    {
        NextCommand.NotifyCanExecuteChanged();
        RefreshStatuses();
    }

    partial void OnServerPathChanged(string value) => RefreshStatuses();

    partial void OnIsBusyChanged(bool value)
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void RefreshStatuses()
    {
        GameStatus = GameProblem ?? (string.IsNullOrWhiteSpace(GamePath) ? Loc.T("Welcome.NotFound") : Loc.F("Welcome.Found", GamePath));
        ServerStatus = string.IsNullOrWhiteSpace(ServerPath) ? Loc.T("Welcome.NotFound") : Loc.F("Welcome.Found", ServerPath);
    }

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');

    /// <summary>Saves the folders (the mod folders are the <c>~mods</c> folders of the game and the server), gets the game's key and looks at the server.</summary>
    private async Task RunSetupAsync()
    {
        IsBusy = true;
        Rows.Clear();
        try
        {
            var game = Clean(GamePath) is { } g ? GameLocator.ResolvePaksFolder(g) ?? g : null;
            var server = NoServer || Clean(ServerPath) is null ? null : GameLocator.ResolvePaksFolder(Clean(ServerPath)!) ?? Clean(ServerPath);
            _services.UpdateSettings(s => s with
            {
                GamePaksFolder = game,
                ServerPaksFolder = server,
                ClientModsOutputFolder = game is null ? null : Path.Combine(game, GameInstallModsFolder),
                ServerModsOutputFolder = server is null ? null : Path.Combine(server, GameInstallModsFolder),
            });
            Rows.Add(new WelcomeRow(Loc.T("Welcome.Row.Folders"), WelcomeCheckState.Ok));

            var key = new WelcomeRow(Loc.T("Welcome.Row.KeyWorking"), WelcomeCheckState.Wait);
            Rows.Add(key);
            await FindKeyAsync(key, game).ConfigureAwait(true);

            if (server is null)
            {
                Rows.Add(new WelcomeRow(Loc.T("Welcome.Row.NoServer"), WelcomeCheckState.Warn));
            }
            else if (ServerInstall.Find(server) is { } install)
            {
                Rows.Add(new WelcomeRow(Loc.F("Welcome.Row.Server", install.Win64), WelcomeCheckState.Ok));
                Rows.Add(install.HasUe4ss
                    ? new WelcomeRow(Loc.T("Welcome.Row.Ue4ssOk"), WelcomeCheckState.Ok)
                    : new WelcomeRow(Loc.T("Welcome.Row.Ue4ssNone"), WelcomeCheckState.Warn));
            }
            else
            {
                Rows.Add(new WelcomeRow(Loc.F("Welcome.Row.ServerMissing", server), WelcomeCheckState.Fail));
            }

            _services.UiState.Update(u => u with { SetupCompleted = true });
            Rows.Add(new WelcomeRow(Loc.T("Welcome.Row.Done"), WelcomeCheckState.Ok));
            IsDone = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Rows.Add(new WelcomeRow(ex.Message, WelcomeCheckState.Fail));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private const string GameInstallModsFolder = "~mods";

    /// <summary>The game's key: the stored one is kept; otherwise it is looked up on the net and kept only when it opens the game's files.</summary>
    private async Task FindKeyAsync(WelcomeRow row, string? game)
    {
        if (_services.Keys.HasKey)
        {
            row.Text = Loc.T("Welcome.Row.KeyStored");
            row.State = WelcomeCheckState.Ok;
            return;
        }

        try
        {
            var key = await _services.FindKeyOnline(CancellationToken.None).ConfigureAwait(true);
            if (key is not null && game is not null)
            {
                var logger = _services.LoggerFactory.CreateLogger("ScumStudio.App.ConnectionTest");
                var test = await Task.Run(() => ConnectionTester.Test(game, key, logger)).ConfigureAwait(true);
                if (test.IsHealthy && _services.TryStoreKey(key))
                {
                    row.Text = Loc.T("Welcome.Row.KeyOk");
                    row.State = WelcomeCheckState.Ok;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            _services.Logger.LogWarning("Welcome: the game's key could not be found online ({Message}).", ex.Message);
        }

        row.Text = Loc.T("Welcome.Row.KeyFail");
        row.State = WelcomeCheckState.Warn;
    }
}
