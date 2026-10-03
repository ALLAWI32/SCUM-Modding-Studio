using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>One thing that spawns (a vehicle spawn preset, a threat zone) in the Spawns page list.</summary>
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
/// <param name="Tunable">The value as stored.</param>
/// <param name="Committed">The value after the project's applied edits (invariant text).</param>
public sealed record SpawnTarget(string Package, Tunable Tunable, string Committed);

/// <summary>
/// One slider over one or more stored values of a data package: every target gets the slider's value (e.g. "chance each
/// part is there" sets the spawn chance of every part of a vehicle preset). Shown value = stored value / <see cref="Scale"/>.
/// </summary>
public sealed partial class SpawnSliderViewModel : ObservableObject
{
    private double _committed;

    /// <summary>Creates the slider.</summary>
    public SpawnSliderViewModel(string label, string tip, double minimum, double maximum, double step, string unit, IReadOnlyList<SpawnTarget> targets, double scale = 1)
    {
        Label = label;
        Tip = tip;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Unit = unit;
        Scale = scale;
        Targets = targets;
        var values = targets.Select(t => Parse(t.Committed) / scale).ToList();
        IsMixed = values.Count > 1 && values.Max() - values.Min() > step / 2;
        _committed = values.Count == 0 ? 0 : Math.Clamp(Math.Round(values.Average() / step) * step, minimum, maximum);
        _value = _committed;
    }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Tooltip.</summary>
    public string Tip { get; }

    /// <summary>Slider range.</summary>
    public double Minimum { get; }

    /// <summary>Slider range.</summary>
    public double Maximum { get; }

    /// <summary>Slider step.</summary>
    public double Step { get; }

    /// <summary>Unit after the number (%, s, m).</summary>
    public string Unit { get; }

    /// <summary>Stored value = shown value × scale (e.g. 100 for metres stored as centimetres).</summary>
    public double Scale { get; }

    /// <summary>The stored values written.</summary>
    public IReadOnlyList<SpawnTarget> Targets { get; }

    /// <summary>True when the targets held different values (the slider shows their average until moved).</summary>
    public bool IsMixed { get; }

    /// <summary>Shown value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsPending))]
    private double _value;

    /// <summary>"40 %", "(mixed) 45 %".</summary>
    public string ValueText =>
        (IsMixed && !IsPending ? Loc.T("Spawns.Mixed") + " " : string.Empty) + Value.ToString(Step < 1 ? "0.##" : "0", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : string.Empty);

    /// <summary>Moved since the last apply.</summary>
    public bool IsPending => Math.Abs(Value - _committed) > Step / 2;

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
            if (text != target.Committed)
            {
                yield return (target, text);
            }
        }
    }

    /// <summary>Back to the last applied value.</summary>
    public void Discard() => Value = _committed;

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

    /// <summary>Sets both ends to the same share of the range (0, 0.5, 1).</summary>
    [RelayCommand]
    private void SetBoth(string share)
    {
        var f = double.Parse(share, CultureInfo.InvariantCulture);
        var v = From.Minimum + ((From.Maximum - From.Minimum) * f);
        To.Value = v;
        From.Value = v;
    }
}

/// <summary>One <c>ServerSettings.ini</c> value: a toggle or a slider. <c>-1</c> usually means "the game decides".</summary>
public sealed partial class ServerSettingViewModel : ObservableObject
{
    private string _original;

    /// <summary>Creates the row from the file's current text.</summary>
    public ServerSettingViewModel(string key, string label, string tip, string current, bool isToggle, bool isInteger, double minimum, double maximum, double step, string unit = "")
    {
        Key = key;
        Label = label;
        Tip = tip;
        IsToggle = isToggle;
        IsInteger = isInteger;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Unit = unit;
        _original = current;
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

    /// <summary>Slider range.</summary>
    public double Minimum { get; }

    /// <summary>Slider range.</summary>
    public double Maximum { get; }

    /// <summary>Slider step.</summary>
    public double Step { get; }

    /// <summary>Unit after the number (x, m).</summary>
    public string Unit { get; }

    /// <summary>Slider value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsPending))]
    private double _value;

    /// <summary>Toggle value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _flag;

    /// <summary>"1.5 x", "Game default".</summary>
    public string ValueText => Value < 0 ? Loc.T("Spawns.GameDefault") : Value.ToString(IsInteger ? "0" : "0.##", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : string.Empty);

    /// <summary>Changed since loaded or saved.</summary>
    public bool IsPending => NewText != _original;

    /// <summary>The text the file gets.</summary>
    public string NewText => IsToggle ? (Flag ? "True" : "False") : ServerSettingsFile.Format(Value, IsInteger);

    /// <summary>After saving: the new text is the file's.</summary>
    public void MarkSaved()
    {
        _original = NewText;
        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Back to the file's value.</summary>
    public void Reset()
    {
        if (IsToggle)
        {
            Flag = _original.Equals("true", StringComparison.OrdinalIgnoreCase) || _original == "1";
        }
        else
        {
            Value = double.TryParse(_original, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        OnPropertyChanged(nameof(IsPending));
    }
}

/// <summary>A titled card of rows (sliders, ranges, server settings) on the Spawns page.</summary>
/// <param name="Title">Card title.</param>
/// <param name="Caption">One line under it.</param>
/// <param name="IconKey">Card icon.</param>
/// <param name="Rows">Rows.</param>
public sealed record SpawnCard(string Title, string Caption, string IconKey, IReadOnlyList<object> Rows)
{
    /// <summary>True when the card has a caption.</summary>
    public bool HasCaption => Caption.Length > 0;
}
