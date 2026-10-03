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
        CloneSharesPaint = false;
        var load = ++_paintLoads;
        _paintHistory.Clear();
        _paintShown.Clear();
        PaintPendingCount = 0;
        // Not magazines: an AK's magazine wears the AK's own material, so painting it painted the whole gun (owner).
        if (!IsVehicleModule && item.Asset.Kind is not ScumStudio.Modding.Catalog.ModdableKind.Weapon)
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
            // A clone paints its own copies of the materials (owner: "paint Rager 1 and Rager 2, the default stays"); one made
            // without copies would paint the stock vehicle too, so it gets none.
            var own = item.IsClone && _services.Projects.Current?.State.FindCloneOf(item.PackagePath) is { } clone
                ? clone.Packages.ToDictionary(p => p.Old, p => p.New, StringComparer.OrdinalIgnoreCase)
                : null;
            var weaponItem = !IsVehicleModule;
            var sources = await Task.Run(() => materials.Select(m => ReadPaint(catalog, m, !known.Contains(m), own, weaponItem)).OfType<PaintSource>().ToList()).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedItem, item) || load != _paintLoads)
            {
                return;
            }

            CloneSharesPaint = own is not null && sources.Count == 0;

            foreach (var source in sources.Where(s => s.Mask is not null))
            {
                _paintMasks[source.MaterialPath] = source.Mask!;
            }

            var paints = new List<PaintMaterialViewModel>();
            foreach (var s in sources)
            {
                var paint = new PaintMaterialViewModel(s.MaterialPath, s.Name, s.Colour, s.Second, s.Metal, s.ClearCoat, s.Added, s.Diffuse,
                    s.Tint, s.Tint ? TintGain(model, s.MaterialPath) : 1f);
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
    /// <summary>Every part back to the game's own paint (one undo step; Apply paint records it).</summary>
    [RelayCommand]
    private void AllOriginalPaint()
    {
        foreach (var paint in Paints)
        {
            paint.OriginalCommand.Execute(null);
        }
    }

    /// <summary>What the paint panel is about: a vehicle's body paint or a weapon's colour.</summary>
    public string PaintCaption => IsVehicleModule ? Loc.T("Paint.Caption") : Loc.T("Paint.Caption.Weapon");

    /// <summary>Who gets the paint in game: every one of the model, or only a clone (its own spawn command).</summary>
    public string PaintNote => SelectedItem?.IsClone == true ? Loc.F("Paint.Note.Clone", SpawnCommandText)
        : IsVehicleModule ? Loc.T("Paint.Note") : Loc.T("Paint.Note.Weapon");

    private sealed record PaintSource(string MaterialPath, string Name, PaintValue Colour, PaintValue? Second, PaintValue? Metal, PaintValue? ClearCoat,
        PaintValue? Added, TextureImage? Mask, PaintValue? Diffuse = null, bool Tint = false);

    /// <summary>A master material's tint, metal and colour texture parameters (texture null: no even finish).</summary>
    private sealed record TintParameters(string Colour, string Metal, string? Texture);

    /// <summary>M_Weapons_Master: Difuse_Colorization multiplies the texture.</summary>
    private static readonly TintParameters WeaponTint = new("Difuse_Colorization", "MetallicAmount", "Color");

    /// <summary>M_ObjectsSkinMaster (a few knives, the baton, the flare gun): its texture holds roughness too, so no even finish.</summary>
    private static readonly TintParameters ObjectTint = new("Diffuse Colorization Global", "Metalness Max", null);

    /// <summary>
    /// How much a weapon's tint is lifted so the colour comes out as picked over its own (dark) texture: the texture's mean
    /// linear luminance brought to about that of a mid-grey, between 1 and 6 times.
    /// </summary>
    private static float TintGain(PreviewModel model, string material)
    {
        var path = model.Parts.FirstOrDefault(p => string.Equals(p.Material, material, StringComparison.OrdinalIgnoreCase) && p.TexturePath is not null)?.TexturePath;
        if (path is null || !model.Textures.TryGetValue(path, out var image))
        {
            return 1f;
        }

        double sum = 0;
        var n = 0;
        for (var i = 0; i < image.Rgba.Length; i += 64)
        {
            var l = PaintMaterialViewModel.ToLinear(Avalonia.Media.Color.FromRgb(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]));
            sum += (0.2126 * l.X) + (0.7152 * l.Y) + (0.0722 * l.Z);
            n++;
        }

        return n == 0 ? 1f : Math.Clamp(0.3f / Math.Max(1e-3f, (float)(sum / n)), 1f, 6f);
    }

    /// <summary>A clone without its own copies of the paint (made before clones had them): painting it is off.</summary>
    [ObservableProperty]
    private bool _cloneSharesPaint;

    /// <param name="catalog">The game files.</param>
    /// <param name="materialPath">The material instance a preview part is drawn with (the stock one).</param>
    /// <param name="readMask">Decode its colour mask too.</param>
    /// <param name="own">A clone's stock → copy package map: the paint is edited in the copy, and a material without one is not offered.</param>
    /// <param name="weaponItem">The material is a weapon's (the object master's tint counts as its paint too).</param>
    private PaintSource? ReadPaint(AssetCatalog catalog, string materialPath, bool readMask, IReadOnlyDictionary<string, string>? own = null, bool weaponItem = false)
    {
        if (!catalog.TryLoadObject<UMaterialInstanceConstant>(materialPath, out var mi))
        {
            return null;
        }

        var stockPackage = materialPath[..materialPath.LastIndexOf('.')];
        var package = own is null ? stockPackage : own.GetValueOrDefault(stockPackage);
        if (package is null)
        {
            return null;
        }

        var vectors = mi.VectorParameterValues.Select(v => v.ParameterInfo.Name.Text).ToList();
        var scalars = mi.ScalarParameterValues.Select(v => v.ParameterInfo.Name.Text).ToList();
        var colourIndex = vectors.IndexOf("Base Color A") is >= 0 and var a ? a : vectors.IndexOf("Paint Color");
        // A weapon's material takes a tint: told by its master, not by what it stores (124 weapon materials leave both
        // the tint and the metal to M_Weapons_Master: the knives, the MP5, the AS Val had no paint; owner).
        var chain = new MaterialInspector(catalog).Inspect(mi).ParentChain;
        var weapon = chain.Any(p => p.Contains("/M_Weapons_Master.", StringComparison.OrdinalIgnoreCase)) ? WeaponTint
            : weaponItem && chain.Any(p => p.Contains("/M_ObjectsSkinMaster_MainShader.", StringComparison.OrdinalIgnoreCase)) ? ObjectTint
            : null;
        // A body paint is dirtied (lights, dashboards' emissive parts and interiors are not paint). A dirtied car paint
        // material without a colour of its own (the armour) gets one added.
        if (weapon is null && !vectors.Contains("Dirt Color") && (colourIndex < 0 || vectors[colourIndex] == "Base Color A"))
        {
            return null;
        }

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
        if (weapon is not null)
        {
            // By name (the export patches a stored entry or adds one); "as the game has it" is what this material stores.
            PaintValue Keyed(string array, string param, TunableKind kind, string stock)
            {
                var path = MaterialParameters.PathOf(array, param);
                return new PaintValue(package, new Tunable(export, path, kind, stock), state?.GetAssetValue(package, export, path)?.Current ?? stock);
            }

            string Stored(string array, List<string> names, string param) =>
                names.IndexOf(param) is >= 0 and var i && tunables.FirstOrDefault(t => t.Path == $"{array}[{i}].ParameterValue") is { } t ? t.Value : string.Empty;

            var textures = mi.TextureParameterValues.Select(v => v.ParameterInfo.Name.Text).ToList();
            var texture = weapon.Texture is { } param && textures.Contains(param)
                ? new MaterialInspector(catalog).Inspect(mi).Textures.FirstOrDefault(t => t.Name == param)?.TexturePath ?? string.Empty
                : string.Empty;
            return new PaintSource(materialPath, name,
                Keyed("VectorParameterValues", weapon.Colour, TunableKind.Color, Stored("VectorParameterValues", vectors, weapon.Colour)), null,
                Keyed("ScalarParameterValues", weapon.Metal, TunableKind.Float, Stored("ScalarParameterValues", scalars, weapon.Metal)), null,
                null, null, weapon.Texture is { } flat ? Keyed("TextureParameterValues", flat, TunableKind.Text, texture) : null, Tint: true);
        }

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
            else if (!paint.IsTint && part.TexturePath is { } path && model.Textures.TryGetValue(path, out var diffuse)
                     && (paint.IsAdded ? WhiteMask : _paintMasks.GetValueOrDefault(paint.MaterialPath)) is { } mask)
            {
                // Through the colour mask; an added paint (armour) is paint all over, its plate print adding the scratches.
                var key = "#paint/" + paint.MaterialPath;
                textures[key] = Baked(paint, diffuse, mask);
                parts.Add(part with { TexturePath = key, Tint = null, Surface = surface });
            }
            else
            {
                parts.Add(part with { Tint = paint.LinearColour, Surface = surface }); // a weapon's tint times its own texture
            }
        }

        Preview = model with { Parts = parts, Textures = textures };
    }

    /// <summary>The export's white colour mask on an added paint (<see cref="PaintMaterialViewModel.WhiteMask"/>): paint everywhere.</summary>
    private static readonly TextureImage WhiteMask = new("WhiteMask", 1, 1, [255, 255, 255, 255], "PF_B8G8R8A8", 0, 1, 1, false, false);

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
