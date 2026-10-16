using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Ingredients picked in 3D (owner, 2026-10-12: "what he pays for them also chosen in 3D, not typed"; CwHedgehog: "Can I
/// add items as new crafting materials?"): Add ingredient and Change open <see cref="Picker"/>; a picked item is written
/// with the tag the game knows it by (<see cref="IngredientIndex.Resolve"/>), or a new one when the user allows it.
/// </summary>
public sealed partial class CraftablesPageViewModel
{
    private AssetCatalog? _pickerOf;
    private CancellationTokenSource? _selectedCts;

    /// <summary>Every inventory item and ingredient group of the game in 3D tiles.</summary>
    public IngredientPickerViewModel Picker { get; private set; }

    /// <summary>The game's items, groups and tags for the picker (each call made on a worker).</summary>
    internal static IngredientSource SourceOf(AssetCatalog catalog)
    {
        // An item the embedded catalogue lists is the game's own: only its entity setup may get a new tag (an imported
        // mod's setup ships with the mod, a DLC's stays as it is; the planner checks the same).
        var stock = new Lazy<HashSet<string>>(() => AssetDumper.Packages.Select(p => p.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return new IngredientSource(
            () =>
            {
                var items = CraftablesPlanner.Items(catalog);
                var sorted = CraftGalleryViewModel.Sort(catalog.Index ?? catalog.BuildIndex(resolveClasses: false), items);
                return (sorted.Items, sorted.Other.Where(o => items.Contains(o.Entry.PackagePath)).Select(o => o.Entry).ToList());
            },
            () => IngredientIndex.Of(catalog).Tags.Select(t => new IngredientGroup(t.Name, t.Caption, t.Representative)).ToList(),
            item => LookupOf(IngredientIndex.Of(catalog), item, stock.Value, setup => CraftablesPlanner.IsGamePackage(catalog, setup)));
    }

    /// <summary>How the game knows <paramref name="item"/>; a new tag only for an item whose entity setup is in <paramref name="stock"/>.</summary>
    internal static IngredientLookup LookupOf(IngredientIndex index, string item, IReadOnlySet<string> stock, Func<string, bool>? gameOwn = null)
    {
        var choice = index.Resolve(item);
        var canAdd = choice.Match is IngredientMatch.GroupOnly or IngredientMatch.NoTag && index.SetupOf(item) is { } setup && stock.Contains(setup) && (gameOwn?.Invoke(setup) ?? true) // not a mod's override
            && !setup.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase); // a DLC item's setup is not changed
        return choice.Match switch
        {
            IngredientMatch.OwnTag => new IngredientLookup(ItemTagState.OwnTag, choice.Tag, choice.Groups, false),
            IngredientMatch.GroupOnly => new IngredientLookup(ItemTagState.GroupOnly, choice.Groups.FirstOrDefault(IngredientIndex.IsGroup) ?? choice.Groups.FirstOrDefault(), choice.Groups, canAdd),
            _ => new IngredientLookup(ItemTagState.NoTag, null, [], canAdd),
        };
    }

    [MemberNotNull(nameof(Picker))]
    private void InitPickers()
    {
        Picker = new IngredientPickerViewModel((item, ct) => PictureAsync(new Craftable { Name = item, Source = item, Mesh = item }, ct));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedItem))
            {
                Picker.CancelCommand.Execute(null); // a pick belongs to the craftable it was opened for
                _ = ShowSelectedAsync(SelectedItem);
            }
        };
    }

    /// <summary>The picker lists the current catalog's items (read again after a new connection).</summary>
    private void EnsurePicker()
    {
        var catalog = _services.Workspace.Catalog;
        if (!ReferenceEquals(_pickerOf, catalog))
        {
            _pickerOf = catalog;
            Picker.Load(catalog is null ? null : SourceOf(catalog));
        }
    }

    /// <summary>Adds an ingredient picked in the picker to the selected craftable.</summary>
    [RelayCommand]
    private async Task PickIngredientAsync()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        EnsurePicker();
        if (await Picker.PickAsync().ConfigureAwait(true) is { } pick && Items.Contains(row))
        {
            Remember();
            row.SetIngredients([.. row.ToModel().Ingredients, pick.ToIngredient()]);
            row.Ingredients[^1].Show(pick.Lookup, pick.Picture);
            Save();
        }
    }

    /// <summary>Picks another item or group for <paramref name="ingredient"/>.</summary>
    [RelayCommand]
    private async Task ChangeIngredientAsync(IngredientRowViewModel? ingredient)
    {
        if (ingredient is null || SelectedItem is not { } row)
        {
            return;
        }

        EnsurePicker();
        var pick = await Picker.PickAsync(ingredient.ToPick()).ConfigureAwait(true);
        var at = row.Ingredients.IndexOf(ingredient);
        if (pick is null || at < 0)
        {
            return;
        }

        // One change, one Undo step; the other rows keep their badges and pictures.
        var shown = row.Ingredients.Select(i => i.ToPick()).ToList();
        Remember();
        row.SetIngredients(row.Ingredients.Select((i, n) => n == at ? pick.ToIngredient() : i.ToModel()).ToList());
        for (var n = 0; n < row.Ingredients.Count; n++)
        {
            var was = n == at ? pick : shown[n];
            row.Ingredients[n].Show(was.Lookup, was.Picture);
        }

        Save();
    }

    /// <summary>The selected craftable's ingredient badges and pictures and its element family, read on a worker.</summary>
    private async Task ShowSelectedAsync(CraftableRowViewModel? row)
    {
        _selectedCts?.Cancel();
        if (row is null || _services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        var cts = _selectedCts = new CancellationTokenSource();
        var element = ShowElementAsync(row, catalog, cts.Token);
        var ingredients = row.Ingredients.ToList();
        try
        {
            var stock = AssetDumper.Packages.Select(p => p.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shown = await Task.Run(() =>
            {
                var index = IngredientIndex.Of(catalog);
                return ingredients.Select(i => i.Item is { } item
                    ? (Lookup: (IngredientLookup?)LookupOf(index, item, stock, setup => CraftablesPlanner.IsGamePackage(catalog, setup)), Picture: (string?)item)
                    : (Lookup: null, Picture: index.Tag(i.Tag)?.Representative)).ToList();
            }, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                for (var i = 0; i < ingredients.Count; i++)
                {
                    ingredients[i].Show(shown[i].Lookup, shown[i].Picture);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Ingredient tags not read: {Message}", ex.Message);
        }

        await element.ConfigureAwait(true);
    }
}

/// <summary>An ingredient picked in 3D: the item (or group), its badge and its picture.</summary>
public sealed partial class IngredientRowViewModel
{
    private readonly Func<string, CancellationToken, Task<Bitmap?>>? _pictures;
    private string? _item;
    private bool _addTag;
    private string? _picturePath;
    private IngredientTileViewModel? _picture;

    /// <summary>The item picked (package path), or null for an ingredient group or a tag.</summary>
    public string? Item => _item;

    /// <summary>Add a tag of its own to the item (changes the game's entity setup of that item; off by default).</summary>
    public bool AddTag
    {
        get => _addTag;
        set
        {
            if (SetProperty(ref _addTag, value && CanAddTag))
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(BadgeTip));
                _changed();
            }
        }
    }

    /// <summary>How the game knows the item (null for a group, or until it is read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTag), nameof(BadgeText), nameof(BadgeTip), nameof(HasBadge))]
    private IngredientLookup? _lookup;

    /// <summary>The item has no tag of its own and its entity setup is the game's (a stored opt-in can always be unticked).</summary>
    public bool CanAddTag => Item is not null && (Lookup?.CanAddTag == true || _addTag);

    /// <summary>The item's name, or the tag.</summary>
    public string Title => Item is { } item ? item[(item.LastIndexOf('/') + 1)..] : Tag;

    /// <summary>The tag the recipe asks for ("…" while a new one is to be made), or the group's caption.</summary>
    public string Detail => Item is not { } item ? string.Empty : AddTag ? IngredientIndex.NewTagName(item) : Tag;

    /// <summary>"Own tag", "Group only", "New tag", "No tag" or "Group".</summary>
    public string? BadgeText => Badges.Text(IsGroup, Lookup, AddTag);

    /// <summary>What the badge means.</summary>
    public string? BadgeTip => Badges.Tip(IsGroup, Lookup, AddTag);

    /// <summary>A badge is shown.</summary>
    public bool HasBadge => BadgeText is not null;

    /// <summary>The 3D picture: the item's, or the group's representative item's.</summary>
    public IngredientTileViewModel Picture => _picture ??= new IngredientTileViewModel(Title, Tag, Item, Item is null ? Tag : null, Item ?? _picturePath,
        _pictures ?? ((_, _) => Task.FromResult<Bitmap?>(null)));

    private bool IsGroup => Item is null && IngredientIndex.IsGroup(Tag);

    /// <summary>Kept by the recipe: an item, or a tag.</summary>
    internal bool IsKept => Item is not null || !string.IsNullOrWhiteSpace(Tag);

    /// <summary>The row as a <see cref="CraftIngredient"/> (amount within 1 and <see cref="IngredientPickerViewModel.MaxAmount"/>).</summary>
    public CraftIngredient ToModel() =>
        new(Tag.Trim(), (int)Math.Clamp(Math.Round(Amount), 1, IngredientPickerViewModel.MaxAmount), IsTool, Item, Item is not null && AddTag);

    /// <summary>The row as the picker opens it.</summary>
    internal IngredientPick ToPick() => new(Item, Tag, ToModel().Amount, IsTool, AddTag, Lookup, Item ?? _picturePath);

    /// <summary>Shows how the game knows the item, or the group's representative picture.</summary>
    internal void Show(IngredientLookup? lookup, string? picture)
    {
        Lookup = lookup; // a stored opt-in stays (the export reports an item it cannot add a tag to)
        if (!string.Equals(_picturePath, picture, StringComparison.OrdinalIgnoreCase))
        {
            _picturePath = picture;
            _picture = null;
            OnPropertyChanged(nameof(Picture));
        }

        OnPropertyChanged(nameof(Detail));
    }

}

/// <summary>The picker's answer as a recipe ingredient.</summary>
internal static class IngredientPicks
{
    /// <summary>The ingredient <paramref name="pick"/> adds to a recipe.</summary>
    public static CraftIngredient ToIngredient(this IngredientPick pick) => new(pick.Tag, pick.Amount, pick.IsTool, pick.Item, pick.Item is not null && pick.AddTag);
}

/// <summary>The pictures of a craftable's ingredients.</summary>
public sealed partial class CraftableRowViewModel
{
    /// <summary>An ingredient item's 3D picture (made like the craftable's own: an item shows its Blueprint).</summary>
    private Task<Bitmap?> IngredientPicture(string item, CancellationToken cancellationToken) =>
        _picture(new Craftable { Name = item, Source = item, Mesh = item }, cancellationToken);
}
