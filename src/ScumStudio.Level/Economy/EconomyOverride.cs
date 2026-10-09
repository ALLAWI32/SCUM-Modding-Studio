using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScumStudio.Level.Economy;

/// <summary>
/// One entry of a trader's section in <c>EconomyOverride.json</c>: what the server changes for one tradeable at that
/// trader. A null value is the game's own (written as <c>-1</c> / <c>-1.0</c> / <c>default</c>, as the game writes it).
/// </summary>
/// <param name="Code">The tradeable (<c>tradeable-code</c>): its <c>Table_TradeableDesc</c> row without <c>_C</c>, e.g. <c>Weapon_AK47</c>.</param>
public sealed record TradeableOverride(string Code)
{
    /// <summary><c>base-purchase-price</c>: what a player pays.</summary>
    public int? PurchasePrice { get; init; }

    /// <summary><c>base-sell-price</c>: what a player gets when selling.</summary>
    public int? SellPrice { get; init; }

    /// <summary><c>delta-price</c>.</summary>
    public float? DeltaPrice { get; init; }

    /// <summary><c>can-be-purchased</c>: false takes it off the trader's shelf.</summary>
    public bool? CanBePurchased { get; init; }

    /// <summary><c>required-famepoints</c>.</summary>
    public int? RequiredFame { get; init; }

    /// <summary><c>available-after-sale-only</c>: sold only after a player sold one.</summary>
    public bool? AfterSaleOnly { get; init; }

    /// <summary>True when the entry changes nothing.</summary>
    public bool IsDefault => PurchasePrice is null && SellPrice is null && DeltaPrice is null && CanBePurchased is null && RequiredFame is null && AfterSaleOnly is null;
}

/// <summary>
/// The dedicated server's <c>SCUM\Saved\Config\WindowsServer\EconomyOverride.json</c> (single player:
/// <c>%LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\</c>). The schema is the one the game itself writes there:
/// <c>"economy-override"</c> holds string settings and <c>"traders"</c>, one array per trader named after its personality's
/// <c>HumanReadableTraderName</c> (<c>A_0_Armory</c>, <c>B_4_Saloon</c> …), each entry a <see cref="TradeableOverride"/>
/// with every value a string. The settings are kept exactly as read (unknown keys too); the traders' entries are parsed.
/// </summary>
public sealed class EconomyOverride
{
    /// <summary>The file name the game reads.</summary>
    public const string FileName = "EconomyOverride.json";

    private const string RootKey = "economy-override";
    private const string TradersKey = "traders";

    /// <summary>The settings the game writes when it creates the file (SCUM 1.x, read from its own generated file).</summary>
    private static readonly (string Key, string Value)[] GameDefaults =
    [
        ("economy-reset-time-hours", "-1.0"),
        ("prices-randomization-time-hours", "-1.0"),
        ("tradeable-rotation-time-ingame-hours-min", "48.0"),
        ("tradeable-rotation-time-ingame-hours-max", "96.0"),
        ("tradeable-rotation-time-of-day-min", "8.0"),
        ("tradeable-rotation-time-of-day-max", "16.0"),
        ("fully-restock-tradeable-hours", "2.0"),
        ("trader-funds-change-rate-per-hour-multiplier", "1.0"),
        ("prices-subject-to-delta", "1"),
        ("prices-subject-to-player-count", "0"),
        ("gold-price-subject-to-global-multiplier", "1"),
        ("gold-base-price", "-1"),
        ("gold-sale-price-modifier", "-1.0"),
        ("gold-price-change-percentage-step", "-1.0"),
        ("gold-price-change-per-step", "-1.0"),
        ("economy-logging", "1"),
        ("traders-unlimited-funds", "0"),
        ("traders-unlimited-stock", "0"),
        ("global-only-after-player-sale-tradeable-availability-enabled", "0"),
        ("tradeable-rotation-enabled", "1"),
        ("enable-fame-point-requirement", "1"),
    ];

    private readonly List<(string Key, JsonNode? Value)> _settings;
    private readonly Dictionary<string, List<TradeableOverride>> _traders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    private EconomyOverride(List<(string Key, JsonNode? Value)> settings) => _settings = settings;

    /// <summary>The traders with a section, in file order.</summary>
    public IReadOnlyList<string> Traders => _order;

    /// <summary>The settings (<c>economy-reset-time-hours</c> …) as written, in file order.</summary>
    public IReadOnlyList<(string Key, string Value)> Settings => _settings.Select(s => (s.Key, s.Value?.ToString() ?? string.Empty)).ToList();

    /// <summary>A file with the game's default settings and no trader section.</summary>
    public static EconomyOverride CreateDefault() => new(GameDefaults.Select(d => (d.Key, (JsonNode?)JsonValue.Create(d.Value))).ToList());

    /// <summary>Reads the text of an <c>EconomyOverride.json</c>.</summary>
    /// <exception cref="JsonException">Not JSON, or no <c>economy-override</c> object.</exception>
    public static EconomyOverride Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?[RootKey] as JsonObject
            ?? throw new JsonException($"No \"{RootKey}\" object.");
        var settings = new List<(string, JsonNode?)>();
        JsonObject? traders = null;
        foreach (var (key, value) in root)
        {
            if (key == TradersKey && value is JsonObject t)
            {
                traders = t;
            }
            else
            {
                settings.Add((key, value?.DeepClone()));
            }
        }

        var result = new EconomyOverride(settings);
        foreach (var (name, section) in traders ?? new JsonObject())
        {
            var entries = result.Section(name, create: true);
            foreach (var item in section as JsonArray ?? new JsonArray())
            {
                if (item is JsonObject o && Text(o, "tradeable-code") is { Length: > 0 } code)
                {
                    entries.Add(new TradeableOverride(code)
                    {
                        PurchasePrice = Int(o, "base-purchase-price"),
                        SellPrice = Int(o, "base-sell-price"),
                        DeltaPrice = Float(o, "delta-price"),
                        CanBePurchased = Bool(o, "can-be-purchased"),
                        RequiredFame = Int(o, "required-famepoints"),
                        AfterSaleOnly = Bool(o, "available-after-sale-only"),
                    });
                }
            }
        }

        return result;
    }

    /// <summary>The file as the game writes it (tab-indented; every value a string).</summary>
    /// <remarks>
    /// Written by hand: a placed trader's section lists its whole stock and the file is saved on every edit, where building
    /// a JSON tree of a few thousand entries first took tens of milliseconds a keystroke.
    /// </remarks>
    public string ToJson()
    {
        var sb = new StringBuilder(1024 + (_traders.Values.Sum(t => t.Count) * 340));
        sb.Append("{\r\n\t").Append(Quote(RootKey)).Append(": {");
        var first = true;
        foreach (var (key, value) in _settings)
        {
            Next(sb, ref first, 2).Append(Quote(key)).Append(": ").Append(Node(value, 2));
        }

        Next(sb, ref first, 2).Append(Quote(TradersKey)).Append(": ");
        if (_order.Count == 0)
        {
            sb.Append("{}");
        }
        else
        {
            sb.Append('{');
            var firstTrader = true;
            foreach (var name in _order)
            {
                Next(sb, ref firstTrader, 3).Append(Quote(name)).Append(": ");
                var entries = _traders[name];
                if (entries.Count == 0)
                {
                    sb.Append("[]");
                    continue;
                }

                sb.Append('[');
                var firstEntry = true;
                foreach (var entry in entries)
                {
                    Next(sb, ref firstEntry, 4).Append('{');
                    var firstValue = true;
                    Value(sb, ref firstValue, "tradeable-code", entry.Code);
                    Value(sb, ref firstValue, "base-purchase-price", Text(entry.PurchasePrice));
                    Value(sb, ref firstValue, "base-sell-price", Text(entry.SellPrice));
                    Value(sb, ref firstValue, "delta-price", entry.DeltaPrice is { } d ? d.ToString("0.0###", CultureInfo.InvariantCulture) : "-1.0");
                    Value(sb, ref firstValue, "can-be-purchased", Text(entry.CanBePurchased));
                    Value(sb, ref firstValue, "required-famepoints", Text(entry.RequiredFame));
                    Value(sb, ref firstValue, "available-after-sale-only", Text(entry.AfterSaleOnly));
                    sb.Append("\r\n").Append('\t', 4).Append('}');
                }

                sb.Append("\r\n").Append('\t', 3).Append(']');
            }

            sb.Append("\r\n").Append('\t', 2).Append('}');
        }

        return sb.Append("\r\n\t}\r\n}\r\n").ToString();
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Starts the next member of an object or array at <paramref name="depth"/> tabs.</summary>
    private static StringBuilder Next(StringBuilder sb, ref bool first, int depth)
    {
        if (!first)
        {
            sb.Append(',');
        }

        first = false;
        return sb.Append("\r\n").Append('\t', depth);
    }

    private static void Value(StringBuilder sb, ref bool first, string key, string value) =>
        Next(sb, ref first, 5).Append(Quote(key)).Append(": ").Append(Quote(value));

    /// <summary>A JSON string; plain ASCII (every code and number) without the serializer.</summary>
    private static string Quote(string text) =>
        text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0 && text.AsSpan().IndexOfAny('"', '\\') < 0 ? "\"" + text + "\"" : JsonSerializer.Serialize(text, WriteOptions);

    /// <summary>A setting's value as written at <paramref name="depth"/> (a string almost always; anything else re-indented with tabs).</summary>
    private static string Node(JsonNode? value, int depth)
    {
        var text = value?.ToJsonString(WriteOptions) ?? "null";
        if (!text.Contains('\n', StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Select((l, i) => i == 0 ? l : new string('\t', depth + ((l.Length - l.TrimStart(' ').Length) / 2)) + l.TrimStart(' '));
        return string.Join("\r\n", lines);
    }

    /// <summary>True when <paramref name="trader"/> has a section.</summary>
    public bool HasSection(string trader) => _traders.ContainsKey(trader);

    /// <summary>A trader's entries (empty when it has no section).</summary>
    public IReadOnlyList<TradeableOverride> Entries(string trader) =>
        _traders.TryGetValue(trader, out var list) ? list : [];

    /// <summary>The entry for <paramref name="code"/> at <paramref name="trader"/>, or null.</summary>
    public TradeableOverride? Find(string trader, string code) =>
        _traders.TryGetValue(trader, out var list) ? list.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Adds an empty section for <paramref name="trader"/> (the game's "no change" for that trader) when it has none.</summary>
    public void EnsureSection(string trader) => Section(trader, create: true);

    /// <summary>
    /// Sets (or, when it changes nothing, removes) the entry of <paramref name="value"/>'s tradeable at <paramref name="trader"/>.
    /// With <paramref name="keep"/> an entry that changes nothing stays listed with the game's values (<c>-1</c> /
    /// <c>default</c>): a placed trader's section lists its whole stock that way.
    /// </summary>
    public void Set(string trader, TradeableOverride value, bool keep = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var list = Section(trader, create: true);
        var index = list.FindIndex(e => string.Equals(e.Code, value.Code, StringComparison.OrdinalIgnoreCase));
        if (value.IsDefault && !keep)
        {
            if (index >= 0)
            {
                list.RemoveAt(index);
            }
        }
        else if (index >= 0)
        {
            list[index] = value;
        }
        else
        {
            list.Add(value);
        }
    }

    /// <summary>Removes every entry of <paramref name="trader"/> (its section stays, empty).</summary>
    public void Clear(string trader) => Section(trader, create: true).Clear();

    /// <summary>Removes the entry of <paramref name="code"/> at <paramref name="trader"/> (an item added to it leaves it); true when there was one.</summary>
    public bool Remove(string trader, string code) =>
        _traders.TryGetValue(trader, out var list) && list.RemoveAll(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// Lists every one of <paramref name="codes"/> that has no entry at <paramref name="trader"/> with the game's values
    /// (<c>-1</c> / <c>default</c>, as the game writes its own sample entries: the server keeps the game's price); returns
    /// how many were added.
    /// </summary>
    public int List(string trader, IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var list = Section(trader, create: true);
        var known = list.Select(e => e.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var code in codes)
        {
            if (known.Add(code))
            {
                list.Add(new TradeableOverride(code));
                added++;
            }
        }

        return added;
    }

    /// <summary>Removes <paramref name="trader"/>'s section; returns its entries, or null when it had none.</summary>
    public IReadOnlyList<TradeableOverride>? RemoveSection(string trader)
    {
        if (!_traders.Remove(trader, out var list))
        {
            return null;
        }

        _order.RemoveAll(t => string.Equals(t, trader, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Gives <paramref name="trader"/> a section holding exactly <paramref name="entries"/> (a removed section put back).</summary>
    public void SetSection(string trader, IEnumerable<TradeableOverride> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = Section(trader, create: true);
        list.Clear();
        list.AddRange(entries);
    }

    /// <summary>A copy without the sections of <paramref name="traders"/> (traders the project removed from the island).</summary>
    public EconomyOverride Without(IEnumerable<string> traders)
    {
        ArgumentNullException.ThrowIfNull(traders);
        var copy = Clone();
        foreach (var trader in traders)
        {
            copy.RemoveSection(trader);
        }

        return copy;
    }

    /// <summary>A copy (edits on the copy leave this one alone).</summary>
    public EconomyOverride Clone() => Parse(ToJson());

    /// <summary>
    /// The project's economy (<c>&lt;project&gt;/EconomyOverride.json</c>), or null when the project has none yet.
    /// </summary>
    /// <exception cref="JsonException">The file is not an economy file.</exception>
    public static EconomyOverride? LoadFrom(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Writes the file into <paramref name="directory"/> (created when missing); returns its path. The text goes to a
    /// temporary file first that then replaces the old one, so a crash mid-write never leaves half a file.
    /// </summary>
    /// <param name="directory">Folder to write into.</param>
    /// <param name="withNote">True next to an exported pak: also writes <see cref="ReadmeName"/> (where the file goes).</param>
    public string SaveTo(string directory, bool withNote = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        if (withNote)
        {
            File.WriteAllText(Path.Combine(directory, ReadmeName), Readme, new UTF8Encoding(true)); // the owner: "a txt that says where it goes"
        }

        return path;
    }

    /// <summary>The note written next to the file: where it goes for a server and for single player.</summary>
    public const string ReadmeName = "EconomyOverride - where it goes.txt";

    private const string Readme = """
        EconomyOverride.json - where it goes
        ====================================

        Dedicated server (online)
          1. Stop the server.
          2. Copy EconomyOverride.json to   <server folder>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json
          3. Put the Server mod pak (and its .sig) in   <server folder>\SCUM\Content\Paks\~mods   and start the server
             with -fileopenlog (as for every mod pak). Placed traders are server objects: without the server pak nobody sees them.
          4. Start the server. It reads the file when it starts.

        Single player / sandbox (your own PC)
          1. Close the game.
          2. Copy EconomyOverride.json to   %LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\EconomyOverride.json
             (paste that path into the Explorer address bar to open the folder).
          3. Put the Client mod pak (and its .sig) in the game's   SCUM\Content\Paks\~mods   folder and start the game.

        Each section is one trader by name (A_0_Armory, B_4_Hospital ...); traders you placed carry the names you gave them.
        "-1" or "default" keeps the game's own value. Players need the Client mod pak too, so their game knows the new traders.

        ------------------------------------------------------------

        EconomyOverride.json - مكان الملف
        ====================================

        السيرفر (أونلاين)
          1. أطفئ السيرفر.
          2. انسخ EconomyOverride.json إلى   <مجلد السيرفر>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json
          3. ضع باك السيرفر (مع ملف .sig) في   <مجلد السيرفر>\SCUM\Content\Paks\~mods   وشغّل السيرفر مع -fileopenlog.
             التجّار الذين وضعتهم من كائنات السيرفر: بدون باك السيرفر لا يراهم أحد.
          4. شغّل السيرفر؛ يقرأ الملف عند التشغيل.

        اللعب الفردي / الساندبوكس (على جهازك)
          1. أغلق اللعبة.
          2. انسخ EconomyOverride.json إلى   %LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\EconomyOverride.json
             (الصق هذا المسار في شريط عنوان مستكشف الملفات لفتح المجلد).
          3. ضع باك الكلاينت (مع ملف .sig) في مجلد اللعبة   SCUM\Content\Paks\~mods   وشغّل اللعبة.

        كل قسم في الملف تاجر باسمه (A_0_Armory و B_4_Hospital ...)؛ التجّار الذين وضعتهم بأسمائهم التي اخترتها.
        القيمة "-1" أو "default" تعني قيمة اللعبة نفسها. اللاعبون يحتاجون باك الكلاينت أيضاً حتى تعرف لعبتهم التجّار الجدد.
        """;

    private List<TradeableOverride> Section(string trader, bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trader);
        if (!_traders.TryGetValue(trader, out var list))
        {
            list = [];
            if (create)
            {
                _traders[trader] = list;
                _order.Add(trader);
            }
        }

        return list;
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    private static int? Int(JsonObject o, string key) =>
        Text(o, key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? (int)Math.Round(d) : null;

    private static float? Float(JsonObject o, string key) =>
        Text(o, key) is { } s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f != -1f ? f : null;

    private static bool? Bool(JsonObject o, string key) => Text(o, key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static string Text(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-1";

    private static string Text(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "default",
    };
}
