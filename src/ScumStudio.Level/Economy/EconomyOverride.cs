using System.Globalization;
using System.Text;
using System.Text.Json;
using ScumStudio.Level.Editing;

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
/// with every value a string. Nothing read is lost (owner #25: "export writes it back unchanged where not edited"): every
/// key and value is kept as its raw JSON text, unknown keys and odd values (<c>12,000</c>, <c>yes</c>) too, an edit
/// rewrites only the values it changes, and <see cref="ToJson()"/> writes the game's own layout byte for byte.
/// </summary>
public sealed class EconomyOverride
{
    /// <summary>The file name the game reads.</summary>
    public const string FileName = "EconomyOverride.json";

    private const string RootKey = "economy-override";
    private const string TradersKey = "traders";
    private const string CodeKey = "tradeable-code";
    private const string PurchaseKey = "base-purchase-price";
    private const string SellKey = "base-sell-price";
    private const string DeltaKey = "delta-price";
    private const string CanBuyKey = "can-be-purchased";
    private const string FameKey = "required-famepoints";
    private const string AfterSaleKey = "available-after-sale-only";

    /// <summary>The keys of an entry, in the order the game writes them (it knows no other: there is no per-item stock).</summary>
    public static IReadOnlyList<string> EntryKeys { get; } = [CodeKey, PurchaseKey, SellKey, DeltaKey, CanBuyKey, FameKey, AfterSaleKey];

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

    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private List<Member> _settings = [];
    private List<Section> _sections = [];
    private List<Member> _rootMembers = [];
    private int _rootAt;
    private int _tradersAt;
    private string? _tradersRaw;

    private EconomyOverride()
    {
    }

    /// <summary>The traders with a section, in file order.</summary>
    public IReadOnlyList<string> Traders => _sections.Select(s => s.Name).ToList();

    /// <summary>The settings (<c>economy-reset-time-hours</c> …) as written, in file order (a string without its quotes).</summary>
    public IReadOnlyList<(string Key, string Value)> Settings => _settings.Select(s => (s.Key, Unquote(s.Raw))).ToList();

    /// <summary>The settings the game writes, in its order.</summary>
    public static IReadOnlyList<string> SettingKeys { get; } = GameDefaults.Select(d => d.Key).ToArray();

    /// <summary>The game's value of a setting (<c>48.0</c>), or null for a key the game does not write.</summary>
    public static string? GameDefault(string settingKey) =>
        GameDefaults.FirstOrDefault(d => string.Equals(d.Key, settingKey, StringComparison.Ordinal)).Value;

    /// <summary>A file with the game's default settings and no trader section.</summary>
    public static EconomyOverride CreateDefault() => new()
    {
        _settings = GameDefaults.Select(d => new Member(d.Key, Quote(d.Key), Quote(d.Value))).ToList(),
        _tradersAt = GameDefaults.Length,
    };

    /// <summary>
    /// Reads the text of an <c>EconomyOverride.json</c>, keeping everything as written. Tolerant: a key twice (the first
    /// <c>traders</c> wins, sections twice are merged in file order), a section without a name, a <c>traders</c> that is not
    /// an object, comments and trailing commas are read (<see cref="EconomyValidator"/> reports them); the settings and
    /// <c>traders</c> without the <c>economy-override</c> wrapper (an older list pasted on its own) are read as the whole file.
    /// </summary>
    /// <exception cref="JsonException">Not JSON, not an object, or neither <c>economy-override</c> nor its members.</exception>
    public static EconomyOverride Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The file is not a JSON object.");
        }

        var result = new EconomyOverride { _tradersAt = -1 };
        var found = false;
        var bare = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var (key, rawKey) = Name(ref reader, bytes);
            reader.Read();
            if (!found && key == RootKey && reader.TokenType == JsonTokenType.StartObject)
            {
                found = true;
                result._rootAt = result._rootMembers.Count;
                result.ReadInner(ref reader, bytes);
            }
            else
            {
                bare |= key == TradersKey || GameDefault(key) is not null;
                result._rootMembers.Add(new Member(key, rawKey, Raw(ref reader, bytes)));
            }
        }

        if (reader.Read())
        {
            throw new JsonException("The file goes on after its last brace.");
        }

        if (!found)
        {
            if (!bare)
            {
                throw new JsonException($"No \"{RootKey}\" object.");
            }

            result = new EconomyOverride { _tradersAt = -1 };
            reader = new Utf8JsonReader(bytes, ReaderOptions);
            reader.Read();
            result.ReadInner(ref reader, bytes);
        }

        if (result._tradersAt < 0)
        {
            result._tradersAt = result._settings.Count;
        }

        return result;
    }

    private void ReadInner(ref Utf8JsonReader reader, byte[] bytes)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var (key, rawKey) = Name(ref reader, bytes);
            reader.Read();
            if (key != TradersKey)
            {
                _settings.Add(new Member(key, rawKey, Raw(ref reader, bytes)));
                continue;
            }

            if (_tradersAt < 0)
            {
                _tradersAt = _settings.Count;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                var raw = Raw(ref reader, bytes);
                _tradersRaw ??= _sections.Count == 0 ? raw : null;
                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var (name, rawName) = Name(ref reader, bytes);
                reader.Read();
                var section = _sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (section is null)
                {
                    _sections.Add(section = new Section(name, rawName));
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    var raw = Raw(ref reader, bytes);
                    section.Raw ??= section.Entries.Count == 0 ? raw : null;
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    section.Entries.Add(ReadEntry(ref reader, bytes));
                    section.Raw = null;
                }
            }

            _tradersRaw = null;
        }
    }

    private static Entry ReadEntry(ref Utf8JsonReader reader, byte[] bytes)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return new Entry([], Raw(ref reader, bytes));
        }

        var members = new List<Member>(7);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var (key, rawKey) = Name(ref reader, bytes);
            reader.Read();
            members.Add(new Member(key, rawKey, Raw(ref reader, bytes)));
        }

        return new Entry([.. members], null);
    }

    /// <summary>A member's name and its text as written (escapes kept).</summary>
    private static (string Key, string RawKey) Name(ref Utf8JsonReader reader, byte[] bytes)
    {
        var key = reader.GetString()!;
        return (key, reader.ValueIsEscaped ? "\"" + Encoding.UTF8.GetString(bytes, (int)reader.TokenStartIndex + 1, reader.ValueSpan.Length) + "\"" : Quote(key));
    }

    /// <summary>The value at the reader as written (an object or array whole); the reader ends on its last token.</summary>
    private static string Raw(ref Utf8JsonReader reader, byte[] bytes)
    {
        var start = (int)reader.TokenStartIndex;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return Encoding.UTF8.GetString(bytes, start, (int)reader.BytesConsumed - start);
    }

    /// <summary>The file in the game's own layout (CRLF, tabs, an object's brace on a line of its own, no newline at the end).</summary>
    /// <remarks>
    /// Written by hand: a placed trader's section lists its whole stock and the file is saved on every edit, where building
    /// a JSON tree of a few thousand entries first took tens of milliseconds a keystroke.
    /// </remarks>
    public string ToJson() => ToJson(null);

    /// <summary>The file as <see cref="ToJson()"/> writes it, without the sections of <paramref name="skipTraders"/> (no copy made).</summary>
    public string ToJson(IReadOnlySet<string>? skipTraders)
    {
        var sb = new StringBuilder(1024 + (_sections.Sum(s => s.Entries.Count) * 340));
        sb.Append('{');
        var first = true;
        for (var i = 0; i <= _rootMembers.Count; i++)
        {
            if (i == _rootAt)
            {
                Next(sb, ref first, 1).Append(Quote(RootKey)).Append(":\r\n\t{");
                WriteInner(sb, skipTraders);
                sb.Append("\r\n\t}");
            }

            if (i < _rootMembers.Count)
            {
                Next(sb, ref first, 1).Append(_rootMembers[i].RawKey).Append(": ").Append(_rootMembers[i].Raw);
            }
        }

        return sb.Append("\r\n}").ToString();
    }

    private void WriteInner(StringBuilder sb, IReadOnlySet<string>? skip)
    {
        var first = true;
        for (var i = 0; i <= _settings.Count; i++)
        {
            if (i == _tradersAt)
            {
                Next(sb, ref first, 2).Append(Quote(TradersKey));
                if (_tradersRaw is not null && _sections.Count == 0)
                {
                    sb.Append(": ").Append(_tradersRaw);
                }
                else
                {
                    sb.Append(":\r\n\t\t{");
                    var firstSection = true;
                    foreach (var section in _sections)
                    {
                        if (skip is null || !skip.Contains(section.Name))
                        {
                            Next(sb, ref firstSection, 3).Append(section.RawName).Append(": ");
                            WriteSection(sb, section);
                        }
                    }

                    sb.Append("\r\n\t\t}");
                }
            }

            if (i < _settings.Count)
            {
                Next(sb, ref first, 2).Append(_settings[i].RawKey).Append(": ").Append(_settings[i].Raw);
            }
        }
    }

    private static void WriteSection(StringBuilder sb, Section section)
    {
        if (section.Raw is not null || section.Entries.Count == 0)
        {
            sb.Append(section.Raw ?? "[]");
            return;
        }

        sb.Append('[');
        var first = true;
        foreach (var entry in section.Entries)
        {
            Next(sb, ref first, 4);
            if (entry.Raw is not null)
            {
                sb.Append(entry.Raw);
                continue;
            }

            sb.Append('{');
            var firstValue = true;
            foreach (var member in entry.Members)
            {
                Next(sb, ref firstValue, 5).Append(member.RawKey).Append(": ").Append(member.Raw);
            }

            sb.Append("\r\n").Append('\t', 4).Append('}');
        }

        sb.Append("\r\n").Append('\t', 3).Append(']');
    }

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

    /// <summary>A JSON string as the file writes it (<c>"48.0"</c>); plain ASCII (every code and number) without the serializer.</summary>
    public static string Quote(string text) =>
        text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0 && text.AsSpan().IndexOfAny('"', '\\') < 0 ? "\"" + text + "\"" : JsonSerializer.Serialize(text, WriteOptions);

    /// <summary>A raw JSON value as text: a string without its quotes (escapes read), anything else as written.</summary>
    internal static string Unquote(string raw)
    {
        if (raw.Length < 2 || raw[0] != '"')
        {
            return raw;
        }

        return raw.AsSpan(1, raw.Length - 2).IndexOf('\\') < 0 ? raw[1..^1] : JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
    }

    /// <summary>True when <paramref name="trader"/> has a section.</summary>
    public bool HasSection(string trader) => IndexOf(trader) >= 0;

    /// <summary>Where <paramref name="trader"/>'s section is among the traders, or -1.</summary>
    public int IndexOf(string trader) => _sections.FindIndex(s => string.Equals(s.Name, trader, StringComparison.OrdinalIgnoreCase));

    /// <summary>A trader's entries (empty when it has no section); an entry without a code is kept in the file but not listed.</summary>
    public IReadOnlyList<TradeableOverride> Entries(string trader) =>
        IndexOf(trader) is var i and >= 0 ? _sections[i].Entries.Where(e => e.Code is not null).Select(e => e.Typed!).ToList() : [];

    /// <summary>The entry for <paramref name="code"/> at <paramref name="trader"/> (the first when the file lists it twice), or null.</summary>
    public TradeableOverride? Find(string trader, string code) =>
        IndexOf(trader) is var i and >= 0 ? _sections[i].Entries.FirstOrDefault(e => e.Is(code))?.Typed : null;

    /// <summary>
    /// A value of the entry for <paramref name="code"/> at <paramref name="trader"/> as the file writes it (a string without
    /// its quotes: <c>12,000</c>, <c>yes</c>), or null when the entry or the key is not there.
    /// </summary>
    public string? RawValue(string trader, string code, string key) =>
        IndexOf(trader) is var i and >= 0 && _sections[i].Entries.FirstOrDefault(e => e.Is(code)) is { } entry ? entry.Value(key) : null;

    /// <summary>A setting's value as written (the first when the file has it twice), or null when the file does not set it.</summary>
    public string? Setting(string key) => _settings.FirstOrDefault(s => s.Key == key) is { Raw: not null } s ? Unquote(s.Raw) : null;

    /// <summary>Adds an empty section for <paramref name="trader"/> (the game's "no change" for that trader) when it has none.</summary>
    public void EnsureSection(string trader) => GetSection(trader, create: true);

    /// <summary>
    /// Sets (or, when it changes nothing, removes) the entry of <paramref name="value"/>'s tradeable at <paramref name="trader"/>;
    /// a code the file lists twice changes in both places. Only the values that change are rewritten: an unknown key or an
    /// odd value of the same entry stays as written. With <paramref name="keep"/> an entry that changes nothing stays listed
    /// with the game's values (<c>-1</c> / <c>default</c>): a placed trader's section lists its whole stock that way.
    /// </summary>
    public void Set(string trader, TradeableOverride value, bool keep = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        GetSection(trader, create: true);
        foreach (var op in SetOps(trader, value.Code, value, keep))
        {
            Apply(op);
        }
    }

    /// <summary>
    /// The journal steps that make <paramref name="code"/>'s entries at <paramref name="trader"/> <paramref name="value"/>
    /// (null or, without <paramref name="keep"/>, a value that changes nothing: removed); empty when nothing changes.
    /// Applied in order (<see cref="Apply"/>) they do what <see cref="Set"/> does.
    /// </summary>
    public IReadOnlyList<EconomyOp> SetOps(string trader, string code, TradeableOverride? value, bool keep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trader);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var remove = value is null || (value.IsDefault && !keep);
        var index = IndexOf(trader);
        if (index < 0)
        {
            return remove ? [] : [new SetEconomySectionOp(trader, _sections.Count, null, "[" + Entry.From(value!).Text + "]")];
        }

        var section = _sections[index];
        var hits = Enumerable.Range(0, section.Entries.Count).Where(i => section.Entries[i].Is(code)).ToList();
        if (remove)
        {
            // A value that changes nothing removes an entry only when nothing else is left in it: a value the app cannot
            // read ("12,000", "yes") or a key of its own stays as written, and only the changed values go back to the game's.
            var steps = new List<EconomyOp>();
            foreach (var i in hits.AsEnumerable().Reverse())
            {
                var entry = section.Entries[i];
                var next = value is null ? null : entry.With(value);
                if (next is null || next.HoldsOnlyGameValues)
                {
                    steps.Add(new SetEconomyEntryOp(section.Name, entry.Code!, i, entry.Text, null));
                }
                else if (!ReferenceEquals(next, entry))
                {
                    steps.Add(new SetEconomyEntryOp(section.Name, entry.Code!, i, entry.Text, next.Text));
                }
            }

            return steps;
        }

        if (hits.Count == 0)
        {
            var added = Entry.From(value!).Text;
            return section.Raw is not null
                ? [new SetEconomySectionOp(section.Name, index, section.Raw, "[" + added + "]")]
                : [new SetEconomyEntryOp(section.Name, value!.Code, section.Entries.Count, null, added)];
        }

        var ops = new List<EconomyOp>();
        foreach (var i in hits)
        {
            var entry = section.Entries[i];
            if (entry.With(value!) is var changed && !ReferenceEquals(changed, entry))
            {
                ops.Add(new SetEconomyEntryOp(section.Name, entry.Code!, i, entry.Text, changed.Text));
            }
        }

        return ops;
    }

    /// <summary>
    /// The journal steps that set <paramref name="key"/> (every place the file has it) to the raw JSON value
    /// <paramref name="raw"/>, or remove it when null; a new key goes after the last setting.
    /// </summary>
    public IReadOnlyList<EconomyOp> SettingOps(string key, string? raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hits = Enumerable.Range(0, _settings.Count).Where(i => _settings[i].Key == key).ToList();
        if (hits.Count == 0)
        {
            return raw is null ? [] : [new SetEconomySettingOp(key, _settings.Count, null, raw)];
        }

        return raw is null
            ? hits.AsEnumerable().Reverse().Select(i => (EconomyOp)new SetEconomySettingOp(key, i, _settings[i].Raw, null)).ToList()
            : hits.Where(i => _settings[i].Raw != raw).Select(i => (EconomyOp)new SetEconomySettingOp(key, i, _settings[i].Raw, raw)).ToList();
    }

    /// <summary>A section as a JSON array (as a <see cref="SetEconomySectionOp"/> carries it), or null when there is none.</summary>
    public string? SectionText(string trader) => IndexOf(trader) is var i and >= 0 ? _sections[i].Text : null;

    /// <summary>The JSON array of a placed trader's stock at the game's values (<see cref="List"/>).</summary>
    public static string StockText(IEnumerable<string> codes) =>
        "[" + string.Join(",", codes.Distinct(StringComparer.OrdinalIgnoreCase).Select(c => Entry.From(new TradeableOverride(c)).Text)) + "]";

    /// <summary>
    /// Applies a journal step (an undo applies its <see cref="EditOp.Inverse"/>). The entry, setting or section is found at
    /// the op's index, or else by its old text or its name; returns false when what is there is not the op's old text (the
    /// step is applied anyway).
    /// </summary>
    public bool Apply(EconomyOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        switch (op)
        {
            case SetEconomyEntryOp e:
                return ApplyEntry(e);
            case SetEconomySettingOp s:
                return ApplySetting(s);
            case SetEconomySectionOp s:
                return ApplySection(s);
            case ReplaceEconomyOp r:
                var exact = r.Old is null || r.Old == ToJson();
                var next = r.New is null ? CreateDefault() : Parse(r.New);
                (_settings, _sections, _rootMembers, _rootAt, _tradersAt, _tradersRaw) = (next._settings, next._sections, next._rootMembers, next._rootAt, next._tradersAt, next._tradersRaw);
                return exact;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.GetType().Name, "Not an economy step.");
        }
    }

    private bool ApplyEntry(SetEconomyEntryOp op)
    {
        var index = IndexOf(op.Trader);
        if (index < 0)
        {
            if (op.New is null)
            {
                return false;
            }

            _sections.Add(new Section(op.Trader, Quote(op.Trader)));
            index = _sections.Count - 1;
        }

        var section = _sections[index];
        var list = section.Entries;
        var exact = true;
        var at = op.Old is null ? -1 : Locate(list.Count, op.Index, i => list[i].Text == op.Old, i => list[i].Is(op.Code), out exact);
        if (at < 0)
        {
            if (op.New is not null)
            {
                list.Insert(Math.Clamp(op.Index, 0, list.Count), Entry.Parse(op.New));
                section.Raw = null;
            }

            return op.Old is null;
        }

        if (op.New is null)
        {
            list.RemoveAt(at);
        }
        else
        {
            list[at] = Entry.Parse(op.New);
        }

        return exact;
    }

    private bool ApplySetting(SetEconomySettingOp op)
    {
        var exact = true;
        var at = op.Old is null ? -1 : Locate(_settings.Count, op.Index, i => _settings[i].Key == op.Key && _settings[i].Raw == op.Old, i => _settings[i].Key == op.Key, out exact);
        if (at < 0)
        {
            if (op.New is not null)
            {
                var insert = Math.Clamp(op.Index, 0, _settings.Count);
                _settings.Insert(insert, new Member(op.Key, Quote(op.Key), op.New));
                _tradersAt += insert <= _tradersAt ? 1 : 0;
            }

            return op.Old is null;
        }

        if (op.New is null)
        {
            _settings.RemoveAt(at);
            _tradersAt -= at < _tradersAt ? 1 : 0;
        }
        else
        {
            _settings[at] = _settings[at] with { Raw = op.New };
        }

        return exact;
    }

    private bool ApplySection(SetEconomySectionOp op)
    {
        var at = IndexOf(op.Trader);
        var exact = op.Old is null ? at < 0 : at >= 0 && _sections[at].Text == op.Old;
        if (op.New is null)
        {
            if (at >= 0)
            {
                _sections.RemoveAt(at);
            }

            return exact;
        }

        var section = Section.Parse(at >= 0 ? _sections[at].Name : op.Trader, at >= 0 ? _sections[at].RawName : Quote(op.Trader), op.New);
        if (at >= 0)
        {
            _sections[at] = section;
        }
        else
        {
            _sections.Insert(Math.Clamp(op.Index, 0, _sections.Count), section);
            _tradersRaw = null;
        }

        return exact;
    }

    /// <summary>The index to change: <paramref name="index"/> when it holds the old text, else the first that does, else by name.</summary>
    private static int Locate(int count, int index, Func<int, bool> same, Func<int, bool> named, out bool exact)
    {
        exact = true;
        if (index >= 0 && index < count && same(index))
        {
            return index;
        }

        for (var i = 0; i < count; i++)
        {
            if (same(i))
            {
                return i;
            }
        }

        exact = false;
        if (index >= 0 && index < count && named(index))
        {
            return index;
        }

        for (var i = 0; i < count; i++)
        {
            if (named(i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A short summary of what changed between two entries (<c>price 15000</c>), for the history list.</summary>
    internal static string DescribeChange(string oldText, string newText)
    {
        var (before, after) = (Entry.Parse(oldText), Entry.Parse(newText));
        var changes = EntryKeys.Skip(1).Where(k => before.Value(k) != after.Value(k)).Select(k => $"{Label(k)} {after.Value(k) ?? "(none)"}").ToList();
        return changes.Count == 0 ? "(same values)" : string.Join(", ", changes);

        static string Label(string key) => key switch
        {
            PurchaseKey => "price",
            SellKey => "sell price",
            DeltaKey => "delta",
            CanBuyKey => "on sale",
            FameKey => "fame",
            AfterSaleKey => "after sale only",
            _ => key,
        };
    }

    /// <summary>Removes every entry of <paramref name="trader"/> (its section stays, empty).</summary>
    public void Clear(string trader)
    {
        var section = GetSection(trader, create: true);
        section.Entries.Clear();
        section.Raw = null;
    }

    /// <summary>Removes the entries of <paramref name="code"/> at <paramref name="trader"/> (an item added to it leaves it); true when there was one.</summary>
    public bool Remove(string trader, string code) =>
        IndexOf(trader) is var i and >= 0 && _sections[i].Entries.RemoveAll(e => e.Is(code)) > 0;

    /// <summary>
    /// Lists every one of <paramref name="codes"/> that has no entry at <paramref name="trader"/> with the game's values
    /// (<c>-1</c> / <c>default</c>, as the game writes its own sample entries: the server keeps the game's price); returns
    /// how many were added.
    /// </summary>
    public int List(string trader, IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var section = GetSection(trader, create: true);
        var known = section.Entries.Where(e => e.Code is not null).Select(e => e.Code!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var code in codes)
        {
            if (known.Add(code))
            {
                section.Entries.Add(Entry.From(new TradeableOverride(code)));
                section.Raw = null;
                added++;
            }
        }

        return added;
    }

    /// <summary>Removes <paramref name="trader"/>'s section; returns its entries, or null when it had none.</summary>
    public IReadOnlyList<TradeableOverride>? RemoveSection(string trader)
    {
        var i = IndexOf(trader);
        if (i < 0)
        {
            return null;
        }

        var entries = _sections[i].Entries.Where(e => e.Code is not null).Select(e => e.Typed!).ToList();
        _sections.RemoveAt(i);
        return entries;
    }

    /// <summary>Gives <paramref name="trader"/> a section holding exactly <paramref name="entries"/> (a removed section put back).</summary>
    public void SetSection(string trader, IEnumerable<TradeableOverride> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var section = GetSection(trader, create: true);
        section.Entries.Clear();
        section.Entries.AddRange(entries.Select(Entry.From));
        section.Raw = null;
    }

    /// <summary>A copy without the sections of <paramref name="traders"/> (traders the project removed from the island).</summary>
    public EconomyOverride Without(IEnumerable<string> traders)
    {
        ArgumentNullException.ThrowIfNull(traders);
        var copy = Clone();
        var gone = traders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        copy._sections.RemoveAll(s => gone.Contains(s.Name));
        return copy;
    }

    /// <summary>A copy (edits on the copy leave this one alone; the entries, which never change, are shared).</summary>
    public EconomyOverride Clone() => new()
    {
        _settings = [.. _settings],
        _sections = _sections.Select(s => s.Copy()).ToList(),
        _rootMembers = [.. _rootMembers],
        _rootAt = _rootAt,
        _tradersAt = _tradersAt,
        _tradersRaw = _tradersRaw,
    };

    /// <summary>
    /// Puts back the sections of <paramref name="names"/> that <paramref name="from"/> has and this one lacks, each after the
    /// section it follows there (a code edit of the page's text, which does not show the hidden traders); returns how many.
    /// </summary>
    public int KeepSections(EconomyOverride from, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(names);
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (at, kept) = (0, 0);
        foreach (var section in from._sections)
        {
            if (IndexOf(section.Name) is var i and >= 0)
            {
                at = i + 1;
            }
            else if (wanted.Contains(section.Name))
            {
                _sections.Insert(at++, section.Copy());
                _tradersRaw = null;
                kept++;
            }
        }

        return kept;
    }

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
    public string SaveTo(string directory, bool withNote = false) => Write(directory, ToJson(), withNote);

    /// <summary>Writes <paramref name="text"/> as the file into <paramref name="directory"/> (see <see cref="SaveTo"/>); returns its path.</summary>
    public static string Write(string directory, string text, bool withNote = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
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

        This file REPLACES the server's own EconomyOverride.json: every price, setting and trader section the server's file
        has and this one does not is gone once it is copied over. Back up the server's file first, or bring it into the
        project before you export (Economy page, Import), so this file carries the server's economy with your changes.

        Dedicated server (online)
          1. Stop the server.
          2. Back up   <server folder>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json   (or Import it into the project).
          3. Copy EconomyOverride.json to   <server folder>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json
          4. Put the Server mod pak (and its .sig) in   <server folder>\SCUM\Content\Paks\~mods   and start the server
             with -fileopenlog (as for every mod pak). Placed traders are server objects: without the server pak nobody sees them.
          5. Start the server. It reads the file when it starts.

        Single player / sandbox (your own PC)
          1. Close the game.
          2. Back up the old file, then copy EconomyOverride.json to   %LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\EconomyOverride.json
             (paste that path into the Explorer address bar to open the folder).
          3. Put the Client mod pak (and its .sig) in the game's   SCUM\Content\Paks\~mods   folder and start the game.

        Each section is one trader by name (A_0_Armory, B_4_Hospital ...); traders you placed carry the names you gave them.
        "-1" or "default" keeps the game's own value. Players need the Client mod pak too, so their game knows the new traders.

        ------------------------------------------------------------

        EconomyOverride.json - مكان الملف
        ====================================

        هذا الملف يستبدل ملف EconomyOverride.json الموجود في السيرفر: كل سعر أو إعداد أو قسم تاجر موجود في ملف السيرفر وغير
        موجود هنا يضيع عند النسخ فوقه. خذ نسخة احتياطية من ملف السيرفر أولاً، أو استورده إلى المشروع قبل التصدير (صفحة
        الاقتصاد، استيراد / Import) حتى يحمل هذا الملف اقتصاد السيرفر مع تعديلاتك.

        السيرفر (أونلاين)
          1. أطفئ السيرفر.
          2. خذ نسخة احتياطية من   <مجلد السيرفر>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json   (أو استورده إلى المشروع).
          3. انسخ EconomyOverride.json إلى   <مجلد السيرفر>\SCUM\Saved\Config\WindowsServer\EconomyOverride.json
          4. ضع باك السيرفر (مع ملف .sig) في   <مجلد السيرفر>\SCUM\Content\Paks\~mods   وشغّل السيرفر مع -fileopenlog.
             التجّار الذين وضعتهم من كائنات السيرفر: بدون باك السيرفر لا يراهم أحد.
          5. شغّل السيرفر؛ يقرأ الملف عند التشغيل.

        اللعب الفردي / الساندبوكس (على جهازك)
          1. أغلق اللعبة.
          2. خذ نسخة احتياطية من الملف القديم، ثم انسخ EconomyOverride.json إلى   %LOCALAPPDATA%\SCUM\Saved\Config\WindowsNoEditor\EconomyOverride.json
             (الصق هذا المسار في شريط عنوان مستكشف الملفات لفتح المجلد).
          3. ضع باك الكلاينت (مع ملف .sig) في مجلد اللعبة   SCUM\Content\Paks\~mods   وشغّل اللعبة.

        كل قسم في الملف تاجر باسمه (A_0_Armory و B_4_Hospital ...)؛ التجّار الذين وضعتهم بأسمائهم التي اخترتها.
        القيمة "-1" أو "default" تعني قيمة اللعبة نفسها. اللاعبون يحتاجون باك الكلاينت أيضاً حتى تعرف لعبتهم التجّار الجدد.
        """;

    /// <summary>The settings as read, for <see cref="EconomyDiff"/>.</summary>
    internal IReadOnlyList<Member> SettingMembers => _settings;

    /// <summary>The sections as read, for <see cref="EconomyDiff"/>.</summary>
    internal IReadOnlyList<Section> Sections => _sections;

    /// <summary>What a step by step edit cannot change: where <c>traders</c> and <c>economy-override</c> are, and the members around them.</summary>
    internal string Frame => string.Create(CultureInfo.InvariantCulture,
        $"{_tradersAt}|{_tradersRaw}|{_rootAt}|{string.Join(",", _rootMembers.Select(m => m.RawKey + ":" + m.Raw))}|{string.Join(",", _settings.Select(s => s.RawKey))}");

    private Section GetSection(string trader, bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trader);
        if (IndexOf(trader) is var i and >= 0)
        {
            return _sections[i];
        }

        var section = new Section(trader, Quote(trader));
        if (create)
        {
            _sections.Add(section);
            _tradersRaw = null;
        }

        return section;
    }

    /// <summary>A member of an object: its key and its value, each as written.</summary>
    internal readonly record struct Member(string Key, string RawKey, string Raw);

    /// <summary>A trader's section: its entries, or the value as written when it is not an array.</summary>
    internal sealed class Section(string name, string rawName)
    {
        public string Name { get; } = name;

        public string RawName { get; } = rawName;

        public List<Entry> Entries { get; private init; } = [];

        public string? Raw { get; set; }

        /// <summary>The section as one line of JSON (its array, or its value as written).</summary>
        public string Text => Raw ?? "[" + string.Join(",", Entries.Select(e => e.Text)) + "]";

        public Section Copy() => new(Name, RawName) { Entries = [.. Entries], Raw = Raw };

        public static Section Parse(string name, string rawName, string text)
        {
            var section = new Section(name, rawName);
            var bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes, ReaderOptions);
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                section.Raw = text;
                return section;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                section.Entries.Add(ReadEntry(ref reader, bytes));
            }

            return section;
        }
    }

    /// <summary>One element of a section, never changed once made: an object's members as written, or another value.</summary>
    internal sealed class Entry(Member[] members, string? raw)
    {
        private string? _text;
        private TradeableOverride? _typed;

        public Member[] Members { get; } = members;

        /// <summary>The element as written when it is not an object.</summary>
        public string? Raw { get; } = raw;

        /// <summary>The <c>tradeable-code</c> (null when there is none, or the element is not an object).</summary>
        public string? Code { get; } = raw is null && members.FirstOrDefault(m => m.Key == CodeKey) is { Raw: not null } code && Unquote(code.Raw) is { Length: > 0 } c ? c : null;

        /// <summary>The element as one line of JSON (what a <see cref="SetEconomyEntryOp"/> carries).</summary>
        public string Text => _text ??= Raw ?? "{" + string.Join(",", Members.Select(m => m.RawKey + ":" + m.Raw)) + "}";

        /// <summary>The typed values (null without a code). A value the app cannot read is the game's (it stays as written).</summary>
        public TradeableOverride? Typed => _typed ??= Code is null ? null : new TradeableOverride(Code)
        {
            PurchasePrice = Int(Value(PurchaseKey)),
            SellPrice = Int(Value(SellKey)),
            DeltaPrice = Float(Value(DeltaKey)),
            CanBePurchased = Bool(Value(CanBuyKey)),
            RequiredFame = Int(Value(FameKey)),
            AfterSaleOnly = Bool(Value(AfterSaleKey)),
        };

        public bool Is(string code) => string.Equals(Code, code, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when every member besides the code is a known key at the game's value (-1 / default): removing it loses nothing.</summary>
        public bool HoldsOnlyGameValues => Raw is null && Members.All(m => m.Key switch
        {
            CodeKey => true,
            PurchaseKey or SellKey or DeltaKey or FameKey =>
                double.TryParse(Unquote(m.Raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == -1,
            CanBuyKey or AfterSaleKey => Unquote(m.Raw).Trim().Equals("default", StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

        public string? Value(string key) => Members.FirstOrDefault(m => m.Key == key) is { Raw: not null } m ? Unquote(m.Raw) : null;

        /// <summary>This entry with <paramref name="value"/>'s values: only those that differ are rewritten (this one when none does).</summary>
        public Entry With(TradeableOverride value)
        {
            var typed = Typed!;
            var members = Members.ToList();
            Change(members, PurchaseKey, typed.PurchasePrice != value.PurchasePrice, ValueText(value.PurchasePrice));
            Change(members, SellKey, typed.SellPrice != value.SellPrice, ValueText(value.SellPrice));
            Change(members, DeltaKey, typed.DeltaPrice != value.DeltaPrice, ValueText(value.DeltaPrice));
            Change(members, CanBuyKey, typed.CanBePurchased != value.CanBePurchased, ValueText(value.CanBePurchased));
            Change(members, FameKey, typed.RequiredFame != value.RequiredFame, ValueText(value.RequiredFame));
            Change(members, AfterSaleKey, typed.AfterSaleOnly != value.AfterSaleOnly, ValueText(value.AfterSaleOnly));
            return members.SequenceEqual(Members) ? this : new Entry([.. members], null);

            static void Change(List<Member> members, string key, bool changed, string text)
            {
                if (!changed)
                {
                    return;
                }

                var i = members.FindIndex(m => m.Key == key);
                if (i >= 0)
                {
                    members[i] = members[i] with { Raw = Quote(text) };
                }
                else
                {
                    members.Add(new Member(key, Quote(key), Quote(text)));
                }
            }
        }

        /// <summary>A new entry with every key, as the game writes one.</summary>
        public static Entry From(TradeableOverride value) => new(
        [
            new(CodeKey, Quote(CodeKey), Quote(value.Code)),
            new(PurchaseKey, Quote(PurchaseKey), Quote(ValueText(value.PurchasePrice))),
            new(SellKey, Quote(SellKey), Quote(ValueText(value.SellPrice))),
            new(DeltaKey, Quote(DeltaKey), Quote(ValueText(value.DeltaPrice))),
            new(CanBuyKey, Quote(CanBuyKey), Quote(ValueText(value.CanBePurchased))),
            new(FameKey, Quote(FameKey), Quote(ValueText(value.RequiredFame))),
            new(AfterSaleKey, Quote(AfterSaleKey), Quote(ValueText(value.AfterSaleOnly))),
        ], null);

        public static Entry Parse(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes, ReaderOptions);
            reader.Read();
            return ReadEntry(ref reader, bytes);
        }
    }

    private static int? Int(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d <= int.MaxValue ? (int)Math.Round(d) : null;

    private static float? Float(string? s) =>
        s is not null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f != -1f && float.IsFinite(f) ? f : null;

    private static bool? Bool(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static string ValueText(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-1";

    /// <summary>A delta as the game writes a float (<c>0.5</c>, <c>2.0</c>), every digit kept.</summary>
    private static string ValueText(float? value)
    {
        if (value is not { } f)
        {
            return "-1.0";
        }

        var text = f.ToString(CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E') < 0 ? text + ".0" : text;
    }

    private static string ValueText(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "default",
    };
}
