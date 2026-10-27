using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>One loot group of the editor: a rarity and the tags and item ids it gives, one per line.</summary>
public sealed partial class LootGroupRow : ObservableObject
{
    /// <summary>Creates a row.</summary>
    public LootGroupRow(string rarity, string entriesText)
    {
        _rarity = rarity;
        _entriesText = entriesText;
    }

    /// <summary>The rarities to pick from.</summary>
    public static IReadOnlyList<string> Rarities => LootPresetFile.Rarities;

    /// <summary>The group's rarity (its weight against the other groups).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightText))]
    private string _rarity;

    /// <summary>The group's tags (<c>ItemLootTreeNodes.Military.Weapons.Rifles</c>) and item ids (<c>KeyCard</c>), one per line.</summary>
    [ObservableProperty]
    private string _entriesText;

    /// <summary>How much more often than the rarest group this one is picked ("x16").</summary>
    public string WeightText => "×" + Weight(Rarity).ToString(CultureInfo.InvariantCulture);

    /// <summary>The weight of a rarity: 32 for Abundant down to 1 for Extremely Rare.</summary>
    public static int Weight(string rarity) => Math.Max(1, 32 >> Math.Max(0, LootPresetFile.Rarities.ToList().IndexOf(rarity)));

    /// <summary>The entries, one per non-empty line.</summary>
    public IReadOnlyList<string> Entries => [.. EntriesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>
/// The loot editor window: loot groups and chances for the places of a map (a dungeon) as the server's own loot customization
/// (<c>Loot\Spawners\Presets\Override\&lt;zone&gt;</c>, with the rectangle the zone covers). A container is any searchable object
/// the game has (a crate pile): its preset is changed only inside the zone. The files are written to the server and read by
/// <see cref="LootOverrideStore.ReloadCommand"/> in the game chat.
/// </summary>
public sealed partial class LootEditorViewModel : ObservableObject
{
    private const int ListMost = 300;
    private readonly AppServices _services;
    private LootPresetFile? _current;
    private IReadOnlyList<string> _defaults = [];
    private LootTables? _tree;

    /// <summary>Creates the editor over the app services.</summary>
    public LootEditorViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _margin = "1500";
        Reload();
    }

    /// <summary>The zones with changes on the server.</summary>
    public ObservableCollection<string> Zones { get; } = [];

    /// <summary>The presets changed in the picked zone.</summary>
    public ObservableCollection<string> ChangedPresets { get; } = [];

    /// <summary>The game's presets that match the search (at most 300).</summary>
    public ObservableCollection<string> DefaultRows { get; } = [];

    /// <summary>The loot groups of the picked preset.</summary>
    public ObservableCollection<LootGroupRow> Groups { get; } = [];

    /// <summary>The loot tree tags that match the tree search (at most 300).</summary>
    public ObservableCollection<string> TagRows { get; } = [];

    /// <summary>The guard sets of the open project, to take a zone's rectangle from.</summary>
    public ObservableCollection<string> GuardSets { get; } = [];

    /// <summary>A server with its settings folder was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _hasServer;

    /// <summary>The game's presets were exported (the list of presets to start from exists).</summary>
    [ObservableProperty]
    private bool _hasDefaults;

    /// <summary>The server's folder line.</summary>
    [ObservableProperty]
    private string _serverLine = string.Empty;

    /// <summary>The zone's folder name (new or picked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _zoneName = "Dungeon";

    /// <summary>The picked existing zone.</summary>
    [ObservableProperty]
    private string? _selectedZone;

    /// <summary>One corner's X (cm).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _x1 = string.Empty;

    /// <summary>One corner's Y (cm).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _y1 = string.Empty;

    /// <summary>The other corner's X (cm).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _x2 = string.Empty;

    /// <summary>The other corner's Y (cm).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _y2 = string.Empty;

    /// <summary>How far outside the guard posts the zone reaches (cm).</summary>
    [ObservableProperty]
    private string _margin;

    /// <summary>The guard set the rectangle is taken from.</summary>
    [ObservableProperty]
    private string? _selectedGuardSet;

    /// <summary>The picked changed preset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreset), nameof(CanSave))]
    private string? _selectedPreset;

    /// <summary>The search text of the game's presets.</summary>
    [ObservableProperty]
    private string _defaultSearch = string.Empty;

    /// <summary>The picked game preset (to add).</summary>
    [ObservableProperty]
    private string? _selectedDefault;

    /// <summary>The picked loot group.</summary>
    [ObservableProperty]
    private LootGroupRow? _selectedGroup;

    /// <summary>The chance (percent) a search gives anything; empty keeps the preset's own.</summary>
    [ObservableProperty]
    private string _probabilityText = "100";

    /// <summary>The fewest items one search gives.</summary>
    [ObservableProperty]
    private decimal _quantityMin = 1;

    /// <summary>The most items one search gives.</summary>
    [ObservableProperty]
    private decimal _quantityMax = 1;

    /// <summary>Items are filtered by the zone's rules.</summary>
    [ObservableProperty]
    private bool _filterByZone;

    /// <summary>The place's chance modifier applies.</summary>
    [ObservableProperty]
    private bool _applyLocationProbability;

    /// <summary>The place's damage modifier applies.</summary>
    [ObservableProperty]
    private bool _applyLocationDamage;

    /// <summary>The same item may drop twice in one search.</summary>
    [ObservableProperty]
    private bool _allowDuplicates;

    /// <summary>The search text of the loot tree.</summary>
    [ObservableProperty]
    private string _tagSearch = string.Empty;

    /// <summary>The picked loot tree tag.</summary>
    [ObservableProperty]
    private string? _selectedTag;

    /// <summary>The tree's state line.</summary>
    [ObservableProperty]
    private string _treeLine = string.Empty;

    /// <summary>What the last action did, or what is wrong.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>A preset is open for editing.</summary>
    public bool HasPreset => SelectedPreset is not null;

    /// <summary>The preset can be written to the server (a zone name, a rectangle and a preset are there).</summary>
    public bool CanSave => HasServer && HasPreset && LootOverrideStore.IsValidZoneName(ZoneName) && TryZone(out _);

    /// <summary>The chat command to run in the game after saving.</summary>
    public static string ReloadChatCommand => LootOverrideStore.ReloadCommand;

    /// <summary>The chat command that exports the game's presets.</summary>
    public static string ExportChatCommand => LootOverrideStore.ExportCommand;

    private ServerInstall? Server => ServerInstall.Find(_services.Settings.Load().ServerPaksFolder);

    partial void OnDefaultSearchChanged(string value) => FilterDefaults();

    partial void OnTagSearchChanged(string value) => FilterTags();

    partial void OnSelectedZoneChanged(string? value)
    {
        if (value is null || Server is not { } server)
        {
            return;
        }

        var store = new LootOverrideStore(server);
        ZoneName = value;
        if (store.ReadZone(value) is { } area)
        {
            ShowZone(area);
        }

        ChangedPresets.Clear();
        foreach (var name in store.PresetsOf(value))
        {
            ChangedPresets.Add(name);
        }

        SelectedPreset = null;
    }

    private bool _addingPreset;

    partial void OnSelectedPresetChanged(string? value)
    {
        if (_addingPreset)
        {
            return;
        }

        if (value is null || Server is not { } server || !LootOverrideStore.IsValidZoneName(ZoneName))
        {
            _current = null;
            Groups.Clear();
            return;
        }

        Open(new LootOverrideStore(server).ReadOverride(ZoneName, value));
    }

    /// <summary>Looks at the server again: its zones, the game's presets and the guard sets of the open project.</summary>
    [RelayCommand]
    private void Reload()
    {
        var server = Server;
        HasServer = server is not null;
        ServerLine = server is null ? Loc.T("Server.NoServer") : new LootOverrideStore(server).OverrideFolder;
        Zones.Clear();
        _defaults = [];
        HasDefaults = false;
        if (server is not null)
        {
            var store = new LootOverrideStore(server);
            HasDefaults = store.HasDefaults;
            _defaults = store.DefaultPresets();
            foreach (var zone in store.Zones())
            {
                Zones.Add(zone);
            }
        }

        GuardSets.Clear();
        if (_services.Projects.Current?.State is { } state)
        {
            foreach (var set in state.GuardSets.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                GuardSets.Add(set);
            }
        }

        SelectedGuardSet = GuardSets.FirstOrDefault();
        FilterDefaults();
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Takes the zone's rectangle from the posts of a guard set, with the margin around them.</summary>
    [RelayCommand]
    private void ZoneFromGuards()
    {
        if (SelectedGuardSet is not { } name || _services.Projects.Current?.State.GetGuardSet(name) is not { Posts.Count: > 0 } set
            || !double.TryParse(Margin, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin))
        {
            Status = Loc.T("Loot.NoGuards");
            return;
        }

        ShowZone(LootZone.Around(set.Posts.Select(p => ((double)p.Position.X, (double)p.Position.Y)), margin));
        Status = Loc.F("Loot.ZoneTaken", name);
    }

    /// <summary>Starts a changed preset in the zone from one of the game's presets.</summary>
    [RelayCommand]
    private void AddPreset()
    {
        if (SelectedDefault is not { } name || Server is not { } server || !LootOverrideStore.IsValidZoneName(ZoneName))
        {
            return;
        }

        var preset = new LootOverrideStore(server).ReadDefault(name);
        if (preset is null)
        {
            return;
        }

        // Outside the game's own zones nothing drops while the zone's rules apply: they start off.
        preset.FilterByZone = false;
        preset.ApplyLocationProbability = false;
        preset.Probability ??= 100;
        if (!ChangedPresets.Contains(name))
        {
            ChangedPresets.Add(name);
        }

        _addingPreset = true; // the picked preset is the new copy: it is opened below, not read from the server
        SelectedPreset = name;
        _addingPreset = false;
        Open(preset);
        Status = Loc.F("Loot.Added", name);
    }

    /// <summary>Adds an empty loot group.</summary>
    [RelayCommand]
    private void AddGroup()
    {
        if (!HasPreset)
        {
            return;
        }

        var row = new LootGroupRow("Common", string.Empty);
        Groups.Add(row);
        SelectedGroup = row;
    }

    /// <summary>Removes the picked loot group.</summary>
    [RelayCommand]
    private void RemoveGroup()
    {
        if (SelectedGroup is { } row)
        {
            Groups.Remove(row);
            SelectedGroup = Groups.LastOrDefault();
        }
    }

    /// <summary>Puts the picked loot tree tag into the picked group.</summary>
    [RelayCommand]
    private void AddTag()
    {
        if (SelectedTag is not { } tag || SelectedGroup is not { } row)
        {
            Status = Loc.T("Loot.PickGroupAndTag");
            return;
        }

        if (!row.Entries.Contains(tag, StringComparer.Ordinal))
        {
            row.EntriesText = (row.EntriesText.TrimEnd() + "\n" + tag).TrimStart('\n');
        }
    }

    /// <summary>Reads the game's loot tree (tags and items) from the opened game files.</summary>
    [RelayCommand]
    private async Task LoadTreeAsync()
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            TreeLine = Loc.T("Loot.NoGameFiles");
            return;
        }

        TreeLine = Loc.T("Loot.TreeLoading");
        try
        {
            _tree = await Task.Run(() => LootTables.Read(catalog)).ConfigureAwait(true);
            TreeLine = Loc.F("Loot.TreeCount", _tree.NodeCount);
            FilterTags();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or FormatException)
        {
            TreeLine = ex.Message;
        }
    }

    /// <summary>Writes the zone's rectangle and the preset to the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Server is not { } server || SelectedPreset is not { } name || _current is null || !TryZone(out var area))
        {
            return;
        }

        var file = _current.Clone();
        if (!Apply(file))
        {
            return;
        }

        if (file.Problems() is { Count: > 0 } problems)
        {
            Status = string.Join("  ", problems.Select(p => string.Format(CultureInfo.CurrentCulture, Loc.Instance["Loot.Problem." + p.Code], p.Detail)));
            return;
        }

        var store = new LootOverrideStore(server);
        try
        {
            store.WriteZone(ZoneName, area);
            store.WritePreset(ZoneName, name, file);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            return;
        }

        _current = file;
        if (!Zones.Contains(ZoneName))
        {
            Zones.Add(ZoneName);
        }

        Status = Loc.F("Loot.Saved", Path.Combine(store.OverrideFolder, ZoneName, name + ".json"), ReloadChatCommand);
    }

    /// <summary>Deletes the picked changed preset from the zone (the game's own applies there again).</summary>
    [RelayCommand]
    private void RemovePreset()
    {
        if (SelectedPreset is not { } name || Server is not { } server)
        {
            return;
        }

        new LootOverrideStore(server).RemovePreset(ZoneName, name);
        ChangedPresets.Remove(name);
        SelectedPreset = null;
        Status = Loc.F("Loot.Removed", name);
    }

    /// <summary>Deletes the whole zone with its changed presets.</summary>
    [RelayCommand]
    private void RemoveZone()
    {
        if (Server is not { } server || !Zones.Contains(ZoneName))
        {
            return;
        }

        new LootOverrideStore(server).RemoveZone(ZoneName);
        Zones.Remove(ZoneName);
        ChangedPresets.Clear();
        SelectedPreset = null;
        Status = Loc.F("Loot.ZoneRemoved", ZoneName);
    }

    private void Open(LootPresetFile? preset)
    {
        _current = preset;
        Groups.Clear();
        if (preset is null)
        {
            return;
        }

        ProbabilityText = preset.Probability?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        QuantityMin = preset.QuantityMin;
        QuantityMax = preset.QuantityMax;
        FilterByZone = preset.FilterByZone;
        ApplyLocationProbability = preset.ApplyLocationProbability;
        ApplyLocationDamage = preset.ApplyLocationDamage;
        AllowDuplicates = preset.AllowDuplicates;
        foreach (var group in preset.Groups)
        {
            Groups.Add(new LootGroupRow(group.Rarity, string.Join('\n', group.Entries)));
        }

        SelectedGroup = Groups.FirstOrDefault();
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Puts the editor's fields into <paramref name="file"/>; false (with a status) when a number is not a number.</summary>
    private bool Apply(LootPresetFile file)
    {
        if (ProbabilityText.Trim().Length == 0)
        {
            file.Probability = null;
        }
        else if (double.TryParse(ProbabilityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        {
            file.Probability = p;
        }
        else
        {
            Status = Loc.F("Loot.Problem.Probability", ProbabilityText);
            return false;
        }

        file.QuantityMin = (int)QuantityMin;
        file.QuantityMax = (int)QuantityMax;
        file.FilterByZone = FilterByZone;
        file.ApplyLocationProbability = ApplyLocationProbability;
        file.ApplyLocationDamage = ApplyLocationDamage;
        file.AllowDuplicates = AllowDuplicates;
        file.SetGroups(Groups.Select(g => new LootGroup(g.Rarity, g.Entries)).Where(g => g.Entries.Count > 0));
        return true;
    }

    private void ShowZone(LootZone area)
    {
        X1 = area.MinX.ToString("0.###", CultureInfo.InvariantCulture);
        Y1 = area.MinY.ToString("0.###", CultureInfo.InvariantCulture);
        X2 = area.MaxX.ToString("0.###", CultureInfo.InvariantCulture);
        Y2 = area.MaxY.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private bool TryZone(out LootZone area)
    {
        area = default;
        if (!double.TryParse(X1, NumberStyles.Float, CultureInfo.InvariantCulture, out var x1)
            || !double.TryParse(Y1, NumberStyles.Float, CultureInfo.InvariantCulture, out var y1)
            || !double.TryParse(X2, NumberStyles.Float, CultureInfo.InvariantCulture, out var x2)
            || !double.TryParse(Y2, NumberStyles.Float, CultureInfo.InvariantCulture, out var y2))
        {
            return false;
        }

        area = LootZone.Of(x1, y1, x2, y2);
        return area.IsValid;
    }

    private void FilterDefaults()
    {
        var words = DefaultSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DefaultRows.Clear();
        foreach (var name in _defaults.Where(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(ListMost))
        {
            DefaultRows.Add(name);
        }
    }

    private void FilterTags()
    {
        TagRows.Clear();
        if (_tree is null)
        {
            return;
        }

        var words = TagSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tag in _tree.Tags.Where(t => words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal).Take(ListMost))
        {
            TagRows.Add(tag);
        }
    }

    partial void OnZoneNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnX1Changed(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnY1Changed(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnX2Changed(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnY2Changed(string value) => SaveCommand.NotifyCanExecuteChanged();
}
