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
    private Task? _indexing;
    private string _lastSaved = new CraftablesFile().ToJson();

    /// <summary>Creates the page.</summary>
    public CraftablesPageViewModel(AppServices services, Action? openSetup = null)
        : base("craftables", "Craftables", "Any object as a craftable players place, with its recipe: the Craftables mod")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _services.Projects.Changed += OnProjectChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
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

    /// <summary>What a craftable can become.</summary>
    public IReadOnlyList<CraftKind> Kinds { get; } = Enum.GetValues<CraftKind>();

    /// <summary>Materials of the recipe rules.</summary>
    public IReadOnlyList<CraftMaterial> Materials { get; } = Enum.GetValues<CraftMaterial>();

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
            var row = new CraftableRowViewModel(craftable with { Name = name }, OnRowChanged, PictureAsync);
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

    /// <summary>A craftable for <paramref name="path"/>: its mesh, a name from the asset, the guessed kind, material and size, the suggested recipe.</summary>
    public static Craftable? Describe(AssetCatalog catalog, string path)
    {
        if (CraftablesPlanner.MeshOf(catalog, path) is not { } mesh)
        {
            return null;
        }

        var info = catalog.LoadFirstExport<UStaticMesh>(mesh) is { } loaded ? MeshExtractor.DescribeStaticMesh(loaded) : null;
        var size = info is null ? 1f : MathF.Max(0.1f, MathF.Max(info.Bounds.Size.X, MathF.Max(info.Bounds.Size.Y, info.Bounds.Size.Z)) / 100f);
        var material = RecipeRules.GuessMaterial([path, mesh, .. info?.Materials.Select(m => m.MaterialPath + " " + m.SlotName) ?? []]);
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var name = string.Join(' ', leaf.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("SM" or "BP" or "BPC")));
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
        _services.Projects.Changed -= OnProjectChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
    }

    partial void OnAddQueryChanged(string value)
    {
        var query = value.Trim();
        if (query.Length < 2 || _services.Workspace.Catalog is not { } catalog)
        {
            AddResults = [];
            return;
        }

        if (catalog.Index is not { } index)
        {
            // The package list is built once (names only; the class follows from the name until the Assets page resolves it).
            _indexing ??= Task.Run(() => catalog.BuildIndex(resolveClasses: false)).ContinueWith(_ => _services.Dispatcher.Post(() => OnAddQueryChanged(AddQuery)), TaskScheduler.Default);
            AddResults = [];
            return;
        }

        static string? ClassOf(PackageEntry p) => p.ClassName
            ?? (p.Name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? "StaticMesh" : p.Name.StartsWith("BP", StringComparison.OrdinalIgnoreCase) ? "Blueprint" : null);
        AddResults = index.Entries
            .Where(p => !p.IsMap && p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && RecipeRules.IsAllowedSource(p.PackagePath, ClassOf(p)))
            .OrderBy(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(p => new CraftSourceRow(p.PackagePath, p.Name, ClassOf(p) ?? string.Empty))
            .ToList();
    }

    [RelayCommand]
    private Task AddSourceAsync(CraftSourceRow? row) => row is null ? Task.CompletedTask : AddAsync(row.PackagePath);

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
        Show(_services.Projects.Current is { } project ? CraftablesFile.Load(project.DirectoryPath) : new CraftablesFile());
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
    }

    private void OnProjectChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(Load);

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(HasCatalog));
        OnAddQueryChanged(AddQuery);
    });

    /// <summary>The 3D picture of a craftable's mesh (the Assets page's thumbnails).</summary>
    private async Task<Bitmap?> PictureAsync(string mesh, CancellationToken cancellationToken)
    {
        if (_services.Workspace.Catalog is not { } catalog || !catalog.TryGetPackageFile(mesh, out var file))
        {
            return null;
        }

        var png = await _services.Thumbnails.GetAssetAsync(catalog, new PackageEntry(file.Path, mesh, "StaticMesh"), cancellationToken).ConfigureAwait(false);
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
    private readonly Action<CraftableRowViewModel> _changed;
    private readonly Func<string, CancellationToken, Task<Bitmap?>> _picture;
    private readonly Craftable _source;
    private bool _quiet;

    /// <summary>Creates the row of <paramref name="craftable"/>; <paramref name="changed"/> runs after each edit.</summary>
    public CraftableRowViewModel(Craftable craftable, Action<CraftableRowViewModel> changed, Func<string, CancellationToken, Task<Bitmap?>> picture)
    {
        _source = craftable;
        _changed = changed;
        _picture = picture;
        _quiet = true;
        _name = craftable.Name;
        _kind = craftable.Kind;
        _material = craftable.Material;
        _station = craftable.Station ?? string.Empty;
        _needsPower = craftable.NeedsPower;
        _radius = (decimal)craftable.Power.RadiusMeters;
        _output = (decimal)craftable.Power.Output;
        _fuel = (decimal)craftable.Power.FuelPerMinute;
        _dayOnly = craftable.Power.DayOnly;
        SetIngredients(craftable.Ingredients);
        _quiet = false;
    }

    private string _name;
    private string _station;

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

    /// <summary>What it becomes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPower), nameof(IsBuilt))]
    private CraftKind _kind;

    /// <summary>Main material.</summary>
    [ObservableProperty]
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

    /// <summary>A power object (shows radius, output and fuel).</summary>
    public bool IsPower => Kind == CraftKind.Power;

    /// <summary>Placed as a base element (shows the station it needs).</summary>
    public bool IsBuilt => Kind != CraftKind.Power;

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
        Kind = Kind,
        Material = Material,
        Station = string.IsNullOrWhiteSpace(Station) ? null : Station,
        NeedsPower = NeedsPower,
        Power = new PowerSettings((float)Radius, (float)Output, (float)Fuel, DayOnly),
        Ingredients = Ingredients.Where(i => !string.IsNullOrWhiteSpace(i.Tag)).Select(i => new CraftIngredient(i.Tag.Trim(), (int)i.Amount, i.IsTool)).ToList(),
    };

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

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) => _picture(_source.Mesh, cancellationToken);

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Kind) or nameof(Material) or nameof(Station) or nameof(NeedsPower)
            or nameof(Radius) or nameof(Output) or nameof(Fuel) or nameof(DayOnly))
        {
            Changed();
        }
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
