using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Export;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Economy (owner: "like the SCUM economy websites"): every trader of the game and every trader placed in the project, each
/// with what it sells (the game's <c>Table_TradeableDesc</c> rows of its type, see <see cref="EconomyDefaults"/>) with the
/// trade menu's pictures: price, sale price, fame needed, on sale or not; items of any other trader can be added and items
/// removed. Edits are the server's <c>EconomyOverride.json</c> (schema as the game writes it, see
/// <see cref="EconomyOverride"/>), kept in the project by <see cref="ProjectEconomy"/> (which also gives a placed trader its
/// section and hides the game's traders the project deleted) and written next to the paks by Export (and by Export mod).
/// </summary>
public sealed partial class EconomyPageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    private const string AllCategories = "*";

    private readonly AppServices _services;
    private readonly ProjectEconomy _store;
    private readonly Action _openSetup;
    private IReadOnlyList<EconomyItemViewModel> _allItems = [];
    private IReadOnlyList<EconomyPickItem> _allPicks = [];
    private string? _shown;
    private Level.Projects.Project? _shownProject;
    private string? _wanted;
    private bool _bulk;
    private bool _keepRows;

    /// <summary>Creates the page.</summary>
    public EconomyPageViewModel(AppServices services, Action? openSetup = null)
        : base("economy", "Economy", "Traders, prices and stock: the server's EconomyOverride.json")
    {
        _services = services;
        _store = services.Economy;
        _openSetup = openSetup ?? (() => { });
        _store.Changed += OnStoreChanged;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        _services.Projects.Changed += OnProjectChanged;
        _store.Refresh();
        Load();
    }

    /// <inheritdoc />
    public override string IconKey => "Icon.Coins";

    /// <summary>The traders (the game's that are still on the island and the project's, by map cell).</summary>
    [ObservableProperty]
    private IReadOnlyList<EconomyTraderRow> _traders = [];

    /// <summary>The trader shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrader), nameof(TraderTitle), nameof(TraderCaption), nameof(TraderIconKey), nameof(TraderCell), nameof(TraderType), nameof(IsPlacedTrader))]
    private EconomyTraderRow? _selectedTrader;

    /// <summary>What the selected trader sells, filtered.</summary>
    [ObservableProperty]
    private IReadOnlyList<EconomyItemViewModel> _items = [];

    /// <summary>The selected trader's categories ("All" first).</summary>
    [ObservableProperty]
    private IReadOnlyList<EconomyCategory> _categories = [];

    /// <summary>The category filter.</summary>
    [ObservableProperty]
    private EconomyCategory? _selectedCategory;

    /// <summary>Item search text.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>Reading the game's tables.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>The Add items box is open.</summary>
    [ObservableProperty]
    private bool _isAddOpen;

    /// <summary>Search text of the Add items box.</summary>
    [ObservableProperty]
    private string _addFilterText = string.Empty;

    /// <summary>The tradeables the Add items box shows.</summary>
    [ObservableProperty]
    private IReadOnlyList<EconomyPickItem> _addItems = [];

    /// <summary>"Add 3 items".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCheckedCommand))]
    private int _addCheckedCount;

    /// <summary>The game's files are connected.</summary>
    public bool HasCatalog => _services.Workspace.Catalog is not null;

    /// <summary>A trader is selected.</summary>
    public bool HasTrader => SelectedTrader is not null;

    /// <summary>A project is open (edits are kept in it).</summary>
    public bool HasProject => _services.Projects.HasProject;

    /// <summary>Selected trader's name.</summary>
    public string TraderTitle => SelectedTrader?.Name ?? string.Empty;

    /// <summary>Selected trader's type ("Armory").</summary>
    public string TraderType => SelectedTrader?.TypeLabel ?? string.Empty;

    /// <summary>Selected trader's map cell.</summary>
    public string TraderCell => SelectedTrader?.Cell ?? string.Empty;

    /// <summary>Selected trader's glyph.</summary>
    public string TraderIconKey => SelectedTrader?.IconKey ?? "Icon.Coins";

    /// <summary>The selected trader was placed in this project.</summary>
    public bool IsPlacedTrader => SelectedTrader?.IsPlaced == true;

    /// <summary>"721 items · 3 changed · 2 added · 5 off sale".</summary>
    public string TraderCaption
    {
        get
        {
            if (SelectedTrader is null)
            {
                return string.Empty;
            }

            var text = Loc.F("Economy.StockCaption", _allItems.Count, _allItems.Count(i => i.IsChanged && !i.IsAdded), _allItems.Count(i => i.IsAdded));
            var off = _allItems.Count(i => i.IsOffSale);
            return off > 0 ? text + " · " + Loc.F("Economy.OffSaleCount", off) : text;
        }
    }

    /// <summary>The economy being edited (tests).</summary>
    public EconomyOverride Economy => _store.Economy;

    /// <summary>Completes when the game's tables are read (tests).</summary>
    public Task LoadCompletion { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void ApplySearch(string? text) => FilterText = text ?? string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
        _services.Projects.Changed -= OnProjectChanged;
    }

    /// <summary>Shows <paramref name="trader"/>'s section (the map's "Edit stock"); remembered until the list is there.</summary>
    public void OpenTrader(string trader)
    {
        _wanted = trader;
        if (Traders.FirstOrDefault(t => string.Equals(t.Name, trader, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            SelectedTrader = row;
            _wanted = null;
        }
    }

    partial void OnSelectedTraderChanged(EconomyTraderRow? value)
    {
        ShowTrader();
        if (IsAddOpen)
        {
            BuildPicks();
        }
    }

    partial void OnFilterTextChanged(string value) => RefreshItems();

    partial void OnSelectedCategoryChanged(EconomyCategory? value) => RefreshItems();

    partial void OnAddFilterTextChanged(string value) => RefreshPicks();

    partial void OnIsAddOpenChanged(bool value)
    {
        if (value)
        {
            BuildPicks();
        }
        else
        {
            _allPicks = [];
            AddItems = [];
            AddCheckedCount = 0;
        }
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    /// <summary>Opens or closes the Add items box.</summary>
    [RelayCommand]
    private void ToggleAdd() => IsAddOpen = !IsAddOpen;

    /// <summary>Every shown price (and sale price) × <paramref name="factor"/> ("1.1" = +10 %).</summary>
    [RelayCommand]
    public void ScalePrices(string factor)
    {
        if (!double.TryParse(factor, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || f <= 0)
        {
            return;
        }

        Bulk(items =>
        {
            foreach (var item in items.Where(i => !i.IsLocked))
            {
                item.PurchasePrice = (int)Math.Round(item.PurchasePrice * f);
                item.SellPrice = (int)Math.Round(item.SellPrice * f);
            }
        });
    }

    /// <summary>Takes the shown items off the shelf (or puts them back: "true").</summary>
    [RelayCommand]
    public void SetOnSale(string onSale)
    {
        var value = string.Equals(onSale, "true", StringComparison.OrdinalIgnoreCase);
        Bulk(items =>
        {
            foreach (var item in items.Where(i => !i.IsLocked))
            {
                item.CanBePurchased = value;
            }
        });
    }

    /// <summary>The selected trader back to the game's stock and values (a placed trader's section lists its stock again).</summary>
    [RelayCommand]
    private void ResetTrader()
    {
        if (SelectedTrader is not { } trader)
        {
            return;
        }

        _store.Economy.Clear(trader.Name);
        if (trader.IsPlaced && _store.Defaults is { } defaults)
        {
            _store.Economy.List(trader.Name, defaults.StockCodes(trader.Type));
        }

        Saved();
        ShowTrader();
    }

    /// <summary>
    /// Adds the tradeables <paramref name="codes"/> to the selected trader with the game's values (see
    /// <see cref="TradeableDefault.AddedEntry"/>); items it already sells and items the server does not let a file change are
    /// skipped. Returns how many were added.
    /// </summary>
    public int AddItemsToTrader(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (SelectedTrader is not { } trader || _store.Defaults is not { } defaults)
        {
            return 0;
        }

        var added = 0;
        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (defaults.Find(code) is { ImmuneToOverrides: false } tradeable && !tradeable.IsSoldBy(trader.Type) && _store.Economy.Find(trader.Name, code) is null)
            {
                _store.Economy.Set(trader.Name, tradeable.AddedEntry());
                added++;
            }
        }

        if (added > 0)
        {
            Saved();
            ShowTrader();
            if (IsAddOpen)
            {
                BuildPicks();
            }

            _services.Notifications.Success(Loc.T("Economy.ItemsAdded"), Loc.F("Economy.ItemsAddedDetail", added, trader.Name));
        }

        return added;
    }

    /// <summary>Adds the items ticked in the Add items box.</summary>
    [RelayCommand(CanExecute = nameof(CanAddChecked))]
    private void AddChecked() => AddItemsToTrader(_allPicks.Where(p => p.IsChecked).Select(p => p.Code).ToList());

    private bool CanAddChecked() => AddCheckedCount > 0;

    /// <summary>
    /// Takes <paramref name="item"/> from the selected trader: an added item leaves its section; one of its default stock goes
    /// off sale (<c>can-be-purchased</c> <c>false</c>: the server cannot drop a trader's own stock, only stop selling it).
    /// </summary>
    public void RemoveItem(EconomyItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (SelectedTrader is not { } trader || item.IsLocked)
        {
            return;
        }

        if (!item.IsAdded)
        {
            item.CanBePurchased = false;
            return;
        }

        _store.Economy.Remove(trader.Name, item.Code);
        _allItems = _allItems.Where(i => !ReferenceEquals(i, item)).ToList();
        Saved();
        RefreshItems();
    }

    /// <summary>
    /// Writes <c>EconomyOverride.json</c> into the export folder's <c>Server</c> and <c>Client</c> folders (where Export mod puts
    /// the paks); the server reads it from <c>SCUM\Saved\Config\WindowsServer</c>.
    /// </summary>
    [RelayCommand]
    public void Export()
    {
        var settings = _services.Settings.Load();
        var folder = string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? ProjectsPageViewModel.DefaultExportFolder() : settings.ClientModsOutputFolder;
        try
        {
            var economy = _store.ForExport();
            var server = economy.SaveTo(Path.Combine(folder, ProjectExporter.ServerFolderName));
            economy.SaveTo(Path.Combine(folder, ProjectExporter.ClientFolderName));
            LastExportPath = server;
            _services.Notifications.Success(Loc.T("Economy.Exported"), Loc.F("Economy.ExportedDetail", server));
            _services.Logger.LogInformation("Economy written to {Path} (copy it to the server's SCUM\\Saved\\Config\\WindowsServer).", server);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Economy.NotExported"), ex.Message);
        }
    }

    /// <summary>The server copy written by the last <see cref="Export"/> (tests).</summary>
    public string? LastExportPath { get; private set; }

    private void Bulk(Action<IReadOnlyList<EconomyItemViewModel>> change)
    {
        _bulk = true;
        try
        {
            change(Items);
        }
        finally
        {
            _bulk = false;
        }

        if (SelectedTrader is { } trader)
        {
            foreach (var item in Items)
            {
                Write(trader, item);
            }

            Saved();
        }
    }

    private void OnItemChanged(EconomyItemViewModel item)
    {
        if (_bulk || SelectedTrader is not { } trader)
        {
            return;
        }

        Write(trader, item);
        Saved();
    }

    /// <summary>A placed trader's section lists every item (back to the game's values = a <c>-1</c> entry); others keep changes only.</summary>
    private void Write(EconomyTraderRow trader, EconomyItemViewModel item) => _store.Economy.Set(trader.Name, item.ToOverride(), keep: trader.IsPlaced);

    /// <summary>Keeps the economy in the project and refreshes the counts.</summary>
    private void Saved()
    {
        _store.Save();
        if (SelectedTrader is { } trader)
        {
            SetChanges(trader);
            OnPropertyChanged(nameof(TraderCaption));
        }
    }

    private void SetChanges(EconomyTraderRow row)
    {
        var defaults = _store.Defaults;
        var changed = 0;
        var added = 0;
        foreach (var entry in _store.Economy.Entries(row.Name))
        {
            if (defaults is not null && defaults.Find(entry.Code)?.IsSoldBy(row.Type) != true)
            {
                added++;
            }
            else if (!entry.IsDefault)
            {
                changed++;
            }
        }

        row.SetChanges(changed, added);
    }

    private void Load()
    {
        if (_store.Defaults is not null)
        {
            BuildTraders();
            return;
        }

        if (!HasCatalog)
        {
            return;
        }

        IsLoading = true;
        LoadCompletion = _store.LoadDefaultsAsync();
    }

    /// <summary>The game's traders still on the island and the project's placed ones, sorted by map cell.</summary>
    private void BuildTraders()
    {
        if (_store.Defaults is not { } defaults)
        {
            return;
        }

        IsLoading = false;
        var selected = _wanted ?? SelectedTrader?.Name;
        var rows = defaults.Traders
            .Where(t => !_store.RemovedStock.Contains(t.Name))
            .Select(t => new EconomyTraderRow(t.Name, t.Type, false, TraderPosts.CellOf(t.Name)))
            .ToList();
        if (_services.Projects.Current is { } project)
        {
            foreach (var (actor, op) in TraderPosts.Placed(project.State))
            {
                if (op.Trader is { Name.Length: > 0 } trader && !rows.Any(r => string.Equals(r.Name, trader.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    rows.Add(new EconomyTraderRow(trader.Name, trader.Type, true, TraderPosts.CellOf(actor.Level) ?? TraderPosts.CellOf(trader.Name)));
                }
            }
        }

        rows = [.. rows.OrderBy(r => r.Cell, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.IsPlaced).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        foreach (var row in rows)
        {
            SetChanges(row);
        }

        var same = SelectedTrader is { } old && ReferenceEquals(_shownProject, _services.Projects.Current)
            && rows.FirstOrDefault(r => r.Name == old.Name) is { } kept && kept.IsPlaced == old.IsPlaced && kept.Type == old.Type;
        Traders = rows;
        var next = rows.FirstOrDefault(r => string.Equals(r.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        if (same && string.Equals(next?.Name, _shown, StringComparison.OrdinalIgnoreCase))
        {
            // The same trader of the same project: keep its rows (and their pictures), only the card is new.
            _keepRows = true;
            try
            {
                SelectedTrader = next;
            }
            finally
            {
                _keepRows = false;
            }
        }
        else
        {
            SelectedTrader = next;
        }

        if (_wanted is not null && string.Equals(SelectedTrader?.Name, _wanted, StringComparison.OrdinalIgnoreCase))
        {
            _wanted = null;
        }

        OnPropertyChanged(nameof(HasProject));
    }

    private void ShowTrader()
    {
        if (_keepRows)
        {
            OnPropertyChanged(nameof(TraderCaption));
            return;
        }

        _shown = SelectedTrader?.Name;
        _shownProject = _services.Projects.Current;
        if (SelectedTrader is not { } trader || _store.Defaults is not { } defaults)
        {
            _allItems = [];
            Categories = [];
            RefreshItems();
            return;
        }

        // By category; in each, what the game puts on the shelf first, then what it only buys.
        var stock = defaults.SoldBy(trader.Type)
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase).ThenBy(t => !t.CanBePurchased).ThenBy(t => t.Caption, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new EconomyItemViewModel(t, _store.Economy.Find(trader.Name, t.Code), OnItemChanged, false, RemoveItem, PictureAsync));
        var added = _store.Economy.Entries(trader.Name)
            .Where(e => defaults.Find(e.Code)?.IsSoldBy(trader.Type) != true)
            .Select(e => new EconomyItemViewModel(
                defaults.Find(e.Code) ?? new TradeableDefault(e.Code, e.Code.Replace('_', ' '), string.Empty, [], 0, 0, true, 0, false),
                e, OnItemChanged, true, RemoveItem, PictureAsync));
        _allItems = [.. added, .. stock];
        var category = SelectedCategory?.Key;
        Categories = [new EconomyCategory(AllCategories, Loc.T("Economy.AllCategories")),
            .. _allItems.Select(i => i.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(c => new EconomyCategory(c, c))];
        SelectedCategory = Categories.FirstOrDefault(c => c.Key == category) ?? Categories[0];
        RefreshItems();
        OnPropertyChanged(nameof(TraderCaption));
    }

    private void RefreshItems()
    {
        var category = SelectedCategory?.Key is { } key && key != AllCategories ? key : null;
        var text = FilterText.Trim();
        Items = _allItems
            .Where(i => category is null || string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(i => text.Length == 0 || i.Caption.Contains(text, StringComparison.CurrentCultureIgnoreCase) || i.Code.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        OnPropertyChanged(nameof(TraderCaption));
    }

    /// <summary>The Add items box's list: every tradeable the selected trader does not sell yet (not the locked ones).</summary>
    private void BuildPicks()
    {
        if (SelectedTrader is null || _store.Defaults is not { } defaults)
        {
            _allPicks = [];
            RefreshPicks();
            return;
        }

        var have = _allItems.Select(i => i.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allPicks = defaults.Tradeables
            .Where(t => !t.ImmuneToOverrides && !have.Contains(t.Code))
            .OrderBy(t => t.Caption, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new EconomyPickItem(t, OnPickChecked, PictureAsync))
            .ToList();
        AddCheckedCount = 0;
        RefreshPicks();
    }

    private void RefreshPicks()
    {
        var text = AddFilterText.Trim();
        AddItems = text.Length == 0
            ? _allPicks
            : _allPicks.Where(p => p.Caption.Contains(text, StringComparison.CurrentCultureIgnoreCase) || p.Code.Contains(text, StringComparison.OrdinalIgnoreCase)
                                   || p.Default.Category.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnPickChecked() => AddCheckedCount = _allPicks.Count(p => p.IsChecked);

    /// <summary>
    /// An item's picture: the trade menu's own icon (<see cref="TradeableDefault.IconPath"/>), else a drawing of what is sold
    /// (its Blueprint's mesh), through the thumbnail disk cache; null for services that show nothing.
    /// </summary>
    private async Task<Bitmap?> PictureAsync(TradeableDefault item, CancellationToken cancellationToken)
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return null;
        }

        string? png = null;
        if (item.IconPath is { } icon)
        {
            png = await _services.Thumbnails.GetTextureAsync(catalog, icon, cancellationToken).ConfigureAwait(false);
        }

        if (png is null && item.ClassPath is { } classPath)
        {
            var dot = classPath.IndexOf('.', classPath.LastIndexOf('/') + 1);
            png = await _services.Thumbnails.GetBlueprintAsync(catalog, dot < 0 ? classPath : classPath[..dot], cancellationToken).ConfigureAwait(false);
        }

        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(false);
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (_store.Defaults is null)
        {
            if (HasCatalog && !IsLoading)
            {
                Load();
            }

            if (!HasCatalog)
            {
                Traders = [];
                SelectedTrader = null;
            }

            return;
        }

        BuildTraders();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(HasCatalog));
        IsLoading = false;
        Load();
    });

    private void OnProjectChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(HasProject));
}

/// <summary>A trade category in the filter ("All" has the key <c>*</c>).</summary>
/// <param name="Key">Category (<c>RangedWeapon</c>).</param>
/// <param name="Label">Shown text.</param>
public sealed record EconomyCategory(string Key, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}
