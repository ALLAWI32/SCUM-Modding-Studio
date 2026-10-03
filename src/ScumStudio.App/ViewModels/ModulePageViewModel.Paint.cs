using System.Diagnostics;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Paint (vehicles): the body materials of the selected vehicle's parts that carry a paint colour (SCUM's car paint
/// <c>M_Car_01</c>: <c>Base Color A</c>, two-tone <c>Base Color B</c>, <c>CarPaint Metalness</c>, <c>ClearCoat Amount</c>;
/// boats: <c>Paint Color</c>), plus car paint materials that have no paint of their own (the armour): those get it added.
/// The 3D view shows the paint as it is chosen, through the material's colour mask and with its metal and clear coat
/// shine; the armour kits can be fitted to see them painted too. "Paint every part the same" keeps all parts on one
/// finish; Ctrl+Z steps back. Apply paint records the parameters as value edits of the material instances (every vehicle
/// using them in the game gets it; the client pak carries it).
/// </summary>
public abstract partial class ModulePageViewModel
{
    /// <summary>Changes closer together than this are one Ctrl+Z step (dragging in the colour picker).</summary>
    private static readonly TimeSpan PaintUndoPause = TimeSpan.FromMilliseconds(600);

    private readonly Dictionary<string, TextureImage> _paintMasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Vector4 A, Vector4? B, TextureImage Image)> _paintBakes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Dictionary<PaintMaterialViewModel, PaintState>> _paintHistory = new();
    private readonly Dictionary<PaintMaterialViewModel, PaintState> _paintShown = [];
    private readonly Stopwatch _paintClock = Stopwatch.StartNew();
    private Dictionary<string, PaintState>? _paintCarry;
    private PaintMaterialViewModel? _paintLastChanged;
    private TimeSpan _paintLastTime;
    private bool _paintRestoring;
    private bool _armourQuiet;
    private int _paintLoads;
    private PreviewModel? _previewBase;

    /// <summary>The selected vehicle's paints.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaints))]
    private IReadOnlyList<PaintMaterialViewModel> _paints = [];

    /// <summary>Paint changes not applied yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPaintCommand), nameof(DiscardPaintCommand))]
    private int _paintPendingCount;

    /// <summary>A change to one part's paint is made to every part (body, doors, armour).</summary>
    [ObservableProperty]
    private bool _paintTogether = true;

    /// <summary>The armour kit fitted in the 3D view: empty (none), <c>ArmorLight</c> or <c>ArmorHeavy</c>.</summary>
    [ObservableProperty]
    private PaintArmourChoice? _armour;

    /// <summary>The armour kits the selected vehicle can wear (none first); empty when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArmour))]
    private IReadOnlyList<PaintArmourChoice> _armourChoices = [];

    /// <summary>The paint panel is shown (a vehicle with paint materials).</summary>
    public bool HasPaints => Paints.Count > 0;

    /// <summary>The armour choice is shown.</summary>
    public bool HasArmour => ArmourChoices.Count > 1;

    /// <summary>A paint change Ctrl+Z can step back.</summary>
    public bool CanUndoPaint => _paintHistory.Count > 0;

    /// <summary>Forgets the armour choice (another vehicle is selected).</summary>
    private void ClearArmour()
    {
        _armourQuiet = true;
        try
        {
            ArmourChoices = [];
            Armour = null;
        }
        finally
        {
            _armourQuiet = false;
        }

        _paintCarry = null;
    }

    /// <summary>Finds the paint materials of <paramref name="model"/>'s parts (vehicles only).</summary>
    private async Task LoadPaintsAsync(AssetCatalog catalog, ModuleItemViewModel item, PreviewModel model)
    {
        if (!ReferenceEquals(_previewBase, model))
        {
            if (_paintCarry is null)
            {
                Paints = []; // another vehicle (a new armour kit keeps the panel while it loads)
            }

            _paintBakes.Clear();
        }

        _previewBase = model;
        var load = ++_paintLoads;
        _paintHistory.Clear();
        _paintShown.Clear();
        PaintPendingCount = 0;
        if (!IsVehicleModule)
        {
            return;
        }

        if (ArmourChoices.Count == 0)
        {
            _armourQuiet = true;
            try
            {
                ArmourChoices = model.AddOns.Count == 0 ? [] : [new PaintArmourChoice(string.Empty), .. model.AddOns.Select(k => new PaintArmourChoice(k))];
                Armour = ArmourChoices.FirstOrDefault();
            }
            finally
            {
                _armourQuiet = false;
            }
        }

        try
        {
            var materials = model.Parts.Select(p => p.Material).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = _paintMasks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = await Task.Run(() => materials.Select(m => ReadPaint(catalog, m, !known.Contains(m))).OfType<PaintSource>().ToList()).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedItem, item) || load != _paintLoads)
            {
                return;
            }

            foreach (var source in sources.Where(s => s.Mask is not null))
            {
                _paintMasks[source.MaterialPath] = source.Mask!;
            }

            var body = BodyBrightness(model, sources);
            var paints = new List<PaintMaterialViewModel>();
            foreach (var s in sources)
            {
                // An added paint matches the body: over its own (darker) print, or over the plain white finish.
                var gain = s.Added is null ? 1f : Math.Clamp(body / Math.Max(1e-4f, Brightness(model, s.MaterialPath, null)), 1f, 8f);
                var paint = new PaintMaterialViewModel(s.MaterialPath, s.Name, s.Colour, s.Second, s.Metal, s.ClearCoat, s.Added, gain, s.Diffuse, Math.Clamp(body, 0.05f, 1f));
                if (_paintCarry is { } carry && carry.TryGetValue(paint.MaterialPath, out var kept))
                {
                    kept.ApplyTo(paint); // another armour kit: the paint chosen so far stays
                }

                paints.Add(paint);
            }

            // A newly fitted part (armour) takes the paint the others have when they are painted together.
            if (_paintCarry is { } previous && PaintTogether && paints.FirstOrDefault(p => previous.ContainsKey(p.MaterialPath) && !p.IsAdded) is { } lead)
            {
                foreach (var paint in paints.Where(p => !previous.ContainsKey(p.MaterialPath)))
                {
                    paint.Follow(lead);
                }
            }

            _paintCarry = null;
            foreach (var paint in paints)
            {
                _paintShown[paint] = PaintState.Of(paint);
                paint.Changed += (_, _) => OnPaintChanged(paint);
            }

            Paints = paints;
            Repaint();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            _services.Logger.LogWarning("Paint of {Vehicle}: {Message}", item.Name, ex.Message);
        }
    }

    /// <summary>What <see cref="ReadPaint"/> found for one material (the view model is made on the UI thread).</summary>
    private sealed record PaintSource(string MaterialPath, string Name, PaintValue Colour, PaintValue? Second, PaintValue? Metal, PaintValue? ClearCoat,
        PaintValue? Added, TextureImage? Mask, PaintValue? Diffuse = null);

    private PaintSource? ReadPaint(AssetCatalog catalog, string materialPath, bool readMask)
    {
        if (!catalog.TryLoadObject<UMaterialInstanceConstant>(materialPath, out var mi))
        {
            return null;
        }

        var vectors = mi.VectorParameterValues.Select(v => v.ParameterInfo.Name.Text).ToList();
        var scalars = mi.ScalarParameterValues.Select(v => v.ParameterInfo.Name.Text).ToList();
        var colourIndex = vectors.IndexOf("Base Color A") is >= 0 and var a ? a : vectors.IndexOf("Paint Color");
        // A body paint is dirtied (lights, dashboards' emissive parts and interiors are not paint). A dirtied car paint
        // material without a colour of its own (the armour) gets one added.
        if (!vectors.Contains("Dirt Color") && (colourIndex < 0 || vectors[colourIndex] == "Base Color A"))
        {
            return null;
        }

        var package = materialPath[..materialPath.LastIndexOf('.')];
        var editing = ReadForEditing(catalog, package);
        var tunables = TunableReader.Read(editing);
        var export = TunableReader.ExportKeys(editing)[0];
        var state = _services.Projects.Current?.State;
        PaintValue? Value(string array, int index)
        {
            if (index < 0)
            {
                return null;
            }

            var path = $"{array}[{index}].ParameterValue";
            var t = tunables.FirstOrDefault(x => x.Path == path && x.CanEdit);
            return t is null ? null : new PaintValue(package, t, state?.GetAssetValue(package, t.Export, t.Path)?.Current ?? t.Value);
        }

        PaintValue Added(string array, string name, TunableKind kind)
        {
            var path = MaterialParameters.PathOf(array, name);
            return new PaintValue(package, new Tunable(export, path, kind, string.Empty), state?.GetAssetValue(package, export, path)?.Current ?? string.Empty);
        }

        var leaf = materialPath[(materialPath.LastIndexOf('.') + 1)..];
        var name = (leaf.StartsWith("MI_", StringComparison.Ordinal) ? leaf[3..] : leaf).Replace('_', ' ');
        if (colourIndex < 0)
        {
            return new PaintSource(materialPath, name,
                Added("VectorParameterValues", "Base Color A", TunableKind.Color), Added("VectorParameterValues", "Base Color B", TunableKind.Color),
                Added("ScalarParameterValues", "CarPaint Metalness", TunableKind.Float), Added("ScalarParameterValues", "ClearCoat Amount", TunableKind.Float),
                Added("TextureParameterValues", "Color Mask", TunableKind.Text), null, Added("TextureParameterValues", "Diffuse Map", TunableKind.Text));
        }

        return Value("VectorParameterValues", colourIndex) is not { } colour
            ? null
            : new PaintSource(materialPath, name, colour,
                Value("VectorParameterValues", vectors.IndexOf("Base Color B")),
                Value("ScalarParameterValues", scalars.IndexOf("CarPaint Metalness")),
                Value("ScalarParameterValues", scalars.IndexOf("ClearCoat Amount")),
                null, readMask ? ReadMask(catalog, mi) : null);
    }

    /// <summary>The material's <c>Color Mask</c> (green: colour A, red: colour B), decoded at preview size; null without one.</summary>
    private TextureImage? ReadMask(AssetCatalog catalog, UMaterialInstanceConstant mi)
    {
        try
        {
            var path = new MaterialInspector(catalog).Inspect(mi).Textures.FirstOrDefault(t => t.Name == "Color Mask")?.TexturePath;
            return path is null || !catalog.TryLoadObject<UTexture2D>(path, out var texture) ? null : TextureDecoder.Decode(texture, maxSize: 1024);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogDebug("Colour mask of {Material}: {Message}", mi.Name, ex.Message);
            return null;
        }
    }

    /// <summary>How bright the body paint's diffuse is where the paint goes (linear luminance), the level added paints match.</summary>
    private float BodyBrightness(PreviewModel model, IReadOnlyList<PaintSource> sources)
    {
        var levels = sources.Where(s => s.Added is null && _paintMasks.ContainsKey(s.MaterialPath))
            .Select(s => Brightness(model, s.MaterialPath, _paintMasks[s.MaterialPath])).Where(b => b > 0).ToList();
        return levels.Count == 0 ? 0.4f : levels.Average();
    }

    /// <summary>Mean linear luminance of <paramref name="material"/>'s diffuse in the preview (weighted by the mask's green).</summary>
    private static float Brightness(PreviewModel model, string material, TextureImage? mask)
    {
        var path = model.Parts.FirstOrDefault(p => string.Equals(p.Material, material, StringComparison.OrdinalIgnoreCase) && p.TexturePath is not null)?.TexturePath;
        if (path is null || !model.Textures.TryGetValue(path, out var image))
        {
            return 0f;
        }

        double sum = 0, weight = 0;
        for (var y = 0; y < image.Height; y += 4)
        {
            for (var x = 0; x < image.Width; x += 4)
            {
                var i = ((y * image.Width) + x) * 4;
                var w = mask is null ? 1.0 : mask.Rgba[((Math.Min(mask.Height - 1, y * mask.Height / image.Height) * mask.Width) + Math.Min(mask.Width - 1, x * mask.Width / image.Width)) * 4 + 1] / 255.0;
                var l = PaintMaterialViewModel.ToLinear(Avalonia.Media.Color.FromRgb(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]));
                sum += w * ((0.2126 * l.X) + (0.7152 * l.Y) + (0.0722 * l.Z));
                weight += w;
            }
        }

        return weight > 0 ? (float)(sum / weight) : 0f;
    }

    partial void OnArmourChanged(PaintArmourChoice? value)
    {
        if (!_armourQuiet && _catalog is { } catalog && SelectedItem is { } item && value is not null && _previewBase is not null)
        {
            _paintCarry = Paints.ToDictionary(p => p.MaterialPath, PaintState.Of, StringComparer.OrdinalIgnoreCase);
            _previewTask = LoadPreviewAsync(catalog, item, value.Key.Length == 0 ? null : value.Key);
        }
    }

    private void OnPaintChanged(PaintMaterialViewModel paint)
    {
        if (!_paintRestoring)
        {
            // One step per pause: a drag through the colour picker is undone at once. A step remembers every part.
            var now = _paintClock.Elapsed;
            if (!ReferenceEquals(paint, _paintLastChanged) || now - _paintLastTime > PaintUndoPause)
            {
                _paintHistory.Push(new Dictionary<PaintMaterialViewModel, PaintState>(_paintShown));
            }

            _paintLastChanged = paint;
            _paintLastTime = now;
            if (PaintTogether)
            {
                _paintRestoring = true; // the followers' own changes are part of this step
                try
                {
                    foreach (var other in Paints.Where(p => !ReferenceEquals(p, paint)))
                    {
                        other.Follow(paint);
                    }
                }
                finally
                {
                    _paintRestoring = false;
                }
            }
        }

        foreach (var p in Paints)
        {
            _paintShown[p] = PaintState.Of(p);
        }

        Repaint();
    }

    /// <summary>Steps the paint back one change (Ctrl+Z while the paint panel has changes to step back).</summary>
    public void UndoPaint()
    {
        if (!_paintHistory.TryPop(out var step))
        {
            return;
        }

        _paintRestoring = true;
        try
        {
            foreach (var (paint, state) in step)
            {
                state.ApplyTo(paint);
            }
        }
        finally
        {
            _paintRestoring = false;
            _paintLastChanged = null;
        }

        Repaint();
    }

    /// <summary>Shows the chosen paints in the 3D view: painted through the colour mask, shiny where the paint is.</summary>
    private void Repaint()
    {
        PaintPendingCount = Paints.Count(p => p.IsPending);
        if (_previewBase is not { } model)
        {
            return;
        }

        var byMaterial = Paints.Where(p => p.Painted).ToDictionary(p => p.MaterialPath, StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, TextureImage>(model.Textures, StringComparer.OrdinalIgnoreCase);
        var parts = new List<PreviewPart>(model.Parts.Count);
        foreach (var part in model.Parts)
        {
            if (!byMaterial.TryGetValue(part.Material, out var paint))
            {
                parts.Add(part);
                continue;
            }

            var surface = new Vector2((float)paint.Metal, (float)paint.ClearCoat);
            if (paint.IsAdded && paint.Plain)
            {
                parts.Add(part with { TexturePath = null, Tint = paint.LinearColour, Surface = surface }); // the plain white finish, painted
            }
            else if (part.TexturePath is { } path && model.Textures.TryGetValue(path, out var diffuse) && !paint.IsAdded && _paintMasks.TryGetValue(paint.MaterialPath, out var mask))
            {
                var key = "#paint/" + paint.MaterialPath;
                textures[key] = Baked(paint, diffuse, mask);
                parts.Add(part with { TexturePath = key, Tint = null, Surface = surface });
            }
            else
            {
                parts.Add(part with { Tint = paint.LinearColour, Surface = surface }); // paint all over (an added paint's mask is white)
            }
        }

        Preview = model with { Parts = parts, Textures = textures };
    }

    /// <summary>The painted texture of <paramref name="paint"/>, baked again only when its colours changed.</summary>
    private TextureImage Baked(PaintMaterialViewModel paint, TextureImage diffuse, TextureImage mask)
    {
        var a = paint.LinearColour;
        Vector4? b = paint.HasSecond ? PaintMaterialViewModel.ToLinear(paint.Second) : null;
        if (_paintBakes.TryGetValue(paint.MaterialPath, out var bake) && bake.A == a && bake.B == b)
        {
            return bake.Image;
        }

        var image = PaintBaker.Bake(diffuse, mask, a, b);
        _paintBakes[paint.MaterialPath] = (a, b, image);
        return image;
    }

    /// <summary>The project changed (an apply, an undo): the applied values are the new starting point.</summary>
    private void ReloadPaints()
    {
        if (HasPaints && PaintPendingCount == 0 && _catalog is { } catalog && SelectedItem is { } item && _previewBase is { } model)
        {
            _ = LoadPaintsAsync(catalog, item, model);
        }
    }

    private bool CanApplyPaint() => PaintPendingCount > 0;

    /// <summary>Records the chosen paints as value edits of their material instances (exported with Export mod).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPaint))]
    private void ApplyPaint()
    {
        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Module.NoProjectChanges"));
            return;
        }

        var changes = Paints.SelectMany(p => p.Changes()).ToList();
        PaintPendingCount = 0; // the project change that follows reads the paints again
        var applied = 0;
        foreach (var (value, text) in changes)
        {
            try
            {
                _services.Projects.Apply(new SetAssetValueOp(value.Package, value.Tunable.Export, value.Tunable.Path, value.Tunable.Kind.ToString(), value.Committed, text));
                applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Module.NotRecorded"), $"{value.Tunable.Path}: {ex.Message}");
            }
        }

        if (applied > 0)
        {
            _services.Notifications.Success(Loc.T("Paint.Applied"), Loc.F("Paint.AppliedDetail", applied));
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyPaint))]
    private void DiscardPaint()
    {
        _paintRestoring = true;
        try
        {
            foreach (var paint in Paints)
            {
                paint.Reset();
            }
        }
        finally
        {
            _paintRestoring = false;
        }

        _paintHistory.Clear();
        foreach (var p in Paints)
        {
            _paintShown[p] = PaintState.Of(p);
        }

        Repaint();
    }
}

/// <summary>An armour kit the 3D view can fit (empty key: none).</summary>
/// <param name="Key"><c>ArmorLight</c>, <c>ArmorHeavy</c> or empty.</param>
public sealed record PaintArmourChoice(string Key)
{
    /// <summary>Text in the choice.</summary>
    public string Label => Key switch
    {
        "ArmorLight" => Loc.T("Paint.Armour.Light"),
        "ArmorHeavy" => Loc.T("Paint.Armour.Heavy"),
        _ => Loc.T("Paint.Armour.None"),
    };

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>What a paint looks like at one moment (a Ctrl+Z step).</summary>
internal readonly record struct PaintState(Avalonia.Media.Color Colour, Avalonia.Media.Color Second, double Metal, double ClearCoat, bool Painted, bool Plain)
{
    public static PaintState Of(PaintMaterialViewModel p) => new(p.Colour, p.Second, p.Metal, p.ClearCoat, p.Painted, p.Plain);

    public void ApplyTo(PaintMaterialViewModel p) => p.Set(Colour, Second, Metal, ClearCoat, Painted, Plain);
}
