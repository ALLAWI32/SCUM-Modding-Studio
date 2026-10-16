using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Level.Economy;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// One setting of the file's <c>economy-override</c> (<c>traders-unlimited-stock</c> …) on the Economy page: a switch for the
/// game's 0/1 settings, a whole number for the gold price, a number for hours and rates; a key the game does not write is a
/// raw text box. The label is the file's own key. Each change is one step of the project's history.
/// </summary>
public sealed partial class EconomySettingRow : ObservableObject
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "prices-subject-to-delta", "prices-subject-to-player-count", "gold-price-subject-to-global-multiplier", "economy-logging",
        "traders-unlimited-funds", "traders-unlimited-stock", "global-only-after-player-sale-tradeable-availability-enabled",
        "tradeable-rotation-enabled", "enable-fame-point-requirement",
    };

    private static readonly HashSet<string> Whole = new(StringComparer.Ordinal) { "gold-base-price" };

    private readonly Action<EconomySettingRow, string> _set;

    /// <summary>Creates the row of <paramref name="key"/> with the file's <paramref name="value"/>.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">Its value as the file writes it, without quotes.</param>
    /// <param name="set">Writes a new value (one journal entry).</param>
    /// <param name="editable">A project is open.</param>
    public EconomySettingRow(string key, string value, Action<EconomySettingRow, string> set, bool editable)
    {
        Key = key;
        Value = value;
        _set = set;
        IsEditable = editable;
    }

    /// <summary>The file's key.</summary>
    public string Key { get; }

    /// <summary>Its value as written.</summary>
    public string Value { get; private set; }

    /// <summary>A project is open.</summary>
    public bool IsEditable { get; }

    /// <summary>The game's value, or null for a key the game does not write.</summary>
    public string? GameValue => EconomyOverride.GameDefault(Key);

    /// <summary>One of the game's 0/1 settings, written as 0 or 1 (anything else shows as text).</summary>
    public bool IsFlag => Flags.Contains(Key) && Value is "0" or "1";

    /// <summary>The switch of a 0/1 setting.</summary>
    public bool Flag
    {
        get => Value == "1";
        set => Commit(value ? "1" : "0");
    }

    /// <summary>The box's text, read when it is left: a number for the game's settings, anything for an unknown key.</summary>
    public string Text
    {
        get => Value;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (GameValue is null)
            {
                Commit(text);
            }
            else if (Normalize(Key, text) is { } normalized)
            {
                Commit(normalized);
            }
            else
            {
                InvalidText = Loc.F("Economy.Value.Invalid", text);
                IsInvalid = true;
            }
        }
    }

    /// <summary>The last text typed was not a number (the value stayed).</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>Why.</summary>
    [ObservableProperty]
    private string _invalidText = string.Empty;

    /// <summary>The hover text: what the setting does.</summary>
    public string Tip => GameValue is null ? Loc.T("Economy.Setting.Unknown.Tip") : Loc.Instance.Or("Economy.Setting." + Key + ".Tip", Key);

    /// <summary>"Back to the game's value (48.0)".</summary>
    public string ResetTip => Loc.F("Economy.ResetValue.Tip", GameValue ?? string.Empty);

    /// <summary>The value is not the game's.</summary>
    public bool CanReset => IsEditable && GameValue is { } game && !SameNumber(game, Value);

    /// <summary>Shows <paramref name="value"/> (the file's after an undo, an import or a code edit) without writing.</summary>
    public void Load(string value)
    {
        Value = value;
        IsInvalid = false;
        InvalidText = string.Empty;
        OnPropertyChanged(string.Empty);
        ResetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A typed value of a game setting in the game's own form: <c>0</c>/<c>1</c> for a switch, a whole number for the gold
    /// price, a number with a decimal point for the rest (<c>48</c> → <c>48.0</c>); empty or negative is <c>-1</c> where the
    /// game uses it. Null when it is not a number.
    /// </summary>
    public static string? Normalize(string key, string text)
    {
        if (Flags.Contains(key))
        {
            return text is "0" or "1" ? text : null;
        }

        if (Whole.Contains(key))
        {
            return EconomyItemViewModel.ParseInt(text)?.ToString(CultureInfo.InvariantCulture);
        }

        if (text.Length == 0)
        {
            return "-1.0";
        }

        var t = text.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d < 0 ? "-1.0" : d.ToString("0.0###", CultureInfo.InvariantCulture)
            : null;
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        if (GameValue is { } game)
        {
            Commit(game);
        }
    }

    private void Commit(string value)
    {
        IsInvalid = false;
        InvalidText = string.Empty;
        if (!IsEditable || value == Value)
        {
            OnPropertyChanged(nameof(Text));
            return;
        }

        _set(this, value);
    }

    private static bool SameNumber(string a, string b) =>
        a == b || (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                   && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y);
}
