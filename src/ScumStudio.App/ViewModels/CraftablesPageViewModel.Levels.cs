using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Materials per skill level (CwHedgehog, 2026-10-17: "adjust the materials required for each level of
/// Engineering/Survival during crafting"). Every recipe slot of the game stores an amount per level of the recipe's skill
/// (<see cref="LevelAmounts"/>): a craftable's ingredient can set all five (<see cref="IngredientRowViewModel.PerLevel"/>),
/// and a recipe of the game can be overridden (<see cref="RecipeOverrides"/>, shipped at its own path by the Craftables pak).
/// </summary>
public sealed partial class CraftablesPageViewModel
{
    private Task<IReadOnlyList<StockRecipeChoice>>? _stockRecipes;
    private AssetCatalog? _stockRecipesOf;

    /// <summary>The game's recipes whose amounts per skill level the project overrides.</summary>
    public ObservableCollection<StockRecipeRowViewModel> RecipeOverrides { get; } = [];

    /// <summary>The game's recipes panel is open (over the editor).</summary>
    [ObservableProperty]
    private bool _isStockOpen;

    /// <summary>Search text of the game's recipes.</summary>
    [ObservableProperty]
    private string _stockQuery = string.Empty;

    /// <summary>The game's recipes matching <see cref="StockQuery"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStockResults))]
    private IReadOnlyList<StockRecipeChoice> _stockResults = [];

    /// <summary>The search has matches.</summary>
    public bool HasStockResults => StockResults.Count > 0;

    /// <summary>The project overrides no recipe of the game yet.</summary>
    public bool HasNoRecipeOverrides => RecipeOverrides.Count == 0;

    [RelayCommand]
    private void CloseStock() => IsStockOpen = false;

    partial void OnStockQueryChanged(string value) => _ = SearchStockAsync(value.Trim());

    /// <summary>The game's recipes whose name has <paramref name="query"/> (the list is read once per catalog, on a worker).</summary>
    private async Task SearchStockAsync(string query)
    {
        if (query.Length < 2 || _services.Workspace.Catalog is not { } catalog)
        {
            StockResults = [];
            return;
        }

        if (_stockRecipes is null || !ReferenceEquals(_stockRecipesOf, catalog))
        {
            _stockRecipesOf = catalog;
            _stockRecipes = Task.Run(() => StockRecipeChoice.All(catalog));
        }

        try
        {
            var all = await _stockRecipes.ConfigureAwait(true);
            if (StockQuery.Trim() == query)
            {
                StockResults = all.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(80).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogWarning("The game's recipes not listed: {Message}", ex.Message);
        }
    }

    /// <summary>Adds the game's recipe <paramref name="choice"/> to the overrides with its own amounts (one Undo step).</summary>
    [RelayCommand]
    private async Task AddStockRecipeAsync(StockRecipeChoice? choice)
    {
        if (choice is null || _services.Workspace.Catalog is not { } catalog || !HasProject)
        {
            _services.Notifications.Warning(Title, Loc.T(HasProject ? "Craftables.ConnectFirst" : "Craftables.NoProject"));
            return;
        }

        if (RecipeOverrides.Any(r => string.Equals(r.Recipe, choice.PackagePath, StringComparison.OrdinalIgnoreCase)))
        {
            return; // already listed
        }

        var reason = string.Empty;
        var read = await Task.Run(() => CraftablesPlanner.StockRecipe(catalog, choice.PackagePath, out reason)).ConfigureAwait(true);
        if (read is not { } stock)
        {
            _services.Notifications.Warning(Title, Loc.F("Craftables.Stock.CannotOverride", choice.Name, reason));
            return;
        }

        Remember();
        var model = new StockRecipeOverride { Recipe = choice.PackagePath, Slots = stock.Slots.Select(s => new StockSlotAmounts(s.TagText, s.Amount)).ToList() };
        var row = new StockRecipeRowViewModel(model, OnStockChanged, RemoveStock);
        row.SetGame(stock.Slots, stock.Skill);
        RecipeOverrides.Add(row);
        OnPropertyChanged(nameof(HasNoRecipeOverrides));
        Save();
    }

    private void OnStockChanged(StockRecipeRowViewModel row)
    {
        if (!_loading)
        {
            Remember();
            Save();
        }
    }

    private void RemoveStock(StockRecipeRowViewModel row)
    {
        Remember();
        RecipeOverrides.Remove(row);
        OnPropertyChanged(nameof(HasNoRecipeOverrides));
        Save();
    }

    /// <summary>The overrides of <paramref name="file"/> as rows (the game's amounts and skill follow, <see cref="ApplyStockAsync"/>).</summary>
    private void ShowStock(CraftablesFile file)
    {
        RecipeOverrides.Clear();
        foreach (var entry in file.RecipeOverrides)
        {
            RecipeOverrides.Add(new StockRecipeRowViewModel(entry, OnStockChanged, RemoveStock));
        }

        OnPropertyChanged(nameof(HasNoRecipeOverrides));
        _ = ApplyStockAsync(RecipeOverrides.ToList());
    }

    /// <summary>Reads each override's recipe from the game (skill, the game's amounts, a slot list the game changed) on a worker.</summary>
    private async Task ApplyStockAsync(IReadOnlyList<StockRecipeRowViewModel> rows)
    {
        if (_services.Workspace.Catalog is not { } catalog || rows.Count == 0)
        {
            return;
        }

        try
        {
            var read = await Task.Run(() => rows.Select(r => (Stock: CraftablesPlanner.StockRecipe(catalog, r.Recipe, out var reason), Reason: reason)).ToList()).ConfigureAwait(true);
            for (var i = 0; i < rows.Count; i++)
            {
                if (read[i].Stock is { } stock)
                {
                    rows[i].SetGame(stock.Slots, stock.Skill);
                }
                else
                {
                    rows[i].Problem = Loc.F("Craftables.Stock.CannotOverride", rows[i].Name, read[i].Reason);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogWarning("The game's recipes not read: {Message}", ex.Message);
        }
    }
}

/// <summary>A recipe of the game the search offers.</summary>
/// <param name="PackagePath">Its package.</param>
/// <param name="Name">Its asset name (<c>CR_Improvised_Workbench</c>).</param>
/// <param name="Folder">"Placeables", "Items" …</param>
public sealed record StockRecipeChoice(string PackagePath, string Name, string Folder)
{
    /// <summary>Every crafting recipe of the game in <paramref name="catalog"/> (<see cref="CraftCategories.IsStockRecipe"/>), by name.</summary>
    public static IReadOnlyList<StockRecipeChoice> All(AssetCatalog catalog) =>
        (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries.Select(e => e.PackagePath).Where(CraftCategories.IsStockRecipe)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new StockRecipeChoice(p, PackageMap.Leaf(p), PackageMap.Leaf(PackageMap.Folder(p))))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>A recipe of the game with the user's amounts per skill level.</summary>
public sealed partial class StockRecipeRowViewModel : ObservableObject
{
    private readonly Action<StockRecipeRowViewModel> _changed;
    private readonly Action<StockRecipeRowViewModel> _remove;
    private IReadOnlyList<RecipeSlot>? _game;

    /// <summary>Creates the row of <paramref name="model"/>; <paramref name="changed"/> runs after each edit.</summary>
    public StockRecipeRowViewModel(StockRecipeOverride model, Action<StockRecipeRowViewModel> changed, Action<StockRecipeRowViewModel> remove)
    {
        Recipe = model.Recipe;
        _changed = changed;
        _remove = remove;
        foreach (var slot in model.Slots)
        {
            Slots.Add(new StockSlotRowViewModel(slot.Tags, new SkillLevelsViewModel(slot.Amount, () => _changed(this))));
        }
    }

    /// <summary>The game's recipe (package path).</summary>
    public string Recipe { get; }

    /// <summary>Its asset name.</summary>
    public string Name => PackageMap.Leaf(Recipe);

    /// <summary>Its slots, in the game's order.</summary>
    public ObservableCollection<StockSlotRowViewModel> Slots { get; } = [];

    /// <summary>"Levels of: Engineering" once the recipe is read, else empty.</summary>
    [ObservableProperty]
    private string _skillText = string.Empty;

    /// <summary>Why the export leaves it out (not overridable, or the game changed its slots), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    /// <summary>A problem is shown.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>The game's slots and skill: each slot shows the game's amounts, and a slot list that no longer matches is said.</summary>
    internal void SetGame(IReadOnlyList<RecipeSlot> slots, string? skill)
    {
        _game = slots;
        SkillText = Loc.F("Craftables.Stock.Skill", SkillLevelsViewModel.SkillLabel(skill));
        var matches = slots.Count == Slots.Count && slots.Select(s => s.TagText).SequenceEqual(Slots.Select(s => s.Tags), StringComparer.OrdinalIgnoreCase);
        Problem = matches ? null : Loc.F("Craftables.Stock.Changed", string.Join(", ", slots.Select(s => s.TagText)));
        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].SetGame(matches ? slots[i] : null);
        }
    }

    /// <summary>The row as stored.</summary>
    public StockRecipeOverride ToModel() => new() { Recipe = Recipe, Slots = Slots.Select(s => new StockSlotAmounts(s.Tags, s.Levels.ToModel())).ToList() };

    /// <summary>The game's own amounts again (one Undo step).</summary>
    [RelayCommand]
    private void ResetToGame()
    {
        if (_game is not { } game || game.Count != Slots.Count)
        {
            return;
        }

        for (var i = 0; i < Slots.Count; i++)
        {
            Slots[i].Levels.Set(game[i].Amount);
        }

        _changed(this);
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>One slot of a game recipe: what it accepts and its amounts per skill level.</summary>
public sealed partial class StockSlotRowViewModel : ObservableObject
{
    /// <summary>Creates the slot.</summary>
    public StockSlotRowViewModel(string tags, SkillLevelsViewModel levels)
    {
        Tags = tags;
        Levels = levels;
    }

    /// <summary>The tags it accepts (<c>CI_Plank</c>, <c>CI_Group_Logs|CI_Group_Logs_Small</c>).</summary>
    public string Tags { get; }

    /// <summary>The tags as the page shows them.</summary>
    public string Title => Tags.Replace("|", " / ", StringComparison.Ordinal);

    /// <summary>The amounts per skill level.</summary>
    public SkillLevelsViewModel Levels { get; }

    /// <summary>"Game: 14/12/11/10/8", or empty until read.</summary>
    [ObservableProperty]
    private string _gameText = string.Empty;

    /// <summary>The slot is a tool (used, not consumed).</summary>
    [ObservableProperty]
    private bool _isTool;

    internal void SetGame(RecipeSlot? slot)
    {
        GameText = slot is null ? string.Empty : Loc.F("Craftables.Stock.Game", slot.Amount);
        IsTool = slot?.IsTool == true;
    }
}

/// <summary>The five amounts of one ingredient, one per skill level (no skill … above advanced).</summary>
public sealed partial class SkillLevelsViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _quiet;

    /// <summary>Creates the editor of <paramref name="amounts"/>; <paramref name="changed"/> runs after each edit.</summary>
    public SkillLevelsViewModel(LevelAmounts amounts, Action changed)
    {
        _changed = changed;
        _quiet = true;
        Set(amounts);
        _quiet = false;
    }

    /// <summary>Without the skill.</summary>
    [ObservableProperty]
    private decimal _noSkill;

    /// <summary>Basic level.</summary>
    [ObservableProperty]
    private decimal _basic;

    /// <summary>Medium level.</summary>
    [ObservableProperty]
    private decimal _medium;

    /// <summary>Advanced level.</summary>
    [ObservableProperty]
    private decimal _advanced;

    /// <summary>Above advanced level.</summary>
    [ObservableProperty]
    private decimal _aboveAdvanced;

    /// <summary>The amounts (each within 1 and <see cref="IngredientPickerViewModel.MaxAmount"/>).</summary>
    public LevelAmounts ToModel() => new LevelAmounts(Whole(NoSkill), Whole(Basic), Whole(Medium), Whole(Advanced), Whole(AboveAdvanced)).Clamped(IngredientPickerViewModel.MaxAmount);

    /// <summary>Shows <paramref name="amounts"/> (no change is reported; the caller makes it one step).</summary>
    public void Set(LevelAmounts amounts)
    {
        var quiet = _quiet;
        _quiet = true;
        NoSkill = amounts.NoSkill;
        Basic = amounts.Basic;
        Medium = amounts.Medium;
        Advanced = amounts.Advanced;
        AboveAdvanced = amounts.AboveAdvanced;
        _quiet = quiet;
    }

    /// <summary>The name of the game's skill class (<c>EngineeringSkill</c>) in the current language; "…" when none.</summary>
    public static string SkillLabel(string? skill) => skill is { Length: > 0 }
        ? Loc.Instance.Or("Craftables.Skill." + skill, skill.EndsWith("Skill", StringComparison.Ordinal) ? skill[..^5] : skill)
        : "…";

    partial void OnNoSkillChanged(decimal value) => Changed();

    partial void OnBasicChanged(decimal value) => Changed();

    partial void OnMediumChanged(decimal value) => Changed();

    partial void OnAdvancedChanged(decimal value) => Changed();

    partial void OnAboveAdvancedChanged(decimal value) => Changed();

    private void Changed()
    {
        if (!_quiet)
        {
            _changed();
        }
    }

    private static int Whole(decimal value) => (int)Math.Clamp(Math.Round(value), 0, int.MaxValue);
}
