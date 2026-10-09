using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Add from the game" on the Craftables page (owner, 2026-10-09: "every item in the game shown in 3D like the Assets page,
/// so you can craft it for a recipe"): what can become a craftable, as the Assets page's tiles (rows virtualized, a 3D
/// picture made only while a tile is on screen) in its catalogue categories. Objects are the catalogue's world objects that
/// <see cref="RecipeRules.IsAllowedSource"/> accepts; items are what <see cref="CraftablesPlanner.IsItem"/> calls one (the
/// game's inventory items: weapons, tools, food, clothes, ammo …); packages the catalogue does not know (imported mods, pak
/// mods in the game's Paks folder, DLC packs and newer game files) get a group of their own. A click hands the package to the page.
/// </summary>
public sealed partial class CraftGalleryViewModel : ViewModelBase
{
    // The Assets page's world categories without what moves on its own (owner's rule, see RecipeRules.IsAllowedSource).
    private static readonly string[] ObjectRoots = [.. AssetsPageViewModel.WorldRoots.Except(["vehicles", "characters"])];

    private readonly AppServices _services;
    private readonly Func<string, Task> _pick;
    private readonly Func<IReadOnlySet<string>> _added;
    private AssetCatalog? _catalog;
    private IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> _objects = [];
    private IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> _items = [];
    private IReadOnlyList<(DumpPackage Package, PackageEntry Entry)> _other = [];
    private ObjectCategoryNode? _itemsNode;
    private ObjectCategoryNode? _otherNode;
    private CancellationTokenSource? _searchCts;

    /// <summary>Creates the gallery; <paramref name="pick"/> runs for a clicked package, <paramref name="added"/> gives the craftables' sources.</summary>
    public CraftGalleryViewModel(AppServices services, Func<string, Task> pick, Func<IReadOnlySet<string>> added)
    {
        _services = services;
        _pick = pick;
        _added = added;
        Load(services.Workspace.Catalog);
    }

    /// <summary>Objects, items and the packages the catalogue does not know, each with its categories.</summary>
    [ObservableProperty]
    private IReadOnlyList<ObjectCategoryNode> _roots = [];

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    private ObjectCategoryNode? _selectedCategory;

    /// <summary>Search text (the Assets page's filter, over every object and item of the gallery).</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>Listed packages.</summary>
    [ObservableProperty]
    private IReadOnlyList<AssetItemViewModel> _tiles = [];

    /// <summary><see cref="Tiles"/> cut into rows of <see cref="Columns"/> for the virtualized grid.</summary>
    [ObservableProperty]
    private IReadOnlyList<AssetTileRow> _rows = [];

    /// <summary>Tiles per row (set by the view from its width).</summary>
    [ObservableProperty]
    private int _columns = 6;

    /// <summary>"120 objects in Furniture".</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>True while the packages are sorted.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Completes when the last <see cref="Load"/> finished (tests).</summary>
    public Task LoadCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Lists what <paramref name="catalog"/> holds (nothing without one).</summary>
    public void Load(AssetCatalog? catalog)
    {
        _catalog = catalog;
        _objects = _items = _other = [];
        Roots = [];
        Tiles = [];
        ResultText = string.Empty;
        IsLoading = catalog is not null;
        if (catalog is not null)
        {
            LoadCompletion = LoadAsync(catalog);
        }
    }

    /// <summary>Renews the "added" marks (the craftables list changed).</summary>
    public void MarkAdded()
    {
        var added = _added();
        foreach (var tile in Tiles)
        {
            tile.IsAdded = added.Contains(tile.Entry.PackagePath);
        }
    }

    private async Task LoadAsync(AssetCatalog catalog)
    {
        try
        {
            var sorted = await Task.Run(() => Sort(catalog.Index ?? catalog.BuildIndex(resolveClasses: false), CraftablesPlanner.Items(catalog))).ConfigureAwait(true);
            if (!ReferenceEquals(_catalog, catalog))
            {
                return;
            }

            (_objects, _items, _other) = (sorted.Objects, sorted.Items, sorted.Other);
            var objects = AssetsPageViewModel.CategoryGroup(Loc.T("Assets.Group.World"), ObjectRoots, _objects.Select(o => o.Package));
            string[] itemRoots = [.. AssetsPageViewModel.PickUpRoots, .. _items.Select(i => Root(i.Package.Node)).Distinct().Except(AssetsPageViewModel.PickUpRoots)];
            _itemsNode = AssetsPageViewModel.CategoryGroup(Loc.T("Assets.Group.PickUp"), itemRoots, _items.Select(i => i.Package));
            _otherNode = new ObjectCategoryNode(Loc.T("Craftables.Gallery.Other"), [], [], _other.Count) { Match = _ => true };
            Roots = _other.Count > 0 ? [objects, _itemsNode, _otherNode] : [objects, _itemsNode];
            SelectedCategory = objects;
            if (!string.IsNullOrWhiteSpace(Query))
            {
                await SearchAsync(Query).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("The craftables gallery could not list the game's objects: {Message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_catalog, catalog))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Sorts the package index into objects, <paramref name="gameItems"/> and packages the catalogue does not know (on a worker).</summary>
    private static (List<(DumpPackage Package, PackageEntry Entry)> Objects, List<(DumpPackage Package, PackageEntry Entry)> Items, List<(DumpPackage Package, PackageEntry Entry)> Other) Sort(PackageIndex index, IReadOnlySet<string> gameItems)
    {
        var catalogue = new Dictionary<string, DumpPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in AssetDumper.Packages)
        {
            catalogue.TryAdd(package.PackagePath, package);
        }

        List<(DumpPackage, PackageEntry)> objects = [], items = [], other = [];
        foreach (var e in index.Entries)
        {
            var path = e.PackagePath;
            if (e.IsMap || path.EndsWith("_ES", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The catalogue knows the class; a large pak index has none, so a package it does not know goes by its name.
            var known = catalogue.TryGetValue(path, out var package);
            var className = known ? package.ClassName
                : e.ClassName ?? (e.Name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? "StaticMesh" : e.Name.StartsWith("BP", StringComparison.OrdinalIgnoreCase) ? "Blueprint" : null);
            if (gameItems.Contains(path))
            {
                (known ? items : other).Add((known ? package : new DumpPackage(path, string.Empty, "Blueprint"), e with { ClassName = "Blueprint" }));
            }
            else if (className is not null && RecipeRules.IsAllowedSource(path, className) && !FarModels.IsFarViewMesh(path) && !FarModels.IsUndersideMesh(path))
            {
                if (!known)
                {
                    other.Add((new DumpPackage(path, string.Empty, className), e with { ClassName = className }));
                }
                else if (ObjectRoots.Contains(Root(package.Node)))
                {
                    objects.Add((package, e with { ClassName = className }));
                }
            }
        }

        return (objects, items, other);
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

    partial void OnTilesChanged(IReadOnlyList<AssetItemViewModel> value) => Rows = AssetsPageViewModel.ChunkRows(value, Columns);

    partial void OnColumnsChanged(int value) => Rows = AssetsPageViewModel.ChunkRows(Tiles, value);

    private void ShowCategory(ObjectCategoryNode? category)
    {
        if (category is null)
        {
            Show([], string.Empty);
            return;
        }

        var list = Under(_itemsNode, category) ? _items : ReferenceEquals(category, _otherNode) ? _other : _objects;
        var all = list.Where(o => category.Contains(o.Package)).Select(o => o.Entry).ToList();
        var max = AssetsPageViewModel.MaxListed;
        Show(all.Take(max), all.Count > max
            ? Loc.F("Assets.ShowingObjects", max, all.Count, category.Title)
            : Loc.F(all.Count == 1 ? "Assets.Objects.One" : "Assets.Objects.Many", all.Count, category.Title));

        static bool Under(ObjectCategoryNode? root, ObjectCategoryNode node) =>
            root is not null && (ReferenceEquals(root, node) || root.Children.Any(c => Under(c, node)));
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

            var lists = new[] { _objects, _items, _other };
            var (matches, total) = await Task.Run(() => AssetsPageViewModel.Filter(lists.SelectMany(l => l).Select(o => o.Entry).ToList(), text, AssetsPageViewModel.MaxListed), cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            Show(matches, total == 0
                ? Loc.F("Assets.NoMatch", text.Trim())
                : total > matches.Count
                    ? Loc.F("Assets.ShowingMatches", matches.Count, total)
                    : Loc.F(total == 1 ? "Assets.Match.One" : "Assets.Match.Many", total));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(IEnumerable<PackageEntry> entries, string text)
    {
        var added = _added();
        Tiles = entries.Select(e => new AssetItemViewModel(e, ThumbnailAsync) { IsAdded = added.Contains(e.PackagePath) }).ToList();
        ResultText = text;
    }

    /// <summary>Makes it a craftable (or opens the craftable it already is).</summary>
    [RelayCommand]
    private Task AddAsync(AssetItemViewModel? tile) => tile is null ? Task.CompletedTask : _pick(tile.Entry.PackagePath);

    /// <summary>A tile's 3D picture from the Assets page's thumbnail cache.</summary>
    private async Task<Bitmap?> ThumbnailAsync(AssetItemViewModel item, CancellationToken cancellationToken)
    {
        if (_catalog is not { } catalog)
        {
            return null;
        }

        var png = await _services.Thumbnails.GetAssetAsync(catalog, item.Entry, cancellationToken).ConfigureAwait(true);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(true);
    }
}
