using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>A row of the function catalog.</summary>
/// <param name="Entry">The catalog entry.</param>
public sealed record LuaApiRow(LuaApiEntry Entry)
{
    /// <summary>The list line: a status mark and the name.</summary>
    public string Display => StatusMark(Entry.Status) + " " + Entry.Name;

    /// <summary>The status as a short mark.</summary>
    public static string StatusMark(LuaApiStatus status) => status switch
    {
        LuaApiStatus.Tested => "✔",
        LuaApiStatus.Seen => "○",
        _ => "✖",
    };
}

/// <summary>A group of the catalog filter.</summary>
/// <param name="Id">The group key ("" is all).</param>
/// <param name="Name">The group in the current language.</param>
public sealed record LuaGroupOption(string Id, string Name);

/// <summary>A finding of the script check as the list shows it.</summary>
/// <param name="Text">The finding in the current language.</param>
/// <param name="Line">The script line (0 for the whole script).</param>
public sealed record LuaFindingRow(string Text, int Line);

/// <summary>
/// The Lua editor window: a catalog of the functions, events and rules a server mod can use (what was really tried on the owner's
/// server), the game's own function list from the class dump, a code editor, a check and "To my server". Scripts are the
/// server's own <c>Mods\&lt;Name&gt;\Scripts\main.lua</c> files, not part of a Studio project.
/// </summary>
public sealed partial class LuaEditorViewModel : ObservableObject
{
    private const int GameListMost = 300;
    private readonly AppServices _services;
    private IReadOnlyList<string> _gameFunctions = [];
    private HashSet<string>? _known;

    /// <summary>Creates the editor over the app services.</summary>
    public LuaEditorViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _modName = "MyMod";
        _selectedGroup = Groups[0];
        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
        LoadServer();
        ApplyFilter();
        ApplyGameFilter();
    }

    /// <summary>The catalog rows that match the search and the group.</summary>
    public ObservableCollection<LuaApiRow> Rows { get; } = [];

    /// <summary>The groups of the filter (the first is "all").</summary>
    public IReadOnlyList<LuaGroupOption> Groups { get; } = [.. new[] { string.Empty }.Concat(LuaApiCatalog.Groups).Select(g => new LuaGroupOption(g, GroupName(g)))];

    /// <summary>The server's mods the editor can open.</summary>
    public ObservableCollection<string> Mods { get; } = [];

    /// <summary>The game functions that match the game search (at most 300).</summary>
    public ObservableCollection<string> GameRows { get; } = [];

    /// <summary>The results of the last check.</summary>
    public ObservableCollection<LuaFindingRow> Findings { get; } = [];

    /// <summary>The search text of the catalog.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>The picked group ("" is all).</summary>
    [ObservableProperty]
    private string _group = string.Empty;

    /// <summary>The group picked in the filter.</summary>
    [ObservableProperty]
    private LuaGroupOption? _selectedGroup;

    /// <summary>The picked catalog row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(SelectedTitle), nameof(SelectedSignature), nameof(SelectedText), nameof(SelectedStatus), nameof(SelectedSnippet))]
    private LuaApiRow? _selected;

    /// <summary>The search text of the game functions.</summary>
    [ObservableProperty]
    private string _gameSearch = string.Empty;

    /// <summary>The picked game function.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameSelected))]
    private string? _gameSelected;

    /// <summary>The script.</summary>
    [ObservableProperty]
    private string _code = string.Empty;

    /// <summary>The mod name to save under.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _modName;

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The server's folder line.</summary>
    [ObservableProperty]
    private string _serverLine = string.Empty;

    /// <summary>The line about the class dump.</summary>
    [ObservableProperty]
    private string _dumpLine = string.Empty;

    /// <summary>A server with UE4SS was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _hasServer;

    /// <summary>The picked row exists.</summary>
    public bool HasSelected => Selected is not null;

    /// <summary>A game function is picked.</summary>
    public bool HasGameSelected => GameSelected is not null;

    /// <summary>The script can be written to the server.</summary>
    public bool CanSave => HasServer && ServerModStore.IsValidName(ModName);

    /// <summary>The picked entry's title.</summary>
    public string SelectedTitle => Selected?.Entry.Name ?? string.Empty;

    /// <summary>The picked entry's call.</summary>
    public string SelectedSignature => Selected?.Entry.Signature ?? string.Empty;

    /// <summary>The picked entry's description in the current language.</summary>
    public string SelectedText => Selected is { } row ? LuaApiCatalog.TextOf(row.Entry, Loc.Instance.Language) : string.Empty;

    /// <summary>The picked entry's status, written out.</summary>
    public string SelectedStatus => Selected is { } row ? LuaApiRow.StatusMark(row.Entry.Status) + " " + Text("Lua.Status.", row.Entry.Status) : string.Empty;

    /// <summary>The picked entry's example.</summary>
    public string SelectedSnippet => Selected?.Entry.Snippet ?? string.Empty;

    /// <summary>Raised to put text at the editor's caret.</summary>
    public event EventHandler<string>? InsertRequested;

    /// <summary>The group's name in the current language.</summary>
    public static string GroupName(string group) => string.IsNullOrEmpty(group) ? Loc.T("Lua.Group.All") : Text("Lua.Group.", group);

    /// <summary>A text whose key is a prefix and a name (the keys are all in the string tables).</summary>
    private static string Text(string prefix, object name) => Loc.T(prefix + name);

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnGroupChanged(string value) => ApplyFilter();

    partial void OnSelectedGroupChanged(LuaGroupOption? value) => Group = value?.Id ?? string.Empty;

    partial void OnGameSearchChanged(string value) => ApplyGameFilter();

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SelectedStatus));
    }

    private ServerInstall? Server => ServerInstall.Find(_services.Settings.Load().ServerPaksFolder);

    private void ApplyFilter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keep = Selected?.Entry.Id;
        Rows.Clear();
        foreach (var entry in LuaApiCatalog.All.Where(e => string.IsNullOrEmpty(Group) || e.Group == Group))
        {
            var hay = entry.Name + " " + entry.Signature + " " + entry.Text + " " + entry.TextRu;
            if (words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                Rows.Add(new LuaApiRow(entry));
            }
        }

        Selected = Rows.FirstOrDefault(r => r.Entry.Id == keep);
    }

    private void ApplyGameFilter()
    {
        var words = GameSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        GameRows.Clear();
        if (_gameFunctions.Count == 0)
        {
            return;
        }

        var found = 0;
        foreach (var path in _gameFunctions)
        {
            if (words.All(w => path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                GameRows.Add(path);
                if (++found >= GameListMost)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Looks for the server, its mods and its class dump again.</summary>
    [RelayCommand]
    private void LoadServer()
    {
        var server = Server;
        HasServer = server?.HasUe4ss == true;
        ServerLine = server is null ? Loc.T("Server.NoServer") : server.Win64;
        Mods.Clear();
        _known = null;
        _gameFunctions = [];
        DumpLine = Loc.T("Lua.Game.NoDump");
        if (server is null)
        {
            return;
        }

        foreach (var mod in new ServerModStore(server).List())
        {
            Mods.Add(mod.Name);
        }

        var dump = Path.Combine(server.Ue4ssFolder, "scum_dump.txt");
        if (File.Exists(dump))
        {
            try
            {
                var lines = File.ReadAllLines(dump);
                _known = LuaCheck.ReadDump(lines);
                _gameFunctions = [.. _known.Order(StringComparer.Ordinal)];
                DumpLine = Loc.F("Lua.Game.Count", _known.Count);
            }
            catch (IOException ex)
            {
                DumpLine = ex.Message;
            }
        }

        ApplyGameFilter();
    }

    /// <summary>Opens a mod of the server in the editor.</summary>
    [RelayCommand]
    private void Open(string? name)
    {
        if (string.IsNullOrEmpty(name) || Server is not { } server)
        {
            return;
        }

        Code = new ServerModStore(server).Read(name) ?? string.Empty;
        ModName = name;
        Findings.Clear();
        Status = Loc.F("Lua.Opened", name);
    }

    /// <summary>Starts an empty script.</summary>
    [RelayCommand]
    private void NewScript()
    {
        Code = "-- " + ModName + "\n";
        Findings.Clear();
        Status = string.Empty;
    }

    /// <summary>Puts the picked entry's example into the editor.</summary>
    [RelayCommand]
    private void Insert()
    {
        if (Selected is { Entry.Snippet.Length: > 0 } row)
        {
            InsertRequested?.Invoke(this, row.Entry.Snippet + "\n");
        }
    }

    /// <summary>Puts a hook on the picked game function into the editor.</summary>
    [RelayCommand]
    private void InsertGameHook()
    {
        if (GameSelected is { } path)
        {
            InsertRequested?.Invoke(this, $"RegisterHook(\"{path}\", function(self)\n  -- remember what you need, do not touch players here\nend)\n");
        }
    }

    /// <summary>Checks the script: syntax, game functions against the dump, known mistakes.</summary>
    [RelayCommand]
    private void Check()
    {
        Findings.Clear();
        var findings = LuaCheck.Run(Code, _known);
        foreach (var f in findings)
        {
            var mark = f.Level == LuaFindingLevel.Error ? "✖" : "!";
            var where = f.Line > 0 ? Loc.F("Lua.Line", f.Line) + ": " : string.Empty;
            Findings.Add(new LuaFindingRow(mark + " " + where + string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("Lua.Finding.", f.Code), f.Detail), f.Line));
        }

        if (findings.Count == 0)
        {
            Findings.Add(new LuaFindingRow("✔ " + Loc.T(_known is null ? "Lua.Check.OkNoDump" : "Lua.Check.Ok"), 0));
        }

        Status = string.Empty;
    }

    /// <summary>Writes the script to the server as <c>Mods\&lt;name&gt;\Scripts\main.lua</c> and switches the mod on.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Server is not { } server)
        {
            return;
        }

        Check();
        if (Findings.Any(f => f.Text.StartsWith('✖')))
        {
            Status = Loc.T("Lua.NotSaved");
            return;
        }

        var mod = new ServerModStore(server).Save(ModName, Code);
        if (!Mods.Contains(mod.Name))
        {
            Mods.Add(mod.Name);
        }

        Status = Loc.F("Lua.Saved", mod.ScriptPath);
    }

    partial void OnModNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnHasServerChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
}
