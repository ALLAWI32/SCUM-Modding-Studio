using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>A ready server mod of the template list.</summary>
/// <param name="Id">Template id (also the suggested mod name).</param>
/// <param name="Title">Name in the current language.</param>
/// <param name="Code">The Lua text.</param>
public sealed record ServerTemplateViewModel(string Id, string Title, string Code);

/// <summary>One Lua mod of the server as the list shows it.</summary>
/// <param name="Name">Folder name.</param>
/// <param name="Enabled">The mod is switched on.</param>
public sealed record ServerModViewModel(string Name, bool Enabled)
{
    /// <summary>The list line.</summary>
    public string Display => Name + (Enabled ? string.Empty : "  (" + Loc.T("Server.Off") + ")");
}

/// <summary>
/// The Server page: Lua mods that run inside the dedicated server (through UE4SS): announcements, a join message, an event log.
/// Installs UE4SS next to <c>SCUMServer.exe</c>, keeps the mods as files and shows UE4SS's log. It never starts or stops the server.
/// </summary>
public sealed partial class ServerPageViewModel : PageViewModel
{
    private readonly AppServices _services;

    /// <summary>Creates the page.</summary>
    public ServerPageViewModel(AppServices services)
        : base("server", "Server", "Lua mods that run inside your dedicated server: announcements, join messages, event logs.")
    {
        _services = services;
        _selectedTemplate = Templates[0];
        Refresh();
    }

    /// <summary>The ready mods.</summary>
    [ObservableProperty]
    private IReadOnlyList<ServerTemplateViewModel> _templates = MakeTemplates();

    private static IReadOnlyList<ServerTemplateViewModel> MakeTemplates() =>
        [.. ServerModTemplates.All.Select(t => new ServerTemplateViewModel(t.Id, TemplateTitle(t.Id), t.Code))];

    /// <summary>The mods of the server.</summary>
    public ObservableCollection<ServerModViewModel> Mods { get; } = [];

    /// <summary>The template picked for a new mod.</summary>
    [ObservableProperty]
    private ServerTemplateViewModel? _selectedTemplate;

    /// <summary>The mod picked in the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMod))]
    private ServerModViewModel? _selectedMod;

    /// <summary>The picked mod's main.lua.</summary>
    [ObservableProperty]
    private string _code = string.Empty;

    /// <summary>The server folder and UE4SS state, one line.</summary>
    [ObservableProperty]
    private string _serverLine = string.Empty;

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>UE4SS's log (its tail).</summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>A server was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanEdit))]
    private bool _hasServer;

    /// <summary>UE4SS is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(SwitchLabel))]
    private bool _hasUe4ss;

    /// <summary>UE4SS is installed and on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwitchLabel))]
    private bool _isActive;

    /// <summary>Something runs (the download).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isBusy;

    /// <summary>A mod is picked.</summary>
    public bool HasSelectedMod => SelectedMod is not null;

    /// <summary>Install UE4SS is offered.</summary>
    public bool CanInstall => HasServer && !IsBusy;

    /// <summary>Mods can be added and edited.</summary>
    public bool CanEdit => HasServer && HasUe4ss;

    /// <summary>The label of the on/off button.</summary>
    public string SwitchLabel => IsActive ? Loc.T("Server.SwitchOff") : Loc.T("Server.SwitchOn");

    private ServerInstall? Server => ServerInstall.Find(_services.Settings.Load().ServerPaksFolder);

    private static string TemplateTitle(string id) => id switch
    {
        "Announcer" => Loc.T("Server.T.Announcer"),
        "Welcome" => Loc.T("Server.T.Welcome"),
        "EventLog" => Loc.T("Server.T.EventLog"),
        "TimeCommand" => Loc.T("Server.T.TimeCommand"),
        "Dump" => Loc.T("Server.T.Dump"),
        _ => id,
    };

    /// <summary>The picked tab: 0 mods, 1 Lua editor, 2 loot editor, 3 log.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>The Lua editor of its tab (made when the tab is first shown, so the server's class dump is read only then).</summary>
    [ObservableProperty]
    private LuaEditorViewModel? _luaEditor;

    /// <summary>The loot editor of its tab (made when the tab is first shown).</summary>
    [ObservableProperty]
    private LootEditorViewModel? _lootEditor;

    /// <summary>Makes the view model of the Lua editor (a window of its own, or the Server page's tab).</summary>
    public LuaEditorViewModel CreateLuaEditor() => new(_services);

    /// <summary>Makes the view model of the loot editor (a window of its own, or the Server page's tab).</summary>
    public LootEditorViewModel CreateLootEditor() => new(_services);

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1)
        {
            LuaEditor ??= CreateLuaEditor();
        }
        else if (value == 2)
        {
            LootEditor ??= CreateLootEditor();
        }
    }

    /// <inheritdoc />
    public override void OnNavigatedTo() => Refresh();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        var picked = SelectedTemplate?.Id;
        Templates = MakeTemplates();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == picked) ?? Templates[0];
        OnPropertyChanged(nameof(SwitchLabel));
        Refresh();
    }

    private bool _keepCode;

    partial void OnSelectedModChanged(ServerModViewModel? value)
    {
        if (_keepCode)
        {
            return;
        }

        Code = value is not null && Server is { } server ? new ServerModStore(server).Read(value.Name) ?? string.Empty : string.Empty;
        OnPropertyChanged(nameof(HasSelectedMod));
    }

    /// <summary>Reads the server's state again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        var keep = SelectedMod?.Name;
        var server = Server;
        HasServer = server is not null;
        HasUe4ss = server?.HasUe4ss == true;
        IsActive = server?.IsUe4ssActive == true;
        ServerLine = server is null
            ? Loc.T("Server.NoServer")
            : Loc.F(HasUe4ss ? (IsActive ? "Server.Line.On" : "Server.Line.Off") : "Server.Line.None", server.Win64);
        Mods.Clear();
        if (server is not null)
        {
            foreach (var mod in new ServerModStore(server).List())
            {
                Mods.Add(new ServerModViewModel(mod.Name, mod.Enabled));
            }
        }

        // The list was rebuilt: the same mod is picked again without reading its text from disk (an unsaved edit stays).
        _keepCode = true;
        SelectedMod = Mods.FirstOrDefault(m => m.Name == keep);
        _keepCode = false;
        ReadLog();
    }

    /// <summary>Downloads the official UE4SS from GitHub, checks it and installs it for a server.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUe4ssAsync()
    {
        if (Server is not { } server)
        {
            return;
        }

        IsBusy = true;
        Status = Loc.T("Server.Downloading");
        string? temp = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var release = await Ue4ssInstaller.FindLatestAsync(http).ConfigureAwait(true);
            temp = Path.Combine(Path.GetTempPath(), "scumstudio-ue4ss-" + Guid.NewGuid().ToString("N"));
            var zip = await Ue4ssInstaller.DownloadAsync(http, release, temp).ConfigureAwait(true);
            var written = Ue4ssInstaller.InstallFromZip(server, zip);
            Status = Loc.F("Server.Installed", release.Name, written);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            _services.Logger.LogWarning("UE4SS install: {Message}", ex.Message);
            Status = Loc.F("Server.InstallFailed", ex.Message);
        }
        finally
        {
            if (temp is not null && Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>Switches UE4SS on or off (the server must be stopped).</summary>
    [RelayCommand]
    private void SwitchUe4ss()
    {
        if (Server is not { } server)
        {
            return;
        }

        try
        {
            Ue4ssInstaller.SetActive(server, !server.IsUe4ssActive);
            Status = Loc.T(server.IsUe4ssActive ? "Server.SwitchedOn" : "Server.SwitchedOff");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("Server.SwitchFailed", ex.Message);
        }

        Refresh();
    }

    /// <summary>Adds a mod made from the picked template.</summary>
    [RelayCommand]
    private void AddTemplate()
    {
        if (Server is not { } server || SelectedTemplate is not { } template)
        {
            return;
        }

        var store = new ServerModStore(server);
        var name = template.Id;
        for (var n = 2; store.Read(name) is not null; n++)
        {
            name = template.Id + n;
        }

        store.Save(name, template.Code);
        Refresh();
        SelectedMod = Mods.FirstOrDefault(m => m.Name == name);
        Status = Loc.F("Server.Added", name);
    }

    /// <summary>Saves the editor's text as the picked mod's main.lua.</summary>
    [RelayCommand]
    private void Save()
    {
        if (Server is not { } server || SelectedMod is not { } mod)
        {
            return;
        }

        new ServerModStore(server).Save(mod.Name, Code);
        Status = Loc.F("Server.Saved", mod.Name);
        Refresh();
    }

    /// <summary>Switches the picked mod on or off.</summary>
    [RelayCommand]
    private void ToggleMod()
    {
        if (Server is not { } server || SelectedMod is not { } mod)
        {
            return;
        }

        new ServerModStore(server).SetEnabled(mod.Name, !mod.Enabled);
        Status = Loc.F(mod.Enabled ? "Server.ModOff" : "Server.ModOn", mod.Name);
        Refresh();
    }

    /// <summary>Deletes the picked mod.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (Server is not { } server || SelectedMod is not { } mod)
        {
            return;
        }

        new ServerModStore(server).Remove(mod.Name);
        Status = Loc.F("Server.Removed", mod.Name);
        _keepCode = true;
        SelectedMod = null;
        _keepCode = false;
        Code = string.Empty;
        Refresh();
    }

    /// <summary>Reads the end of UE4SS's log.</summary>
    [RelayCommand]
    private void ReadLog()
    {
        LogText = string.Empty;
        if (Server is not { } server || !File.Exists(server.LogFile))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(server.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            LogText = string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').TakeLast(60).Select(l => l.TrimEnd('\r')));
        }
        catch (IOException ex)
        {
            LogText = ex.Message;
        }
    }
}
