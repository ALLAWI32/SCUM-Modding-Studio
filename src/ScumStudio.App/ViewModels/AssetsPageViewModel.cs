using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Asset browser: folder tree of the workspace's <see cref="PackageIndex"/>, search-as-you-type list, details pane
/// (class, exports, size) and PNG/glTF export through ScumStudio.Assets.
/// </summary>
public sealed partial class AssetsPageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    /// <summary>Maximum rows shown in the list.</summary>
    public const int MaxListed = 2000;

    /// <summary>Catalogs up to this many packages get classes resolved while indexing (headers read in parallel).</summary>
    public const int ResolveClassesLimit = 30_000;

    /// <summary>Largest edge of the preview image / mesh textures.</summary>
    public const int PreviewTextureSize = 1024;

    /// <summary>Width a tile takes in the grid (120 px picture plus padding, border and gap).</summary>
    public const int TileStride = 142;

    private readonly AppServices _services;
    private readonly Action _openSetup;
    private AssetCatalog? _catalog;
    private CancellationTokenSource? _searchCts;
    private Task _loadTask = Task.CompletedTask;
    private Task _detailsTask = Task.CompletedTask;
    private bool _suppressFolderRefresh;
    private readonly Action<string>? _placeMesh;

    /// <summary>True when the selected asset is a static mesh and a Map page can place it.</summary>
    public bool CanPlaceInMap => _placeMesh is not null && string.Equals(SelectedItem?.Entry.ClassName, "StaticMesh", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates the page and loads the workspace catalog when one is open.</summary>
    public AssetsPageViewModel(AppServices services, Action? openSetup = null, Action<string>? placeMesh = null)
        : base("assets", "Assets", "Browse cooked packages, inspect exports and export textures and meshes")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _placeMesh = placeMesh;
        _isGridView = services.UiState.Current.AssetsGridView;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadIndexAsync(catalog);
        }
    }

    /// <summary>Top-level folders.</summary>
    public ObservableCollection<AssetFolderNode> Roots { get; } = [];

    /// <summary>The package index in use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalog), nameof(ShowEmptyState))]
    private PackageIndex? _index;

    /// <summary>True while the index is built.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isIndexing;

    /// <summary>Selected folder.</summary>
    [ObservableProperty]
    private AssetFolderNode? _selectedFolder;

    /// <summary>Search text (tokens must all match the package path or class; <c>class:Name</c> filters by class).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Listed packages.</summary>
    [ObservableProperty]
    private IReadOnlyList<AssetItemViewModel> _items = [];

    /// <summary>Selected package.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AssetItemViewModel? _selectedItem;

    /// <summary>Details of <see cref="SelectedItem"/>.</summary>
    [ObservableProperty]
    private AssetDetailsViewModel? _details;

    /// <summary>"Showing 120 of 3,400 packages in /Game/...".</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>Most common classes (click to filter).</summary>
    [ObservableProperty]
    private IReadOnlyList<CountChip> _classChips = [];

    /// <summary>Where the catalog comes from.</summary>
    [ObservableProperty]
    private string _sourceText = Loc.T("Assets.NoSource");

    /// <summary>Tiles (picture with the name under it) instead of rows; remembered in the UI state.</summary>
    [ObservableProperty]
    private bool _isGridView = true;

    /// <summary>Tiles per grid row (set by the view from its width).</summary>
    [ObservableProperty]
    private int _tileColumns = 6;

    /// <summary><see cref="Items"/> cut into rows of <see cref="TileColumns"/> for the virtualized tile grid.</summary>
    [ObservableProperty]
    private IReadOnlyList<AssetTileRow> _tileRows = [];

    /// <summary>True when an index is loaded.</summary>
    public bool HasCatalog => Index is not null;

    /// <summary>True when the empty-state guidance is shown.</summary>
    public bool ShowEmptyState => Index is null && !IsIndexing;

    /// <summary>True when a package is selected.</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>Completes when the current index load finished (tests).</summary>
    public Task LoadCompletion => _loadTask;

    /// <summary>Completes when the selected package's details and preview are loaded (tests, screenshots).</summary>
    public Task DetailsCompletion => _detailsTask;

    /// <inheritdoc />
    public void ApplySearch(string? text) => SearchText = text ?? string.Empty;

    /// <summary>
    /// Opens a loose extracted folder (the folder containing <c>SCUM/Content</c>, the project folder or <c>Content</c>)
    /// as the workspace source and waits until it is indexed.
    /// </summary>
    public async Task<bool> OpenLooseFolderAsync(string folder)
    {
        var ok = await _services.Operations.RunAsync(Loc.F("Assets.Opening", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
            (p, ct) => _services.Workspace.OpenLooseAsync(folder, p, ct)).ConfigureAwait(true);
        await _loadTask.ConfigureAwait(true);
        return ok && Index is not null;
    }

    /// <summary>Builds the index of <paramref name="catalog"/> and shows it.</summary>
    public Task LoadCatalogAsync(AssetCatalog catalog)
    {
        _loadTask = LoadIndexAsync(catalog);
        return _loadTask;
    }

    /// <summary>Runs a search now (no debounce) and returns when the list is updated.</summary>
    public async Task SearchAsync(string? text, CancellationToken cancellationToken = default)
    {
        var index = Index;
        if (index is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (IsObjectsView)
            {
                ShowCategory(SelectedCategory);
            }
            else
            {
                ShowFolder(SelectedFolder);
            }

            return;
        }

        var scope = SearchScope(index);
        var (matches, total) = await Task.Run(() => Filter(scope, text, MaxListed), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        Items = matches.Select(e => new AssetItemViewModel(e, LoadThumbnailAsync)).ToList();
        SelectedItem = null;
        ResultText = total == 0
            ? Loc.F("Assets.NoMatch", text.Trim())
            : total > matches.Count
                ? Loc.F("Assets.ShowingMatches", matches.Count, total)
                : Loc.F(total == 1 ? "Assets.Match.One" : "Assets.Match.Many", total);
    }

    /// <summary>
    /// Filters <paramref name="entries"/>: every whitespace-separated token (one letter is enough) must occur in the
    /// package path or class (case-insensitive); <c>class:X</c> tokens must match the class exactly (C++ prefix
    /// optional). Packages whose name starts with the first word come first, then names containing it, then matches
    /// in the folder or class; the first <paramref name="max"/> of that order are returned.
    /// </summary>
    public static (IReadOnlyList<PackageEntry> Matches, int Total) Filter(IReadOnlyList<PackageEntry> entries, string text, int max)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var classTokens = tokens.Where(t => t.StartsWith("class:", StringComparison.OrdinalIgnoreCase)).Select(t => t[6..]).Where(t => t.Length > 0).ToArray();
        var words = tokens.Where(t => !t.StartsWith("class:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var ranked = new List<(int Rank, PackageEntry Entry)>();
        var total = 0;
        foreach (var e in entries)
        {
            if (classTokens.Length > 0 && (e.ClassName is null || !classTokens.All(c => PackageIndex.ClassNameMatches(e.ClassName, c))))
            {
                continue;
            }

            var ok = true;
            foreach (var w in words)
            {
                if (!e.PackagePath.Contains(w, StringComparison.OrdinalIgnoreCase) &&
                    !(e.ClassName?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                continue;
            }

            total++;
            var rank = words.Length == 0 || e.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0
                : e.Name.Contains(words[0], StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            ranked.Add((rank, e));
        }

        // OrderBy is stable, so entries of one rank keep the index order.
        return (ranked.OrderBy(r => r.Rank).Take(max).Select(r => r.Entry).ToList(), total);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync(value);

    partial void OnSelectedFolderChanged(AssetFolderNode? value)
    {
        if (!_suppressFolderRefresh && !IsObjectsView && string.IsNullOrWhiteSpace(SearchText))
        {
            ShowFolder(value);
        }
    }

    partial void OnSelectedItemChanged(AssetItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanPlaceInMap));
        _detailsTask = LoadDetailsAsync(value);
    }

    partial void OnSelectedItemChanged(AssetItemViewModel? oldValue, AssetItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    partial void OnItemsChanged(IReadOnlyList<AssetItemViewModel> value) => TileRows = ChunkRows(value, TileColumns);

    partial void OnTileColumnsChanged(int value) => TileRows = ChunkRows(Items, value);

    partial void OnIsGridViewChanged(bool value) => _services.UiState.Update(u => u with { AssetsGridView = value });

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadIndexAsync(catalog);
        }
        else
        {
            Clear();
            SourceText = Loc.T("Assets.NoSource");
        }
    }

    private async Task LoadIndexAsync(AssetCatalog catalog)
    {
        _catalog = catalog;
        Clear();
        IsIndexing = true;
        SourceText = catalog.DisplayName;
        try
        {
            var (ok, index) = await _services.Operations.RunAsync(Loc.T("Assets.IndexingPackages"), (sink, ct) =>
            {
                var total = catalog.PackageFiles.Count;
                var resolve = catalog.SourceKind == AssetSourceKind.Loose || total <= ResolveClassesLimit;
                sink.Report(Loc.T("Assets.IndexingPackages"), 0, total);
                var progress = new InlineProgress(n => sink.Report(Loc.T("Assets.IndexingPackages"), n, total));
                return Task.FromResult(catalog.BuildIndex(resolve, progress, ct));
            }).ConfigureAwait(true);

            if (!ok || index is null || !ReferenceEquals(_catalog, catalog))
            {
                return;
            }

            Show(index);
            _services.Logger.LogInformation("Indexed {Count} packages from {Source}.", index.Count, catalog.DisplayName);
        }
        finally
        {
            if (ReferenceEquals(_catalog, catalog))
            {
                IsIndexing = false;
            }
        }
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        if (Index is { } index)
        {
            Subtitle = PackagesSubtitle(index);
        }
        else if (_services.Workspace.Catalog is null)
        {
            SourceText = Loc.T("Assets.NoSource");
        }
    }

    private static string PackagesSubtitle(PackageIndex index) =>
        Loc.F("Assets.Subtitle", index.Count) + (index.HasClasses ? string.Empty : Loc.T("Assets.Subtitle.Lazy"));

    private void Show(PackageIndex index)
    {
        Index = index;
        Roots.Clear();
        foreach (var folder in index.Root.Folders)
        {
            Roots.Add(new AssetFolderNode(folder));
        }

        ClassChips = index.HasClasses
            ? index.ClassHistogram().Take(8).Select(c => new CountChip(c.ClassName, c.Count)).ToList()
            : [];
        Subtitle = PackagesSubtitle(index);

        var first = Roots.FirstOrDefault(r => string.Equals(r.Name, "Game", StringComparison.OrdinalIgnoreCase)) ?? Roots.FirstOrDefault();
        if (first is not null)
        {
            first.IsExpanded = true;
        }

        _suppressFolderRefresh = true;
        SelectedFolder = first;
        BuildObjects(index);
        // Objects by category for the game's paks; an extracted or mod folder is not in the catalogue: its files.
        IsObjectsView = _catalog?.SourceKind == AssetSourceKind.Paks && _objects.Count > 0;
        _suppressFolderRefresh = false;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            if (IsObjectsView)
            {
                ShowCategory(SelectedCategory);
            }
            else
            {
                ShowFolder(first);
            }
        }
        else
        {
            _ = DebouncedSearchAsync(SearchText, immediate: true);
        }
    }

    private void ShowFolder(AssetFolderNode? folder)
    {
        if (Index is null)
        {
            return;
        }

        var root = folder?.Folder ?? Index.Root;
        var list = new List<PackageEntry>(Math.Min(MaxListed, 512));
        Collect(root, list);
        var total = folder?.Count ?? Index.Count;
        Items = list.Select(e => new AssetItemViewModel(e, LoadThumbnailAsync)).ToList();
        var where = folder is null ? Loc.T("Assets.AllFolders") : folder.Path;
        ResultText = total > list.Count
            ? Loc.F("Assets.ShowingPackages", list.Count, total, where)
            : Loc.F(total == 1 ? "Assets.Packages.One" : "Assets.Packages.Many", total, where);

        static void Collect(PackageFolder f, List<PackageEntry> into)
        {
            foreach (var p in f.Packages)
            {
                if (into.Count >= MaxListed)
                {
                    return;
                }

                into.Add(p);
            }

            foreach (var child in f.Folders)
            {
                if (into.Count >= MaxListed)
                {
                    return;
                }

                Collect(child, into);
            }
        }
    }

    private async Task DebouncedSearchAsync(string text, bool immediate = false)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (!immediate)
            {
                await Task.Delay(150, cts.Token).ConfigureAwait(true);
            }

            await SearchAsync(text, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Logger.LogWarning("Search failed: {Message}", ex.Message);
        }
    }

    private async Task LoadDetailsAsync(AssetItemViewModel? item)
    {
        if (item is null || _catalog is not { } catalog)
        {
            Details = null;
            return;
        }

        var details = new AssetDetailsViewModel(item.Entry);
        Details = details;
        try
        {
            var entry = item.Entry;
            var (className, size, exports) = await Task.Run(() =>
            {
                var infos = catalog.GetExports(entry.PackagePath);
                var cls = entry.ClassName ?? catalog.GetMainClassName(entry.PackagePath) ?? (entry.IsMap ? "World" : "Unknown");
                var stem = entry.FilePath[..^Path.GetExtension(entry.FilePath).Length];
                long bytes = 0;
                foreach (var ext in new[] { Path.GetExtension(entry.FilePath), ".uexp", ".ubulk", ".uptnl" })
                {
                    if (catalog.Provider.Files.TryGetValue(stem + ext, out var file))
                    {
                        bytes += file.Size;
                    }
                }

                return (cls, bytes, infos);
            }).ConfigureAwait(true);
            details.Complete(className, size, exports);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            details.Error = ex.Message;
            details.IsLoading = false;
            _services.Logger.LogWarning("Could not read {Package}: {Message}", item.Entry.PackagePath, ex.Message);
            return;
        }

        if (ReferenceEquals(Details, details))
        {
            await LoadPreviewAsync(details, catalog).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Prepares what the preview area shows for <paramref name="details"/> on a worker: a mesh model with its base-colour
    /// textures, a texture's image, or a material's base-colour texture plus its parameters.
    /// </summary>
    private async Task LoadPreviewAsync(AssetDetailsViewModel details, AssetCatalog catalog)
    {
        var entry = details.Entry;
        var className = details.ClassName;
        details.IsPreviewLoading = true;
        try
        {
            if (AssetExportService.IsMeshClass(className) || AssetExportService.IsBlueprintClass(className))
            {
                var model = await Task.Run(() =>
                {
                    var loader = new MeshPreviewLoader(catalog, _services.Logger) { TextureSize = PreviewTextureSize };
                    return AssetExportService.IsBlueprintClass(className) ? loader.LoadBlueprint(entry.PackagePath) : loader.LoadMesh(entry.ObjectPath);
                }).ConfigureAwait(true);
                if (ReferenceEquals(Details, details))
                {
                    details.Preview = model;
                }
            }
            else if (AssetExportService.IsTextureClass(className))
            {
                var image = await Task.Run(() => catalog.LoadFirstExport<UTexture2D>(entry.PackagePath) is { } texture ? TextureDecoder.Decode(texture, maxSize: PreviewTextureSize) : null).ConfigureAwait(true);
                if (ReferenceEquals(Details, details))
                {
                    details.Image = image;
                }
            }
            else if (AssetExportService.IsMaterialClass(className))
            {
                var (image, rows) = await Task.Run(() =>
                {
                    var info = new MaterialInspector(catalog).Inspect(entry.ObjectPath);
                    var baseColor = info.BaseColorTexture is { } path ? TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(path), maxSize: PreviewTextureSize) : null;
                    return (baseColor, MaterialRows(info));
                }).ConfigureAwait(true);
                if (ReferenceEquals(Details, details))
                {
                    details.Image = image;
                    details.MaterialRows = rows;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            details.PreviewError = ex.Message;
            _services.Logger.LogWarning("No preview for {Package}: {Message}", entry.PackagePath, ex.Message);
        }
        finally
        {
            details.IsPreviewLoading = false;
        }
    }

    /// <summary>Parent chain, texture, colour and scalar parameters of a material as rows.</summary>
    public static IReadOnlyList<PropertyRow> MaterialRows(MaterialInfo info)
    {
        var rows = new List<PropertyRow>();
        if (info.ParentChain.Count > 0)
        {
            rows.Add(new PropertyRow(Loc.T("Assets.Row.Parent"), string.Join(" › ", info.ParentChain.Select(Leaf))));
        }

        rows.AddRange(info.Textures.Select(t => new PropertyRow(t.Name, Leaf(t.TexturePath))));
        rows.AddRange(info.Vectors.Select(v => new PropertyRow(v.Name, string.Create(CultureInfo.InvariantCulture, $"({v.Value.X:0.###}, {v.Value.Y:0.###}, {v.Value.Z:0.###}, {v.Value.W:0.###})"))));
        rows.AddRange(info.Scalars.Select(s => new PropertyRow(s.Name, s.Value.ToString("0.###", CultureInfo.InvariantCulture))));
        return rows;

        static string Leaf(string path) => path.Length == 0 ? "-" : path[(path.LastIndexOfAny(['/', '.']) + 1)..];
    }

    /// <summary>Makes a tile's picture: resolves the class when the index has none (large pak sets), then asks the thumbnail cache.</summary>
    private async Task<Bitmap?> LoadThumbnailAsync(AssetItemViewModel item, CancellationToken cancellationToken)
    {
        if (_catalog is not { } catalog)
        {
            return null;
        }

        if (item.Entry.ClassName is null)
        {
            var packagePath = item.Entry.PackagePath;
            item.ResolveClass(await Task.Run(() => catalog.GetMainClassName(packagePath), cancellationToken).ConfigureAwait(true));
        }

        var png = await _services.Thumbnails.GetAssetAsync(catalog, item.Entry, cancellationToken).ConfigureAwait(true);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Cuts <paramref name="items"/> into rows of <paramref name="columns"/> tiles.</summary>
    public static IReadOnlyList<AssetTileRow> ChunkRows(IReadOnlyList<AssetItemViewModel> items, int columns)
    {
        columns = Math.Max(1, columns);
        var rows = new List<AssetTileRow>((items.Count + columns - 1) / columns);
        for (var start = 0; start < items.Count; start += columns)
        {
            var tiles = new AssetItemViewModel[Math.Min(columns, items.Count - start)];
            for (var i = 0; i < tiles.Length; i++)
            {
                tiles[i] = items[start + i];
            }

            rows.Add(new AssetTileRow(tiles));
        }

        return rows;
    }

    /// <summary>Tiles that fit in a grid <paramref name="width"/> pixels wide (at least one).</summary>
    public static int ColumnsFor(double width) => Math.Max(1, (int)((width - 16) / TileStride));

    /// <summary>Selects a tile.</summary>
    [RelayCommand]
    private void Select(AssetItemViewModel? item)
    {
        if (item is not null)
        {
            SelectedItem = item;
        }
    }

    private void Clear()
    {
        Index = null;
        Roots.Clear();
        Items = [];
        SelectedItem = null;
        Details = null;
        ClassChips = [];
        ResultText = string.Empty;
    }

    /// <summary>Exports the selected texture to <paramref name="path"/> as PNG; false on failure (toast shown).</summary>
    public async Task<bool> ExportPngToAsync(string path)
    {
        if (SelectedItem?.Entry is not { } entry || _catalog is not { } catalog)
        {
            return false;
        }

        var (ok, image) = await _services.Operations.RunAsync(Loc.F("Assets.ExportingPng", entry.Name),
            (_, ct) => AssetExportService.ExportTexturePngAsync(catalog, entry.PackagePath, path, ct)).ConfigureAwait(true);
        if (ok && image is not null)
        {
            _services.Notifications.Success(Loc.T("Assets.TextureExported"), Loc.F("Assets.TextureExportedDetail", image.Width, image.Height, image.PixelFormat, path));
        }

        return ok;
    }

    /// <summary>Exports the selected mesh to <paramref name="path"/> as glTF; false on failure (toast shown).</summary>
    public async Task<bool> ExportGltfToAsync(string path)
    {
        if (SelectedItem?.Entry is not { } entry || _catalog is not { } catalog)
        {
            return false;
        }

        var (ok, files) = await _services.Operations.RunAsync(Loc.F("Assets.ExportingGltf", entry.Name),
            (_, ct) => AssetExportService.ExportMeshGltfAsync(catalog, entry.PackagePath, path, ct)).ConfigureAwait(true);
        if (ok && files is not null)
        {
            _services.Notifications.Success(Loc.T("Assets.MeshExported"), string.Join(", ", files.Select(Path.GetFileName)));
        }

        return ok;
    }

    [RelayCommand]
    private async Task ExportPngAsync()
    {
        if (SelectedItem?.Entry is { } entry &&
            await _services.Dialogs.SaveFileAsync(Loc.T("Assets.SavePng"), entry.Name + ".png", "png", Loc.T("Assets.PngImage")).ConfigureAwait(true) is { } path)
        {
            await ExportPngToAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ExportGltfAsync()
    {
        if (SelectedItem?.Entry is { } entry &&
            await _services.Dialogs.SaveFileAsync(Loc.T("Assets.SaveGltf"), entry.Name + ".gltf", "gltf", "glTF 2.0").ConfigureAwait(true) is { } path)
        {
            await ExportGltfToAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Places the selected static mesh as a new actor in the level loaded on the Map page.</summary>
    [RelayCommand]
    private void PlaceInMap()
    {
        if (SelectedItem?.Entry is { } entry && _placeMesh is { } place && string.Equals(entry.ClassName, "StaticMesh", StringComparison.OrdinalIgnoreCase))
        {
            place(entry.ObjectPath);
        }
    }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (SelectedItem?.Entry is { } entry)
        {
            await _services.Dialogs.SetClipboardTextAsync(entry.ObjectPath).ConfigureAwait(true);
            _services.Notifications.Info(Loc.T("Assets.Copied"), entry.ObjectPath);
        }
    }

    [RelayCommand]
    private async Task OpenLooseFolderPickerAsync()
    {
        if (await _services.Dialogs.PickFolderAsync(Loc.T("Assets.PickLoose")).ConfigureAwait(true) is { } folder)
        {
            await OpenLooseFolderAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_services.Workspace.IsLoose && _services.Workspace.SourcePath is { } loose)
        {
            await OpenLooseFolderAsync(loose).ConfigureAwait(true);
        }
        else
        {
            await _services.Operations.RunAsync(Loc.T("Shell.Connecting"), (p, ct) => _services.Workspace.ConnectAsync(p, ct)).ConfigureAwait(true);
            await _loadTask.ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void FilterByClass(CountChip? chip)
    {
        if (chip is not null)
        {
            SearchText = "class:" + chip.Label;
        }
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
