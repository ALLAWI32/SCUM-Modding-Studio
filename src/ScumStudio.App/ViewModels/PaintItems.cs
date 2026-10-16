using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>One colour or number of a paint material (a material instance parameter), as stored and as last applied.</summary>
/// <param name="Package">Material instance package.</param>
/// <param name="Tunable">The stored parameter value (<c>VectorParameterValues[i].ParameterValue</c> or <c>ScalarParameterValues[i].ParameterValue</c>).</param>
/// <param name="Committed">The value after the project's applied edits.</param>
public sealed record PaintValue(string Package, Tunable Tunable, string Committed);

/// <summary>
/// A finish the paint panel offers: colour (sRGB; null keeps the one picked), and where the material has them metal, clear
/// coat and roughness (0..1), and the brightness the colour is stored with (1 = as picked, more = a bright gold).
/// </summary>
public sealed record PaintFinish(string Key, Color? Colour, double? Metal, double? ClearCoat, double? Roughness = null, double? Brightness = 1)
{
    /// <summary>The finishes, in button order (Original is handled by the panel).</summary>
    public static IReadOnlyList<PaintFinish> All { get; } =
    [
        new("Gold", Color.FromRgb(255, 186, 45), 1, 1, 0.25),
        new("GoldBright", Color.FromRgb(255, 190, 60), 1, 1, 0.12, 1.6),
        new("GoldDull", Color.FromRgb(214, 168, 80), 1, 0.15, 0.55, 0.7),
        new("RoseGold", Color.FromRgb(240, 160, 140), 1, 1, 0.25),
        new("Chrome", Color.FromRgb(235, 235, 235), 1, 1, 0.05),
        new("Diamond", Color.FromRgb(232, 242, 255), 1, 1, 0.02, 1.2),
        new("Bronze", Color.FromRgb(205, 127, 50), 1, 0.8, 0.35),
        new("Pink", Color.FromRgb(255, 105, 180), 0.6, 1, 0.3),
        new("CandyRed", Color.FromRgb(170, 10, 20), 0.7, 1, 0.2),
        new("MetallicBlue", Color.FromRgb(25, 75, 200), 0.8, 1, 0.25),
        new("Pearl", Color.FromRgb(242, 238, 228), 0.3, 1, 0.3),
        new("MatteBlack", Color.FromRgb(22, 22, 22), 0, 0, 0.8),
        new("Plain", null, 0, 0, 0.6, null),
    ];

    /// <summary>Button text.</summary>
    public string Label => Key switch
    {
        "Gold" => Loc.T("Paint.Finish.Gold"),
        "GoldBright" => Loc.T("Paint.Finish.GoldBright"),
        "GoldDull" => Loc.T("Paint.Finish.GoldDull"),
        "RoseGold" => Loc.T("Paint.Finish.RoseGold"),
        "Chrome" => Loc.T("Paint.Finish.Chrome"),
        "Diamond" => Loc.T("Paint.Finish.Diamond"),
        "Bronze" => Loc.T("Paint.Finish.Bronze"),
        "Pink" => Loc.T("Paint.Finish.Pink"),
        "MetallicBlue" => Loc.T("Paint.Finish.MetallicBlue"),
        "Pearl" => Loc.T("Paint.Finish.Pearl"),
        "MatteBlack" => Loc.T("Paint.Finish.MatteBlack"),
        "Plain" => Loc.T("Paint.Finish.Plain"),
        _ => Loc.T("Paint.Finish.CandyRed"),
    };
}

/// <summary>
/// The paint of one body material of a vehicle (a <c>M_Car_01</c> material instance such as <c>MI_Rager_Outer</c>): the
/// colour (<c>Base Color A</c>, or <c>Paint Color</c> on boats), a second colour for two-tone bodies, and the metal, clear
/// coat and roughness of the finish. Values are stored linear; the pickers show sRGB, and brightness and saturation turn
/// the picked colour into the stored one (a bright gold is stored above 1).
/// </summary>
/// <remarks>
/// An <em>added</em> paint belongs to a material that leaves paint to its parent (the armour, <c>MI_WW_Armor</c>): its
/// values are by-name parameters the export adds (colour A and B, metal, clear coat and the white colour mask, so the
/// plate is paint all over), or none of them while it is not painted. Like every car paint it shows its colour itself, the
/// plate's print only adding scratches, so the same colour matches the body.
/// <para>A <em>tint</em> belongs to a weapon or magazine (<c>M_Weapons_Master</c>): <c>Difuse_Colorization</c> multiplies the
/// weapon's own texture (the game itself lifts the dark MP5 with 3, 3, 3), so the colour is stored times <see cref="Gain"/>
/// to come out as picked; the even finish swaps the texture (<c>Color</c>) for flat white instead. It is an added paint:
/// left alone it keeps the weapon as the game has it.</para>
/// <para>Roughness is <c>M_Car_01</c>'s <c>Bottom Roughness_01</c> (colour A) and <c>Bottom Roughness_02</c> (colour B): the
/// paint's own roughness under the clear coat (the game lerps it with the texture's by the colour mask), written by name.</para>
/// </remarks>
public sealed partial class PaintMaterialViewModel : ObservableObject
{
    /// <summary>The flat white colour mask every car paint material imports from its parent.</summary>
    public const string WhiteMask = "/Game/ConZ_Files/Textures/Basic/T_FlatWhiteMask_Dummy_01.T_FlatWhiteMask_Dummy_01";

    /// <summary>The game's flat white colour texture: an added paint's plain finish (the plate keeps its shape, not its scrap-metal print).</summary>
    public const string FlatDiffuse = "/Game/ConZ_Files/Textures/Basic/T_FlatWhiteColor_Dummy_01.T_FlatWhiteColor_Dummy_01";

    /// <summary><c>M_Car_01</c>'s roughness of the paint in colour A (and <see cref="RoughnessBParameter"/> in colour B).</summary>
    public const string RoughnessParameter = "Bottom Roughness_01";

    /// <summary><c>M_Car_01</c>'s roughness of the paint in colour B.</summary>
    public const string RoughnessBParameter = "Bottom Roughness_02";

    /// <summary>Highest brightness: a colour stored at four times what was picked.</summary>
    public const double MaxBrightness = 4;

    private readonly float _tintGain;
    private readonly double _stockRoughness;
    private bool _committedPlain;
    private Color _committedColour;
    private Color? _committedSecond;
    private double _committedBrightness = 1;
    private double? _committedMetal;
    private double? _committedCoat;
    private double _committedRoughness;
    private bool _committedPainted;
    private bool _quiet;

    /// <summary>Creates the paint of <paramref name="materialPath"/>.</summary>
    /// <param name="materialPath">Material instance object path.</param>
    /// <param name="name">Readable name.</param>
    /// <param name="colour">Main colour.</param>
    /// <param name="second">Second colour (two-tone bodies; an added paint gives it the main colour), or null.</param>
    /// <param name="metal">Metal, or null.</param>
    /// <param name="clearCoat">Clear coat, or null.</param>
    /// <param name="mask">The colour mask of an added paint (null for a material with its own paint).</param>
    /// <param name="diffuse">An added paint's diffuse texture (for the plain finish), or null.</param>
    /// <param name="tint">A weapon's tint (see the remarks).</param>
    /// <param name="tintGain">How much a tint is lifted over the weapon's own texture.</param>
    /// <param name="roughness">The paint's roughness in colour A (by name), or null when the material has none.</param>
    /// <param name="roughnessB">The paint's roughness in colour B (by name), or null.</param>
    /// <param name="stockRoughness">The roughness the material has when it stores none (its parent's).</param>
    public PaintMaterialViewModel(string materialPath, string name, PaintValue colour, PaintValue? second, PaintValue? metal, PaintValue? clearCoat,
        PaintValue? mask = null, PaintValue? diffuse = null, bool tint = false, float tintGain = 1f, PaintValue? roughness = null, PaintValue? roughnessB = null,
        double stockRoughness = 0.5)
    {
        IsTint = tint;
        _tintGain = tintGain;
        _stockRoughness = stockRoughness;
        DiffuseValue = mask is null && !tint ? null : diffuse;
        MaterialPath = materialPath;
        Name = name;
        ColourValue = colour;
        SecondValue = second;
        MetalValue = metal;
        ClearCoatValue = clearCoat;
        MaskValue = mask;
        RoughnessValue = roughness;
        RoughnessBValue = roughnessB;
        Commit();
        Reset();
    }

    /// <summary>Raised when what the paint looks like changed (the 3D view repaints).</summary>
    public event EventHandler? Changed;

    /// <summary>Material object path (the preview parts drawn with it get its colour).</summary>
    public string MaterialPath { get; }

    /// <summary>Readable name ("Outer", "Body Outside A").</summary>
    public string Name { get; }

    /// <summary>The main colour parameter.</summary>
    public PaintValue ColourValue { get; private set; }

    /// <summary>The second colour (two-tone bodies), or null.</summary>
    public PaintValue? SecondValue { get; private set; }

    /// <summary><c>CarPaint Metalness</c>, or null when the material does not set it.</summary>
    public PaintValue? MetalValue { get; private set; }

    /// <summary><c>ClearCoat Amount</c>, or null.</summary>
    public PaintValue? ClearCoatValue { get; private set; }

    /// <summary>The colour mask of an added paint, or null.</summary>
    public PaintValue? MaskValue { get; private set; }

    /// <summary><see cref="RoughnessParameter"/> by name, or null.</summary>
    public PaintValue? RoughnessValue { get; private set; }

    /// <summary><see cref="RoughnessBParameter"/> by name, or null.</summary>
    public PaintValue? RoughnessBValue { get; private set; }

    /// <summary>True when the material has no paint of its own and the export adds it (armour, a weapon's tint).</summary>
    public bool IsAdded => MaskValue is not null || IsTint;

    /// <summary>True for armour (an added car paint with a white colour mask).</summary>
    public bool IsArmour => MaskValue is not null;

    /// <summary>True for a weapon's tint.</summary>
    public bool IsTint { get; }

    /// <summary>How much the stored colour is lifted over the texture (a tint over the weapon's own texture; 1 otherwise).</summary>
    public float Gain => GainFor(Plain);

    /// <summary>An added paint's diffuse texture parameter (plain finish), or null.</summary>
    public PaintValue? DiffuseValue { get; private set; }

    /// <summary>The plain finish is offered (an added paint whose diffuse can be swapped).</summary>
    public bool HasPlain => DiffuseValue is not null;

    /// <summary>A second colour is offered.</summary>
    public bool HasSecond => SecondValue is not null && !IsAdded;

    /// <summary>A metal slider is offered.</summary>
    public bool HasMetal => MetalValue is not null;

    /// <summary>A clear coat slider is offered.</summary>
    public bool HasClearCoat => ClearCoatValue is not null;

    /// <summary>A roughness slider is offered (car paint).</summary>
    public bool HasRoughness => RoughnessValue is not null;

    /// <summary>Brightness and saturation are offered (not on a weapon's tint, which is lifted over its texture already).</summary>
    public bool HasTone => !IsTint;

    /// <summary>The finishes.</summary>
    public IReadOnlyList<PaintFinish> Finishes => PaintFinish.All;

    /// <summary>The parts of the model painted with this material ("Body, Door_FrontLeft"), shown under its name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedByText), nameof(HasUsedBy))]
    private string _usedBy = string.Empty;

    /// <summary>"Used by: Body, Door_FrontLeft".</summary>
    public string UsedByText => UsedBy.Length == 0 ? string.Empty : Loc.F("Paint.UsedBy", UsedBy);

    /// <summary>The parts are known.</summary>
    public bool HasUsedBy => UsedBy.Length > 0;

    /// <summary>Main colour (sRGB).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private Color _colour;

    /// <summary>Second colour (sRGB).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private Color _second;

    /// <summary>Metal, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(MetalText))]
    private double _metal;

    /// <summary>Clear coat, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(ClearCoatText))]
    private double _clearCoat;

    /// <summary>Roughness of the paint, 0 (mirror) .. 1 (matte).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(RoughnessText))]
    private double _roughness;

    /// <summary>Linear gain of the picked colours, 0..<see cref="MaxBrightness"/> (1 = as picked; over 1 is stored over 1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(BrightnessText))]
    private double _brightness = 1;

    /// <summary>Saturation of the picked colours, 0 (grey) .. 2 (twice as strong), 1 = as picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(SaturationText))]
    private double _saturation = 1;

    /// <summary>False while an added paint is left as the game has it (no paint of its own); always true otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _painted = true;

    /// <summary>Plain finish of an added paint: an even colour instead of the paint over the part's own printed texture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _plain;

    /// <summary>"80 %".</summary>
    public string MetalText => Percent(Metal);

    /// <summary>"100 %".</summary>
    public string ClearCoatText => Percent(ClearCoat);

    /// <summary>"35 %".</summary>
    public string RoughnessText => Percent(Roughness);

    /// <summary>"160 %".</summary>
    public string BrightnessText => Percent(Brightness);

    /// <summary>"100 %".</summary>
    public string SaturationText => Percent(Saturation);

    /// <summary>Changed since the last apply.</summary>
    public bool IsPending => Changes().Any();

    /// <summary>The main colour as the 3D view draws it and the game stores it (linear RGBA, brightness and saturation applied).</summary>
    public System.Numerics.Vector4 LinearColour => Toned(Colour) * new System.Numerics.Vector4(Gain, Gain, Gain, 1f);

    /// <summary>The second colour as stored (linear RGBA, brightness and saturation applied).</summary>
    public System.Numerics.Vector4 LinearSecond => Toned(Second);

    /// <summary>True when the paint is the game's own (what Original gives).</summary>
    public bool IsOriginal => IsAdded
        ? !Painted
        : Colour == ToSrgb(ColourValue.Tunable.Value, (float)BrightnessOf(ColourValue.Tunable.Value)) && Math.Abs(Saturation - 1) < 0.005
          && Math.Abs(Brightness - BrightnessOf(ColourValue.Tunable.Value)) < 0.005
          && (MetalValue is null || Math.Abs(Metal - ParseFloat(MetalValue.Tunable.Value)) < 0.005)
          && (ClearCoatValue is null || Math.Abs(ClearCoat - ParseFloat(ClearCoatValue.Tunable.Value)) < 0.005)
          && (RoughnessValue is null || Math.Abs(Roughness - StockRoughness) < 0.005);

    private double StockRoughness => RoughnessValue is { Tunable.Value.Length: > 0 } r ? ParseFloat(r.Tunable.Value) : _stockRoughness;

    partial void OnColourChanged(Color value) => Touched();

    partial void OnSecondChanged(Color value) => Touched();

    partial void OnMetalChanged(double value) => Touched();

    partial void OnClearCoatChanged(double value) => Touched();

    partial void OnRoughnessChanged(double value) => Touched();

    partial void OnBrightnessChanged(double value) => Touched();

    partial void OnSaturationChanged(double value) => Touched();

    partial void OnPaintedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnPlainChanged(bool value) => Touched();

    /// <summary>Any change the user makes paints an added paint.</summary>
    private void Touched()
    {
        if (!_quiet)
        {
            Painted = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a finish (colour, and metal, clear coat, roughness and brightness where offered). A two-tone body takes it in
    /// both colours: "gold" is a gold plane, wings included; the second colour's swatch can change them afterwards. A finish
    /// without a colour (plain) keeps the one picked.
    /// </summary>
    [RelayCommand]
    private void ApplyFinish(PaintFinish finish) =>
        Set(finish.Colour ?? Colour, HasSecond ? finish.Colour ?? Second : Second, finish.Metal is { } m && HasMetal ? m : Metal,
            finish.ClearCoat is { } c && HasClearCoat ? c : ClearCoat, painted: true, Plain,
            finish.Roughness is { } r && HasRoughness ? r : Roughness, HasTone ? finish.Brightness ?? Brightness : 1, finish.Colour is not null ? 1 : Saturation);

    /// <summary>Back to the game's own paint (an added paint: no paint at all).</summary>
    [RelayCommand]
    private void Original()
    {
        if (IsAdded)
        {
            // A weapon keeps the metal its material stores (a colour picked afterwards does not make the gun matte).
            Set(Colors.White, default, MetalValue is { Tunable.Value.Length: > 0 } stock ? ParseFloat(stock.Tunable.Value) : 0, 0, painted: false, Plain, StockRoughness);
            return;
        }

        var bright = BrightnessOf(ColourValue.Tunable.Value);
        Set(ToSrgb(ColourValue.Tunable.Value, (float)bright), SecondValue is { } s ? ToSrgb(s.Tunable.Value, (float)bright) : default,
            MetalValue is { } m ? ParseFloat(m.Tunable.Value) : 0, ClearCoatValue is { } c ? ParseFloat(c.Tunable.Value) : 0, painted: true, plain: false,
            StockRoughness, bright);
    }

    /// <summary>Takes <paramref name="source"/>'s paint (Paint every part the same): its finish, or the game's own when it went back to it.</summary>
    public void Follow(PaintMaterialViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsOriginal)
        {
            Original();
            return;
        }

        Set(source.Colour, HasSecond && source.HasSecond ? source.Second : Second, HasMetal && source.HasMetal ? source.Metal : Metal,
            HasClearCoat && source.HasClearCoat ? source.ClearCoat : ClearCoat, painted: true, HasPlain && source.HasPlain ? source.Plain : Plain,
            HasRoughness && source.HasRoughness ? source.Roughness : Roughness, HasTone && source.HasTone ? source.Brightness : Brightness,
            HasTone && source.HasTone ? source.Saturation : Saturation);
    }

    /// <summary>Back to the last applied paint.</summary>
    public void Reset() => Set(_committedColour, _committedSecond ?? default, _committedMetal ?? 0, _committedCoat ?? 0, _committedPainted, _committedPlain,
        _committedRoughness, _committedBrightness);

    /// <summary>
    /// Takes what the project now has applied as the starting point (a Redo or an Undo landed): a paint changed since its
    /// last apply keeps what is shown (Apply records it over the new values), an unchanged one shows the applied values.
    /// </summary>
    /// <param name="committed">The applied text of a value (the project's override, or the stored value).</param>
    public void Rebase(Func<PaintValue, string> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        var pending = IsPending;
        PaintValue? Re(PaintValue? value) => value is null ? null : value with { Committed = committed(value) };
        ColourValue = Re(ColourValue)!;
        SecondValue = Re(SecondValue);
        MetalValue = Re(MetalValue);
        ClearCoatValue = Re(ClearCoatValue);
        MaskValue = Re(MaskValue);
        DiffuseValue = Re(DiffuseValue);
        RoughnessValue = Re(RoughnessValue);
        RoughnessBValue = Re(RoughnessBValue);
        Commit();
        if (!pending)
        {
            Reset();
        }

        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Sets every value at once (an undo step, Original, Reset).</summary>
    public void Set(Color colour, Color second, double metal, double clearCoat, bool painted, bool plain, double? roughness = null, double brightness = 1, double saturation = 1)
    {
        _quiet = true;
        try
        {
            Colour = colour;
            Second = second;
            Metal = metal;
            ClearCoat = clearCoat;
            Roughness = roughness ?? Roughness;
            Brightness = Math.Clamp(brightness, 0, MaxBrightness);
            Saturation = Math.Clamp(saturation, 0, 2);
            Plain = HasPlain && plain;
            Painted = painted;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>The value edits to record: (value, new text) for what changed.</summary>
    public IEnumerable<(PaintValue Value, string Text)> Changes()
    {
        var edits = new List<(PaintValue, string)>();
        void Diff(PaintValue? value, string text)
        {
            if (value is not null && value.Committed != text)
            {
                edits.Add((value, text));
            }
        }

        var metalChanged = MetalValue is not null && Math.Abs(Metal - (_committedMetal ?? 0)) > 0.005;
        var coatChanged = ClearCoatValue is not null && Math.Abs(ClearCoat - (_committedCoat ?? 0)) > 0.005;
        var roughChanged = RoughnessValue is not null && Math.Abs(Roughness - _committedRoughness) > 0.005;
        var toneChanged = Math.Abs(Brightness - _committedBrightness) > 0.005 || Math.Abs(Saturation - 1) > 0.005;
        if (IsAdded)
        {
            if (!Painted)
            {
                foreach (var value in new[] { ColourValue, SecondValue, MetalValue, ClearCoatValue, MaskValue, DiffuseValue, RoughnessValue, RoughnessBValue })
                {
                    Diff(value, value?.Tunable.Value ?? string.Empty); // as the game has it (nothing added, a stored value kept)
                }

                return edits;
            }

            var repainted = !_committedPainted;
            if (repainted || Colour != _committedColour || Plain != _committedPlain || toneChanged)
            {
                var text = LinearText(Toned(Colour), null, Gain);
                Diff(ColourValue, text);
                Diff(SecondValue, text);
            }

            Diff(MaskValue, WhiteMask);
            Diff(DiffuseValue, Plain ? FlatDiffuse : DiffuseValue?.Tunable.Value ?? string.Empty);
            if (repainted || metalChanged)
            {
                Diff(MetalValue, Number(Metal));
            }

            if (repainted || coatChanged)
            {
                Diff(ClearCoatValue, Number(ClearCoat));
            }

            if (roughChanged)
            {
                Diff(RoughnessValue, Number(Roughness));
                Diff(RoughnessBValue, Number(Roughness));
            }

            return edits;
        }

        if (Colour != _committedColour || toneChanged)
        {
            edits.Add((ColourValue, LinearText(Toned(Colour), ColourValue.Committed)));
        }

        if (SecondValue is { } s && _committedSecond is { } cs && (Second != cs || toneChanged))
        {
            edits.Add((s, LinearText(Toned(Second), s.Committed)));
        }

        if (metalChanged)
        {
            edits.Add((MetalValue!, Number(Metal)));
        }

        if (coatChanged)
        {
            edits.Add((ClearCoatValue!, Number(ClearCoat)));
        }

        if (roughChanged)
        {
            Diff(RoughnessValue, Number(Roughness));
            Diff(RoughnessBValue, Number(Roughness));
        }

        return edits;
    }

    /// <summary>
    /// <paramref name="c"/> (sRGB) as stored: linear, its saturation scaled around its brightest channel (0 = grey, as HSV
    /// does) and then multiplied by the brightness.
    /// </summary>
    public static System.Numerics.Vector4 Tone(Color c, double brightness, double saturation)
    {
        var l = ToLinear(c);
        var v = MathF.Max(l.X, MathF.Max(l.Y, l.Z));
        var s = (float)saturation;
        float Channel(float x) => MathF.Max(0f, v - ((v - x) * s)) * (float)brightness;
        return new(Channel(l.X), Channel(l.Y), Channel(l.Z), 1f);
    }

    /// <summary>sRGB colour of a stored linear <c>r, g, b, a</c> (divided by <paramref name="gain"/>).</summary>
    public static Color ToSrgb(string linear, float gain = 1f)
    {
        var f = TunableValue.ParseFloats(linear, 4);
        byte Channel(float v)
        {
            v /= gain;
            return (byte)Math.Clamp(Math.Round(255 * (v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f)), 0, 255);
        }

        return Color.FromRgb(Channel(f[0]), Channel(f[1]), Channel(f[2]));
    }

    /// <summary>Linear RGBA of an sRGB colour.</summary>
    public static System.Numerics.Vector4 ToLinear(Color c)
    {
        static float Channel(byte b)
        {
            var v = b / 255f;
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        return new(Channel(c.R), Channel(c.G), Channel(c.B), 1f);
    }

    /// <summary>The brightness a stored linear colour was written with: its brightest channel when over 1 (a bright gold), else 1.</summary>
    private static double BrightnessOf(string linear)
    {
        if (linear.Length == 0)
        {
            return 1;
        }

        var f = TunableValue.ParseFloats(linear, 4);
        return Math.Clamp(Math.Max(f[0], Math.Max(f[1], f[2])), 1, MaxBrightness);
    }

    /// <summary>The applied values as the starting point (construction, <see cref="Rebase"/>).</summary>
    private void Commit()
    {
        _committedPainted = !IsAdded || ColourValue.Committed != ColourValue.Tunable.Value;
        _committedPlain = _committedPainted ? DiffuseValue?.Committed == FlatDiffuse : HasPlain;
        _committedBrightness = _committedPainted && !IsTint ? BrightnessOf(ColourValue.Committed) : 1;
        _committedColour = _committedPainted ? ToSrgb(ColourValue.Committed, GainFor(_committedPlain) * (float)_committedBrightness) : Colors.White;
        _committedSecond = SecondValue is null || IsAdded ? null : ToSrgb(SecondValue.Committed, (float)_committedBrightness);
        _committedMetal = Committed(MetalValue);
        _committedCoat = Committed(ClearCoatValue);
        _committedRoughness = Committed(RoughnessValue) ?? StockRoughness;
    }

    private System.Numerics.Vector4 Toned(Color c) => HasTone ? Tone(c, Brightness, Saturation) : ToLinear(c);

    private static string LinearText(System.Numerics.Vector4 l, string? committed, float gain = 1f)
    {
        var alpha = committed is { Length: > 0 } ? TunableValue.ParseFloats(committed, 4)[3] : 1f;
        return TunableValue.Format(l.X * gain, l.Y * gain, l.Z * gain, alpha);
    }

    private float GainFor(bool plain) => IsTint && !(plain && HasPlain) ? _tintGain : 1f;

    private static string Percent(double v) => (v * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    private static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double? Committed(PaintValue? value) => value is { Committed.Length: > 0 } ? ParseFloat(value.Committed) : null;

    private static double ParseFloat(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
