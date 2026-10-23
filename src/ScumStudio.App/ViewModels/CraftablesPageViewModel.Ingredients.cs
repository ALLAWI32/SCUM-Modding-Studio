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
                var sorted = CraftGalleryViewModel.Sort(catalog.Index ?? catalog.BuildIndex(resolveClasses: false), items, p => CraftablesPlanner.NativeClassOf(catalog, p));
                return (sorted.Items, sorted.Other.Where(o => items.Contains(o.Entry.PackagePath)).Select(o => o.Entry).ToList());
            },
            () =>
            {
                var index = IngredientIndex.Of(catalog);
                return index.Tags.Select(t => new IngredientGroup(t.Name, t.Caption, t.Representative, index.Members(t.Name))).ToList();
            },
            item => LookupOf(IngredientIndex.Of(catalog), item, stock.Value, setup => CraftablesPlanner.IsGamePackage(catalog, setup)),
            item => CraftablesPlanner.NewTagRefusalOf(catalog, IngredientIndex.Of(catalog), item) switch
            {
                NewTagRefusal.None => null,
                NewTagRefusal.NotItem => Loc.T("Craftables.Groups.Refused.NotItem"),
                NewTagRefusal.NoSetup => Loc.T("Craftables.Groups.Refused.NoSetup"),
                NewTagRefusal.Dlc => Loc.T("Craftables.Groups.Refused.Dlc"),
                _ => Loc.T("Craftables.Groups.Refused.NotGameOwn"),
            });
    }

    /// <summary>
    /// How the game knows <paramref name="item"/>. A new tag only for an item whose entity setup is the game's own:
    /// <paramref name="gameOwn"/> (the catalog's check) decides; the embedded list <paramref name="stock"/> (game build
    /// 1.3.3.4) only stands in without it. Items a later game update added (Mutant_Jacket, Crow_Meat …: 10 in 1.3.4) are the
    /// game's too: the list alone left their "Only this item" box greyed out (r40).
    /// </summary>
    internal static IngredientLookup LookupOf(IngredientIndex index, string item, IReadOnlySet<string> stock, Func<string, bool>? gameOwn = null)
    {
        var choice = index.Resolve(item);
        var canAdd = choice.Match is IngredientMatch.GroupOnly or IngredientMatch.NoTag && index.SetupOf(item) is { } setup && (gameOwn?.Invoke(setup) ?? stock.Contains(setup)) // not a mod's override
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
            _source = catalog is null ? null : SourceOf(catalog);
            Picker.Load(_source);
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
            RefreshIngredients(); // a group shows its members
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
        row.SetIngredients(row.Ingredients.Select((i, n) => n == at ? pick.ToIngredient(i.ToModel()) : i.ToModel()).ToList());
        for (var n = 0; n < row.Ingredients.Count; n++)
        {
            var was = n == at ? pick : shown[n];
            row.Ingredients[n].Show(was.Lookup, was.Picture);
        }

        Save();
        RefreshIngredients();
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
        await ShowIngredientsAsync(row, catalog, cts.Token).ConfigureAwait(true);
        await element.ConfigureAwait(true);
    }

    /// <summary>
    /// The badges, pictures and group members of <paramref name="row"/>'s ingredients, read on a worker: an item's tags, a
    /// tag's items (igor8802: "view groups of ingredients"), a project group's own list.
    /// </summary>
    private async Task ShowIngredientsAsync(CraftableRowViewModel row, AssetCatalog catalog, CancellationToken cancellationToken)
    {
        var ingredients = row.Ingredients.ToList();
        var groups = Groups.ToDictionary(g => g.Tag, g => g.ToModel(), StringComparer.OrdinalIgnoreCase);
        try
        {
            var stock = AssetDumper.Packages.Select(p => p.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shown = await Task.Run(() =>
            {
                var index = IngredientIndex.Of(catalog);
                (IngredientLookup? Lookup, string? Picture, IReadOnlyList<string>? Members, CraftGroup? Group) Read(IngredientRowViewModel i)
                {
                    if (i.Item is { } item)
                    {
                        return (LookupOf(index, item, stock, setup => CraftablesPlanner.IsGamePackage(catalog, setup)), item, null, null);
                    }

                    if (groups.GetValueOrDefault(i.Tag.Trim()) is { } group)
                    {
                        return (null, group.Members.FirstOrDefault(), group.Members, group);
                    }

                    return index.Tag(i.Tag) is { } tag ? (null, tag.Representative, index.Members(tag.Name), null) : (null, null, null, null);
                }

                return ingredients.Select(Read).ToList();
            }, cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested)
            {
                for (var i = 0; i < ingredients.Count; i++)
                {
                    ingredients[i].ShowMembers(shown[i].Members, shown[i].Group?.Name);
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
    }

    /// <summary>The selected craftable's ingredient badges and members again (after its list or a group changed).</summary>
    private void RefreshIngredients()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        if (_services.Workspace.Catalog is { } catalog)
        {
            _ = ShowIngredientsAsync(row, catalog, _selectedCts?.Token ?? CancellationToken.None);
            return;
        }

        // Without the game's files only the project's groups are known (their members are the project's own list).
        foreach (var ingredient in row.Ingredients.Where(i => i.Item is null))
        {
            var group = Groups.FirstOrDefault(g => string.Equals(g.Tag, ingredient.Tag.Trim(), StringComparison.OrdinalIgnoreCase));
            ingredient.ShowMembers(group?.Members, group?.Name);
        }
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

    /// <summary>The item's name, the project group's name, or the tag.</summary>
    public string Title => Item is { } item ? item[(item.LastIndexOf('/') + 1)..] : _groupName ?? Tag;

    /// <summary>The tag the recipe asks for ("…" while a new one is to be made), or a project group's tag.</summary>
    public string Detail => Item is not { } item ? (_groupName is null ? string.Empty : Tag) : AddTag ? IngredientIndex.NewTagName(item) : Tag;

    /// <summary>"Own tag", "Group only", "New tag", "No tag", "Group" or "Project group".</summary>
    public string? BadgeText => Badges.Text(IsGroup, Lookup, AddTag, IsProjectGroup);

    /// <summary>What the badge means.</summary>
    public string? BadgeTip => Badges.Tip(IsGroup, Lookup, AddTag, IsProjectGroup);

    private string? _groupName;
    private IReadOnlyList<string>? _members;

    /// <summary>A group of the project (<see cref="CraftGroup"/>), made by the export.</summary>
    public bool IsProjectGroup => _groupName is not null;

    /// <summary>"Members: Rags, Cloth strip …" for a tag or group (the items that count as it), or null for an item.</summary>
    public string? MembersText => Item is null ? GroupMembers.Text(_members) : null;

    /// <summary>Every member, one per line.</summary>
    public string? MembersTip => Item is null ? GroupMembers.Full(_members) : null;

    /// <summary>The members line is shown.</summary>
    public bool HasMembers => MembersText is not null;

    /// <summary>Shows the items that count as this tag or group (null: not known), and the project group's name when it is one.</summary>
    internal void ShowMembers(IReadOnlyList<string>? members, string? projectGroup)
    {
        _members = members;
        var wasGroup = _groupName;
        _groupName = projectGroup;
        OnPropertyChanged(nameof(MembersText));
        OnPropertyChanged(nameof(MembersTip));
        OnPropertyChanged(nameof(HasMembers));
        if (!string.Equals(wasGroup, projectGroup, StringComparison.Ordinal))
        {
            _picture = null;
            OnPropertyChanged(nameof(IsProjectGroup));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(BadgeTip));
            OnPropertyChanged(nameof(HasBadge));
            OnPropertyChanged(nameof(Picture));
        }
    }

    /// <summary>A badge is shown.</summary>
    public bool HasBadge => BadgeText is not null;

    /// <summary>The 3D picture: the item's, or the group's representative item's.</summary>
    public IngredientTileViewModel Picture => _picture ??= new IngredientTileViewModel(Title, Tag, Item, Item is null ? Tag : null, Item ?? _picturePath,
        _pictures ?? ((_, _) => Task.FromResult<Bitmap?>(null)));

    private bool IsGroup => Item is null && IngredientIndex.IsGroup(Tag);

    /// <summary>Kept by the recipe: an item, or a tag.</summary>
    internal bool IsKept => Item is not null || !string.IsNullOrWhiteSpace(Tag);

    /// <summary>
    /// The row as a <see cref="CraftIngredient"/> (amounts within 1 and <see cref="IngredientPickerViewModel.MaxAmount"/>);
    /// with <see cref="PerLevel"/> on it carries the five amounts, its amount being the no-skill one.
    /// </summary>
    public CraftIngredient ToModel()
    {
        var levels = PerLevel ? Levels.ToModel() : (LevelAmounts?)null;
        return new(Tag.Trim(), levels?.NoSkill ?? (int)Math.Clamp(Math.Round(Amount), 1, IngredientPickerViewModel.MaxAmount), IsTool, Item, Item is not null && AddTag, levels);
    }

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

    /// <summary>
    /// The ingredient <paramref name="pick"/> puts in place of <paramref name="was"/>: its amounts per skill level stay
    /// while the amount is the same (another item for the same price), a new amount starts them again.
    /// </summary>
    public static CraftIngredient ToIngredient(this IngredientPick pick, CraftIngredient was) =>
        pick.ToIngredient() is var picked && was.Levels is not null && picked.Amount == was.Amount ? picked with { Levels = was.Levels } : picked;
}

/// <summary>The pictures of a craftable's ingredients.</summary>
public sealed partial class CraftableRowViewModel
{
    /// <summary>An ingredient item's 3D picture (made like the craftable's own: an item shows its Blueprint).</summary>
    private Task<Bitmap?> IngredientPicture(string item, CancellationToken cancellationToken) =>
        _picture(new Craftable { Name = item, Source = item, Mesh = item }, cancellationToken);
}
