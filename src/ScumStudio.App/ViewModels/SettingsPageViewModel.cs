using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core;
using ScumStudio.Core.Games;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;
using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Transports;

namespace ScumStudio.App.ViewModels;

/// <summary>An accent swatch.</summary>
public sealed partial class AccentChoiceViewModel : ViewModelBase
{
    /// <summary>Creates a swatch.</summary>
    public AccentChoiceViewModel(AccentOption option, Action<AccentChoiceViewModel> select)
    {
        Option = option;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>The colour.</summary>
    public AccentOption Option { get; }

    /// <summary>Name in the current UI language.</summary>
    public string Name => Loc.Instance.Or("Accent." + Option.Name, Option.Name);

    /// <summary>Hover text of the swatch.</summary>
    public string Tip => Loc.F("Settings.Accent.Tip", Name);

    /// <summary>Re-reads <see cref="Name"/> and <see cref="Tip"/> after the UI language changed.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Tip));
    }

    /// <summary><c>#RRGGBB</c>.</summary>
    public string Hex => Option.Hex;

    /// <summary>True for the active accent.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Selects this accent.</summary>
    public IRelayCommand SelectCommand { get; }
}

/// <summary>Settings page: language, accent colour, folders, AES key, AI control (MCP server) and data locations.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openSetup;

    /// <summary>Creates the page.</summary>
    public SettingsPageViewModel(AppServices services, Action? openSetup = null)
        : base("settings", "Settings", "Language, appearance, folders, the protected AES key and AI control")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        Accents = AccentPalette.Options.Select(o => new AccentChoiceViewModel(o, SelectAccent)).ToList();
        _customAccent = AccentPalette.ParseOrDefault(_services.UiState.Current.Accent);
        MarkSelected(_services.UiState.Current.Accent);
        _services.KeyChanged += OnKeyChanged;
        _services.SettingsChanged += OnSettingsChanged;
        _services.Mcp.StateChanged += OnMcpStateChanged;
        Reload();
    }

    /// <summary>Accent swatches.</summary>
    public IReadOnlyList<AccentChoiceViewModel> Accents { get; }

    /// <summary>The accent as any colour (the picker next to the swatches); the swatches set it too.</summary>
    [ObservableProperty]
    private Avalonia.Media.Color _customAccent;

    partial void OnCustomAccentChanged(Avalonia.Media.Color value)
    {
        var hex = AccentPalette.ToHex(value);
        if (string.Equals(hex, _services.UiState.Current.Accent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ponytail: saved on every change of the picker (a small file); a drag writes it often.
        _services.UiState.Update(u => u with { Accent = hex });
        AccentPalette.Apply(Application.Current, hex);
        MarkSelected(hex);
    }

    /// <summary>Game Paks folder.</summary>
    [ObservableProperty]
    private string _gamePaksFolder = string.Empty;

    /// <summary>Server Paks folder.</summary>
    [ObservableProperty]
    private string _serverPaksFolder = string.Empty;

    /// <summary>Client mods output folder.</summary>
    [ObservableProperty]
    private string _clientModsFolder = string.Empty;

    /// <summary>Server mods output folder.</summary>
    [ObservableProperty]
    private string _serverModsFolder = string.Empty;

    /// <summary>True when a key is stored.</summary>
    [ObservableProperty]
    private bool _hasKey;

    /// <summary>Key status sentence.</summary>
    [ObservableProperty]
    private string _keyStatus = string.Empty;

    /// <summary>Name of the selected accent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentCaption))]
    private string _accentName = string.Empty;

    /// <summary>"Dark theme - accent: Blaze".</summary>
    public string AccentCaption => Loc.F("Settings.AccentCaption", AccentName);

    /// <summary>User-interface languages, each shown in its own name.</summary>
    public IReadOnlyList<LanguageOption> Languages => Loc.Languages;

    /// <summary>The language of the interface; switching it takes effect at once and is saved.</summary>
    [ObservableProperty]
    private LanguageOption _selectedLanguage = Loc.Languages[0];

    /// <summary>AI control: start the MCP server with the editor.</summary>
    [ObservableProperty]
    private bool _mcpEnabled;

    /// <summary>AI control: TCP port (text box).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpPortError))]
    private string _mcpPort = McpServerSettings.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>AI control: "Listening on …" / "Off" / error.</summary>
    [ObservableProperty]
    private string _mcpStatus = Loc.T("Mcp.Off");

    /// <summary>AI control: true while the server runs.</summary>
    [ObservableProperty]
    private bool _mcpRunning;

    /// <summary>AI control: the token with all but its last 4 characters hidden.</summary>
    [ObservableProperty]
    private string _mcpTokenMasked = Loc.T("Settings.TokenNotCreated");

    /// <summary>AI control: "12 calls · last: …".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMcpActivity))]
    private string _mcpActivity = string.Empty;

    /// <summary>AI control: Claude Code command with the token hidden (the copy button copies the real one).</summary>
    [ObservableProperty]
    private string _mcpCommandPreview = string.Empty;

    /// <summary>True when <see cref="McpActivity"/> has text.</summary>
    public bool HasMcpActivity => McpActivity.Length > 0;

    /// <summary>Validation message of <see cref="McpPort"/>, or null.</summary>
    public string? McpPortError => ParsePort(McpPort) is null ? Loc.T("Settings.PortInvalidDetail") : null;

    private bool _reloading;

    /// <summary>Data folder.</summary>
    public string DataDirectory => _services.DataDirectory;

    /// <summary>"Data folder: …".</summary>
    public string DataFolderText => Loc.F("Settings.DataFolder", DataDirectory);

    /// <summary>How the key is protected.</summary>
    public string KeyProtection => ProtectedKeyStore.IsOsProtected
        ? Loc.T("Settings.KeyProtection.Dpapi")
        : Loc.T("Settings.KeyProtection.File");

    /// <summary>Product version.</summary>
    public string Version => Loc.F("Settings.Version", CoreInfo.ProductName, MainWindowViewModel.ShortVersion, CoreInfo.TargetEngineVersion);

    /// <summary>Full build version including the source revision.</summary>
    public string BuildVersion => CoreInfo.Version;

    /// <summary>Re-reads settings and key status.</summary>
    public void Reload()
    {
        var s = _services.Settings.Load();
        GamePaksFolder = s.GamePaksFolder ?? string.Empty;
        ServerPaksFolder = s.ServerPaksFolder ?? string.Empty;
        ClientModsFolder = s.ClientModsOutputFolder ?? string.Empty;
        ServerModsFolder = s.ServerModsOutputFolder ?? string.Empty;
        HasKey = _services.Keys.HasKey;
        KeyStatus = HasKey ? Loc.T("Settings.KeyStored") : Loc.T("Settings.NoKeyStored");
        _reloading = true;
        try
        {
            SelectedLanguage = Loc.Languages.FirstOrDefault(l => l.Code == s.Ui.Language) ?? Loc.Languages[0];
            RenderQuality = s.Ui.RenderQuality;
            McpEnabled = s.Mcp.Enabled;
            McpPort = s.Mcp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _reloading = false;
        }

        RefreshMcp();
    }

    /// <summary>Re-reads the AI control status from <see cref="AppServices.Mcp"/>.</summary>
    public void RefreshMcp()
    {
        var mcp = _services.Mcp;
        McpRunning = mcp.IsRunning;
        McpStatus = mcp.IsRunning || mcp.Error is not null ? mcp.StatusText : Loc.T("Mcp.Off");
        var token = mcp.Token;
        McpTokenMasked = string.IsNullOrEmpty(token) ? Loc.T("Settings.TokenNotCreated") : new string('•', 12) + token[^4..];
        McpCommandPreview = token is null ? mcp.ClaudeCodeCommand() : mcp.ClaudeCodeCommand().Replace(token, "<token>", StringComparison.Ordinal);
        McpActivity = mcp.CallCount == 0
            ? string.Empty
            : Loc.F("Settings.McpCalls", mcp.CallCount) + (mcp.ClientName.Length > 0 ? Loc.F("Settings.McpFrom", mcp.ClientName) : string.Empty)
              + Loc.F("Settings.McpLast", mcp.LastActivity);
    }

    /// <inheritdoc />
    public override void OnNavigatedTo() => Reload();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var accent in Accents)
        {
            accent.OnLanguageChanged();
        }

        MarkSelected(_services.UiState.Current.Accent);
        OnPropertyChanged(nameof(AccentCaption));
        OnPropertyChanged(nameof(McpPortError));
        OnPropertyChanged(nameof(DataFolderText));
        OnPropertyChanged(nameof(KeyProtection));
        OnPropertyChanged(nameof(Version));
        Reload();
    }

    /// <summary>The map gizmo's arrows follow the selected object's own axes (on by default); the map follows this setting live.</summary>
    public bool LocalAxes
    {
        get => _services.UiState.Current.LocalAxes;
        set
        {
            _services.UiState.Update(u => u with { LocalAxes = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Reopen the last project when the app starts (on by default).</summary>
    public bool ReopenLastProject
    {
        get => _services.UiState.Current.ReopenLastProject;
        set
        {
            _services.UiState.Update(u => u with { ReopenLastProject = value });
            OnPropertyChanged();
        }
    }

    /// <summary>3D view quality (the Map toolbar shows the same choice).</summary>
    [ObservableProperty]
    private ScumStudio.Core.Settings.RenderQuality _renderQuality = ScumStudio.Core.Settings.RenderQuality.Balanced;

    /// <summary>The quality presets.</summary>
    public IReadOnlyList<ScumStudio.Core.Settings.RenderQuality> RenderQualities { get; } = Enum.GetValues<ScumStudio.Core.Settings.RenderQuality>();

    partial void OnRenderQualityChanged(ScumStudio.Core.Settings.RenderQuality value)
    {
        if (!_reloading)
        {
            _services.UpdateSettings(s => s with { Ui = s.Ui with { RenderQuality = value } });
        }
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_reloading || value is null)
        {
            return;
        }

        // Save first: the language switch below re-reads the settings on every page.
        _services.UpdateSettings(s => s with { Ui = s.Ui with { Language = value.Code } });
        Loc.Instance.Language = value.Code;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.KeyChanged -= OnKeyChanged;
        _services.SettingsChanged -= OnSettingsChanged;
        _services.Mcp.StateChanged -= OnMcpStateChanged;
    }

    private void OnKeyChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(Reload);

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(Reload);

    private void OnMcpStateChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(RefreshMcp);

    private static int? ParsePort(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535
            ? port
            : null;

    partial void OnMcpEnabledChanged(bool value)
    {
        if (_reloading)
        {
            return;
        }

        _services.UpdateSettings(s => s with { Mcp = s.Mcp with { Enabled = value } });
        McpApplyTask = _services.Mcp.ApplySettingsAsync();
        if (!value)
        {
            _services.Notifications.Info(Loc.T("Settings.AiOff"), Loc.T("Settings.AiOffDetail"));
        }
    }

    /// <summary>The last start/stop triggered from this page (tests await it).</summary>
    public Task McpApplyTask { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void SaveMcpPort()
    {
        if (ParsePort(McpPort) is not { } port)
        {
            _services.Notifications.Warning(Loc.T("Settings.PortInvalid"), McpPortError);
            return;
        }

        _services.UpdateSettings(s => s with { Mcp = s.Mcp with { Port = port } });
        McpApplyTask = _services.Mcp.ApplySettingsAsync();
    }

    [RelayCommand]
    private async Task CopyMcpTokenAsync()
    {
        if (_services.Mcp.Token is not { } token)
        {
            _services.Notifications.Info(Loc.T("Settings.NoToken"), Loc.T("Settings.NoTokenDetail"));
            return;
        }

        await _services.Dialogs.SetClipboardTextAsync(token).ConfigureAwait(true);
        _services.Notifications.Success(Loc.T("Settings.TokenCopied"), Loc.T("Settings.TokenCopiedDetail"));
    }

    [RelayCommand]
    private void NewMcpToken()
    {
        var token = McpHttpServerOptions.NewToken();
        _services.UpdateSettings(s => s with { Mcp = s.Mcp with { Token = token } });
        McpApplyTask = _services.Mcp.ApplySettingsAsync();
        _services.Notifications.Info(Loc.T("Settings.NewToken"), Loc.T("Settings.NewTokenDetail"));
    }

    [RelayCommand]
    private async Task CopyClaudeCodeCommandAsync()
    {
        await EnsureTokenAsync().ConfigureAwait(true);
        await _services.Dialogs.SetClipboardTextAsync(_services.Mcp.ClaudeCodeCommand()).ConfigureAwait(true);
        _services.Notifications.Success(Loc.T("Settings.CommandCopied"), Loc.T("Settings.CommandCopiedDetail"));
    }

    [RelayCommand]
    private async Task CopyClaudeDesktopConfigAsync()
    {
        await EnsureTokenAsync().ConfigureAwait(true);
        await _services.Dialogs.SetClipboardTextAsync(_services.Mcp.ClaudeDesktopConfig()).ConfigureAwait(true);
        _services.Notifications.Success(Loc.T("Settings.ConfigCopied"), Loc.T("Settings.ConfigCopiedDetail"));
    }

    [RelayCommand]
    private void InstallClaudeSkill()
    {
        try
        {
            var path = StudioSkill.Install();
            _services.Notifications.Success(Loc.T("Settings.SkillInstalled"), Loc.F("Settings.SkillInstalledDetail", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Settings.SkillNotInstalled"), ex.Message);
        }
    }

    private async Task EnsureTokenAsync()
    {
        if (_services.Mcp.Token is null)
        {
            var token = McpHttpServerOptions.NewToken();
            _services.UpdateSettings(s => s with { Mcp = s.Mcp with { Token = token } });
            await McpApplyTask.ConfigureAwait(true);
        }
    }

    private void SelectAccent(AccentChoiceViewModel choice)
    {
        _services.UiState.Update(u => u with { Accent = choice.Hex });
        AccentPalette.Apply(Application.Current, choice.Hex);
        MarkSelected(choice.Hex);
        CustomAccent = AccentPalette.ParseOrDefault(choice.Hex);
    }

    private void MarkSelected(string hex)
    {
        foreach (var a in Accents)
        {
            a.IsSelected = string.Equals(a.Hex, hex, StringComparison.OrdinalIgnoreCase);
        }

        AccentName = Accents.FirstOrDefault(a => a.IsSelected)?.Name ?? Loc.T("Accent.Custom");
    }

    [RelayCommand]
    private void SavePaths()
    {
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
        _services.Notifications.Success(Loc.T("Settings.FoldersSaved"), Loc.T("Settings.FoldersSavedDetail"));
    }

    [RelayCommand]
    private async Task BrowseAsync(string? which)
    {
        var current = which switch
        {
            "game" => GamePaksFolder,
            "server" => ServerPaksFolder,
            "clientMods" => ClientModsFolder,
            _ => ServerModsFolder,
        };
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Common.SelectFolder"), current).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        switch (which)
        {
            case "game":
                GamePaksFolder = GameLocator.ResolvePaksFolder(path) ?? path;
                break;
            case "server":
                ServerPaksFolder = GameLocator.ResolvePaksFolder(path) ?? path;
                break;
            case "clientMods":
                ClientModsFolder = path;
                break;
            default:
                ServerModsFolder = path;
                break;
        }
    }

    [RelayCommand]
    private void ForgetKey()
    {
        _services.ForgetKey();
        _services.Notifications.Info(Loc.T("Settings.KeyRemoved"), Loc.T("Settings.KeyRemovedDetail"));
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    /// <summary>Where the prefab files (saved selections, plain JSON) live.</summary>
    public string PrefabsFolder => _services.Prefabs.Folder;

    /// <summary>Opens the prefab library folder in the file manager.</summary>
    [RelayCommand]
    private void OpenPrefabsFolder()
    {
        try
        {
            Directory.CreateDirectory(PrefabsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PrefabsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Info(Loc.T("Settings.Prefabs.Folder"), PrefabsFolder + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>Import…: a .ssprefab file someone shared goes into the library.</summary>
    [RelayCommand]
    private async Task ImportPrefabAsync()
    {
        if (await _services.Dialogs.OpenFileAsync(Loc.T("Map.Prefabs.Import.Title"), ScumStudio.Level.Editing.Prefab.Extension[1..], Loc.T("Map.Prefabs.FileType"), _services.Prefabs.Folder).ConfigureAwait(true) is { } path)
        {
            _services.Prefabs.ImportFile(path, _services.Workspace.Catalog, _services.Notifications);
        }
    }

    /// <summary>The project's home page (source, releases, issues).</summary>
    public const string ProjectPage = "https://github.com/ALLAWI32/SCUM-Modding-Studio";

    /// <summary>Opens <see cref="ProjectPage"/> in the browser.</summary>
    [RelayCommand]
    private void OpenProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProjectPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _services.Notifications.Warning(ProjectPage, ex.Message);
        }
    }
}
