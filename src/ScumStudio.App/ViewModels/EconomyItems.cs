using System.Globalization;
using System.Text.RegularExpressions;
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
/// <param name="type">Its trader type (<c>Armorer</c>), empty for a section of a trader neither the game nor the map knows.</param>
/// <param name="isPlaced">Placed in this project (not one of the game's).</param>
/// <param name="cell">The map cell it stands in (<c>A_0</c>), or null.</param>
/// <param name="isUnknown">A section of the file for a trader that is neither the game's nor placed on the map (an imported file's).</param>
public sealed partial class EconomyTraderRow(string name, string type, bool isPlaced, string? cell, bool isUnknown = false) : ObservableObject
{
    /// <summary>Section name.</summary>
    public string Name { get; } = name;

    /// <summary>Trader type.</summary>
    public string Type { get; } = type;

    /// <summary>Placed in this project.</summary>
    public bool IsPlaced { get; } = isPlaced;

    /// <summary>Not on this map: the server only uses the section if a trader of that name exists there.</summary>
    public bool IsUnknown { get; } = isUnknown;

    /// <summary>Map cell (<c>B_4</c>), or "?".</summary>
    public string Cell { get; } = cell ?? "?";

    /// <summary>The type as the trade menu calls it ("Armory").</summary>
    public string TypeLabel => IsUnknown ? Loc.T("Economy.UnknownTrader") : Loc.Instance.Or("Trader.Type." + Type, Type);

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
/// One tradeable at the selected trader: the game's values with the project's entry on top. The entry is kept as the file
/// has it and only the values edited here are replaced in it, so a value the file pins (even to the game's own) or leaves at
/// <c>-1</c> stays that way. A negative or empty value is the game's (<c>-1</c>); typing the game's value back on one of the
/// trader's own items removes it from the entry. An item the trader does not sell by default (<see cref="IsAdded"/>) is
/// written out in full; one the game's tables do not know (<see cref="IsUnknown"/>) only with what the file says.
/// </summary>
public sealed partial class EconomyItemViewModel : ThumbnailItem
{
    private readonly Action<EconomyItemViewModel> _changed;
    private readonly Action<EconomyItemViewModel>? _remove;
    private readonly Func<TradeableDefault, CancellationToken, Task<Bitmap?>>? _picture;
    private TradeableOverride _value;
    private readonly bool _editable = true;
    private bool _loading;

    /// <summary>Creates the row for <paramref name="tradeable"/> with its override <paramref name="value"/> (or none).</summary>
    /// <param name="tradeable">The game's values (placeholders for a code the tables do not know).</param>
    /// <param name="value">The project's entry, or null.</param>
    /// <param name="changed">Called after an edit.</param>
    /// <param name="isAdded">Not in the trader's default stock: added by the project.</param>
    /// <param name="remove">Called by the row's remove button.</param>
    /// <param name="picture">Makes the row's picture.</param>
    /// <param name="isUnknown">The game's tables do not know the code: <paramref name="tradeable"/> only names it.</param>
    public EconomyItemViewModel(
        TradeableDefault tradeable, TradeableOverride? value, Action<EconomyItemViewModel> changed, bool isAdded = false,
        Action<EconomyItemViewModel>? remove = null, Func<TradeableDefault, CancellationToken, Task<Bitmap?>>? picture = null, bool isUnknown = false)
    {
        Default = tradeable;
        IsAdded = isAdded;
        IsUnknown = isUnknown;
        _changed = changed;
        _remove = remove;
        _picture = picture;
        _value = new TradeableOverride(tradeable.Code);
        Load(value);
    }

    /// <summary>The game's values.</summary>
    public TradeableDefault Default { get; }

    /// <summary>The trader does not sell it by default: the project added it.</summary>
    public bool IsAdded { get; }

    /// <summary>The game's tables do not know the code (a mod's item, a typo): shown with the file's values, skipped by bulk actions.</summary>
    public bool IsUnknown { get; }

    /// <summary>The <c>tradeable-code</c>.</summary>
    public string Code => Default.Code;

    /// <summary>Its name in the trade menu.</summary>
    public string Caption => Default.Caption;

    /// <summary>Its trade category.</summary>
    public string Category => Default.Category;

    /// <summary>Placeholder glyph until the picture is there.</summary>
    public string IconKey => EconomyGlyphs.ForCategory(Category);

    /// <summary>Game values, shown under the row.</summary>
    public string DefaultText => IsUnknown ? Loc.T("Economy.Badge.Unknown.Tip") : Loc.F("Economy.DefaultValues", Default.PurchasePrice, Default.SellPrice, Default.RequiredFame);

    /// <summary>The server ignores overrides of this tradeable (the game's <c>IsImmuneToUserOverrides</c>).</summary>
    public bool IsLocked => Default.ImmuneToOverrides;

    /// <summary>The page can change it: a project is open and the server lets a file change it.</summary>
    public bool IsEditable
    {
        get => _editable && !IsLocked;
        init => _editable = value;
    }

    /// <summary>Purchase price (a negative value: the game's).</summary>
    public int PurchasePrice
    {
        get => _value.PurchasePrice ?? Default.PurchasePrice;
        set => SetInt(value, PurchasePrice, Default.PurchasePrice, v => _value with { PurchasePrice = v });
    }

    /// <summary>Sale price (what a player gets; a negative value: the game's).</summary>
    public int SellPrice
    {
        get => _value.SellPrice ?? Default.SellPrice;
        set => SetInt(value, SellPrice, Default.SellPrice, v => _value with { SellPrice = v });
    }

    /// <summary>Fame points needed (a negative value: the game's).</summary>
    public int RequiredFame
    {
        get => _value.RequiredFame ?? Default.RequiredFame;
        set => SetInt(value, RequiredFame, Default.RequiredFame, v => _value with { RequiredFame = v });
    }

    /// <summary><c>delta-price</c>, or null for the game's (<c>-1.0</c>; the tables do not say what that is).</summary>
    public float? DeltaPrice
    {
        get => _value.DeltaPrice;
        set => Replace(_value with { DeltaPrice = value is >= 0f ? value : null });
    }

    /// <summary>On the shelf.</summary>
    public bool CanBePurchased
    {
        get => _value.CanBePurchased ?? BaseCanBuy;
        set => SetBool(value, CanBePurchased, BaseCanBuy, v => _value with { CanBePurchased = v });
    }

    /// <summary>Sold only after a player sold one.</summary>
    public bool AfterSaleOnly
    {
        get => _value.AfterSaleOnly ?? Default.AfterSaleOnly;
        set => SetBool(value, AfterSaleOnly, Default.AfterSaleOnly, v => _value with { AfterSaleOnly = v });
    }

    /// <summary>The price box's text (read leniently when committed: <c>12,000</c>, <c>12 000</c>; empty or <c>-1</c> = the game's).</summary>
    public string PurchaseText
    {
        get => Show(_value.PurchasePrice, PurchasePrice);
        set => CommitInt(value, v => PurchasePrice = v);
    }

    /// <summary>The sale price box's text.</summary>
    public string SellText
    {
        get => Show(_value.SellPrice, SellPrice);
        set => CommitInt(value, v => SellPrice = v);
    }

    /// <summary>The fame box's text.</summary>
    public string FameText
    {
        get => Show(_value.RequiredFame, RequiredFame);
        set => CommitInt(value, v => RequiredFame = v);
    }

    /// <summary>The delta box's text (empty: the game's).</summary>
    public string DeltaText
    {
        get => _value.DeltaPrice is { } d ? d.ToString("0.0###", CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                Valid(() => DeltaPrice = null);
            }
            else if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out f))
            {
                Valid(() => DeltaPrice = float.IsFinite(f) ? f : null);
            }
            else
            {
                Invalid(text);
            }
        }
    }

    /// <summary>The last committed text was not a number (the value stayed as it was).</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>Why, for the row's tooltip.</summary>
    [ObservableProperty]
    private string _invalidText = string.Empty;

    /// <summary>A player gets more for selling it than it costs.</summary>
    public bool SellAboveBuy => SellPrice > PurchasePrice && (!IsUnknown || (_value.SellPrice is not null && _value.PurchasePrice is not null));

    /// <summary>Differs from the game (an added item: from the values it was added with; an unknown one: anything written).</summary>
    public bool IsChanged => IsUnknown
        ? !_value.IsDefault
        : PurchasePrice != Default.PurchasePrice || SellPrice != Default.SellPrice || RequiredFame != Default.RequiredFame
          || CanBePurchased != BaseCanBuy || AfterSaleOnly != Default.AfterSaleOnly || _value.DeltaPrice is not null;

    /// <summary>Not sold (taken off sale, or the game does not sell it).</summary>
    public bool IsOffSale => !CanBePurchased;

    /// <summary>The remove button: an added item leaves the trader, a default one goes off sale.</summary>
    public bool CanRemove => IsEditable && (IsAdded || CanBePurchased);

    /// <summary>The put-back button of a default item that is off sale.</summary>
    public bool CanPutBack => IsEditable && !IsAdded && !CanBePurchased;

    /// <summary>The price is not what its Reset writes.</summary>
    public bool CanResetPurchase => IsEditable && _value.PurchasePrice != TargetInt(Default.PurchasePrice);

    /// <summary>The sale price is not what its Reset writes.</summary>
    public bool CanResetSell => IsEditable && _value.SellPrice != TargetInt(Default.SellPrice);

    /// <summary>The fame is not what its Reset writes.</summary>
    public bool CanResetFame => IsEditable && _value.RequiredFame != TargetInt(Default.RequiredFame);

    /// <summary>The delta is set.</summary>
    public bool CanResetDelta => IsEditable && _value.DeltaPrice is not null;

    /// <summary>On sale is not what its Reset writes.</summary>
    public bool CanResetOnSale => IsEditable && _value.CanBePurchased != TargetBool(true);

    /// <summary>After-sale-only is not what its Reset writes.</summary>
    public bool CanResetAfterSale => IsEditable && _value.AfterSaleOnly != TargetBool(Default.AfterSaleOnly);

    /// <summary>"Back to the game's value (11200)" for each box.</summary>
    public string PurchaseResetTip => ResetTip(TargetInt(Default.PurchasePrice));

    /// <inheritdoc cref="PurchaseResetTip"/>
    public string SellResetTip => ResetTip(TargetInt(Default.SellPrice));

    /// <inheritdoc cref="PurchaseResetTip"/>
    public string FameResetTip => ResetTip(TargetInt(Default.RequiredFame));

    /// <inheritdoc cref="PurchaseResetTip"/>
    public string DeltaResetTip => Loc.F("Economy.ResetValue.Tip", "-1.0");

    /// <inheritdoc cref="PurchaseResetTip"/>
    public string OnSaleResetTip => ResetTip(TargetBool(true));

    /// <inheritdoc cref="PurchaseResetTip"/>
    public string AfterSaleResetTip => ResetTip(TargetBool(Default.AfterSaleOnly));

    /// <summary>What the trader sells it for by default: an added item is on sale.</summary>
    private bool BaseCanBuy => IsAdded || Default.CanBePurchased;

    /// <summary>The entry this row stands for: the loaded one with only the values edited here replaced.</summary>
    public TradeableOverride ToOverride() => _value;

    /// <summary>Shows <paramref name="value"/> without reporting a change.</summary>
    public void Load(TradeableOverride? value)
    {
        _value = value ?? new TradeableOverride(Code);
        _loading = true;
        try
        {
            Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Price and sale price × <paramref name="factor"/>, rounded and kept within the largest price the file can hold; true when
    /// a value changed. Locked and unknown items are left alone.
    /// </summary>
    public bool ScalePrices(double factor)
    {
        if (IsLocked || IsUnknown || !(factor > 0) || double.IsInfinity(factor))
        {
            return false;
        }

        var before = _value;
        PurchasePrice = Scaled(PurchasePrice, factor);
        SellPrice = Scaled(SellPrice, factor);
        return !Equals(before, _value);
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        _picture?.Invoke(Default, cancellationToken) ?? Task.FromResult<Bitmap?>(null);

    private static int Scaled(int value, double factor) => (int)Math.Min(Math.Round(value * factor, MidpointRounding.AwayFromZero), int.MaxValue);

    /// <summary>Back to the game's values (an added item: the values it was added with).</summary>
    [RelayCommand]
    private void Reset() => Replace(_value with
    {
        PurchasePrice = TargetInt(Default.PurchasePrice),
        SellPrice = TargetInt(Default.SellPrice),
        RequiredFame = TargetInt(Default.RequiredFame),
        DeltaPrice = null,
        CanBePurchased = TargetBool(true),
        AfterSaleOnly = TargetBool(Default.AfterSaleOnly),
    });

    [RelayCommand(CanExecute = nameof(CanResetPurchase))]
    private void ResetPurchase() => Replace(_value with { PurchasePrice = TargetInt(Default.PurchasePrice) });

    [RelayCommand(CanExecute = nameof(CanResetSell))]
    private void ResetSell() => Replace(_value with { SellPrice = TargetInt(Default.SellPrice) });

    [RelayCommand(CanExecute = nameof(CanResetFame))]
    private void ResetFame() => Replace(_value with { RequiredFame = TargetInt(Default.RequiredFame) });

    [RelayCommand(CanExecute = nameof(CanResetDelta))]
    private void ResetDelta() => Replace(_value with { DeltaPrice = null });

    [RelayCommand(CanExecute = nameof(CanResetOnSale))]
    private void ResetOnSale() => Replace(_value with { CanBePurchased = TargetBool(true) });

    [RelayCommand(CanExecute = nameof(CanResetAfterSale))]
    private void ResetAfterSale() => Replace(_value with { AfterSaleOnly = TargetBool(Default.AfterSaleOnly) });

    /// <summary>Takes the item from the trader (see <see cref="CanRemove"/>).</summary>
    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    /// <summary>Puts a default item that is off sale back on sale.</summary>
    [RelayCommand]
    private void PutBack() => CanBePurchased = true;

    /// <summary>What a value's Reset writes: an added item's game value written out (its entry stands on its own), else <c>-1</c>.</summary>
    private int? TargetInt(int game) => IsAdded && !IsUnknown ? game : null;

    /// <summary>A flag's Reset: an added item's on-sale is <c>true</c> and its after-sale rule the game's, written out; else <c>default</c>.</summary>
    private bool? TargetBool(bool added) => IsAdded && !IsUnknown ? added : null;

    private static string ResetTip(object? target) =>
        Loc.F("Economy.ResetValue.Tip", target switch
        {
            null => "-1",
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => target.ToString() ?? string.Empty,
        });

    /// <summary>A box's text: the value, or <c>-1</c> for an unknown item's value the file leaves to the game.</summary>
    private string Show(int? written, int shown) =>
        written is null && IsUnknown ? "-1" : shown.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A typed number: the shown value typed again changes nothing; negative is the game's (<c>-1</c>); on one of the trader's
    /// own items the game's value is the game's too; an added or unknown item keeps what was typed.
    /// </summary>
    private void SetInt(int value, int shown, int game, Func<int?, TradeableOverride> with)
    {
        if (value >= 0 && value == shown)
        {
            return;
        }

        Replace(with(value < 0 ? null : !IsAdded && !IsUnknown && value == game ? null : value));
    }

    private void SetBool(bool value, bool shown, bool game, Func<bool?, TradeableOverride> with)
    {
        if (value == shown)
        {
            return;
        }

        Replace(with(!IsAdded && !IsUnknown && value == game ? null : value));
    }

    private void CommitInt(string? text, Action<int> set)
    {
        if (ParseInt(text) is { } value)
        {
            Valid(() => set(value));
        }
        else
        {
            Invalid((text ?? string.Empty).Trim());
        }
    }

    private void Valid(Action apply)
    {
        IsInvalid = false;
        InvalidText = string.Empty;
        apply();
        Refresh(); // a text the value did not change ("12 000" for 12000) shows the number again
    }

    private void Invalid(string text)
    {
        InvalidText = Loc.F("Economy.Value.Invalid", text);
        IsInvalid = true;
    }

    /// <summary>
    /// A typed whole number, read leniently: digits with group separators (<c>12,000</c>, <c>12 000</c>, <c>12.000</c>, the
    /// current culture's) every three digits; empty or negative is <c>-1</c> (the game's); above the largest price it is that.
    /// Null when it is not a number.
    /// </summary>
    public static int? ParseInt(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return -1;
        }

        var group = CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator;
        if (group.Length > 0 && !" ,.'".Contains(group, StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace(group, ",", StringComparison.Ordinal);
        }

        if (!WholeNumber().IsMatch(trimmed))
        {
            return null;
        }

        var negative = trimmed[0] is '-' or '−';
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        if (negative)
        {
            return -1;
        }

        return digits.Length > 10 || (digits.Length > 0 && long.Parse(digits, CultureInfo.InvariantCulture) > int.MaxValue)
            ? int.MaxValue
            : digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^[-−]?(\d+|\d{1,3}([ ,.'  ]\d{3})+)$")]
    private static partial Regex WholeNumber();

    private void Replace(TradeableOverride value)
    {
        if (Equals(value, _value))
        {
            return;
        }

        _value = value;
        Refresh();
        if (!_loading)
        {
            _changed(this);
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        ResetPurchaseCommand.NotifyCanExecuteChanged();
        ResetSellCommand.NotifyCanExecuteChanged();
        ResetFameCommand.NotifyCanExecuteChanged();
        ResetDeltaCommand.NotifyCanExecuteChanged();
        ResetOnSaleCommand.NotifyCanExecuteChanged();
        ResetAfterSaleCommand.NotifyCanExecuteChanged();
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
