using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>One item of the Map header that can be switched off (the settings button at the end of the header, Settings › Map toolbar).</summary>
public sealed partial class MapToolbarItem : ViewModelBase
{
    private readonly string _titleKey;
    private readonly Action<MapToolbarItem> _changed;

    internal MapToolbarItem(string key, string titleKey, Action<MapToolbarItem> changed)
    {
        Key = key;
        _titleKey = titleKey;
        _changed = changed;
    }

    /// <summary>The key kept in <see cref="UiState.HiddenMapToolbarItems"/>.</summary>
    public string Key { get; }

    /// <summary>The item's name, as the header shows it.</summary>
    public string Title => Loc.T(_titleKey);

    /// <summary>What the switch does.</summary>
    public string Tip => Loc.F("Map.Toolbar.Item.Tip", Title);

    /// <summary>The header shows the item (when it applies: Frame selection still waits for a selection).</summary>
    [ObservableProperty]
    private bool _isShown = true;

    partial void OnIsShownChanged(bool value) => _changed(this);

    internal void Retranslate()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>
/// Which items the Map header shows (owner: "show only what I want"). Kept in <c>ui-state.json</c>; every instance (the Map's,
/// the Settings page's) follows <see cref="UiStateStore.Changed"/>, so a switch in one shows in the other at once.
/// </summary>
public sealed partial class MapToolbar : ViewModelBase, IDisposable
{
    /// <summary>The items in header order: key and the string key of its name.</summary>
    public static IReadOnlyList<(string Key, string TitleKey)> Layout { get; } =
    [
        ("Maximize", "Map.Maximize"), ("PopOut", "Map.PopOut"), ("Quality", "Map.Toolbar.Item.Quality"), ("Time", "Map.TimeOfDay"),
        ("Spawns", "Map.Spawns"), ("Legend", "Map.Toolbar.Item.Legend"), ("Parts", "Map.Parts"), ("Brush", "Map.Brush"),
        ("Axes", "Map.Axes"), ("Ground", "Map.Ground"), ("Snap", "Map.Snap"), ("FrameAll", "Map.FrameAll"),
        ("FrameSelection", "Map.FrameSelection"), ("Shape", "Map.Shape"), ("Landscape", "Map.Landscape"), ("Replace", "Map.Replace"),
        ("Extend", "Map.Extend"), ("LastObject", "Map.LastObject"), ("Prefabs", "Map.Prefabs"), ("Paste", "Map.Paste"),
        ("AddObject", "Map.AddObject"), ("LoadMode", "Map.Toolbar.Item.LoadMode"), ("Project", "Map.Toolbar.Item.Project"),
        ("Export", "Projects.Export"), ("LevelChip", "Map.Toolbar.Item.LevelChip"), ("WorldChip", "Map.Toolbar.Item.WorldChip"),
        ("Stats", "Map.Toolbar.Item.Stats"),
    ];

    private readonly UiStateStore _store;
    private bool _applying;

    /// <summary>Creates the switches from <paramref name="store"/> and follows its changes.</summary>
    public MapToolbar(UiStateStore store)
    {
        _store = store;
        Items = Layout.Select(l => new MapToolbarItem(l.Key, l.TitleKey, OnItemChanged)).ToList();
        Apply(store.Current);
        _store.Changed += Apply;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Every switch, in header order (the gear menu's and the Settings card's lists).</summary>
    public IReadOnlyList<MapToolbarItem> Items { get; }

    /// <summary>The 3D view fills the page (F11).</summary>
    public MapToolbarItem Maximize => this["Maximize"];

    /// <summary>The 3D view in its own window.</summary>
    public MapToolbarItem PopOut => this["PopOut"];

    /// <summary>The quality picker.</summary>
    public MapToolbarItem Quality => this["Quality"];

    /// <summary>The time-of-day slider.</summary>
    public MapToolbarItem Time => this["Time"];

    /// <summary>The spawn pins switch.</summary>
    public MapToolbarItem Spawns => this["Spawns"];

    /// <summary>The pin colours' legend.</summary>
    public MapToolbarItem Legend => this["Legend"];

    /// <summary>Picking parts of buildings.</summary>
    public MapToolbarItem Parts => this["Parts"];

    /// <summary>The brush.</summary>
    public MapToolbarItem Brush => this["Brush"];

    /// <summary>The gizmo's orientation.</summary>
    public MapToolbarItem Axes => this["Axes"];

    /// <summary>Fit to ground.</summary>
    public MapToolbarItem Ground => this["Ground"];

    /// <summary>Snap.</summary>
    public MapToolbarItem Snap => this["Snap"];

    /// <summary>Frame all.</summary>
    public MapToolbarItem FrameAll => this["FrameAll"];

    /// <summary>Frame selection.</summary>
    public MapToolbarItem FrameSelection => this["FrameSelection"];

    /// <summary>The Shape menu.</summary>
    public MapToolbarItem Shape => this["Shape"];

    /// <summary>The Landscape menu.</summary>
    public MapToolbarItem Landscape => this["Landscape"];

    /// <summary>The Replace menu.</summary>
    public MapToolbarItem Replace => this["Replace"];

    /// <summary>Extend.</summary>
    public MapToolbarItem Extend => this["Extend"];

    /// <summary>Last object.</summary>
    public MapToolbarItem LastObject => this["LastObject"];

    /// <summary>The Prefabs menu.</summary>
    public MapToolbarItem Prefabs => this["Prefabs"];

    /// <summary>Paste.</summary>
    public MapToolbarItem Paste => this["Paste"];

    /// <summary>The Add object menu.</summary>
    public MapToolbarItem AddObject => this["AddObject"];

    /// <summary>The load mode (whole island or one sector).</summary>
    public MapToolbarItem LoadMode => this["LoadMode"];

    /// <summary>The project's name and its New / Open / Close buttons.</summary>
    public MapToolbarItem Project => this["Project"];

    /// <summary>Export mod.</summary>
    public MapToolbarItem Export => this["Export"];

    /// <summary>The loaded levels chip.</summary>
    public MapToolbarItem LevelChip => this["LevelChip"];

    /// <summary>The island streaming chip.</summary>
    public MapToolbarItem WorldChip => this["WorldChip"];

    /// <summary>The speed and frame statistics.</summary>
    public MapToolbarItem Stats => this["Stats"];

    /// <summary>The switch of <paramref name="key"/>.</summary>
    public MapToolbarItem this[string key] => Items.First(i => i.Key == key);

    /// <summary>Shows every item.</summary>
    [RelayCommand]
    public void ShowAll() => Save([]);

    /// <summary>Back to the stock header.</summary>
    [RelayCommand]
    public void ResetDefaults() => Save(new UiState().HiddenMapToolbarItems);

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Changed -= Apply;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnItemChanged(MapToolbarItem item)
    {
        if (!_applying)
        {
            Save(Items.Where(i => !i.IsShown).Select(i => i.Key).ToList());
        }
    }

    private void Save(IReadOnlyList<string> hidden)
    {
        // Keys this build does not know (written by a newer one) stay hidden for that build.
        var unknown = (_store.Current.HiddenMapToolbarItems ?? []).Where(k => Items.All(i => i.Key != k));
        _store.Update(u => u with { HiddenMapToolbarItems = [.. hidden, .. unknown] });
    }

    private void Apply(UiState state)
    {
        _applying = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsShown = state.HiddenMapToolbarItems?.Contains(item.Key) != true; // null: a hand-edited file
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
        {
            item.Retranslate();
        }
    }
}
