using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Level.Economy;

namespace ScumStudio.App.ViewModels;

/// <summary>Glyphs of the Economy page: one per trader type, one per trade category group (shown until an item's picture is there).</summary>
public static class EconomyGlyphs
{
    /// <summary>The glyph of a trader type (<c>Armorer</c> → a sight, <c>Doctor</c> → a cross …).</summary>
    public static string ForType(string type) => type switch
    {
        "Armorer" => "Icon.Weapon",
        "GeneralGoods" => "Icon.Basket",
        "Mechanic" => "Icon.Vehicle",
        "Doctor" => "Icon.Medical",
        "Bartender" => "Icon.Cup",
        "Barber" => "Icon.Scissors",
        "Harbourmaster" => "Icon.Anchor",
        "Hunter" => "Icon.Tree",
        "MasterHunter" => "Icon.Mountain",
        _ => "Icon.Coins",
    };

    /// <summary>The placeholder of a trade category (<c>RangedWeapon</c>, <c>Vehicles</c> …).</summary>
    public static string ForCategory(string category) =>
        category.Contains("Weapon", StringComparison.OrdinalIgnoreCase) || category.Contains("Ammo", StringComparison.OrdinalIgnoreCase) || category.Contains("Explosive", StringComparison.OrdinalIgnoreCase) ? "Icon.Weapon"
        : category.Contains("Vehicle", StringComparison.OrdinalIgnoreCase) ? "Icon.Vehicle"
        : category.Contains("Medic", StringComparison.OrdinalIgnoreCase) || category.Contains("FirstAid", StringComparison.OrdinalIgnoreCase) ? "Icon.Medical"
        : category.Contains("Food", StringComparison.OrdinalIgnoreCase) || category.Contains("Drink", StringComparison.OrdinalIgnoreCase) ? "Icon.Cup"
        : "Icon.Image";
}

/// <summary>A trader in the Economy page's list: its card shows the type glyph, the map cell and what the project changed.</summary>
/// <param name="name">Its economy section (<c>A_0_Armory</c>).</param>
/// <param name="type">Its trader type (<c>Armorer</c>).</param>
/// <param name="isPlaced">Placed in this project (not one of the game's).</param>
/// <param name="cell">The map cell it stands in (<c>A_0</c>), or null.</param>
public sealed partial class EconomyTraderRow(string name, string type, bool isPlaced, string? cell) : ObservableObject
{
    /// <summary>Section name.</summary>
    public string Name { get; } = name;

    /// <summary>Trader type.</summary>
    public string Type { get; } = type;

    /// <summary>Placed in this project.</summary>
    public bool IsPlaced { get; } = isPlaced;

    /// <summary>Map cell (<c>B_4</c>), or "?".</summary>
    public string Cell { get; } = cell ?? "?";

    /// <summary>The type as the trade menu calls it ("Armory").</summary>
    public string TypeLabel => Loc.Instance.Or("Trader.Type." + Type, Type);

    /// <summary>Glyph of the type.</summary>
    public string IconKey => EconomyGlyphs.ForType(Type);

    /// <summary>"Armory · placed".</summary>
    [ObservableProperty]
    private string _caption = string.Empty;

    /// <summary>"3 changed · 2 added", or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private string _changesText = string.Empty;

    /// <summary>The project changed this trader.</summary>
    public bool HasChanges => ChangesText.Length > 0;

    /// <summary>Refreshes the texts with the number of changed and added items.</summary>
    public void SetChanges(int changed, int added = 0)
    {
        Caption = IsPlaced ? $"{TypeLabel} · {Loc.T("Economy.Placed")}" : TypeLabel;
        var parts = new List<string>();
        if (changed > 0)
        {
            parts.Add(Loc.F("Economy.Changes", changed));
        }

        if (added > 0)
        {
            parts.Add(Loc.F("Economy.AddedCount", added));
        }

        ChangesText = string.Join(" · ", parts);
    }
}

/// <summary>
/// One tradeable at the selected trader: the game's values with the project's override on top. Setting a value back to the
/// game's removes it from the override (the server then uses the game's). An item the trader does not sell by default
/// (<see cref="IsAdded"/>) is written out in full, on sale unless taken off.
/// </summary>
public sealed partial class EconomyItemViewModel : ThumbnailItem
{
    private readonly Action<EconomyItemViewModel> _changed;
    private readonly Action<EconomyItemViewModel>? _remove;
    private readonly Func<TradeableDefault, CancellationToken, Task<Bitmap?>>? _picture;
    private int? _purchase;
    private int? _sell;
    private int? _fame;
    private bool? _canBuy;
    private bool? _afterSale;
    private float? _delta;
    private bool _loading;

    /// <summary>Creates the row for <paramref name="tradeable"/> with its override <paramref name="value"/> (or none).</summary>
    /// <param name="tradeable">The game's values.</param>
    /// <param name="value">The project's entry, or null.</param>
    /// <param name="changed">Called after an edit.</param>
    /// <param name="isAdded">Not in the trader's default stock: added by the project.</param>
    /// <param name="remove">Called by the row's remove button.</param>
    /// <param name="picture">Makes the row's picture.</param>
    public EconomyItemViewModel(
        TradeableDefault tradeable, TradeableOverride? value, Action<EconomyItemViewModel> changed, bool isAdded = false,
        Action<EconomyItemViewModel>? remove = null, Func<TradeableDefault, CancellationToken, Task<Bitmap?>>? picture = null)
    {
        Default = tradeable;
        IsAdded = isAdded;
        _changed = changed;
        _remove = remove;
        _picture = picture;
        Load(value);
    }

    /// <summary>The game's values.</summary>
    public TradeableDefault Default { get; }

    /// <summary>The trader does not sell it by default: the project added it.</summary>
    public bool IsAdded { get; }

    /// <summary>The <c>tradeable-code</c>.</summary>
    public string Code => Default.Code;

    /// <summary>Its name in the trade menu.</summary>
    public string Caption => Default.Caption;

    /// <summary>Its trade category.</summary>
    public string Category => Default.Category;

    /// <summary>Placeholder glyph until the picture is there.</summary>
    public string IconKey => EconomyGlyphs.ForCategory(Category);

    /// <summary>Game values, shown under the row.</summary>
    public string DefaultText => Loc.F("Economy.DefaultValues", Default.PurchasePrice, Default.SellPrice, Default.RequiredFame);

    /// <summary>The server ignores overrides of this tradeable (the game's <c>IsImmuneToUserOverrides</c>).</summary>
    public bool IsLocked => Default.ImmuneToOverrides;

    /// <summary>Purchase price.</summary>
    public int PurchasePrice
    {
        get => _purchase ?? Default.PurchasePrice;
        set => Set(ref _purchase, value == Default.PurchasePrice ? null : Math.Max(0, value));
    }

    /// <summary>Sale price (what a player gets).</summary>
    public int SellPrice
    {
        get => _sell ?? Default.SellPrice;
        set => Set(ref _sell, value == Default.SellPrice ? null : Math.Max(0, value));
    }

    /// <summary>Fame points needed.</summary>
    public int RequiredFame
    {
        get => _fame ?? Default.RequiredFame;
        set => Set(ref _fame, value == Default.RequiredFame ? null : Math.Max(0, value));
    }

    /// <summary>On the shelf.</summary>
    public bool CanBePurchased
    {
        get => _canBuy ?? BaseCanBuy;
        set => Set(ref _canBuy, value == BaseCanBuy ? null : value);
    }

    /// <summary>Sold only after a player sold one.</summary>
    public bool AfterSaleOnly
    {
        get => _afterSale ?? Default.AfterSaleOnly;
        set => Set(ref _afterSale, value == Default.AfterSaleOnly ? null : value);
    }

    /// <summary>Differs from the game (an added item: from the values it was added with).</summary>
    public bool IsChanged => _purchase is not null || _sell is not null || _fame is not null || _canBuy is not null || _afterSale is not null || _delta is not null;

    /// <summary>Not sold (taken off sale, or the game does not sell it).</summary>
    public bool IsOffSale => !CanBePurchased;

    /// <summary>The remove button: an added item leaves the trader, a default one goes off sale.</summary>
    public bool CanRemove => !IsLocked && (IsAdded || CanBePurchased);

    /// <summary>The put-back button of a default item that is off sale.</summary>
    public bool CanPutBack => !IsLocked && !IsAdded && !CanBePurchased;

    /// <summary>What the trader sells it for by default: an added item is on sale.</summary>
    private bool BaseCanBuy => IsAdded || Default.CanBePurchased;

    /// <summary>The override entry this row stands for (an added item's with every value written out).</summary>
    public TradeableOverride ToOverride() => IsAdded
        ? new(Code)
        {
            PurchasePrice = PurchasePrice,
            SellPrice = SellPrice,
            RequiredFame = RequiredFame,
            CanBePurchased = CanBePurchased,
            AfterSaleOnly = AfterSaleOnly,
            DeltaPrice = _delta,
        }
        : new(Code)
        {
            PurchasePrice = _purchase,
            SellPrice = _sell,
            RequiredFame = _fame,
            CanBePurchased = _canBuy,
            AfterSaleOnly = _afterSale,
            DeltaPrice = _delta,
        };

    /// <summary>Shows <paramref name="value"/> without reporting a change (values equal to the game's count as unchanged).</summary>
    public void Load(TradeableOverride? value)
    {
        _purchase = value?.PurchasePrice is { } p && p != Default.PurchasePrice ? p : null;
        _sell = value?.SellPrice is { } s && s != Default.SellPrice ? s : null;
        _fame = value?.RequiredFame is { } f && f != Default.RequiredFame ? f : null;
        _canBuy = value?.CanBePurchased is { } c && c != BaseCanBuy ? c : null;
        _afterSale = value?.AfterSaleOnly is { } a && a != Default.AfterSaleOnly ? a : null;
        _delta = value?.DeltaPrice;
        _loading = true;
        try
        {
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        _picture?.Invoke(Default, cancellationToken) ?? Task.FromResult<Bitmap?>(null);

    /// <summary>Back to the game's values.</summary>
    [RelayCommand]
    private void Reset()
    {
        Load(null);
        _changed(this);
    }

    /// <summary>Takes the item from the trader (see <see cref="CanRemove"/>).</summary>
    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    /// <summary>Puts a default item that is off sale back on sale.</summary>
    [RelayCommand]
    private void PutBack() => CanBePurchased = true;

    private void Set(ref int? field, int? value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Changed();
    }

    private void Set(ref bool? field, bool? value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(string.Empty);
        if (!_loading)
        {
            _changed(this);
        }
    }
}

/// <summary>A tradeable in the Add items box: any of the game's tradeables the trader does not sell yet.</summary>
public sealed partial class EconomyPickItem : ThumbnailItem
{
    private readonly Action _checkedChanged;
    private readonly Func<TradeableDefault, CancellationToken, Task<Bitmap?>> _picture;

    /// <summary>Creates the entry.</summary>
    public EconomyPickItem(TradeableDefault tradeable, Action checkedChanged, Func<TradeableDefault, CancellationToken, Task<Bitmap?>> picture)
    {
        Default = tradeable;
        _checkedChanged = checkedChanged;
        _picture = picture;
    }

    /// <summary>The game's values (the added entry's).</summary>
    public TradeableDefault Default { get; }

    /// <summary>The <c>tradeable-code</c>.</summary>
    public string Code => Default.Code;

    /// <summary>Its name in the trade menu.</summary>
    public string Caption => Default.Caption;

    /// <summary>Placeholder glyph until the picture is there.</summary>
    public string IconKey => EconomyGlyphs.ForCategory(Default.Category);

    /// <summary>"Weapon_AK47 · RangedWeapon · Armory".</summary>
    public string Details => string.Join(" · ", new[] { Default.Code, Default.Category, string.Join(", ", Default.TraderTypes.Select(t => Loc.Instance.Or("Trader.Type." + t, t))) }.Where(p => p.Length > 0));

    /// <summary>Its game price.</summary>
    public string PriceText => Default.PurchasePrice.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Picked to be added.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _checkedChanged();

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) => _picture(Default, cancellationToken);
}
