using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>A filter chip of a module page (kind and/or category).</summary>
/// <param name="Label">Chip text.</param>
/// <param name="Kind">Kind to show, or null for every kind.</param>
/// <param name="Category">Category to show, or null for every category.</param>
public sealed record ModuleFilter(string Label, ModdableKind? Kind, string? Category)
{
    /// <summary>True when <paramref name="item"/> passes the filter.</summary>
    public bool Matches(ModuleItemViewModel item) =>
        (Kind is null || item.Asset.Kind == Kind) && (Category is null || string.Equals(item.Asset.Category, Category, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A vehicle/weapon/ammo entry of a module page list (stock or a project clone); its inventory icon loads while the row is on screen.</summary>
public sealed partial class ModuleItemViewModel : ThumbnailItem
{
    private readonly Func<ModuleItemViewModel, CancellationToken, Task<Bitmap?>>? _icon;

    /// <summary>Creates the entry; <paramref name="icon"/> makes the inventory icon bitmap on request.</summary>
    public ModuleItemViewModel(ModdableAsset asset, string? cloneOf = null, Func<ModuleItemViewModel, CancellationToken, Task<Bitmap?>>? icon = null)
    {
        Asset = asset;
        CloneOf = cloneOf;
        _icon = icon;
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        _icon is { } load ? load(this, cancellationToken) : Task.FromResult<Bitmap?>(null);

    /// <summary>The asset.</summary>
    public ModdableAsset Asset { get; }

    /// <summary>Stock template this clone was made from, or null for stock assets.</summary>
    public string? CloneOf { get; }

    /// <summary>True for a project clone.</summary>
    public bool IsClone => CloneOf is not null;

    /// <summary>Package leaf name.</summary>
    public string Name => Asset.Name;

    /// <summary>Package path.</summary>
    public string PackagePath => Asset.PackagePath;

    /// <summary>"Weapon · Ranged_Weapons".</summary>
    public string Detail => $"{KindLabel(Asset.Kind)} · {Asset.Category}" + (CloneOf is null ? string.Empty : Localization.Loc.F("Module.CloneOf", ShortName(CloneOf)));

    /// <summary>Icon resource key.</summary>
    public string IconKey => Asset.Kind == ModdableKind.Vehicle ? "Icon.Vehicle" : "Icon.Weapon";

    /// <summary>Number of edited values in this asset's packages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge), nameof(HasBadge))]
    private int _editCount;

    /// <summary>"Clone", "3 edits" or empty.</summary>
    public string Badge => IsClone
        ? (EditCount > 0 ? Localization.Loc.F("Module.Badge.CloneEdits", EditCount) : Localization.Loc.T("Module.Badge.Clone"))
        : EditCount > 0 ? Localization.Loc.F(EditCount == 1 ? "Module.Badge.Edit" : "Module.Badge.Edits", EditCount) : string.Empty;

    /// <summary>True when <see cref="Badge"/> is not empty.</summary>
    public bool HasBadge => Badge.Length > 0;

    /// <summary>Re-reads <see cref="Detail"/> and <see cref="Badge"/> after the UI language changed.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Badge));
    }

    /// <summary>Readable kind.</summary>
    public static string KindLabel(ModdableKind kind) => kind switch
    {
        ModdableKind.Vehicle => Localization.Loc.T("Module.Kind.Vehicle"),
        ModdableKind.Weapon => Localization.Loc.T("Module.Kind.Weapon"),
        ModdableKind.Magazine => Localization.Loc.T("Module.Kind.Magazine"),
        ModdableKind.Ammo => Localization.Loc.T("Module.Kind.Ammo"),
        ModdableKind.Projectile => Localization.Loc.T("Module.Kind.Projectile"),
        _ => kind.ToString(),
    };

    /// <summary>Leaf of a package path.</summary>
    public static string ShortName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}

/// <summary>One package of the selected asset whose values can be edited (the asset, its entity setup, an attachment).</summary>
/// <param name="Label">Selector text.</param>
/// <param name="PackagePath">Package path.</param>
/// <param name="Description">What the part holds.</param>
public sealed record ModulePart(string Label, string PackagePath, string Description)
{
    /// <summary>For a DataTable part: the row shown (a melee weapon's <c>WeaponDesc_Table</c> row, named after it), else null.</summary>
    public string? Row { get; init; }

    /// <summary>The row whose values a clone's new row starts from (its template's), or null when the row exists.</summary>
    public string? RowFrom { get; init; }

    /// <summary>True for a vehicle's engine torque curve: only the torque of each key is shown, labelled with its rpm.</summary>
    public bool IsTorqueCurve { get; init; }

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>An editable stored value (a <see cref="Tunable"/>) with its stock, committed and pending values.</summary>
public sealed partial class TunableRowViewModel : ObservableObject
{
    private string _committed;

    /// <summary>Creates the row.</summary>
    /// <param name="package">Package the value lives in.</param>
    /// <param name="tunable">The value as stored in the stock (or freshly cloned) package.</param>
    /// <param name="committed">The value after the project's applied edits.</param>
    public TunableRowViewModel(string package, Tunable tunable, string committed)
    {
        Package = package;
        Tunable = tunable;
        _committed = committed;
        _value = committed;
    }

    /// <summary>Package path.</summary>
    public string Package { get; }

    /// <summary>The stock value.</summary>
    public Tunable Tunable { get; }

    /// <summary>Readable name (<c>DamagePerShot</c> → "Damage per shot").</summary>
    public string Label => Tunable.Name.Contains(' ', StringComparison.Ordinal) ? Tunable.Name : Humanize(Tunable.Name); // a name with spaces is already words ("Torque at 3250 rpm")

    /// <summary>Raw property path (tooltip).</summary>
    public string PathText => $"{Tunable.Export} › {Tunable.Path} ({Tunable.Kind})";

    /// <summary>Stock value.</summary>
    public string StockValue => Tunable.Value;

    /// <summary>Stock value for display (enum values without their <c>EType::</c> prefix).</summary>
    public string StockDisplay => Tunable.Kind == TunableKind.Enum && Tunable.Value.IndexOf("::", StringComparison.Ordinal) is var i and > 0
        ? Tunable.Value[(i + 2)..]
        : Tunable.Value;

    /// <summary>The value after applied edits.</summary>
    public string CommittedValue => _committed;

    /// <summary>Edited (pending) value text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(BoolValue), nameof(Error))]
    private string _value;

    /// <summary>True for bool values (check box).</summary>
    public bool IsBool => Tunable.Kind == TunableKind.Bool;

    /// <summary>True for enum values (combo box).</summary>
    public bool IsEnum => Tunable.Kind == TunableKind.Enum && Tunable.CanEdit;

    /// <summary>True for values edited in a text box.</summary>
    public bool IsText => !IsBool && !IsEnum;

    /// <summary>True when the value cannot be edited.</summary>
    public bool IsReadOnly => !Tunable.CanEdit;

    /// <summary>Enum choices.</summary>
    public IReadOnlyList<string> Choices => Tunable.Choices;

    /// <summary>Check box binding for bools.</summary>
    public bool BoolValue
    {
        get => TryBool(Value) ?? false;
        set => Value = value ? "true" : "false";
    }

    /// <summary>True when the pending value differs from the committed one.</summary>
    public bool IsPending => !TunableValue.AreEqual(Tunable.Kind, Value, _committed);

    /// <summary>True when the committed value differs from stock (an applied edit).</summary>
    public bool IsOverridden => !TunableValue.AreEqual(Tunable.Kind, _committed, Tunable.Value);

    /// <summary>Validation message for the pending value, or null.</summary>
    public string? Error => Validate(Tunable.Kind, Value, Tunable.Choices);

    /// <summary>Sets the committed value (after an apply/undo) and resets the pending value to it.</summary>
    public void Commit(string committed)
    {
        _committed = committed;
        Value = committed;
        OnPropertyChanged(nameof(CommittedValue));
        OnPropertyChanged(nameof(IsOverridden));
        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Normalized pending value (numbers in invariant form).</summary>
    public string NormalizedValue => Normalize(Tunable.Kind, Value);

    /// <summary>Why <paramref name="text"/> is not a valid value of <paramref name="kind"/>, or null.</summary>
    public static string? Validate(TunableKind kind, string text, IReadOnlyList<string> choices)
    {
        try
        {
            _ = Normalize(kind, text);
            return kind == TunableKind.Enum && !choices.Contains(text.Trim(), StringComparer.Ordinal) ? Localization.Loc.T("Module.PickListed") : null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return ex.Message;
        }
    }

    /// <summary>Invariant form of <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">Not a valid value.</exception>
    public static string Normalize(TunableKind kind, string text) => kind switch
    {
        TunableKind.Float => TunableValue.Format(TunableValue.ParseFloat(text)),
        TunableKind.Double => double.Parse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        TunableKind.Int => long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        TunableKind.UInt => ulong.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        TunableKind.Bool => TunableValue.ParseBool(text) ? "true" : "false",
        TunableKind.Vector or TunableKind.Rotator => TunableValue.Format(TunableValue.ParseFloats(text, 3)),
        TunableKind.Color => TunableValue.Format(TunableValue.ParseFloats(text, 4)),
        TunableKind.Enum => text.Trim(),
        _ => text,
    };

    /// <summary>"_maxPushForce" → "Max push force", "bAutoBrake" → "Auto brake", "MaxRPM" → "Max RPM".</summary>
    public static string Humanize(string name)
    {
        var n = name.TrimStart('_');
        if (n.Length > 1 && n[0] == 'b' && char.IsUpper(n[1]))
        {
            n = n[1..];
        }

        var words = Words().Matches(n).Select(m => m.Value).ToList();
        if (words.Count == 0)
        {
            return name;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var acronym = w.Length > 1 && w.All(c => char.IsUpper(c) || char.IsDigit(c));
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : acronym ? w : w.ToLowerInvariant());
        }

        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|[0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Words();

    private static bool? TryBool(string text)
    {
        try
        {
            return TunableValue.ParseBool(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A titled group of rows (one export or one struct inside it).</summary>
/// <param name="Title">Group title.</param>
/// <param name="Rows">Rows.</param>
public sealed record TunableGroupViewModel(string Title, IReadOnlyList<TunableRowViewModel> Rows);
