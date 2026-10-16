using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.App.ViewModels;

/// <summary>How the game knows an item as a crafting ingredient (the tags its entity setup lists).</summary>
public enum ItemTagState
{
    /// <summary>A tag of its own (<c>Lead_Plate</c> → <c>CI_Lead_Plate</c>): a recipe asks for exactly this item.</summary>
    OwnTag,

    /// <summary>Only group tags (<c>CI_Group_Rags</c>): a recipe asking for the group takes any item of it.</summary>
    GroupOnly,

    /// <summary>No tag: no recipe can ask for it until one is added to its entity setup.</summary>
    NoTag,
}

/// <summary>An ingredient group of the game (a <c>CI_*</c> tag) as the picker shows it.</summary>
/// <param name="Tag">Its name (<c>CI_Group_Rags</c>).</param>
/// <param name="Caption">The game's caption for it (<c>ClassRepresentativeCaption</c>), or null.</param>
/// <param name="Representative">The item the game shows for it (<c>UIClassRepresentative</c>, package path), or null.</param>
public sealed record IngredientGroup(string Tag, string? Caption, string? Representative);

/// <summary>How the game knows a picked item.</summary>
/// <param name="State">Own tag, groups only or none.</param>
/// <param name="Tag">The tag a recipe asks for: its own, else its first group; null without any.</param>
/// <param name="Groups">The group tags it satisfies.</param>
/// <param name="CanAddTag">A tag of its own can be added (it has none and its entity setup is the game's own).</param>
public sealed record IngredientLookup(ItemTagState State, string? Tag, IReadOnlyList<string> Groups, bool CanAddTag);

/// <summary>What the picker reads from the game; each call runs on a worker, never on the UI thread (tests give their own).</summary>
/// <param name="Items">Inventory items the catalogue knows (with their category) and the ones it does not (imported mods).</param>
/// <param name="Groups">The game's ingredient groups.</param>
/// <param name="Resolve">How the game knows an item (package path).</param>
public sealed record IngredientSource(
    Func<(IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> Known, IReadOnlyList<PackageEntry> Other)> Items,
    Func<IReadOnlyList<IngredientGroup>> Groups,
    Func<string, IngredientLookup> Resolve);

/// <summary>The picker's answer: an item of the game or an ingredient group, with its amount.</summary>
/// <param name="Item">The item (package path), or null for a group.</param>
/// <param name="Tag">The tag the recipe asks for (empty when a new one is to be added).</param>
/// <param name="Amount">Amount without skill, 1 to <see cref="IngredientPickerViewModel.MaxAmount"/>.</param>
/// <param name="IsTool">Used, not consumed.</param>
/// <param name="AddTag">A tag of its own is added to the item's entity setup.</param>
/// <param name="Lookup">How the game knows the item, or null for a group.</param>
/// <param name="Picture">The item its picture shows (the item, or a group's representative), or null.</param>
public sealed record IngredientPick(string? Item, string Tag, int Amount, bool IsTool, bool AddTag, IngredientLookup? Lookup, string? Picture);

/// <summary>
/// The ingredient picker of the Craftables page (owner, 2026-10-12: "what he pays for them also chosen in 3D, not typed"):
/// every inventory item of the game and of imported mods in 3D tiles, by the catalogue's pick-up categories (the gallery's
/// item list, <see cref="CraftGalleryViewModel.Sort"/>), plus the game's ingredient groups shown by their representative
/// item. Rows are virtualized and a picture is made only for a tile on screen; the item list, the groups and each item's
/// tags are read on a worker. <see cref="PickAsync"/> opens it and completes with OK or Cancel.
/// </summary>
public sealed partial class IngredientPickerViewModel : ViewModelBase
{
    /// <summary>Largest amount of an ingredient.</summary>
    public const int MaxAmount = 9999;

    private readonly Func<string, CancellationToken, Task<Bitmap?>> _pictures;
    private IngredientSource? _source;
    private IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> _items = [];
    private IReadOnlyList<PackageEntry> _other = [];
    private IReadOnlyList<IngredientGroup> _groups = [];
    private ObjectCategoryNode? _otherNode;
    private ObjectCategoryNode? _groupsNode;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _resolveCts;
    private TaskCompletionSource<IngredientPick?>? _answer;
    private decimal _amount = 1;

    /// <summary>Creates the picker; <paramref name="pictures"/> makes an item's 3D picture.</summary>
    public IngredientPickerViewModel(Func<string, CancellationToken, Task<Bitmap?>> pictures) => _pictures = pictures;

    /// <summary>Shown over the recipe.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The pick-up categories, the ingredient groups and the items the catalogue does not know.</summary>
    [ObservableProperty]
    private IReadOnlyList<ObjectCategoryNode> _roots = [];

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    private ObjectCategoryNode? _selectedCategory;

    /// <summary>Search text (items by the Assets page's filter, groups by tag and caption).</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>Listed items and groups.</summary>
    [ObservableProperty]
    private IReadOnlyList<IngredientTileViewModel> _tiles = [];

    /// <summary><see cref="Tiles"/> cut into rows of <see cref="Columns"/> for the virtualized grid.</summary>
    [ObservableProperty]
    private IReadOnlyList<IngredientTileRow> _rows = [];

    /// <summary>Tiles per row (set by the view from its width).</summary>
    [ObservableProperty]
    private int _columns = 5;

    /// <summary>"120 objects in Food".</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>True while the items and groups are read.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>The picked tile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(BadgeText), nameof(BadgeTip), nameof(HasBadge), nameof(SelectedText))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private IngredientTileViewModel? _selected;

    /// <summary>How the game knows the picked item (null for a group or while it is read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTag), nameof(BadgeText), nameof(BadgeTip), nameof(HasBadge))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private IngredientLookup? _lookup;

    /// <summary>The picked item's tags are being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(HasBadge))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private bool _isResolving;

    /// <summary>Used, not consumed.</summary>
    [ObservableProperty]
    private bool _isTool;

    /// <summary>Add a tag of its own to the item (changes the game's entity setup of that item; off by default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(BadgeTip))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private bool _addTag;

    /// <summary>Amount without skill, kept within 1 and <see cref="MaxAmount"/>.</summary>
    public decimal Amount
    {
        get => _amount;
        set => SetProperty(ref _amount, Math.Clamp(Math.Round(value), 1, MaxAmount));
    }

    /// <summary>A tile is picked.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>"Lead plate · CI_Lead_Plate".</summary>
    public string SelectedText => Selected is null ? Loc.T("Craftables.Picker.None") : Selected.Name + " · " + Selected.Detail;

    /// <summary>The picked item has no tag of its own and its entity setup is the game's.</summary>
    public bool CanAddTag => Lookup?.CanAddTag == true;

    /// <summary>The badge of the picked tile.</summary>
    public string? BadgeText => Selected is null ? null : IsResolving ? Loc.T("Craftables.Ingredient.Checking") : Badges.Text(Selected.IsGroup, Lookup, AddTag);

    /// <summary>What the badge means.</summary>
    public string? BadgeTip => Selected is null ? null : Badges.Tip(Selected.IsGroup, Lookup, AddTag);

    /// <summary>A badge is shown.</summary>
    public bool HasBadge => BadgeText is not null;

    /// <summary>Completes when the last <see cref="Load"/> finished (tests).</summary>
    public Task LoadCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the picked item's tags are read (tests).</summary>
    public Task ResolveCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the items and groups of <paramref name="source"/> on a worker (nothing without one).</summary>
    public void Load(IngredientSource? source)
    {
        _source = source;
        _items = [];
        _other = [];
        _groups = [];
        Roots = [];
        Tiles = [];
        ResultText = string.Empty;
        IsLoading = source is not null;
        LoadCompletion = source is null ? Task.CompletedTask : LoadAsync(source);
    }

    /// <summary>
    /// Opens the picker, set to <paramref name="current"/> (a row being changed) or to one of the item; completes with the
    /// pick, or null on Cancel.
    /// </summary>
    public Task<IngredientPick?> PickAsync(IngredientPick? current = null)
    {
        _answer?.TrySetResult(null);
        _answer = new TaskCompletionSource<IngredientPick?>();
        Amount = current?.Amount ?? 1;
        IsTool = current?.IsTool ?? false;
        AddTag = current?.AddTag ?? false;
        Selected = null;
        Lookup = null;
        if (current is not null)
        {
            var tile = current.Item is { } item
                ? new IngredientTileViewModel(item[(item.LastIndexOf('/') + 1)..], current.Tag, item, null, item, _pictures)
                : new IngredientTileViewModel(current.Tag, current.Tag, null, current.Tag, current.Picture, _pictures);
            Select(tile);
        }

        IsOpen = true;
        return _answer.Task;
    }

    /// <summary>The lists as <see cref="Load"/> gives them; builds the category tree (tests call it with their own lists).</summary>
    internal void Show(IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> items, IReadOnlyList<PackageEntry> other, IReadOnlyList<IngredientGroup> groups)
    {
        (_items, _other, _groups) = (items, other, groups);
        string[] roots = [.. AssetsPageViewModel.PickUpRoots, .. items.Select(i => Root(i.Package.Node)).Distinct().Except(AssetsPageViewModel.PickUpRoots)];
        var known = AssetsPageViewModel.CategoryGroup(Loc.T("Assets.Group.PickUp"), roots, items.Select(i => i.Package));
        _groupsNode = new ObjectCategoryNode(Loc.T("Craftables.Picker.Groups"), [], [], groups.Count) { Match = _ => false };
        _otherNode = other.Count > 0 ? new ObjectCategoryNode(Loc.T("Craftables.Gallery.Other"), [], [], other.Count) { Match = _ => false } : null;
        Roots = _otherNode is null ? [known, _groupsNode] : [known, _groupsNode, _otherNode];
        SelectedCategory = known;
        if (!string.IsNullOrWhiteSpace(Query))
        {
            _ = SearchAsync(Query);
        }
        else
        {
            ShowCategory(SelectedCategory);
        }
    }

    /// <summary>The group of <paramref name="tag"/> as the game lists it, or null.</summary>
    internal IngredientGroup? GroupOf(string tag) => _groups.FirstOrDefault(g => string.Equals(g.Tag, tag, StringComparison.OrdinalIgnoreCase));

    private async Task LoadAsync(IngredientSource source)
    {
        try
        {
            var (lists, groups) = await Task.Run(() => (source.Items(), source.Groups())).ConfigureAwait(true);
            if (ReferenceEquals(_source, source))
            {
                Show(lists.Known, lists.Other, groups);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ResultText = ex.Message; // an unreadable game lists nothing, and says why
        }
        finally
        {
            if (ReferenceEquals(_source, source))
            {
                IsLoading = false;
            }
        }
    }

    private static string Root(string node) => node[..(node.IndexOf('.') is var dot and >= 0 ? dot : node.Length)];

    partial void OnSelectedCategoryChanged(ObjectCategoryNode? value)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            ShowCategory(value);
        }
    }

    partial void OnQueryChanged(string value) => _ = SearchAsync(value, debounce: true);

    partial void OnTilesChanged(IReadOnlyList<IngredientTileViewModel> value) => Rows = IngredientTileRow.Chunk(value, Columns);

    partial void OnColumnsChanged(int value) => Rows = IngredientTileRow.Chunk(Tiles, value);

    private void ShowCategory(ObjectCategoryNode? category)
    {
        var max = AssetsPageViewModel.MaxListed;
        if (category is null)
        {
            Show([], string.Empty);
            return;
        }

        if (ReferenceEquals(category, _groupsNode))
        {
            Show(_groups.Take(max).Select(GroupTile), Loc.F(_groups.Count == 1 ? "Assets.Objects.One" : "Assets.Objects.Many", _groups.Count, category.Title));
            return;
        }

        var all = ReferenceEquals(category, _otherNode) ? _other : _items.Where(o => category.Contains(o.Package)).Select(o => o.Entry).ToList();
        Show(all.Take(max).Select(ItemTile), all.Count > max
            ? Loc.F("Assets.ShowingObjects", max, all.Count, category.Title)
            : Loc.F(all.Count == 1 ? "Assets.Objects.One" : "Assets.Objects.Many", all.Count, category.Title));
    }

    private async Task SearchAsync(string text, bool debounce = false)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (debounce)
            {
                await Task.Delay(150, cts.Token).ConfigureAwait(true);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowCategory(SelectedCategory);
                return;
            }

            var entries = _items.Select(i => i.Entry).Concat(_other).ToList();
            var groups = _groups;
            var (items, found, total) = await Task.Run(() =>
            {
                var (matches, count) = AssetsPageViewModel.Filter(entries, text, AssetsPageViewModel.MaxListed);
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hits = groups.Where(g => words.All(w => g.Tag.Contains(w, StringComparison.OrdinalIgnoreCase) || g.Caption?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)).ToList();
                return (matches, hits, count + hits.Count);
            }, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            Show(found.Select(GroupTile).Concat(items.Select(ItemTile)), total == 0
                ? Loc.F("Assets.NoMatch", text.Trim())
                : Loc.F(total == 1 ? "Assets.Match.One" : "Assets.Match.Many", total));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(IEnumerable<IngredientTileViewModel> tiles, string text)
    {
        Tiles = tiles.ToList();
        ResultText = text;
    }

    private IngredientTileViewModel ItemTile(PackageEntry entry) =>
        new(entry.Name, string.Join('/', entry.PackagePath.Split('/').SkipLast(1).TakeLast(2)), entry.PackagePath, null, entry.PackagePath, _pictures);

    private IngredientTileViewModel GroupTile(IngredientGroup group) =>
        new(group.Caption is { Length: > 0 } caption ? caption : group.Tag, group.Tag, null, group.Tag, group.Representative, _pictures);

    /// <summary>Picks a tile; an item's tags are read on a worker.</summary>
    [RelayCommand]
    private void Select(IngredientTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (Selected is not null)
        {
            Selected.IsSelected = false;
        }

        tile.IsSelected = true;
        Selected = tile;
        Lookup = null;
        _resolveCts?.Cancel();
        if (tile.Item is { } item && _source is { } source)
        {
            var cts = new CancellationTokenSource();
            _resolveCts = cts;
            ResolveCompletion = ResolveAsync(source, item, cts);
        }
        else
        {
            IsResolving = false;
        }
    }

    private async Task ResolveAsync(IngredientSource source, string item, CancellationTokenSource cts)
    {
        IsResolving = true;
        try
        {
            var lookup = await Task.Run(() => source.Resolve(item), cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                Lookup = lookup;
                AddTag = AddTag && lookup.CanAddTag;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Lookup = new IngredientLookup(ItemTagState.NoTag, null, [], false); // unreadable: no tag known
        }
        finally
        {
            if (ReferenceEquals(_resolveCts, cts))
            {
                IsResolving = false;
            }
        }
    }

    /// <summary>A group, an item with a tag, or an item that gets one of its own.</summary>
    private bool CanOk() => Selected is { } tile && !IsResolving && (tile.IsGroup || Lookup is { Tag: not null } || (AddTag && CanAddTag));

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (Selected is not { } tile)
        {
            return;
        }

        // An item known only by its groups is asked for by its group (any item of it), unless it gets a tag of its own.
        var addTag = tile.Item is not null && AddTag && CanAddTag;
        var pick = tile.IsGroup || (Lookup is { State: ItemTagState.GroupOnly } && !addTag)
            ? new IngredientPick(null, (tile.IsGroup ? tile.Group : Lookup?.Tag) ?? string.Empty, (int)Amount, IsTool, false, null, tile.PicturePath)
            : new IngredientPick(tile.Item, Lookup?.Tag ?? string.Empty, (int)Amount, IsTool, addTag, Lookup, tile.PicturePath);
        Close(pick);
    }

    [RelayCommand]
    private void Cancel() => Close(null);

    private void Close(IngredientPick? pick)
    {
        IsOpen = false;
        _resolveCts?.Cancel();
        var answer = _answer;
        _answer = null;
        answer?.TrySetResult(pick);
    }
}

/// <summary>The badge an ingredient shows: how the recipe asks for it.</summary>
internal static class Badges
{
    /// <summary>"Own tag", "Group only", "New tag", "No tag" or "Group"; null while unknown.</summary>
    public static string? Text(bool isGroup, IngredientLookup? lookup, bool addTag) => Key(isGroup, lookup, addTag) is { } key ? Loc.T(key) : null;

    /// <summary>What <see cref="Text"/> means for this ingredient.</summary>
    public static string? Tip(bool isGroup, IngredientLookup? lookup, bool addTag) =>
        Key(isGroup, lookup, addTag) is { } key ? Loc.F(key + ".Tip", lookup?.Tag ?? string.Empty) : null;

    private static string? Key(bool isGroup, IngredientLookup? lookup, bool addTag) =>
        isGroup ? "Craftables.Ingredient.Badge.Group"
        : lookup is null ? null
        : lookup.State == ItemTagState.OwnTag ? "Craftables.Ingredient.Badge.OwnTag"
        : addTag && lookup.CanAddTag ? "Craftables.Ingredient.Badge.NewTag"
        : lookup.State == ItemTagState.GroupOnly ? "Craftables.Ingredient.Badge.GroupOnly"
        : "Craftables.Ingredient.Badge.NoTag";
}

/// <summary>A tile of the ingredient picker: an item, or an ingredient group shown by its representative item.</summary>
public sealed class IngredientTileViewModel : ThumbnailItem
{
    private readonly Func<string, CancellationToken, Task<Bitmap?>> _pictures;
    private bool _isSelected;

    /// <summary>Creates the tile; <paramref name="picturePath"/> is the item whose 3D picture it shows.</summary>
    public IngredientTileViewModel(string name, string detail, string? item, string? group, string? picturePath, Func<string, CancellationToken, Task<Bitmap?>> pictures)
    {
        Name = name;
        Detail = detail;
        Item = item;
        Group = group;
        PicturePath = picturePath;
        _pictures = pictures;
        (IconKey, Tint) = AssetIcons.ForClass("Blueprint");
    }

    /// <summary>Item name or the group's caption.</summary>
    public string Name { get; }

    /// <summary>The item's folder or the group's tag.</summary>
    public string Detail { get; }

    /// <summary>The item (package path), or null for a group.</summary>
    public string? Item { get; }

    /// <summary>The group's tag, or null for an item.</summary>
    public string? Group { get; }

    /// <summary>An ingredient group.</summary>
    public bool IsGroup => Group is not null;

    /// <summary>The item whose picture is shown, or null.</summary>
    public string? PicturePath { get; }

    /// <summary>The item's package path or the group's tag (the tile's hover text).</summary>
    public string Tip => (Item ?? Group) ?? Name;

    /// <summary>Icon resource key (while there is no picture).</summary>
    public string IconKey { get; }

    /// <summary>Icon tint.</summary>
    public string Tint { get; }

    /// <summary>The picked tile (highlight).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        PicturePath is { } path ? _pictures(path, cancellationToken) : Task.FromResult<Bitmap?>(null);
}

/// <summary>A row of the picker's virtualized tile grid.</summary>
/// <param name="Tiles">Its tiles.</param>
public sealed record IngredientTileRow(IReadOnlyList<IngredientTileViewModel> Tiles)
{
    /// <summary>Cuts <paramref name="tiles"/> into rows of <paramref name="columns"/>.</summary>
    public static IReadOnlyList<IngredientTileRow> Chunk(IReadOnlyList<IngredientTileViewModel> tiles, int columns) =>
        tiles.Chunk(Math.Max(1, columns)).Select(c => new IngredientTileRow(c)).ToList();
}
