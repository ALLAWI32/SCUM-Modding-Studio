using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Level.Import;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Craftables (owner, 2026-10-09: "pick any table, chair, wall, house, pole or solar panel and make it craftable, placed
/// like the game's furniture"): the project's <see cref="CraftablesFile"/> with 3D pictures, a recipe per entry suggested by
/// <see cref="RecipeRules"/> (editable), a station it needs and, for power objects, radius, output and fuel. Every change
/// is saved to <c>craftables.json</c> in the project (Undo steps back); Export mod builds the Craftables pak from it
/// (<see cref="Level.Export.CraftablesExporter"/>).
/// </summary>
public sealed partial class CraftablesPageViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openSetup;
    private readonly Stack<string> _undo = new();
    private bool _loading;
    private Task<IReadOnlySet<string>>? _items;
    private AssetCatalog? _itemsOf;
    private string _lastSaved = new CraftablesFile().ToJson();
    private CancellationTokenSource? _importCancel;
    private Task<IReadOnlyList<CraftCategory>>? _sections;
    private AssetCatalog? _sectionsOf;

    /// <summary>Creates the page.</summary>
    public CraftablesPageViewModel(AppServices services, Action? openSetup = null)
        : base("craftables", "Craftables", "Any object as a craftable players place, with its recipe: the Craftables mod")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _services.Projects.Changed += OnProjectChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        Gallery = new CraftGalleryViewModel(services, PickAsync, AddedSources);
        Items.CollectionChanged += (_, e) =>
        {
            if (!_loading)
            {
                Gallery.MarkAdded(); // Show marks them once for the whole list
            }

            if (e.NewItems is { } added)
            {
                _ = ApplySectionsAsync(added.OfType<CraftableRowViewModel>().ToList());
            }
        };
        Load();
    }

    /// <inheritdoc />
    public override string IconKey => "Icon.Cube3d";

    /// <summary>The project's craftables.</summary>
    public ObservableCollection<CraftableRowViewModel> Items { get; } = [];

    /// <summary>The craftable being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(SuggestRecipeCommand), nameof(AddIngredientCommand))]
    private CraftableRowViewModel? _selectedItem;

    /// <summary>Search text of the Add box.</summary>
    [ObservableProperty]
    private string _addQuery = string.Empty;

    /// <summary>Static meshes and Blueprints matching <see cref="AddQuery"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddResults))]
    private IReadOnlyList<CraftSourceRow> _addResults = [];

    /// <summary>A 3D file is being imported (shows Cancel).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportModelCommand))]
    private bool _isImporting;

    /// <summary>Undo has a step.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    /// <summary>The Add box has matches.</summary>
    public bool HasAddResults => AddResults.Count > 0;

    /// <summary>A craftable is selected.</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>A project is open (craftables are kept in it).</summary>
    public bool HasProject => _services.Projects.HasProject;

    /// <summary>The game's files are connected.</summary>
    public bool HasCatalog => _services.Workspace.Catalog is not null;

    /// <summary>Materials of the recipe rules.</summary>
    public IReadOnlyList<CraftMaterial> Materials { get; } = Enum.GetValues<CraftMaterial>();

    /// <summary><see cref="Materials"/> named in the current language (the material picker).</summary>
    public IReadOnlyList<CraftChoice> MaterialChoices { get; } = Enum.GetValues<CraftMaterial>().Select(m => CraftChoice.Of(m)).ToList();

    /// <summary>Ingredient tags offered by the editor (any other <c>CI_*</c> tag can be typed).</summary>
    public IReadOnlyList<string> Tags { get; } = RecipeRules.CommonTags;

    /// <summary>"None" and every station craftable of the project.</summary>
    public IReadOnlyList<string> Stations { get; private set; } = [string.Empty];

    /// <summary>The file as the page shows it (tests).</summary>
    public CraftablesFile File => new() { Items = Items.Select(i => i.ToModel()).ToList() };

    /// <summary>Adds the static mesh or Blueprint at <paramref name="packagePath"/> (the Assets page's "Make craftable").</summary>
    public async Task AddAsync(string packagePath)
    {
        if (_services.Workspace.Catalog is not { } catalog || !HasProject)
        {
            _services.Notifications.Warning(Title, Loc.T(HasProject ? "Craftables.ConnectFirst" : "Craftables.NoProject"));
            return;
        }

        var path = packagePath.Contains('.', StringComparison.Ordinal) ? packagePath[..packagePath.LastIndexOf('.')] : packagePath;
        try
        {
            var craftable = await Task.Run(() => Describe(catalog, path)).ConfigureAwait(true);
            if (craftable is null)
            {
                _services.Notifications.Warning(Title, Loc.F("Craftables.NoMesh", path));
                return;
            }

            var name = craftable.Name;
            for (var n = 2; Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            {
                name = $"{craftable.Name} {n}";
            }

            Remember();
            var token = CraftablesFile.NewTokenFor(name, Items.Select(i => i.Token));
            var row = new CraftableRowViewModel(craftable with { Name = name, Token = token }, OnRowChanged, PictureAsync);
            Items.Add(row);
            SelectedItem = row;
            Save();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            _services.Logger.LogWarning("Craftable from {Path} not added: {Message}", path, ex.Message);
            _services.Notifications.Warning(Title, ex.Message);
        }
    }

    /// <summary>
    /// A craftable for <paramref name="path"/>: its mesh, a name from the asset, the guessed kind, material and size, the
    /// suggested recipe. An item of the game (weapon, food, clothes …) becomes an Item craftable shown by its Blueprint. Its
    /// token is given when it is added, after its name is made unique (<see cref="CraftablesFile.NewTokenFor"/>).
    /// </summary>
    public static Craftable? Describe(AssetCatalog catalog, string path)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var name = string.Join(' ', leaf.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("SM" or "BP" or "BPC")));
        if (CraftablesPlanner.IsItem(catalog, path))
        {
            var guess = RecipeRules.GuessMaterial([path]);
            return new Craftable { Name = name.Length > 0 ? name : leaf, Source = path, Mesh = path, Kind = CraftKind.Item, Material = guess, Ingredients = RecipeRules.Suggest(guess, 1) };
        }

        if (CraftablesPlanner.MeshOf(catalog, path) is not { } mesh)
        {
            return null;
        }

        var info = catalog.LoadFirstExport<UStaticMesh>(mesh) is { } loaded ? MeshExtractor.DescribeStaticMesh(loaded) : null;
        var size = info is null ? 1f : MathF.Max(0.1f, MathF.Max(info.Bounds.Size.X, MathF.Max(info.Bounds.Size.Y, info.Bounds.Size.Z)) / 100f);
        var material = RecipeRules.GuessMaterial([path, mesh, .. info?.Materials.Select(m => m.MaterialPath + " " + m.SlotName) ?? []]);
        return new Craftable
        {
            Name = name.Length > 0 ? name : leaf,
            Source = path,
            Mesh = mesh,
            Kind = RecipeRules.GuessKind(path),
            Material = material,
            SizeMeters = MathF.Round(size, 2),
            Ingredients = RecipeRules.Suggest(material, size),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _importCancel?.Cancel(); // the engine stops with the page
        _services.Projects.Changed -= OnProjectChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var row in Items)
        {
            row.OnLanguageChanged();
            row.RefreshSections();
        }
    }

    /// <summary>
    /// Gives <paramref name="rows"/> the sections of the game's crafting menu (read once per catalog on a worker) and an
    /// item's template recipe, for their "Shows in" lists.
    /// </summary>
    private async Task ApplySectionsAsync(IReadOnlyList<CraftableRowViewModel> rows)
    {
        if (_services.Workspace.Catalog is not { } catalog || rows.Count == 0)
        {
            return;
        }

        if (_sections is null || !ReferenceEquals(_sectionsOf, catalog))
        {
            _sectionsOf = catalog;
            _sections = Task.Run(() => CraftCategories.Read(catalog));
        }

        try
        {
            var sections = await _sections.ConfigureAwait(true);
            var templates = await Task.Run(() => rows.Select(r => r.IsItem ? CraftablesPlanner.ItemTemplate(catalog, CraftablesPlanner.GamePath(PackageMap.Normalize(r.Source))) : null).ToList()).ConfigureAwait(true);
            if (!ReferenceEquals(_services.Workspace.Catalog, catalog))
            {
                return; // the new catalog's sections follow (OnCatalogChanged)
            }

            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].SetSections(sections, templates[i]);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            _services.Logger.LogWarning("Crafting sections not read: {Message}", ex.Message);
        }
    }

    partial void OnAddQueryChanged(string value)
    {
        var query = value.Trim();
        if (query.Length < 2 || _services.Workspace.Catalog is not { } catalog)
        {
            AddResults = [];
            return;
        }

        if (!ReferenceEquals(_itemsOf, catalog))
        {
            // The package list and the game's items are found once per catalog on a worker (names only; the class follows
            // from the name until the Assets page resolves it), not on every key press.
            _itemsOf = catalog;
            _items = Task.Run(() => CraftablesPlanner.Items(catalog));
            _items.ContinueWith(_ => _services.Dispatcher.Post(() => OnAddQueryChanged(AddQuery)), TaskScheduler.Default);
        }

        if (_items is not { IsCompletedSuccessfully: true } found || catalog.Index is not { } index)
        {
            AddResults = [];
            return;
        }

        // Items of the game (weapons, food, clothes …) too (CraftablesPlanner.IsItem).
        var items = found.Result;
        string? ClassOf(PackageEntry p) => items.Contains(p.PackagePath) ? "Item" : p.ClassName
            ?? (p.Name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? "StaticMesh" : p.Name.StartsWith("BP", StringComparison.OrdinalIgnoreCase) ? "Blueprint" : null);
        AddResults = index.Entries
            .Where(p => !p.IsMap && p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Entry: p, Class: ClassOf(p)))
            .Where(p => p.Class == "Item" || RecipeRules.IsAllowedSource(p.Entry.PackagePath, p.Class))
            .OrderBy(p => p.Entry.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(p => new CraftSourceRow(p.Entry.PackagePath, p.Entry.Name, p.Class ?? string.Empty))
            .ToList();
    }

    [RelayCommand]
    private Task AddSourceAsync(CraftSourceRow? row) => row is null ? Task.CompletedTask : AddAsync(row.PackagePath);

    /// <summary>
    /// Owner, 2026-10-09: "a list where I can add external files, for example 3D tables or a new weapon". Picks an FBX or OBJ
    /// file, imports and cooks it with Unreal Engine 4.27 (<see cref="UeCook"/>) and adds it as a craftable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportModel))]
    private async Task ImportModelAsync()
    {
        if (_services.Workspace.Catalog is not { } catalog || _services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Title, Loc.T(HasProject ? "Craftables.ConnectFirst" : "Craftables.NoProject"));
            return;
        }

        if (UeCook.FindEngine() is null)
        {
            _services.Notifications.Error(Title, Loc.F("Craftables.Import.NoEngine", UeCook.DefaultEngineDirectory, UeCook.EngineVariable));
            return;
        }

        var file = await _services.Dialogs.OpenFileAsync(Loc.T("Craftables.Import"), string.Join(';', UeCook.Extensions), Loc.T("Craftables.Import.Filter")).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        using var cancel = _importCancel = new CancellationTokenSource();
        IsImporting = true;
        try
        {
            _services.Keys.TryGet(out var key);
            var (ok, model) = await _services.Operations.RunAsync(Loc.F("Craftables.Importing", Path.GetFileName(file)),
                (sink, ct) => UeCook.ImportAsync(catalog, file, project.DirectoryPath, sink, aesKey: key, cancellationToken: ct), cancel.Token).ConfigureAwait(true);
            if (!ok || model is null)
            {
                return;
            }

            // The cooked files are in the project the import started in: another one opened meanwhile gets nothing, the
            // craftable is added to the file of the first.
            var other = string.Equals(_services.Projects.Current?.DirectoryPath, project.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : CraftablesFile.Load(project.DirectoryPath);
            var taken = (other?.Items.Select(i => i.Name) ?? Items.Select(i => i.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tokens = other?.Items.Select(i => i.Token) ?? Items.Select(i => i.Token);
            var leaf = Path.GetFileNameWithoutExtension(file);
            var material = RecipeRules.GuessMaterial([leaf]);
            var name = leaf;
            for (var n = 2; taken.Contains(name); n++)
            {
                name = $"{leaf} {n}";
            }

            var craftable = new Craftable
            {
                Name = name,
                Token = CraftablesFile.NewTokenFor(name, tokens),
                Source = file,
                Mesh = model.Mesh,
                Imported = model.Folder,
                Kind = RecipeRules.GuessKind(leaf),
                Material = material,
                SizeMeters = MathF.Round(MathF.Max(0.1f, model.SizeMeters), 2),
                Ingredients = RecipeRules.Suggest(material, model.SizeMeters),
            };
            if (other is not null)
            {
                try
                {
                    (other with { Items = [.. other.Items, craftable] }).Save(project.DirectoryPath);
                    _services.Notifications.Info(Title, Loc.F("Craftables.Import.OtherProject", name, project.Manifest.Name));
                }
                catch (IOException ex)
                {
                    _services.Notifications.Error(Title, ex.Message);
                }

                return;
            }

            Remember();
            var row = new CraftableRowViewModel(craftable, OnRowChanged, PictureAsync);
            Items.Add(row);
            SelectedItem = row;
            Save();
        }
        finally
        {
            _importCancel = null;
            IsImporting = false;
        }
    }

    private bool CanImportModel() => !IsImporting;

    [RelayCommand]
    private void CancelImport() => _importCancel?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is { } row)
        {
            Remember();
            var index = Items.IndexOf(row);
            Items.Remove(row);
            SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
            Save();
        }
    }

    /// <summary>The recipe from the rules again (material and size of the entry).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SuggestRecipe()
    {
        if (SelectedItem is { } row)
        {
            Remember();
            row.SetIngredients(RecipeRules.Suggest(row.Material, row.SizeMeters));
            Save();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddIngredient()
    {
        if (SelectedItem is { } row)
        {
            Remember();
            row.SetIngredients([.. row.ToModel().Ingredients, new CraftIngredient("CI_Plank", 1)]);
            Save();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var selected = SelectedItem?.Name;
        Show(CraftablesFile.Parse(_undo.Pop()));
        SelectedItem = Items.FirstOrDefault(i => i.Name == selected) ?? Items.FirstOrDefault();
        CanUndo = _undo.Count > 0;
        Save();
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    private void OnRowChanged(CraftableRowViewModel row)
    {
        if (!_loading)
        {
            Remember();
            if (row.TokenTakenBy is not null)
            {
                // Left out for a token another craftable keeps (a hand-edited file): it never shipped with it, so an edit
                // gives it one of its own.
                row.Token = CraftablesFile.NewTokenFor(row.Name, Items.Where(i => i != row).Select(i => i.Token));
            }

            Save();
        }
    }

    /// <summary>Keeps the current list for Undo (before a change).</summary>
    private void Remember()
    {
        _undo.Push(_lastSaved);
        CanUndo = true;
    }

    private void Save()
    {
        _lastSaved = File.ToJson();
        if (_services.Projects.Current is { } project)
        {
            try
            {
                File.Save(project.DirectoryPath);
            }
            catch (IOException ex)
            {
                _services.Notifications.Error(Title, ex.Message);
            }
        }

        RefreshStations();
        RefreshTokens();
    }

    /// <summary>Marks the rows the export would leave out for a token another craftable keeps (<see cref="CraftablesPlanner.TokenOwners"/>).</summary>
    private void RefreshTokens()
    {
        var owners = CraftablesPlanner.TokenOwners(Items.Select(i => i.ToModel()).ToList());
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].TokenTakenBy = owners[i];
        }
    }

    /// <summary>A new station list only when the names changed (a new list resets the station boxes' selection).</summary>
    private void RefreshStations()
    {
        IReadOnlyList<string> next = [string.Empty, .. Items.Where(i => i.Kind == CraftKind.Station).Select(i => i.Name)];
        if (!next.SequenceEqual(Stations))
        {
            Stations = next;
            OnPropertyChanged(nameof(Stations));
        }
    }

    private void Load()
    {
        _undo.Clear();
        CanUndo = false;
        var file = _services.Projects.Current is { } project ? CraftablesFile.Load(project.DirectoryPath) : new CraftablesFile();
        Show(file);
        if (file.TokensAdded)
        {
            Save(); // the tokens a file from before 3.2 gets on load are kept from now on, whatever the names become
        }

        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasCatalog));
    }

    private void Show(CraftablesFile file)
    {
        _loading = true;
        Items.Clear();
        foreach (var craftable in file.Items)
        {
            Items.Add(new CraftableRowViewModel(craftable, OnRowChanged, PictureAsync));
        }

        _loading = false;
        _lastSaved = File.ToJson();
        RefreshStations();
        RefreshTokens();
        Gallery.MarkAdded();
    }

    private void OnProjectChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(Load);

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(HasCatalog));
        OnAddQueryChanged(AddQuery);
        Gallery.Load(_services.Workspace.Catalog);
        _ = ApplySectionsAsync(Items.ToList());
    });

    /// <summary>The 3D picture of a craftable's mesh (the Assets page's thumbnails); an imported model's from its own cooked files.</summary>
    private async Task<Bitmap?> PictureAsync(Craftable craftable, CancellationToken cancellationToken)
    {
        var folder = craftable.Imported is { } imported && _services.Projects.Current is { } project ? Path.Combine(project.DirectoryPath, imported) : null;
        using var own = folder is not null && Directory.Exists(folder) ? AssetCatalog.OpenLoose(folder) : null;
        var mesh = craftable.Mesh;
        if ((own ?? _services.Workspace.Catalog) is not { } catalog || !catalog.TryGetPackageFile(mesh, out var file))
        {
            return null;
        }

        var png = CraftablesPlanner.IsItem(catalog, mesh)
            ? await _services.Thumbnails.GetBlueprintAsync(catalog, mesh, cancellationToken).ConfigureAwait(false)
            : await _services.Thumbnails.GetAssetAsync(catalog, new PackageEntry(file.Path, mesh, "StaticMesh"), cancellationToken).ConfigureAwait(false);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A static mesh or Blueprint the Add box offers.</summary>
/// <param name="PackagePath">Its package.</param>
/// <param name="Name">Its name.</param>
/// <param name="ClassName">Its class.</param>
public sealed record CraftSourceRow(string PackagePath, string Name, string ClassName)
{
    /// <summary>"StaticMesh · Indoor/Armory/Table": the same name lives in several folders.</summary>
    public string Detail => ClassName + " · " + string.Join('/', PackagePath.Split('/').SkipLast(1).TakeLast(3));
}

/// <summary>One craftable of the list: name, kind, material, recipe, station, power settings and its 3D picture.</summary>
public sealed partial class CraftableRowViewModel : ThumbnailItem
{
    private static readonly CraftKind[] ObjectKinds = [CraftKind.Furniture, CraftKind.Station, CraftKind.Power];
    private static readonly CraftKind[] ItemKinds = [CraftKind.Item];
    private readonly Action<CraftableRowViewModel> _changed;
    private readonly Func<Craftable, CancellationToken, Task<Bitmap?>> _picture;
    private readonly Craftable _source;
    private bool _quiet;
    private IReadOnlyList<CraftCategory> _categories = [];
    private string? _itemTemplate;

    /// <summary>Creates the row of <paramref name="craftable"/>; <paramref name="changed"/> runs after each edit.</summary>
    public CraftableRowViewModel(Craftable craftable, Action<CraftableRowViewModel> changed, Func<Craftable, CancellationToken, Task<Bitmap?>> picture)
    {
        _source = craftable;
        _changed = changed;
        _picture = picture;
        _quiet = true;
        _name = craftable.Name;
        _token = craftable.Token ?? CraftablesFile.LegacyTokenOf(craftable.Name);
        _kind = craftable.Kind;
        _material = craftable.Material;
        _category = craftable.Category;
        KindChoices = Kinds.Select(k => CraftChoice.Of(k)).ToList();
        _station = craftable.Station ?? string.Empty;
        _needsPower = craftable.NeedsPower;
        _radius = (decimal)craftable.Power.RadiusMeters;
        _output = (decimal)craftable.Power.Output;
        _fuel = (decimal)craftable.Power.FuelPerMinute;
        _dayOnly = craftable.Power.DayOnly;
        SetIngredients(craftable.Ingredients);
        RefreshSections();
        _quiet = false;
    }

    private string _name;
    private string _station;
    private string? _category;

    /// <summary>Name in game. Empty or null writes are ignored (a text box being recycled sends them; a craftable keeps a name).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetProperty(ref _name, value);
            }
        }
    }

    /// <summary>Station needed nearby ("" = none). Null writes are ignored (a station box sends one while its list is renewed).</summary>
    public string Station
    {
        get => _station;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _station, value);
            }
        }
    }

    /// <summary>The token of its assets (<c>CR_</c>/<c>BP_</c> + it), fixed when it was first saved: a rename keeps it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssetName), nameof(TokenTaken))]
    private string _token;

    /// <summary>The craftable that keeps this one's token (the export leaves this one out), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenTaken))]
    private string? _tokenTakenBy;

    /// <summary>"Asset name: SS_Oak_Table", under the name box.</summary>
    public string AssetName => Loc.F("Craftables.AssetName", Token);

    /// <summary>The inline note when <see cref="TokenTakenBy"/> is set, or null.</summary>
    public string? TokenTaken => TokenTakenBy is { } owner ? Loc.F("Craftables.TokenTaken", owner, Token) : null;

    /// <summary>What it becomes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPower), nameof(IsBuilt), nameof(IsBuilding), nameof(KindChoice))]
    private CraftKind _kind;

    /// <summary>Main material.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaterialChoice))]
    private CraftMaterial _material;

    /// <summary>The station must have power (kept; the game's data cannot hold it).</summary>
    [ObservableProperty]
    private bool _needsPower;

    /// <summary>Power radius in metres.</summary>
    [ObservableProperty]
    private decimal _radius;

    /// <summary>Electricity given.</summary>
    [ObservableProperty]
    private decimal _output;

    /// <summary>Fuel per minute (0 = no fuel).</summary>
    [ObservableProperty]
    private decimal _fuel;

    /// <summary>Solar: only by day (kept; the game's data cannot hold it).</summary>
    [ObservableProperty]
    private bool _dayOnly;

    /// <summary>The recipe's ingredients.</summary>
    public ObservableCollection<IngredientRowViewModel> Ingredients { get; } = [];

    /// <summary>What it can become: an item of the game stays an item; any other object is furniture, a station or power.</summary>
    public IReadOnlyList<CraftKind> Kinds => IsItem ? ItemKinds : ObjectKinds;

    /// <summary>An item of the game (its new recipe makes the item; the game shows the item's own name and icon).</summary>
    public bool IsItem => _source.Kind == CraftKind.Item;

    /// <summary>A power object (shows radius, output and fuel).</summary>
    public bool IsPower => Kind == CraftKind.Power;

    /// <summary>Placed as a base element (shows the station it needs).</summary>
    public bool IsBuilt => Kind != CraftKind.Power;

    /// <summary>Made by a building recipe (placed in the world, built with resources), not crafted into the inventory.</summary>
    public bool IsBuilding => CraftCategories.IsPlaceable(Kind);

    /// <summary><see cref="Kinds"/> named in the current language (the kind picker).</summary>
    public IReadOnlyList<CraftChoice> KindChoices { get; }

    /// <summary><see cref="Kind"/> as a named choice (the picker and the list's chip).</summary>
    public CraftChoice KindChoice
    {
        get => CraftChoice.Of(Kind);
        set
        {
            if (value?.Value is CraftKind kind)
            {
                Kind = kind;
            }
        }
    }

    /// <summary><see cref="Material"/> as a named choice (the picker and the list's chip).</summary>
    public CraftChoice MaterialChoice
    {
        get => CraftChoice.Of(Material);
        set
        {
            if (value?.Value is CraftMaterial material)
            {
                Material = material;
            }
        }
    }

    /// <summary>
    /// The crafting menu section it shows in (a gameplay tag of the game's, <see cref="Sections"/>), or null for its
    /// default. Empty writes clear it.
    /// </summary>
    public string? Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, string.IsNullOrEmpty(value) ? null : value))
            {
                OnPropertyChanged(nameof(SelectedSection));
            }
        }
    }

    /// <summary>
    /// "Game default (…)" and the game's crafting menu sections of its recipe class (building or crafting), plus a chosen
    /// one the game files do not list (kept, the export reports it).
    /// </summary>
    public IReadOnlyList<CraftSectionChoice> Sections { get; private set; } = [];

    /// <summary>The entry of <see cref="Sections"/> for <see cref="Category"/>. Null writes are ignored (the box sends one while its list is renewed).</summary>
    public CraftSectionChoice? SelectedSection
    {
        get => Sections.FirstOrDefault(s => s.Tag == Category) ?? Sections.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                Category = value.Tag;
            }
        }
    }

    /// <summary>Largest extent in metres.</summary>
    public float SizeMeters => _source.SizeMeters;

    /// <summary>"SM_Table_01 · 1.6 m".</summary>
    public string Caption => $"{_source.Mesh[(_source.Mesh.LastIndexOf('/') + 1)..]} · {_source.SizeMeters:0.##} m";

    /// <summary>The asset it was made from.</summary>
    public string Source => _source.Source;

    /// <summary>The row as a <see cref="Craftable"/>.</summary>
    public Craftable ToModel() => _source with
    {
        Name = Name.Trim(),
        Token = Token,
        Kind = Kind,
        Material = Material,
        Category = Category,
        Station = string.IsNullOrWhiteSpace(Station) ? null : Station,
        NeedsPower = NeedsPower,
        Power = new PowerSettings((float)Radius, (float)Output, (float)Fuel, DayOnly),
        Ingredients = Ingredients.Where(i => !string.IsNullOrWhiteSpace(i.Tag)).Select(i => new CraftIngredient(i.Tag.Trim(), (int)i.Amount, i.IsTool)).ToList(),
    };

    /// <summary>Re-reads the texts composed here in the new UI language.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AssetName));
        OnPropertyChanged(nameof(TokenTaken));
    }

    /// <summary>Replaces the ingredient rows.</summary>
    public void SetIngredients(IEnumerable<CraftIngredient> ingredients)
    {
        var quiet = _quiet;
        _quiet = true;
        Ingredients.Clear();
        foreach (var ingredient in ingredients.ToList())
        {
            Ingredients.Add(new IngredientRowViewModel(ingredient, Changed, Remove));
        }

        _quiet = quiet;
    }

    /// <summary>The game's crafting menu sections (<see cref="CraftCategories.Read"/>) and, for an item, its template recipe.</summary>
    internal void SetSections(IReadOnlyList<CraftCategory> categories, string? itemTemplate)
    {
        _categories = categories;
        _itemTemplate = itemTemplate;
        RefreshSections();
    }

    /// <summary>Builds <see cref="Sections"/> again (kind, sections or language changed).</summary>
    internal void RefreshSections()
    {
        var template = Kind switch
        {
            CraftKind.Station => CraftablesPlanner.StationRecipe,
            CraftKind.Power => CraftablesPlanner.PowerRecipe,
            CraftKind.Item => _itemTemplate,
            _ => CraftablesPlanner.FurnitureRecipe,
        };
        var fallback = template is null ? null : CraftCategories.DefaultFor(ToModel(), _categories, template);
        var placeable = IsBuilding;
        var listed = _categories.Where(c => c.Placeable == placeable).Select(c => new CraftSectionChoice(c.Tag, CraftSectionChoice.LabelOf(c.Tag)))
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (Category is { } chosen && listed.All(c => c.Tag != chosen))
        {
            listed.Insert(0, new CraftSectionChoice(chosen, CraftSectionChoice.LabelOf(chosen)));
        }

        var game = fallback is not null ? CraftSectionChoice.LabelOf(fallback.Tag) : template is null ? "…" : PackageMap.Leaf(template);
        Sections = [new CraftSectionChoice(null, Loc.F("Craftables.ShowsIn.Default", game)), .. listed];
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(SelectedSection));
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) => _picture(_source, cancellationToken);

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Kind) or nameof(Material) or nameof(Category) or nameof(Station) or nameof(NeedsPower)
            or nameof(Radius) or nameof(Output) or nameof(Fuel) or nameof(DayOnly))
        {
            Changed();
        }
    }

    // A building section is no section of a crafting recipe (and back): the new kind shows in its default. Its default
    // can follow the kind too, so the list is built again.
    partial void OnKindChanged(CraftKind oldValue, CraftKind newValue)
    {
        if (CraftCategories.IsPlaceable(oldValue) != CraftCategories.IsPlaceable(newValue))
        {
            _category = null;
        }

        RefreshSections();
    }

    private void Remove(IngredientRowViewModel row)
    {
        Ingredients.Remove(row);
        Changed();
    }

    private void Changed()
    {
        if (_quiet)
        {
            return;
        }

        _changed(this);
    }
}

/// <summary>One ingredient of a craftable's recipe.</summary>
public sealed partial class IngredientRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<IngredientRowViewModel> _remove;

    /// <summary>Creates the row.</summary>
    public IngredientRowViewModel(CraftIngredient ingredient, Action changed, Action<IngredientRowViewModel> remove)
    {
        _tag = ingredient.Tag;
        _amount = ingredient.Amount;
        _isTool = ingredient.IsTool;
        _changed = changed;
        _remove = remove;
    }

    private string _tag;

    /// <summary>Ingredient tag (<c>CI_*</c>). Empty or null writes are ignored (the tag box sends them when it is recycled; Remove deletes a row).</summary>
    public string Tag
    {
        get => _tag;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _tag, value))
            {
                _changed();
            }
        }
    }

    /// <summary>Amount without skill.</summary>
    [ObservableProperty]
    private decimal _amount;

    /// <summary>Used as a tool, not consumed.</summary>
    [ObservableProperty]
    private bool _isTool;

    partial void OnAmountChanged(decimal value) => _changed();

    partial void OnIsToolChanged(bool value) => _changed();

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>A kind or material of the Craftables page (picker entry and list chip), named in the current language.</summary>
public sealed class CraftChoice : ObservableObject
{
    private static readonly Dictionary<Enum, CraftChoice> All = Enum.GetValues<CraftKind>().Cast<Enum>()
        .Concat(Enum.GetValues<CraftMaterial>().Cast<Enum>()).ToDictionary(v => v, v => new CraftChoice(v));

    private CraftChoice(Enum value)
    {
        Value = value;
        Key = $"Craftables.{(value is CraftKind ? "Kind" : "Material")}.{value}";
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label)); // one instance per value, for the app's lifetime
    }

    /// <summary>The <see cref="CraftKind"/> or <see cref="CraftMaterial"/>.</summary>
    public Enum Value { get; }

    /// <summary>Its string key (<c>Craftables.Kind.Furniture</c>).</summary>
    public string Key { get; }

    /// <summary>Its name.</summary>
    public string Label => Loc.T(Key);

    /// <summary>The choice of <paramref name="value"/>.</summary>
    public static CraftChoice Of(Enum value) => All[value];

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>A section of the game's crafting menu in the "Shows in" picker.</summary>
/// <param name="Tag">Its gameplay tag; null for the craftable's default section.</param>
/// <param name="Label">Its name.</param>
public sealed record CraftSectionChoice(string? Tag, string Label)
{
    /// <summary>
    /// The name of a section in the current language, part by part (<see cref="CraftCategories.Segments"/>, keys
    /// <c>Craftables.Category.&lt;Part&gt;</c>); a part without a key is the game's tag in plain English, like an asset name.
    /// </summary>
    public static string LabelOf(string tag) =>
        string.Join(" › ", CraftCategories.Segments(tag).Select(s => Loc.Instance.Or("Craftables.Category." + s, CraftCategories.Humanize(s))));

    /// <inheritdoc />
    public override string ToString() => Label;
}
