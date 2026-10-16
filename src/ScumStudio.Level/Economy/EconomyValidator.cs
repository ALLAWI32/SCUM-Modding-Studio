using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScumStudio.Level.Economy;

/// <summary>What is wrong (or odd) in an <c>EconomyOverride.json</c>.</summary>
public enum EconomyProblemKind
{
    /// <summary>Not JSON: nothing can be read past this point.</summary>
    Syntax,

    /// <summary>No <c>economy-override</c> object: a warning when its settings and <c>traders</c> are there without it (an older list pasted on its own), else an error.</summary>
    NoRoot,

    /// <summary>The file, <c>traders</c>, a section or an entry is not the object or array the game reads.</summary>
    NotAnObject,

    /// <summary>An entry without a <c>tradeable-code</c>: the game cannot know what it changes.</summary>
    MissingCode,

    /// <summary>A price, fame or delta that is not a number the game reads (<c>12,000</c>, <c>1.5</c> fame).</summary>
    BadNumber,

    /// <summary>A yes/no value that is not <c>true</c>, <c>false</c> or <c>default</c>.</summary>
    BadBool,

    /// <summary>A key, setting or section twice (the app reads both; the game may keep either).</summary>
    DuplicateKey,

    /// <summary>A tradeable listed twice at one trader.</summary>
    DuplicateEntry,

    /// <summary>A section of a trader that is neither the game's nor placed in the project (or a section without a name).</summary>
    UnknownTrader,

    /// <summary>A tradeable the game's tables do not have.</summary>
    UnknownCode,

    /// <summary>A tradeable the game keeps out of the server's reach (<c>IsImmuneToUserOverrides</c>).</summary>
    ImmuneCode,

    /// <summary>A section of a trader the project took off the island (it is left out of the export).</summary>
    HiddenTrader,

    /// <summary>A key the game does not write (it is kept as written).</summary>
    UnknownKey,

    /// <summary>A sale price above the price a player pays.</summary>
    SellAboveBuy,

    /// <summary>A negative value other than the game's <c>-1</c>.</summary>
    NegativeValue,
}

/// <summary>One problem of an economy file, where it is (1-based line and column of the token, in characters).</summary>
/// <param name="Kind">What it is.</param>
/// <param name="IsError">True when the file cannot be taken as it is (an import or a code edit is refused).</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Trader">The section, when the problem is in one.</param>
/// <param name="Code">The entry's <c>tradeable-code</c>, when known.</param>
/// <param name="Key">The key or setting.</param>
/// <param name="Value">The value as written (for <see cref="EconomyProblemKind.Syntax"/>: the reader's message).</param>
public sealed record EconomyProblem(EconomyProblemKind Kind, bool IsError, int Line, int Column, string? Trader, string? Code, string? Key, string? Value);

/// <summary>What the validator checks names against; every part may be empty (no game files: names are not checked).</summary>
/// <param name="GameTraders">The game's traders (<c>A_0_Armory</c> …).</param>
/// <param name="PlacedTraders">The project's placed traders (live and off the island).</param>
/// <param name="HiddenTraders">Traders whose sections are left out of the export.</param>
/// <param name="FindTradeable">The game's tradeable of a code, or null; null itself: codes are not checked.</param>
public sealed record EconomyContext(
    IReadOnlySet<string> GameTraders, IReadOnlySet<string> PlacedTraders, IReadOnlySet<string> HiddenTraders, Func<string, TradeableDefault?>? FindTradeable)
{
    /// <summary>No names known: only the file's own form is checked.</summary>
    public static EconomyContext None { get; } = new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null);
}

/// <summary>
/// Checks the text of an <c>EconomyOverride.json</c> the way the game reads it (owner, 2026-10-12: "syntax check and
/// validation messages: unknown trader, unknown tradeable code, bad number"). One pass of <see cref="Utf8JsonReader"/>
/// over the bytes (a few milliseconds for a megabyte); every problem carries its line and column.
/// </summary>
public static class EconomyValidator
{
    private const string RootKey = "economy-override";
    private const string TradersKey = "traders";

    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The problems of <paramref name="json"/>, in file order; empty for a file the game reads as meant.</summary>
    public static IReadOnlyList<EconomyProblem> Validate(string json, EconomyContext? context)
    {
        ArgumentNullException.ThrowIfNull(json);
        var walk = new Walk(Encoding.UTF8.GetBytes(json), context ?? EconomyContext.None);
        try
        {
            walk.Run();
        }
        catch (JsonException ex)
        {
            walk.Syntax(ex);
        }

        return walk.Problems;
    }

    /// <summary>True when <paramref name="problems"/> holds an error.</summary>
    public static bool HasErrors(IEnumerable<EconomyProblem> problems) => problems.Any(p => p.IsError);

    private sealed class Walk(byte[] bytes, EconomyContext context)
    {
        private readonly HashSet<string> _sections = new(StringComparer.OrdinalIgnoreCase);
        private List<int>? _lines;

        public List<EconomyProblem> Problems { get; } = [];

        public void Run()
        {
            // First the frame: is there an economy-override object, or its members without it? (Skip reads every value, so a
            // syntax error anywhere is found here.)
            var reader = new Utf8JsonReader(bytes, Options);
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                Add(EconomyProblemKind.NotAnObject, true, reader.TokenStartIndex);
                reader.Skip();
                while (reader.Read())
                {
                }

                return;
            }

            var (wrapped, bare) = (false, false);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                wrapped |= key == RootKey && reader.TokenType == JsonTokenType.StartObject;
                bare |= key == TradersKey || (key is not null && EconomyOverride.GameDefault(key) is not null);
                reader.Skip();
            }

            while (reader.Read())
            {
            }

            reader = new Utf8JsonReader(bytes, Options);
            reader.Read();
            if (!wrapped)
            {
                Add(EconomyProblemKind.NoRoot, !bare, reader.TokenStartIndex);
                if (bare)
                {
                    Inner(ref reader);
                }

                return;
            }

            var found = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var at = reader.TokenStartIndex;
                var key = reader.GetString()!;
                reader.Read();
                if (key == RootKey && reader.TokenType == JsonTokenType.StartObject && !found)
                {
                    found = true;
                    Inner(ref reader);
                    continue;
                }

                Add(key == RootKey ? EconomyProblemKind.DuplicateKey : EconomyProblemKind.UnknownKey, false, at, key: key);
                reader.Skip();
            }
        }

        public void Syntax(JsonException ex)
        {
            var line = (int)(ex.LineNumber ?? 0);
            var lines = Lines();
            var start = lines[Math.Clamp(line, 0, lines.Count - 1)];
            var offset = (int)Math.Min(bytes.Length, start + (ex.BytePositionInLine ?? 0));
            Problems.Add(new EconomyProblem(EconomyProblemKind.Syntax, true, line + 1, Encoding.UTF8.GetCharCount(bytes, start, offset - start) + 1, null, null, null, ex.Message));
        }

        /// <summary>The members of <c>economy-override</c>: settings and <c>traders</c>.</summary>
        private void Inner(ref Utf8JsonReader reader)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var at = reader.TokenStartIndex;
                var key = reader.GetString()!;
                if (!keys.Add(key))
                {
                    Add(EconomyProblemKind.DuplicateKey, false, at, key: key);
                }

                reader.Read();
                if (key == TradersKey)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        Traders(ref reader);
                    }
                    else
                    {
                        Add(EconomyProblemKind.NotAnObject, true, reader.TokenStartIndex, key: key);
                        reader.Skip();
                    }

                    continue;
                }

                var text = Text(ref reader, out var valueAt);
                if (EconomyOverride.GameDefault(key) is null)
                {
                    Add(EconomyProblemKind.UnknownKey, false, at, key: key, value: text);
                }
                else if (text is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    Add(EconomyProblemKind.BadNumber, true, valueAt, key: key, value: text);
                }
            }
        }

        private void Traders(ref Utf8JsonReader reader)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var at = reader.TokenStartIndex;
                var name = reader.GetString()!;
                if (name.Length == 0)
                {
                    Add(EconomyProblemKind.UnknownTrader, false, at, trader: name);
                }
                else if (!_sections.Add(name))
                {
                    Add(EconomyProblemKind.DuplicateKey, false, at, trader: name);
                }
                else if (context.HiddenTraders.Contains(name))
                {
                    Add(EconomyProblemKind.HiddenTrader, false, at, trader: name);
                }
                else if (context.GameTraders.Count > 0 && !context.GameTraders.Contains(name) && !context.PlacedTraders.Contains(name))
                {
                    Add(EconomyProblemKind.UnknownTrader, false, at, trader: name);
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    Add(EconomyProblemKind.NotAnObject, true, reader.TokenStartIndex, trader: name);
                    reader.Skip();
                    continue;
                }

                var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        Add(EconomyProblemKind.NotAnObject, true, reader.TokenStartIndex, trader: name);
                        reader.Skip();
                        continue;
                    }

                    Entry(ref reader, name, codes);
                }
            }
        }

        private void Entry(ref Utf8JsonReader reader, string trader, HashSet<string> codes)
        {
            var entryAt = reader.TokenStartIndex;
            var first = Problems.Count;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string? code = null;
            var codeAt = entryAt;
            (double? Value, long At) buy = (null, 0), sell = (null, 0);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var at = reader.TokenStartIndex;
                var key = reader.GetString()!;
                if (!keys.Add(key))
                {
                    Add(EconomyProblemKind.DuplicateKey, false, at, trader, key: key);
                }

                reader.Read();
                var text = Text(ref reader, out var valueAt);
                switch (key)
                {
                    case "tradeable-code":
                        (code, codeAt) = text is { Length: > 0 } ? (text, valueAt) : (code, codeAt);
                        break;
                    case "base-purchase-price":
                        buy = (Number(text, whole: true, trader, key, valueAt), valueAt);
                        break;
                    case "base-sell-price":
                        sell = (Number(text, whole: true, trader, key, valueAt), valueAt);
                        break;
                    case "required-famepoints":
                        Number(text, whole: true, trader, key, valueAt);
                        break;
                    case "delta-price":
                        Number(text, whole: false, trader, key, valueAt);
                        break;
                    case "can-be-purchased" or "available-after-sale-only":
                        if (text?.Trim().ToLowerInvariant() is not ("true" or "false" or "default" or "1" or "0"))
                        {
                            Add(EconomyProblemKind.BadBool, true, valueAt, trader, key: key, value: text);
                        }

                        break;
                    default:
                        Add(EconomyProblemKind.UnknownKey, false, at, trader, key: key, value: text);
                        break;
                }
            }

            if (code is null)
            {
                Add(EconomyProblemKind.MissingCode, true, entryAt, trader);
                return;
            }

            for (var i = first; i < Problems.Count; i++)
            {
                Problems[i] = Problems[i] with { Code = code };
            }

            if (!codes.Add(code))
            {
                Add(EconomyProblemKind.DuplicateEntry, false, codeAt, trader, code);
            }

            var known = context.FindTradeable?.Invoke(code);
            if (context.FindTradeable is not null && known is null)
            {
                Add(EconomyProblemKind.UnknownCode, false, codeAt, trader, code);
            }
            else if (known is { ImmuneToOverrides: true })
            {
                Add(EconomyProblemKind.ImmuneCode, false, codeAt, trader, code);
            }

            var paid = buy.Value is { } b and >= 0 ? b : (double?)known?.PurchasePrice;
            var got = sell.Value is { } s and >= 0 ? s : (double?)known?.SellPrice;
            if (paid is { } p && got is { } g && g > p && (buy.Value is >= 0 || sell.Value is >= 0))
            {
                Add(EconomyProblemKind.SellAboveBuy, false, sell.Value is >= 0 ? sell.At : buy.At, trader, code, "base-sell-price", g.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Checks a number (<c>-1</c> is the game's own value); returns it when it is one.</summary>
        private double? Number(string? text, bool whole, string trader, string key, long at)
        {
            if (text is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)
                || (whole && (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue)))
            {
                Add(EconomyProblemKind.BadNumber, true, at, trader, key: key, value: text);
                return null;
            }

            if (value < 0 && value != -1)
            {
                Add(EconomyProblemKind.NegativeValue, false, at, trader, key: key, value: text);
            }

            return value;
        }

        /// <summary>A value as text (a string's content, a number or literal as written); null for an object, array or null.</summary>
        private static string? Text(ref Utf8JsonReader reader, out long at)
        {
            at = reader.TokenStartIndex;
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False:
                    return Encoding.UTF8.GetString(reader.ValueSpan);
                default:
                    reader.Skip();
                    return null;
            }
        }

        private void Add(EconomyProblemKind kind, bool error, long at, string? trader = null, string? code = null, string? key = null, string? value = null)
        {
            var lines = Lines();
            var line = lines.BinarySearch((int)at);
            line = line >= 0 ? line : ~line - 1;
            Problems.Add(new EconomyProblem(kind, error, line + 1, Encoding.UTF8.GetCharCount(bytes, lines[line], (int)at - lines[line]) + 1, trader, code, key, value));
        }

        /// <summary>Where each line starts (byte offsets), made when the first problem is found.</summary>
        private List<int> Lines()
        {
            if (_lines is null)
            {
                _lines = [0];
                for (var i = bytes.AsSpan().IndexOf((byte)'\n'); i >= 0;)
                {
                    _lines.Add(i + 1);
                    var next = bytes.AsSpan(i + 1).IndexOf((byte)'\n');
                    i = next < 0 ? -1 : i + 1 + next;
                }
            }

            return _lines;
        }
    }
}
