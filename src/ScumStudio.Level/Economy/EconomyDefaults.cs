using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.Level.Economy;

/// <summary>
/// One tradeable as the game sells it (a row of <c>Table_TradeableDesc</c> or of a DLC's <c>Table_TradeableDesc_*</c>).
/// </summary>
/// <param name="Code">The row without <c>_C</c> (<c>Weapon_AK47</c>): the <c>tradeable-code</c> of <c>EconomyOverride.json</c>.</param>
/// <param name="Caption">Its name in the trade menu.</param>
/// <param name="Category">Trade category without its enum prefix (<c>RangedWeapon</c>).</param>
/// <param name="TraderTypes">The trader types that sell it (<c>Armorer</c>, <c>GeneralGoods</c> …).</param>
/// <param name="PurchasePrice">Base purchase price.</param>
/// <param name="SellPrice">Base sale price (what a player gets).</param>
/// <param name="CanBePurchased">On the shelf.</param>
/// <param name="RequiredFame">Fame points needed to buy it.</param>
/// <param name="AfterSaleOnly">Sold only after a player sold one.</param>
public sealed record TradeableDefault(
    string Code, string Caption, string Category, IReadOnlyList<string> TraderTypes,
    int PurchasePrice, int SellPrice, bool CanBePurchased, int RequiredFame, bool AfterSaleOnly)
{
    /// <summary>The row's <c>IsImmuneToUserOverrides</c>: the server ignores <c>EconomyOverride.json</c> entries for it.</summary>
    public bool ImmuneToOverrides { get; init; }

    /// <summary>The trade menu's picture (<c>TradingEntryIconTexture</c>, a Texture2D object path), or null (most services have none).</summary>
    public string? IconPath { get; init; }

    /// <summary>What is sold (<c>TradeableClass</c>: the item's Blueprint class, <c>/Game/.../Weapon_AK47.Weapon_AK47_C</c>), or null.</summary>
    public string? ClassPath { get; init; }

    /// <summary>True when a trader of <paramref name="traderType"/> sells it by default.</summary>
    public bool IsSoldBy(string traderType) => TraderTypes.Contains(traderType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The entry that makes a trader that does not sell it by default sell it: every value written out (the row's price,
    /// sale price, fame and after-sale rule) and <c>can-be-purchased</c> <c>true</c>, so the entry stands on its own.
    /// </summary>
    public TradeableOverride AddedEntry() => new(Code)
    {
        PurchasePrice = PurchasePrice,
        SellPrice = SellPrice,
        CanBePurchased = true,
        RequiredFame = RequiredFame,
        AfterSaleOnly = AfterSaleOnly,
    };
}

/// <summary>A trader of the game (its personality data asset): the name its economy section uses and its type.</summary>
/// <param name="Name"><c>HumanReadableTraderName</c> (<c>A_0_Armory</c>).</param>
/// <param name="Type">Trader type without its enum prefix (<c>Armorer</c>).</param>
/// <param name="PersonalityPath">Object path of the personality data asset.</param>
public sealed record TraderInfo(string Name, string Type, string PersonalityPath);

/// <summary>
/// The game's default economy: every tradeable with its price, sale price, fame requirement and the trader types that sell
/// it (the data registry <c>DataRegistry_TradeableDesc</c> merges <c>/Game/ConZ_Files/Economy/Table_TradeableDesc</c>
/// with each DLC plugin's <c>Economy/Table_TradeableDesc_*</c>), and every trader (the
/// <c>TraderPersonalityDataAsset</c>s under <c>Economy/TraderPersonalities</c>). A trader sells what its type sells; the
/// server's <c>EconomyOverride.json</c> changes that per trader.
/// </summary>
public sealed class EconomyDefaults
{
    private readonly Dictionary<string, TradeableDefault> _byCode;

    /// <summary>Creates the defaults from read (or, in tests, made-up) tables.</summary>
    public EconomyDefaults(IReadOnlyList<TradeableDefault> tradeables, IReadOnlyList<TraderInfo> traders)
    {
        Tradeables = tradeables;
        Traders = traders;
        _byCode = new Dictionary<string, TradeableDefault>(StringComparer.OrdinalIgnoreCase);
        foreach (var tradeable in tradeables)
        {
            _byCode.TryAdd(tradeable.Code, tradeable);
        }
    }

    /// <summary>Every tradeable (first table wins when two tables have the same row).</summary>
    public IReadOnlyList<TradeableDefault> Tradeables { get; }

    /// <summary>The game's traders, in asset path order.</summary>
    public IReadOnlyList<TraderInfo> Traders { get; }

    /// <summary>What a trader of <paramref name="traderType"/> sells.</summary>
    public IEnumerable<TradeableDefault> SoldBy(string traderType) => Tradeables.Where(t => t.IsSoldBy(traderType));

    /// <summary>The tradeable with <paramref name="code"/> (<c>tradeable-code</c>), or null.</summary>
    public TradeableDefault? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>
    /// The codes a placed trader of <paramref name="traderType"/> lists in its section: its type's stock, without the rows the
    /// server does not let a file change.
    /// </summary>
    public IEnumerable<string> StockCodes(string traderType) => SoldBy(traderType).Where(t => !t.ImmuneToOverrides).Select(t => t.Code);

    /// <summary>Reads the tables and personalities of <paramref name="catalog"/>.</summary>
    public static EconomyDefaults Read(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var tradeables = new List<TradeableDefault>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var personalities = new List<string>();
        foreach (var file in catalog.PackageFiles.OrderBy(f => f.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var path = AssetPaths.ToPackagePath(file, catalog.ProjectName);
            if (path.Contains("/Economy/TraderPersonalities/", StringComparison.OrdinalIgnoreCase))
            {
                personalities.Add(path);
                continue;
            }

            var leaf = path[(path.LastIndexOf('/') + 1)..];
            if (!path.Contains("/Economy/", StringComparison.OrdinalIgnoreCase) || !leaf.StartsWith("Table_TradeableDesc", StringComparison.OrdinalIgnoreCase)
                || !catalog.TryLoadObject<UDataTable>(path, out var table))
            {
                continue;
            }

            foreach (var (rowName, row) in table.RowMap)
            {
                var code = rowName.Text.EndsWith("_C", StringComparison.Ordinal) ? rowName.Text[..^2] : rowName.Text;
                if (seen.Add(code))
                {
                    tradeables.Add(ReadRow(code, row));
                }
            }
        }

        return new EconomyDefaults(tradeables, ReadTraders(catalog, personalities));
    }

    /// <summary>Only the game's traders (the personality data assets), in asset path order.</summary>
    public static IReadOnlyList<TraderInfo> ReadTraders(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ReadTraders(catalog, catalog.PackageFiles
            .OrderBy(f => f.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.Contains("/Economy/TraderPersonalities/", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<TraderInfo> ReadTraders(AssetCatalog catalog, IEnumerable<string> personalities)
    {
        var traders = new List<TraderInfo>();
        foreach (var path in personalities)
        {
            if (catalog.TryLoadObject<UObject>(path, out var asset) && asset.TryGetValue(out string name, "HumanReadableTraderName") && name.Length > 0)
            {
                var type = asset.TryGetValue(out FName t, "TraderType") ? EnumText(t.Text) : string.Empty;
                traders.Add(new TraderInfo(name, type, path + "." + path[(path.LastIndexOf('/') + 1)..]));
            }
        }

        return traders;
    }

    private static TradeableDefault ReadRow(string code, FStructFallback row)
    {
        var caption = row.TryGetValue(out FText text, "TradingEntryCaption") && !string.IsNullOrWhiteSpace(text.Text) ? text.Text : code.Replace('_', ' ');
        var category = row.TryGetValue(out FName c, "TradeCategory") ? EnumText(c.Text) : string.Empty;
        // A set of enums (UScriptSet), which TryGetValue does not convert.
        var types = row.Properties.FirstOrDefault(p => p.Name.Text == "TraderTypes")?.Tag?.GenericValue is UScriptSet set
            ? set.Properties.Select(e => e.GenericValue is FName n ? EnumText(n.Text) : string.Empty).Where(t => t.Length > 0).ToList()
            : [];
        return new TradeableDefault(
            code, caption, category, types,
            row.TryGetValue(out int buy, "BasePurchasePrice") ? buy : 0,
            row.TryGetValue(out int sell, "BaseSalePrice") ? sell : 0,
            !row.TryGetValue(out bool purchasable, "CanBePurchasedByPlayer") || purchasable,
            row.TryGetValue(out int fame, "RequiredFamePoints") ? fame : 0,
            row.TryGetValue(out bool afterSale, "OnlyAvailableAfterPlayerSale") && afterSale)
        {
            ImmuneToOverrides = row.TryGetValue(out bool immune, "IsImmuneToUserOverrides") && immune,
            IconPath = SoftPath(row, "TradingEntryIconTexture"),
            ClassPath = SoftPath(row, "TradeableClass"),
        };
    }

    private static string? SoftPath(FStructFallback row, string property) =>
        row.TryGetValue(out FSoftObjectPath path, property) && path.AssetPathName.Text is { Length: > 0 } text && text != "None" ? text : null;

    /// <summary><c>ETraderType::Armorer</c> → <c>Armorer</c>.</summary>
    public static string EnumText(string text) => text[(text.LastIndexOf(':') + 1)..];
}
