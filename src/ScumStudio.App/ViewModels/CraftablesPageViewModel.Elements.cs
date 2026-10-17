using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Placeables that behave like the game's own (owner, 2026-10-12: "a bed must behave like a normal bed: its health, its
/// recipe, respawn at it"): a source that is a base element of the game (or shows one) becomes an
/// <see cref="CraftKind.Element"/> craftable cloned with its whole family (<see cref="ElementFamilies"/>), with the values
/// each level of the game's chain stores and a way back to the game's value.
/// </summary>
public sealed partial class CraftablesPageViewModel
{
    /// <summary><paramref name="craftable"/> as a clone of <paramref name="family"/> (built, like the game's element), or unchanged without one.</summary>
    internal static Craftable AsElement(Craftable craftable, ElementFamily? family) => family is null
        ? craftable
        : craftable with { Kind = CraftKind.Element, Element = new ElementSettings { Template = family.Element }, Use = CraftUse.Build };

    /// <summary>The base element family <paramref name="path"/> is or shows, or null (an unreadable one is none).</summary>
    private static ElementFamily? ElementOf(AssetCatalog catalog, string path)
    {
        try
        {
            return ElementFamilies.Of(catalog, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Reads the family of <paramref name="row"/>'s element (or of its source, for an object that could be one) on a worker.</summary>
    private async Task ShowElementAsync(CraftableRowViewModel row, AssetCatalog catalog, CancellationToken cancellationToken)
    {
        if (row.IsItem || row.ToModel().Imported is not null || row.ElementLoaded)
        {
            return;
        }

        var source = row.ToModel().Element?.Template ?? row.Source;
        try
        {
            var spawned = row.ToModel().Element?.Storage?.Item;
            var (family, why, storage, fridge) = await Task.Run<(ElementFamily? Family, string? Why, IReadOnlyList<Tunable> Storage, bool Fridge)>(() =>
            {
                if (ElementOf(catalog, source) is not { } found)
                {
                    return (null, ElementFamilies.WhyNot(catalog, source), [], false);
                }

                // What the item it spawns holds (a chest, the fridge): its size is set here too.
                var item = spawned ?? found.ItemClass;
                return item is null ? (found, null, [], false) : (found, null, ItemStorage.Of(catalog, item), CraftablesPlanner.NativeClassOf(catalog, item) == "RefrigeratorItem");
            }, cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(_services.Workspace.Catalog, catalog))
            {
                row.SetFamily(family, why, storage, fridge);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Base element of {Source} not read: {Message}", source, ex.Message);
        }
    }
}

/// <summary>The element, its Use and the kind warning of a craftable.</summary>
public sealed partial class CraftableRowViewModel
{
    private static readonly CraftKind[] ElementKinds = [CraftKind.Element, CraftKind.Furniture, CraftKind.Station, CraftKind.Power];
    private CraftUse? _use;

    /// <summary>The game's element it is cloned from, with its levels' values (null until read, or when the source is none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kinds), nameof(KindChoices), nameof(HasElement), nameof(CanCraft), nameof(CanBoth), nameof(IsBuild), nameof(IsCraft), nameof(IsBoth))]
    private ElementEditorViewModel? _element;

    /// <summary>Why the source is no element this version clones (shown for an Element craftable), or null.</summary>
    [ObservableProperty]
    private string? _elementNote;

    /// <summary>The warning after the kind of a craftable that may have been exported changed, or null.</summary>
    [ObservableProperty]
    private string? _kindWarning;

    /// <summary>It came from the project's file (an export may have shipped it): a kind change warns.</summary>
    internal bool MayBeExported { get; init; }

    /// <summary>The family was read (<see cref="SetFamily"/>).</summary>
    internal bool ElementLoaded { get; private set; }

    /// <summary>A clone of the game's own element (a bed stays a bed).</summary>
    public bool IsElement => Kind == CraftKind.Element;

    /// <summary>The element's values are shown.</summary>
    public bool HasElement => IsElement && Element is not null;

    /// <summary>Where it goes once made: built, crafted into the bag, or both.</summary>
    public CraftUse Use
    {
        get => _use ?? (Kind == CraftKind.Element ? _source.Use : null) ?? DefaultUse(Kind);
        set
        {
            if (value == Use || (value == CraftUse.Both && !CanBoth) || (value == CraftUse.Craft && !CanCraft) || (value == CraftUse.Build && !CanBuild))
            {
                return;
            }

            _use = value;
            OnPropertyChanged(nameof(Use));
            OnPropertyChanged(nameof(IsBuild));
            OnPropertyChanged(nameof(IsCraft));
            OnPropertyChanged(nameof(IsBoth));
            Changed();
        }
    }

    /// <summary>Use is Build (segmented button).</summary>
    public bool IsBuild
    {
        get => Use == CraftUse.Build;
        set => SetUse(value, CraftUse.Build);
    }

    /// <summary>Use is Craft (segmented button).</summary>
    public bool IsCraft
    {
        get => Use == CraftUse.Craft;
        set => SetUse(value, CraftUse.Craft);
    }

    /// <summary>Use is Both (segmented button).</summary>
    public bool IsBoth
    {
        get => Use == CraftUse.Both;
        set => SetUse(value, CraftUse.Both);
    }

    /// <summary>Can be placed in the world (not an item, not a power object, which are items placed afterwards).</summary>
    public bool CanBuild => Kind is not (CraftKind.Item or CraftKind.Power);

    /// <summary>Crafted into the bag only: an item or a power object (an element is built; Both adds its item).</summary>
    public bool CanCraft => Kind is CraftKind.Item or CraftKind.Power;

    /// <summary>Both recipes: only an element whose game element spawns an item (a workbench, a lamp; a bed has none).</summary>
    public bool CanBoth => IsElement && Element is { HasItem: true, SpawnsOther: false };

    /// <summary>The source is a base element of the game (or the craftable is one already): Element is offered.</summary>
    private bool OffersElement => _source.Element is not null || _source.Kind == CraftKind.Element || Element is not null;

    /// <summary>The family of its element (<see cref="ElementFamilies.Of"/>), or null with <paramref name="whyNot"/>.</summary>
    internal void SetFamily(ElementFamily? family, string? whyNot, IReadOnlyList<Tunable>? storage = null, bool fridge = false)
    {
        ElementLoaded = true;
        var quiet = _quiet;
        _quiet = true;
        Element = family is null ? null : new ElementEditorViewModel(family, _source.Element, Changed, storage, fridge);
        ElementNote = family is null ? Loc.F("Craftables.Element.NotOffered", whyNot ?? Source) : null;
        RefreshSections(); // "Shows in" defaults to the game element's own section
        _quiet = quiet;
    }

    private static CraftUse DefaultUse(CraftKind kind) => kind is CraftKind.Item or CraftKind.Power ? CraftUse.Craft : CraftUse.Build;

    private void SetUse(bool on, CraftUse use)
    {
        if (on)
        {
            Use = use;
        }
        else
        {
            OnPropertyChanged(use switch { CraftUse.Build => nameof(IsBuild), CraftUse.Craft => nameof(IsCraft), _ => nameof(IsBoth) }); // a click on the checked one keeps it
        }
    }

    /// <summary>The element settings ToModel writes: the editor's, else the ones the file had.</summary>
    private ElementSettings? ElementModel() => Kind == CraftKind.Element ? Element?.ToModel() ?? _source.Element : _source.Element; // a save never adds settings to another kind

    /// <summary>Use as stored: an element's choice; the other kinds go where their kind goes (null).</summary>
    private CraftUse? UseModel() => Kind == CraftKind.Element ? Use : null;

    partial void OnKindChanged(CraftKind value)
    {
        _use = null;
        KindWarning = MayBeExported && IsLocked ? Loc.T("Craftables.KindChange.Warning") : null;
        foreach (var name in new[] { nameof(IsElement), nameof(HasElement), nameof(Use), nameof(IsBuild), nameof(IsCraft), nameof(IsBoth), nameof(CanBuild), nameof(CanCraft), nameof(CanBoth) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>"Like the game's bed": the element's levels and the values each one stores, with Reset to the game's.</summary>
public sealed partial class ElementEditorViewModel : ObservableObject
{
    /// <summary>Creates the editor of <paramref name="family"/> with the values <paramref name="saved"/> changed; <paramref name="changed"/> runs after each edit.</summary>
    public ElementEditorViewModel(ElementFamily family, ElementSettings? saved, Action changed, IReadOnlyList<Tunable>? storage = null, bool isFridge = false)
    {
        Family = family;
        Levels = family.Levels.Select((level, i) => new ElementLevelViewModel(i, level, saved?.Levels.ElementAtOrDefault(i), changed)).ToList();
        _storageItem = saved?.Storage?.Item;
        var values = saved?.Storage?.Values ?? new Dictionary<string, string>();
        Storage = (storage ?? []).Select(t => new ElementValueViewModel(t, values.GetValueOrDefault(t.Path), changed)).ToList();
        _keptStorage = values.Where(e => Storage.All(s => s.Key != e.Key)).ToDictionary(e => e.Key, e => e.Value);
        IsFridge = isFridge;
        _selectedLevel = Levels.FirstOrDefault();
    }

    /// <summary>The game's element family.</summary>
    public ElementFamily Family { get; }

    /// <summary>"Like the game's BP_Bed_Improvised".</summary>
    public string Like => Loc.F("Craftables.Element.Like", Leaf(Family.Element));

    /// <summary>Players respawn at it, as at the game's (its <c>_isUsedForRespawn</c>).</summary>
    public bool IsRespawnPoint => Family.IsRespawnPoint;

    /// <summary>The game's element spawns an item (<c>_itemClass</c>), so it can be crafted into the bag too.</summary>
    public bool HasItem => Family.ItemClass is not null;

    /// <summary>The element and its upgrades, in the game's order.</summary>
    public IReadOnlyList<ElementLevelViewModel> Levels { get; }

    /// <summary>The chain has upgrades (the level list is shown).</summary>
    public bool HasLevels => Levels.Count > 1;

    /// <summary>The level whose values are shown.</summary>
    [ObservableProperty]
    private ElementLevelViewModel? _selectedLevel;

    private readonly string? _storageItem;
    private readonly IReadOnlyDictionary<string, string> _keptStorage;

    /// <summary>What the item it spawns holds (rows, columns, weight; <see cref="ItemStorage"/>), with the game's values.</summary>
    public IReadOnlyList<ElementValueViewModel> Storage { get; }

    /// <summary>It spawns another container of the game than its own item (the fridge): it fits in no bag, so it is only built.</summary>
    public bool SpawnsOther => _storageItem is not null;

    /// <summary>The item it spawns holds things (the storage card is shown).</summary>
    public bool HasStorage => Storage.Count > 0;

    /// <summary>It spawns a copy of the game's fridge (native <c>RefrigeratorItem</c>): the note says what the game's code does.</summary>
    public bool IsFridge { get; }

    /// <summary>The settings to store: the template and each level's changed values (none when nothing changed).</summary>
    public ElementSettings ToModel()
    {
        var levels = Levels.Select(l => l.Edits()).ToList();
        while (levels.Count > 0 && levels[^1].Count == 0)
        {
            levels.RemoveAt(levels.Count - 1);
        }

        var storage = new Dictionary<string, string>(_keptStorage, StringComparer.Ordinal);
        foreach (var value in Storage.Where(v => v.IsEdited))
        {
            storage[value.Key] = value.Text;
        }

        return new ElementSettings
        {
            Template = Family.Element,
            Levels = levels,
            Storage = _storageItem is null && storage.Count == 0 ? null : new StorageSettings { Item = _storageItem, Values = storage },
        };
    }

    internal static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>One level of an element: the values its class default stores, and the ones it inherits (not editable here).</summary>
public sealed class ElementLevelViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, string> _kept;

    /// <summary>Creates level <paramref name="index"/> with the values <paramref name="saved"/> changed.</summary>
    public ElementLevelViewModel(int index, ElementLevel level, IReadOnlyDictionary<string, string>? saved, Action changed)
    {
        Title = Loc.F("Craftables.Element.Level", index + 1, ElementEditorViewModel.Leaf(level.Element));
        var stored = ElementSettings.Editable.Where(k => k != Respawn)
            .Select(k => level.Values.FirstOrDefault(v => v.Path == k))
            .OfType<Tunable>()
            .ToList();
        Values = stored.Select(t => new ElementValueViewModel(t, saved?.GetValueOrDefault(t.Path), changed)).ToList();
        Inherited = ElementSettings.Editable.Where(k => k != Respawn && stored.All(t => t.Path != k)).Select(ElementValueViewModel.LabelOf).ToList();

        // A value the file has for a property this level does not store stays in the file: the export reports it.
        _kept = (saved ?? new Dictionary<string, string>()).Where(e => stored.All(t => t.Path != e.Key)).ToDictionary(e => e.Key, e => e.Value);
    }

    /// <summary>"Level 2 · BP_Bed_Improvised_Upgrade".</summary>
    public string Title { get; }

    /// <summary>The values the level stores.</summary>
    public IReadOnlyList<ElementValueViewModel> Values { get; }

    /// <summary>Names of the values it does not store (it keeps its parent class's).</summary>
    public IReadOnlyList<string> Inherited { get; }

    /// <summary>Some values are inherited.</summary>
    public bool HasInherited => Inherited.Count > 0;

    /// <summary>"Kept from the game's parent class: Health · Max per squad".</summary>
    public string InheritedText => Loc.F("Craftables.Element.Inherited", string.Join(" · ", Inherited));

    /// <summary>The changed values, by property.</summary>
    public IReadOnlyDictionary<string, string> Edits()
    {
        var edits = new Dictionary<string, string>(_kept, StringComparer.Ordinal);
        foreach (var value in Values.Where(v => v.IsEdited))
        {
            edits[value.Key] = value.Text;
        }

        return edits;
    }

    private const string Respawn = "_isUsedForRespawn";
}

/// <summary>A value of an element level: the game's, the edited one and Reset.</summary>
public sealed partial class ElementValueViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["_healthPoints"] = "Health",
        ["_hoursToDecay"] = "HoursToDecay",
        ["_hoursToDecayOutsideFlagArea"] = "HoursToDecayOutsideFlag",
        ["_canBeDamaged"] = "CanBeDamaged",
        ["_explosionResistance"] = "ExplosionResistance",
        ["_canBePlacedWithoutFlag"] = "PlaceWithoutFlag",
        ["_canBePlacedInBuilding"] = "PlaceInBuilding",
        ["_canBePlacedInRestrictedArea"] = "PlaceInRestrictedArea",
        ["_canBePlacedOnOtherBaseElements"] = "PlaceOnOtherElements",
        ["_maxInstancesPerFlagArea"] = "MaxPerFlagArea",
        ["_maxInstancesPerSquad"] = "MaxPerSquad",
        ["NumRows"] = "StorageRows",
        ["NumColumns"] = "StorageColumns",
        ["MaxContainedWeight"] = "StorageWeight",
    };

    private readonly Action _changed;
    private readonly decimal _stockNumber;
    private readonly bool _stockFlag;

    /// <summary>Creates the value of <paramref name="stock"/>, set to <paramref name="saved"/> when the file changed it.</summary>
    public ElementValueViewModel(Tunable stock, string? saved, Action changed)
    {
        Key = stock.Path;
        IsFlag = stock.Kind == TunableKind.Bool;
        Stock = stock.Value;
        _stockFlag = IsFlag && TunableValue.ParseBool(stock.Value);
        _stockNumber = !IsFlag && decimal.TryParse(stock.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        _flag = saved is not null && IsFlag ? TunableValue.ParseBool(saved) : _stockFlag;
        _number = saved is not null && !IsFlag && decimal.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : _stockNumber;
        _changed = changed;
    }

    /// <summary>The property (<c>_healthPoints</c>).</summary>
    public string Key { get; }

    /// <summary>Its name in the UI language.</summary>
    public string Label => LabelOf(Key);

    /// <summary>What it does.</summary>
    public string Tip => Names.TryGetValue(Key, out var name) ? Loc.T($"Craftables.Element.{name}.Tip") : Key;

    /// <summary>A yes/no value (check box) instead of a number.</summary>
    public bool IsFlag { get; }

    /// <summary>A number.</summary>
    public bool IsNumber => !IsFlag;

    /// <summary>The game's value as stored (invariant text).</summary>
    public string Stock { get; }

    /// <summary>"Game: 500".</summary>
    public string StockText => Loc.F("Craftables.Element.Stock", IsFlag ? (_stockFlag ? "✓" : "✗") : _stockNumber.ToString("0.###", CultureInfo.CurrentCulture));

    /// <summary>The number (for a number value).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private decimal _number;

    /// <summary>The yes/no (for a flag).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _flag;

    /// <summary>It differs from the game's value.</summary>
    public bool IsEdited => IsFlag ? Flag != _stockFlag : Number != _stockNumber;

    /// <summary>The value as stored (invariant text).</summary>
    public string Text => IsFlag ? (Flag ? "true" : "false") : Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The name of <paramref name="key"/> in the UI language (the property itself when it has none).</summary>
    public static string LabelOf(string key) => Names.TryGetValue(key, out var name) ? Loc.T($"Craftables.Element.{name}") : key;

    partial void OnNumberChanged(decimal value) => _changed();

    partial void OnFlagChanged(bool value) => _changed();

    /// <summary>Back to the game's value.</summary>
    [RelayCommand(CanExecute = nameof(IsEdited))]
    private void Reset()
    {
        if (IsFlag)
        {
            Flag = _stockFlag;
        }
        else
        {
            Number = _stockNumber;
        }
    }
}
