using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>One thing that spawns (a vehicle spawn preset, a threat zone, a hunting biome) in the Spawns page list.</summary>
/// <param name="PackagePath">Its data package.</param>
/// <param name="Name">Readable name.</param>
/// <param name="Group">Readable group ("World spawns", "High threat").</param>
/// <param name="IconKey">List icon.</param>
public sealed record SpawnEntry(string PackagePath, string Name, string Group, string IconKey)
{
    /// <summary>Search text.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0 || Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || Group.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A stored value a slider writes (see <see cref="SpawnSliderViewModel"/>).</summary>
/// <param name="Package">Package path.</param>
/// <param name="Tunable">The value as stored (its <see cref="Tunable.Value"/> is the game's).</param>
/// <param name="Committed">The value after the project's applied edits (invariant text).</param>
public sealed record SpawnTarget(string Package, Tunable Tunable, string Committed);

/// <summary>The game data the Spawns page reads: the connected catalog, or a test's own.</summary>
internal interface ISpawnSource
{
    /// <summary>Every package path of the game.</summary>
    IReadOnlyList<string> Packages { get; }

    /// <summary>The stored values of a package (throws when it cannot be read).</summary>
    IReadOnlyList<Tunable> Read(string packagePath);

    /// <summary>The chance lists of a zone settings or biome package (<see cref="SpawnChoices.ForZone"/>, <see cref="SpawnChoices.ForBiome"/>).</summary>
    IReadOnlyList<SpawnWeightList> WeightLists(string packagePath);

    /// <summary>The zone settings (names) whose chance lists read the list stored in <paramref name="package"/> (slow the first time).</summary>
    IReadOnlyList<string> SettingsUsing(string package);

    /// <summary>The island's spawn places as the game has them (slow the first time: a worker's job); empty when unreadable.</summary>
    IReadOnlyList<SpawnPlace> Places { get; }

    /// <summary>The ground's height under a world point (cm), null where no terrain is read; slow for a new tile.</summary>
    float? GroundAt(float x, float y);

    /// <summary>False for a place inside an underground level (a bunker, a tunnel), which the ground check leaves alone.</summary>
    bool AboveGround(SpawnPlace place);

    /// <summary>A copy of the zone settings <paramref name="zonePackage"/> under a free name (<see cref="ZoneCopies"/>); throws when refused.</summary>
    CloneAssetOp ZoneCopy(string zonePackage, IReadOnlyCollection<string> planned);

    /// <summary>A 3D model of a character Blueprint by name, else the stand-in of its kind; null when there is none.</summary>
    PreviewModel? Preview(string? blueprint, SpawnCategory category);
}

/// <summary>
/// One slider over one or more stored values of a data package: every target gets the slider's value (e.g. "chance each
/// part is there" sets the spawn chance of every part of a vehicle preset). Shown value = stored value / <see cref="Scale"/>.
/// The stored value is shown as it is (never rounded or clamped); the range widens to hold it.
/// </summary>
public sealed partial class SpawnSliderViewModel : ObservableObject
{
    private readonly string _tip;
    private double _committed;

    /// <summary>Creates the slider.</summary>
    public SpawnSliderViewModel(string label, string tip, double minimum, double maximum, double step, string unit, IReadOnlyList<SpawnTarget> targets, double scale = 1)
    {
        Label = label;
        _tip = tip;
        Step = step;
        Unit = unit;
        Scale = scale;
        Targets = targets;
        var stored = targets.Select(t => Parse(t.Committed) / scale).ToList();
        var stock = targets.Select(t => Parse(t.Tunable.Value) / scale).ToList();
        IsMixed = stored.Count > 1 && stored.Max() - stored.Min() > step / 2;
        IsOutOfRange = stored.Concat(stock).Any(v => v < minimum || v > maximum);
        var all = stored.Concat(stock).DefaultIfEmpty(minimum).ToList();
        Minimum = Math.Min(minimum, Math.Floor(all.Min() / step) * step);
        Maximum = Math.Max(maximum, Math.Ceiling(all.Max() * 1.5 / step) * step);
        StockValue = stock.Count == 0 ? 0 : stock.Average();
        _committed = stored.Count == 0 ? 0 : stored.Average();
        _value = _committed;
    }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Tooltip: what the value does and the game's value.</summary>
    public string Tip => Targets.Count == 0 ? _tip : (_tip.Length > 0 ? _tip + "\n" : string.Empty) + Loc.F("Spawns.GameValue", Format(StockValue));

    /// <summary>Slider range (widened to hold a stored value beyond the usual range).</summary>
    public double Minimum { get; }

    /// <summary>Slider range (widened to hold a stored value beyond the usual range).</summary>
    public double Maximum { get; }

    /// <summary>Slider step.</summary>
    public double Step { get; }

    /// <summary>Unit after the number (localized).</summary>
    public string Unit { get; }

    /// <summary>Stored value = shown value × scale (e.g. 100 for metres stored as centimetres).</summary>
    public double Scale { get; }

    /// <summary>The stored values written.</summary>
    public IReadOnlyList<SpawnTarget> Targets { get; private set; }

    /// <summary>True when the targets held different values (the slider shows their average until moved).</summary>
    public bool IsMixed { get; }

    /// <summary>True when the game or the project stores a value outside the usual range (the range was widened).</summary>
    public bool IsOutOfRange { get; }

    /// <summary>The game's value (shown units).</summary>
    public double StockValue { get; }

    /// <summary>Shown value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsPending), nameof(IsStock))]
    private double _value;

    /// <summary>"40 %", "(mixed) 45 %".</summary>
    public string ValueText => (IsMixed && !IsPending ? Loc.T("Spawns.Mixed") + " " : string.Empty) + Format(Value);

    /// <summary>Moved since the last apply.</summary>
    public bool IsPending => Math.Abs(Value - _committed) > Step / 2;

    /// <summary>At the game's value.</summary>
    public bool IsStock => Math.Abs(Value - StockValue) <= Step / 2;

    /// <summary>The value edits to record: one per target whose stored value changes.</summary>
    public IEnumerable<(SpawnTarget Target, string Value)> Changes()
    {
        if (!IsPending)
        {
            yield break;
        }

        foreach (var target in Targets)
        {
            var stored = Value * Scale;
            var text = target.Tunable.Kind == TunableKind.Int
                ? ((long)Math.Round(stored)).ToString(CultureInfo.InvariantCulture)
                : stored.ToString("0.######", CultureInfo.InvariantCulture);
            if (!TunableValue.AreEqual(target.Tunable.Kind, text, target.Committed))
            {
                yield return (target, text);
            }
        }
    }

    /// <summary>Back to the last applied value.</summary>
    public void Discard() => Value = _committed;

    /// <summary>
    /// The project's values changed (undo, redo, another page): the targets take the applied values; an unmoved slider
    /// follows them, a moved one keeps the user's value.
    /// </summary>
    public void Refresh(Func<SpawnTarget, string> current)
    {
        var moved = IsPending;
        Targets = Targets.Select(t => t with { Committed = current(t) }).ToList();
        _committed = Targets.Count == 0 ? 0 : Targets.Average(t => Parse(t.Committed) / Scale);
        if (!moved)
        {
            Value = _committed;
        }

        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(ValueText));
    }

    /// <summary>Back to the game's value.</summary>
    [RelayCommand]
    private void Reset() => Value = StockValue;

    private string Format(double value) =>
        value.ToString(Step < 1 || Math.Abs(value - Math.Round(value)) > 1e-9 ? "0.##" : "0", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : string.Empty);

    private static double Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : text.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

/// <summary>A min/max pair of sliders ("fuel at spawn: from 0 % to 10 %"), kept so that From never passes To.</summary>
public sealed partial class SpawnRangeViewModel : ObservableObject
{
    /// <summary>Creates the range.</summary>
    public SpawnRangeViewModel(string label, string tip, SpawnSliderViewModel from, SpawnSliderViewModel to, bool quickButtons = false)
    {
        Label = label;
        Tip = tip;
        From = from;
        To = to;
        HasQuickButtons = quickButtons;
        from.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpawnSliderViewModel.Value) && From.Value > To.Value)
            {
                To.Value = From.Value;
            }
        };
        to.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpawnSliderViewModel.Value) && To.Value < From.Value)
            {
                From.Value = To.Value;
            }
        };
    }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Tooltip.</summary>
    public string Tip { get; }

    /// <summary>Lowest value.</summary>
    public SpawnSliderViewModel From { get; }

    /// <summary>Highest value.</summary>
    public SpawnSliderViewModel To { get; }

    /// <summary>Shows Empty / Half / Full (fuel, battery).</summary>
    public bool HasQuickButtons { get; }

    /// <summary>Sets both ends to the same share of the usual range (0, 0.5, 1).</summary>
    [RelayCommand]
    private void SetBoth(string share)
    {
        var f = double.Parse(share, CultureInfo.InvariantCulture);
        var v = Math.Max(From.Minimum, 0) + ((100 - Math.Max(From.Minimum, 0)) * f);
        To.Value = v;
        From.Value = v;
    }
}

/// <summary>
/// One <c>ServerSettings.ini</c> value: a toggle or a slider. The file's text is kept until the user moves the control
/// (a hand-written <c>2</c> or <c>true</c> is not rewritten as <c>2.000000</c> or <c>True</c>); a value beyond the usual
/// range widens the slider. <c>-1</c> ("the game decides") is a separate switch where the key has one.
/// </summary>
public sealed partial class ServerSettingViewModel : ObservableObject
{
    private string _original;
    private double _baseline;
    private bool _baselineFlag;
    private bool _baselineDefault;

    /// <summary>Creates the row from the file's current text.</summary>
    public ServerSettingViewModel(string key, string label, string tip, string current, bool isToggle, bool isInteger, double minimum, double maximum, double step, string unit = "", bool hasGameDefault = false)
    {
        Key = key;
        Label = label;
        Tip = tip;
        IsToggle = isToggle;
        IsInteger = isInteger;
        Step = step;
        Unit = unit;
        HasGameDefault = hasGameDefault;
        _original = current;
        var stored = ParseNumber(current) ?? 0;
        IsOutOfRange = !isToggle && !(hasGameDefault && stored < 0) && (stored < minimum || stored > maximum);
        Minimum = hasGameDefault || stored >= minimum ? minimum : Math.Floor(stored / step) * step;
        Maximum = stored <= maximum ? maximum : Math.Ceiling(stored * 1.5 / step) * step;
        Reset();
    }

    /// <summary>Key, e.g. <c>scum.MaxAllowedPuppets</c>.</summary>
    public string Key { get; }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Tooltip.</summary>
    public string Tip { get; }

    /// <summary>On/off value.</summary>
    public bool IsToggle { get; }

    /// <summary>Whole numbers only.</summary>
    public bool IsInteger { get; }

    /// <summary>Slider shown.</summary>
    public bool IsSlider => !IsToggle;

    /// <summary>The key takes <c>-1</c> for "the game decides" (shown as a switch).</summary>
    public bool HasGameDefault { get; }

    /// <summary>Slider range.</summary>
    public double Minimum { get; }

    /// <summary>Slider range.</summary>
    public double Maximum { get; }

    /// <summary>Slider step.</summary>
    public double Step { get; }

    /// <summary>Unit after the number (localized).</summary>
    public string Unit { get; }

    /// <summary>The file holds a value beyond the usual range (the slider was widened).</summary>
    public bool IsOutOfRange { get; }

    /// <summary>Slider value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsPending))]
    private double _value;

    /// <summary>Toggle value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _flag;

    /// <summary>"The game decides" (<c>-1</c>) for keys that have it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsPending), nameof(CanSlide))]
    private bool _isGameDefault;

    /// <summary>The slider moves (not while the game decides).</summary>
    public bool CanSlide => !IsGameDefault;

    /// <summary>"1.5 x", "Game default".</summary>
    public string ValueText => IsGameDefault || (!HasGameDefault && Value < 0) ? Loc.T("Spawns.GameDefault")
        : Value.ToString(IsInteger ? "0" : "0.##", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : string.Empty);

    /// <summary>Changed since loaded or saved (numerically: <c>2</c> and <c>2.000000</c> are the same value).</summary>
    public bool IsPending => IsToggle ? Flag != _baselineFlag
        : IsGameDefault != _baselineDefault || (!IsGameDefault && Math.Abs(Value - _baseline) > Math.Max(Step, 1e-6) / 2);

    /// <summary>The text the file gets: the file's own text while the value did not move.</summary>
    public string NewText => !IsPending ? _original
        : IsToggle ? (Flag ? "True" : "False")
        : IsGameDefault ? "-1"
        : ServerSettingsFile.Format(Value, IsInteger);

    /// <summary>After saving: the new text is the file's.</summary>
    public void MarkSaved()
    {
        _original = NewText;
        _baseline = Value;
        _baselineFlag = Flag;
        _baselineDefault = IsGameDefault;
        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Back to the file's value.</summary>
    public void Reset()
    {
        if (IsToggle)
        {
            Flag = _baselineFlag = _original.Trim() == "1" || _original.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var stored = ParseNumber(_original) ?? 0;
            IsGameDefault = _baselineDefault = HasGameDefault && stored < 0;
            Value = _baseline = _baselineDefault ? Math.Max(Minimum, 0) : stored;
        }

        OnPropertyChanged(nameof(IsPending));
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>A line of text inside a card: a note, or a warning shown while its condition holds.</summary>
public sealed partial class SpawnNoteViewModel : ObservableObject
{
    private readonly Func<bool>? _when;

    /// <summary>Creates the note; <paramref name="when"/> null = always shown.</summary>
    public SpawnNoteViewModel(string text, bool isWarning, Func<bool>? when = null)
    {
        Text = text;
        IsWarning = isWarning;
        _when = when;
        _isShown = when?.Invoke() ?? true;
    }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>Shown as a warning.</summary>
    public bool IsWarning { get; }

    /// <summary>Shown now.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>Checks the condition again.</summary>
    public void Update() => IsShown = _when?.Invoke() ?? true;
}

/// <summary>A titled card of rows (sliders, ranges, server settings, notes) on the Spawns page.</summary>
/// <param name="Title">Card title.</param>
/// <param name="Caption">One line under it.</param>
/// <param name="IconKey">Card icon.</param>
/// <param name="Rows">Rows.</param>
public sealed record SpawnCard(string Title, string Caption, string IconKey, IReadOnlyList<object> Rows)
{
    /// <summary>True when the card has a caption.</summary>
    public bool HasCaption => Caption.Length > 0;
}
