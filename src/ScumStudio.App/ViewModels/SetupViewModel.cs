using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Games;
using ScumStudio.Core.Security;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// First-run setup (also reachable from the top bar and Settings): game and server Paks folders, mod output folders,
/// the AES key and a connection test. The key is validated, stored through <see cref="ProtectedKeyStore"/> and cleared
/// from the view model immediately; it is never displayed, logged or kept in settings.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<bool> _close;

    /// <summary>Creates the view model from the current settings.</summary>
    /// <param name="services">Services.</param>
    /// <param name="close">Called with true after a successful save, false on cancel.</param>
    public SetupViewModel(AppServices services, Action<bool> close)
    {
        _services = services;
        _close = close;
        var settings = services.Settings.Load();
        _gamePaksFolder = settings.GamePaksFolder ?? string.Empty;
        _serverPaksFolder = settings.ServerPaksFolder ?? string.Empty;
        _clientModsFolder = settings.ClientModsOutputFolder ?? string.Empty;
        _serverModsFolder = settings.ServerModsOutputFolder ?? string.Empty;
        RefreshKeyStatus();

        // No key yet: look it up online right away; the field stays there for when that fails.
        if (!HasStoredKey)
        {
            _ = FindKeyOnlineCommand.ExecuteAsync(null);
        }
    }

    /// <summary>What the last online key lookup found, or null before one.</summary>
    [ObservableProperty]
    private string? _keyOnlineStatus;

    /// <summary>True while the key is looked up online.</summary>
    [ObservableProperty]
    private bool _isFindingKey;

    /// <summary>
    /// Looks the key up on the public key list (<see cref="OnlineKeyFinder"/>), tests it on the game's paks when the folder
    /// is set and stores it only when it opens them; otherwise asks for it to be pasted. The key is never shown or logged.
    /// </summary>
    [RelayCommand]
    private async Task FindKeyOnlineAsync()
    {
        if (IsFindingKey)
        {
            return;
        }

        IsFindingKey = true;
        KeyOnlineStatus = Loc.T("Setup.FindingKey");
        try
        {
            string? key;
            try
            {
                key = await _services.FindKeyOnline(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _services.Logger.LogWarning("Online key lookup: the key list could not be read ({Message}).", ex.Message);
                KeyOnlineStatus = Loc.T("Setup.KeyListUnreachable");
                return;
            }

            if (key is null)
            {
                _services.Logger.LogWarning("Online key lookup: no key for SCUM on the list.");
                KeyOnlineStatus = Loc.T("Setup.KeyNotFoundOnline");
                return;
            }

            // Tested on the game's own paks before it is kept: a stale key would only lock them.
            var folder = GamePaksFolder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var logger = _services.LoggerFactory.CreateLogger("ScumStudio.App.ConnectionTest");
                var test = await Task.Run(() => ConnectionTester.Test(folder, key, logger)).ConfigureAwait(true);
                if (!test.IsHealthy)
                {
                    _services.Logger.LogWarning("Online key lookup: the key on the list does not open this game's paks.");
                    KeyOnlineStatus = Loc.T("Setup.KeyOnlineWrong");
                    return;
                }
            }

            if (_services.TryStoreKey(key))
            {
                _services.Logger.LogInformation("Online key lookup: the key was found and stored.");
                KeyOnlineStatus = Loc.T("Setup.KeyFoundOnline");
                KeyError = null;
                RefreshKeyStatus();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            KeyOnlineStatus = Loc.F("Setup.KeyNotStored", ex.Message);
        }
        finally
        {
            IsFindingKey = false;
        }
    }

    /// <summary>The game's <c>SCUM/Content/Paks</c> folder (install root or SCUM.exe folder also accepted).</summary>
    [ObservableProperty]
    private string _gamePaksFolder;

    /// <summary>The dedicated server's Paks folder (optional).</summary>
    [ObservableProperty]
    private string _serverPaksFolder;

    /// <summary>Where client mod paks are written (e.g. <c>Paks/~mods</c>).</summary>
    [ObservableProperty]
    private string _clientModsFolder;

    /// <summary>Where server mod paks are written.</summary>
    [ObservableProperty]
    private string _serverModsFolder;

    /// <summary>Key being typed (masked in the view). Cleared as soon as it is stored or the dialog closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyInput))]
    private string _keyInput = string.Empty;

    /// <summary>Validation message for the key, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyError))]
    private string? _keyError;

    /// <summary>True when a key is stored.</summary>
    [ObservableProperty]
    private bool _hasStoredKey;

    /// <summary>"A key is stored (protected with Windows DPAPI)" etc.</summary>
    [ObservableProperty]
    private string _keyStatusText = string.Empty;

    /// <summary>Result of the last auto-detection.</summary>
    [ObservableProperty]
    private string? _detectStatus;

    /// <summary>Result of the last connection test.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string? _testResult;

    /// <summary>True when the last test mounted paks without locked ones; null before any test.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestOk), nameof(IsTestFailed))]
    private bool? _testSucceeded;

    /// <summary>True while detecting or testing.</summary>
    [ObservableProperty]
    private bool _isWorking;

    /// <summary>True when something was typed into the key box.</summary>
    public bool HasKeyInput => !string.IsNullOrEmpty(KeyInput);

    /// <summary>True when <see cref="KeyError"/> is set.</summary>
    public bool HasKeyError => KeyError is not null;

    /// <summary>True when <see cref="TestResult"/> is set.</summary>
    public bool HasTestResult => TestResult is not null;

    /// <summary>True after a healthy connection test (green result line).</summary>
    public bool IsTestOk => TestSucceeded == true;

    /// <summary>True after a failed connection test (red result line).</summary>
    public bool IsTestFailed => TestSucceeded == false;

    /// <summary>How the key is protected on this OS.</summary>
    public string KeyProtectionText => ProtectedKeyStore.IsOsProtected
        ? Loc.T("Setup.Protection.Dpapi")
        : Loc.T("Setup.Protection.File");

    /// <summary>Data folder shown for reference.</summary>
    public string DataDirectory => _services.DataDirectory;

    /// <summary>"Settings folder: …" in the footer.</summary>
    public string SettingsFolderText => Loc.F("Setup.SettingsFolder", DataDirectory);

    partial void OnKeyInputChanged(string value)
    {
        if (KeyError is not null)
        {
            KeyError = null;
        }
    }

    /// <summary>Finds the Steam client and dedicated server installs and fills empty fields.</summary>
    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        IsWorking = true;
        try
        {
            var locator = _services.CreateLocator();
            var (game, server) = await Task.Run(() => (locator.FindGame(), locator.FindDedicatedServer())).ConfigureAwait(true);
            var found = new List<string>();
            if (game is not null)
            {
                GamePaksFolder = game.PaksDirectory;
                if (string.IsNullOrWhiteSpace(ClientModsFolder))
                {
                    ClientModsFolder = game.ModsDirectory;
                }

                found.Add(Loc.F("Setup.FoundGame", game.InstallDirectory));
            }

            if (server is not null)
            {
                ServerPaksFolder = server.PaksDirectory;
                if (string.IsNullOrWhiteSpace(ServerModsFolder))
                {
                    ServerModsFolder = server.ModsDirectory;
                }

                found.Add(Loc.F("Setup.FoundServer", server.InstallDirectory));
            }

            DetectStatus = found.Count == 0
                ? Loc.T("Setup.NothingFound")
                : Loc.F("Setup.Found", string.Join(Loc.T("Setup.And"), found));
            _services.Logger.LogInformation("Auto-detect: {Result}", DetectStatus);
        }
        catch (Exception ex)
        {
            DetectStatus = Loc.F("Setup.DetectFailed", ex.Message);
            _services.Logger.LogWarning("Auto-detect failed: {Message}", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Picks the game folder.</summary>
    [RelayCommand]
    private async Task BrowseGameAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickGame"), GamePaksFolder).ConfigureAwait(true) is { } path)
        {
            GamePaksFolder = GameLocator.ResolvePaksFolder(path) ?? path;
        }
    }

    /// <summary>Picks the server folder.</summary>
    [RelayCommand]
    private async Task BrowseServerAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickServer"), ServerPaksFolder).ConfigureAwait(true) is { } path)
        {
            ServerPaksFolder = GameLocator.ResolvePaksFolder(path) ?? path;
        }
    }

    /// <summary>Picks the client mods output folder.</summary>
    [RelayCommand]
    private async Task BrowseClientModsAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickClientMods"), ClientModsFolder).ConfigureAwait(true) is { } path)
        {
            ClientModsFolder = path;
        }
    }

    /// <summary>Picks the server mods output folder.</summary>
    [RelayCommand]
    private async Task BrowseServerModsAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Setup.PickServerMods"), ServerModsFolder).ConfigureAwait(true) is { } path)
        {
            ServerModsFolder = path;
        }
    }

    /// <summary>
    /// Opens the game Paks folder with ScumStudio.Pak (using the typed key when valid, else the stored key) and reports
    /// the pak and entry counts.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(GamePaksFolder))
        {
            TestSucceeded = false;
            TestResult = Loc.T("Setup.ChooseFolderFirst");
            return;
        }

        string? key = null;
        if (HasKeyInput)
        {
            if (!AesKeyHex.TryNormalize(KeyInput, out key))
            {
                KeyError = Loc.T("Setup.InvalidKey");
                TestSucceeded = false;
                TestResult = Loc.T("Setup.KeyInvalidNoTest");
                return;
            }
        }
        else
        {
            _services.Keys.TryGet(out key);
        }

        IsWorking = true;
        TestResult = Loc.T("Setup.Mounting");
        TestSucceeded = null;
        var folder = GamePaksFolder;
        try
        {
            var logger = _services.LoggerFactory.CreateLogger("ScumStudio.App.ConnectionTest");
            var (ok, result) = await _services.Operations.RunAsync(Loc.T("Setup.Testing"), (_, ct) =>
                Task.FromResult(ConnectionTester.Test(folder, key, logger, ct))).ConfigureAwait(true);
            key = null;
            if (ok && result is not null)
            {
                GamePaksFolder = result.Folder;
                TestSucceeded = result.IsHealthy;
                TestResult = result.Summary;
                _services.Logger.LogInformation("Connection test: {Summary}", result.Summary);
            }
            else
            {
                TestSucceeded = false;
                TestResult = Loc.T("Setup.TestFailed");
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Removes the stored key.</summary>
    [RelayCommand]
    private void ForgetKey()
    {
        _services.ForgetKey();
        KeyInput = string.Empty;
        RefreshKeyStatus();
        _services.Notifications.Info(Loc.T("Settings.KeyRemoved"), Loc.T("Settings.KeyRemovedDetail"));
    }

    /// <summary>
    /// Validates everything and saves: the key (when one was typed) goes to the protected key store, the folders to
    /// <c>settings.json</c>. Returns false and keeps the dialog open when the key is malformed; nothing is saved then.
    /// </summary>
    public bool Save()
    {
        if (HasKeyInput)
        {
            if (!AesKeyHex.IsValid(KeyInput))
            {
                KeyError = Loc.T("Setup.InvalidKey");
                _services.Logger.LogWarning("Setup: the entered AES key was rejected (not 64 hex digits).");
                return false;
            }

            // Take the text out of the bound property first so it is gone from the UI whatever happens next.
            var input = KeyInput;
            KeyInput = string.Empty;
            try
            {
                if (!_services.TryStoreKey(input))
                {
                    KeyError = Loc.T("Setup.InvalidKey");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
            {
                KeyError = Loc.F("Setup.KeyNotStored", ex.Message);
                return false;
            }
        }

        static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        var game = Clean(GamePaksFolder);
        var server = Clean(ServerPaksFolder);
        _services.UpdateSettings(s => s with
        {
            GamePaksFolder = game is null ? null : GameLocator.ResolvePaksFolder(game) ?? game,
            ServerPaksFolder = server is null ? null : GameLocator.ResolvePaksFolder(server) ?? server,
            ClientModsOutputFolder = Clean(ClientModsFolder),
            ServerModsOutputFolder = Clean(ServerModsFolder),
        });
        _services.UiState.Update(u => u with { SetupCompleted = true });
        RefreshKeyStatus();
        _services.Logger.LogInformation("Setup saved.");
        return true;
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        if (Save())
        {
            _close(true);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        KeyInput = string.Empty;
        _services.UiState.Update(u => u with { SetupCompleted = true });
        _close(false);
    }

    private void RefreshKeyStatus()
    {
        HasStoredKey = _services.Keys.HasKey;
        KeyStatusText = HasStoredKey
            ? Loc.T("Setup.KeyStatus.Stored")
            : Loc.T("Setup.KeyStatus.None");
    }
}
