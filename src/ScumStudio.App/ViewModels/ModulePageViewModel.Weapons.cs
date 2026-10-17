using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Modding.Catalog;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Weapons studio (owner, r37): the gun drawn with its own metal, wood and occlusion (<see cref="WeaponLook"/>), the
/// box-made person off by default, each material of the gun named after the part it paints, and the clone's in-game name
/// and spawn command in plain sight.
/// </summary>
public abstract partial class ModulePageViewModel
{
    /// <summary>The look of each gun material by its path: stock, and the even finish (made when first asked for).</summary>
    private readonly Dictionary<string, WeaponLookPair> _weaponLooks = new(StringComparer.OrdinalIgnoreCase);

    private bool? _showScaleFigure;

    /// <summary>True on the Weapons page.</summary>
    public bool IsWeaponModule => _kinds.Contains(ModdableKind.Weapon);

    /// <summary>
    /// The 1.8 m person stands beside the model while Scale is on: on by default for vehicles, off for weapons (owner: "the
    /// box-made character next to the weapons"; the grid stays).
    /// </summary>
    public bool ShowScaleFigure
    {
        get => _showScaleFigure ?? !IsWeaponModule;
        set
        {
            if (value != ShowScaleFigure)
            {
                _showScaleFigure = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The paint switch's text: one colour for the whole vehicle, or the same colour on every part of the gun.</summary>
    public string PaintTogetherText => IsVehicleModule ? Loc.T("Paint.Together") : Loc.T("Weapons.Paint.Together");

    /// <summary>The paint switch's tip.</summary>
    public string PaintTogetherTip => IsVehicleModule ? Loc.T("Paint.Together.Tip") : Loc.T("Weapons.Paint.Together.Tip");

    /// <summary>What the rows of the paint list are (weapons: one row per part of the gun the game colours on its own).</summary>
    public string PaintListNote => IsVehicleModule ? string.Empty : Loc.T("Weapons.Paint.Rows");

    /// <summary>The list note is shown (weapons).</summary>
    public bool HasPaintListNote => !IsVehicleModule;

    /// <summary>The in-game name typed for the selected weapon (its entity setup's <c>Caption</c>).</summary>
    [ObservableProperty]
    private string _captionEdit = string.Empty;

    // A name the game takes from a text table ("[/Game/…/ST_UI_Items.ST_UI_Items:RagerItemContainer]") is no name to edit:
    // the box stays empty with its "In-game name" hint until a name is typed (review 3.5.0: the Rager showed the reference).
    partial void OnCaptionChanged(string value) => CaptionEdit = IsTextTableReference(value) ? string.Empty : value;

    /// <summary>True for a text the game reads from a string table (<c>[/Game/…:Key]</c>), not a typed name.</summary>
    internal static bool IsTextTableReference(string? text) =>
        text is { Length: > 3 } t && t.StartsWith("[/", StringComparison.Ordinal) && t.EndsWith(']') && t.Contains(':', StringComparison.Ordinal);

    /// <summary>Records <see cref="CaptionEdit"/> as the selected item's in-game name (one journal step).</summary>
    [RelayCommand]
    private async Task ApplyCaptionAsync()
    {
        var text = CaptionEdit.Trim();
        if (SelectedItem is null || text.Length == 0 || text == Caption)
        {
            return;
        }

        var part = SelectedPart;
        if (await SetCaptionAsync(text).ConfigureAwait(true) && part is not null && !ReferenceEquals(part, SelectedPart))
        {
            SelectedPart = part; // back to the values that were shown
        }
    }

    /// <summary>
    /// Owner: "let the user pick parts of the weapon one by one". A paint row's eye shows the part it paints selected in the
    /// 3D view (the magazine, the stock …); again (or another row's) moves or clears it.
    /// </summary>
    [RelayCommand]
    private void ShowPaintPart(PaintMaterialViewModel? paint)
    {
        HighlightAttachment = paint is null || string.Equals(HighlightAttachment, paint.MaterialPath, StringComparison.OrdinalIgnoreCase) ? string.Empty : paint.MaterialPath;
        if (HighlightAttachment.Length > 0)
        {
            ShowPreviewTab = true;
        }
    }

    /// <summary>"#SpawnItem Weapon_AKM_Gold": the command the clone being named will spawn with, or empty while the name is not valid.</summary>
    public string CloneSpawnText
    {
        get
        {
            var name = EffectiveCloneName; // the name the clone is saved under (Rager_1 typed → Rager_v1)
            if (SelectedItem is null || SelectedItem.IsClone || !Modding.Cloning.CloneFamilyPlanner.IsValidName(name))
            {
                return string.Empty;
            }

            return SelectedItem.Asset.Kind == ModdableKind.Vehicle
                ? "#SpawnVehicle BPC_" + (name.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name)
                : "#SpawnItem " + name;
        }
    }

    /// <summary>The selected entry is a clone: its name, spawn command and template are shown on the clone card.</summary>
    public string CloneOfText => SelectedItem is { IsClone: true } clone ? Loc.F("Weapons.Clone.Of", ModuleItemViewModel.ShortName(clone.CloneOf!)) : string.Empty;

    /// <summary>The part a gun material paints: "Magazine" when only the magazine laid out beside the gun wears it, else by its name (<see cref="WeaponPartLabel"/>).</summary>
    private string WeaponPaintLabel(PreviewModel model, string materialPath, int index)
    {
        var users = model.Parts.Where(p => string.Equals(p.Material, materialPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (users.Count > 0 && users.All(IsLaidOutMagazine))
        {
            return Loc.T("Weapons.Part.Magazine");
        }

        // Named after the part it paints; the only material besides the magazines paints the whole weapon (the SVD, a knife);
        // else by what it is made of: mostly metal (the receiver), or not (wood, polymer).
        var own = model.Parts.Where(p => p.Material.Length > 0 && !IsLaidOutMagazine(p)).Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return NamedPart(materialPath) is { } named ? Loc.T(PartKey(named))
            : own == 1 ? Loc.T("Weapons.Part.Whole")
            : _weaponLooks.TryGetValue(materialPath, out var look) ? Loc.T(look.Stock.MetalShare >= MetalPartShare ? "Weapons.Part.Metal" : "Weapons.Part.Furniture")
            : WeaponPartLabel(materialPath, index);
    }

    /// <summary>
    /// The readable part a gun material paints: "Magazine" for a drum, "Stock" for a butt … from its name, else the gun's
    /// body for its first material and "Part 2" for the others (<paramref name="index"/> is the material's place in the list).
    /// </summary>
    internal static string WeaponPartLabel(string materialPath, int index) =>
        NamedPart(materialPath) is { } named ? Loc.T(PartKey(named)) : index == 0 ? Loc.T("Weapons.Part.Body") : Loc.F("Weapons.Part.Other", index + 1);

    /// <summary>The string key of a gun part's name (<c>Weapons.Part.Magazine</c>).</summary>
    private static string PartKey(string part) => "Weapons.Part." + part;

    /// <summary>A gun material whose texture is at least this much metal paints the metal parts; less: the stock, grip and handguard.</summary>
    private const float MetalPartShare = 0.4f;

    /// <summary>The part a material's name tells (<c>Drum</c>: Magazine, <c>Receiver</c>: Body …), or null.</summary>
    private static string? NamedPart(string materialPath)
    {
        var leaf = materialPath[(materialPath.LastIndexOfAny(['/', '.']) + 1)..];
        (string Token, string Key)[] known =
        [
            ("Drum", "Magazine"), ("Mag", "Magazine"), ("Clip", "Magazine"), ("Stock", "Stock"), ("Butt", "Stock"),
            ("Scope", "Sight"), ("Sight", "Sight"), ("Optic", "Sight"), ("Reticle", "Sight"), ("Lens", "Sight"), ("Glass", "Sight"),
            ("Silencer", "Muzzle"), ("Suppressor", "Muzzle"), ("Muzzle", "Muzzle"), ("Barrel", "Muzzle"), ("Bayonet", "Muzzle"),
            ("Handguard", "Handguard"), ("Grip", "Handguard"), ("Wood", "Handguard"), ("Rail", "Rail"), ("Bipod", "Bipod"),
            ("Strap", "Strap"), ("Sling", "Strap"), ("Arrow", "Ammo"), ("Shell", "Ammo"), ("Bullet", "Ammo"), ("Rocket", "Ammo"),
            ("Receiver", "Body"), ("Frame", "Body"), ("Slide", "Body"),
        ];
        foreach (var (token, key) in known)
        {
            if (leaf.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the look of every gun material of <paramref name="model"/> (on a worker): its colour texture's pixels from
    /// <paramref name="pixels"/> (decoded into it when missing) with its <c>AOMEC</c> map, roughness range and metal.
    /// </summary>
    private Dictionary<string, WeaponLookPair> ReadWeaponLooks(AssetCatalog catalog, PreviewModel model, Dictionary<string, TextureImage> pixels)
    {
        var looks = new Dictionary<string, WeaponLookPair>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in model.Parts.Where(p => p.Material.Length > 0 && p.TexturePath is not null).DistinctBy(p => p.Material, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new MaterialInspector(catalog).Inspect(part.Material);
                if (info.Textures.FirstOrDefault(t => t.Name == WeaponLook.PackedMapParameter)?.TexturePath is not { } packedPath
                    || RgbaOf(model, part.TexturePath!, pixels, p => DecodePixels(catalog, p), PaintTextureSize) is not { } colour
                    || !catalog.TryLoadObject<UTexture2D>(packedPath, out var packedTexture))
                {
                    continue;
                }

                var packed = TextureDecoder.Decode(packedTexture, maxSize: PaintTextureSize);
                float Scalar(string name, float fallback) => info.Scalars.FirstOrDefault(s => s.Name == name)?.Value ?? fallback;
                var (min, max, metal) = (Scalar("Roughness Min", 0.15f), Scalar("Roughness Max", 1f), Scalar("MetallicAmount", 0.75f));
                looks[part.Material] = new WeaponLookPair(WeaponLook.Bake(colour, packed, min, max, metal), () => WeaponLook.Bake(colour, packed, min, max, metal, flat: true));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _services.Logger.LogDebug("Gun look of {Material}: {Message}", part.Material, ex.Message);
            }
        }

        return looks;
    }

    /// <summary>
    /// <paramref name="part"/> drawn with its gun look (and <paramref name="paint"/>'s tint, metal and even finish when it is
    /// painted), its texture put in <paramref name="textures"/>; null when the material has no look.
    /// </summary>
    private PreviewPart? WithWeaponLook(PreviewPart part, PaintMaterialViewModel? paint, Dictionary<string, TextureImage> textures)
    {
        if (!_weaponLooks.TryGetValue(part.Material, out var looks))
        {
            return null;
        }

        var painted = paint is { Painted: true };
        var flat = painted && paint!.HasPlain && paint.Plain;
        var look = flat ? looks.Flat : looks.Stock;
        var key = (flat ? "#flatlook/" : "#look/") + part.Material;
        textures[key] = look.Image;
        return part with
        {
            TexturePath = key,
            Tint = painted ? paint!.LinearColour : part.Tint,
            Surface = new Vector2(painted && paint!.HasMetal ? (float)paint.Metal : look.Metal, 0f),
            Roughness = look.Roughness,
            AlphaCutoff = 0f, // the look's alpha is the metal, never a cut-out
        };
    }

    /// <summary>The Weapons page's model of <paramref name="item"/>: its gun parts opaque, a gun's magazines laid out beside it.</summary>
    private static PreviewModel? WeaponModel(AssetCatalog catalog, ModuleItemViewModel item, PreviewModel? model)
    {
        model = OpaqueGuns(catalog, model);
        return item.Asset.Kind == ModdableKind.Weapon ? WithMagazinesApart(model) : model;
    }

    /// <summary>
    /// The parts of <paramref name="model"/> drawn with SCUM's gun master (<c>M_Weapons_Master</c>) made opaque. The master is
    /// a masked material whose opacity is its dissolve effect, not the colour texture's alpha (that is the roughness): drawn
    /// cut out by that alpha, as the previews draw leaves, a gun lost its smooth metal and the AK magazine came out ghostly
    /// (owner: "weapon textures look broken in 3D").
    /// </summary>
    internal static PreviewModel? OpaqueGuns(AssetCatalog catalog, PreviewModel? model)
    {
        if (model is null || model.Parts.All(p => p.AlphaCutoff <= 0f))
        {
            return model;
        }

        var inspector = new MaterialInspector(catalog);
        var guns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsGun(string material)
        {
            if (!guns.TryGetValue(material, out var gun))
            {
                try
                {
                    gun = material.Length > 0 && inspector.Inspect(material).ParentChain.Any(p => p.Contains("/M_Weapons_Master.", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    gun = false;
                }

                guns[material] = gun;
            }

            return gun;
        }

        return model with { Parts = model.Parts.Select(p => p.AlphaCutoff > 0f && IsGun(p.Material) ? p with { AlphaCutoff = 0f } : p).ToList() };
    }

    /// <summary>Gap between the gun and the magazines laid out under it, and between the magazines (cm).</summary>
    private const float ExplodedGap = 6f;

    /// <summary>The materials of the gun's magazines that are laid out under it (its mesh carries every magazine it takes).</summary>
    private IReadOnlySet<string> _laidOutMagazines = new HashSet<string>();

    /// <summary>The gun shown has magazines laid out beside it (the exploded view switch is offered).</summary>
    [ObservableProperty]
    private bool _hasWeaponMagazine;

    /// <summary>The magazines are drawn beside the gun (off: the gun alone, no magazine in it).</summary>
    [ObservableProperty]
    private bool _showWeaponMagazine = true;

    partial void OnShowWeaponMagazineChanged(bool value) => Repaint();

    /// <summary>Notes which magazine materials <paramref name="model"/> lays out beside the gun.</summary>
    private void NoteMagazine(PreviewModel? model)
    {
        _laidOutMagazines = model is null || !IsWeaponModule
            ? new HashSet<string>()
            : model.Parts.Where(p => IsMagazineMaterial(p.Material)).Select(p => p.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HasWeaponMagazine = _laidOutMagazines.Count > 0;
    }

    /// <summary>A magazine part laid out beside the gun.</summary>
    private bool IsLaidOutMagazine(PreviewPart part) => _laidOutMagazines.Contains(part.Material);

    /// <summary>A gun material that paints a magazine (a drum, a box magazine, a clip), by its name.</summary>
    internal static bool IsMagazineMaterial(string materialPath)
    {
        var leaf = materialPath[(materialPath.LastIndexOfAny(['/', '.']) + 1)..];
        return leaf.Contains("Drum", StringComparison.OrdinalIgnoreCase) || leaf.Contains("Magazine", StringComparison.OrdinalIgnoreCase)
               || leaf.EndsWith("_Mag", StringComparison.OrdinalIgnoreCase) || leaf.Contains("_Mag_", StringComparison.OrdinalIgnoreCase)
               || leaf.Contains("Clip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Owner: "magazines and other attachments shown NEXT TO the weapon, not hidden inside". A SCUM gun's mesh carries every
    /// magazine it takes as its own material sections, all in the magazine well (the AKM: its AK magazine and the RPK
    /// drum); the game shows the one fitted. Here they are laid out side by side under the gun, each where it can be seen
    /// and coloured. The gun itself is <paramref name="model"/>'s other parts; a model without such sections is returned as it is.
    /// </summary>
    internal static PreviewModel? WithMagazinesApart(PreviewModel? model)
    {
        if (model is null)
        {
            return null;
        }

        var magazines = model.Parts.Where(p => IsMagazineMaterial(p.Material)).ToList();
        if (magazines.Count == 0 || magazines.Count == model.Parts.Count)
        {
            return model;
        }

        var (gunMin, gunMax) = UeBounds(model.Parts.Where(p => !IsMagazineMaterial(p.Material)));
        var alongX = gunMax.X - gunMin.X >= gunMax.Y - gunMin.Y; // the gun's length runs along this axis
        var boxes = magazines.Select(UeBounds).ToList();
        var width = boxes.Sum(b => alongX ? b.Max.X - b.Min.X : b.Max.Y - b.Min.Y) + (ExplodedGap * (boxes.Count - 1));
        var cursor = ((alongX ? gunMin.X + gunMax.X : gunMin.Y + gunMax.Y) / 2f) - (width / 2f);
        var cross = alongX ? (gunMin.Y + gunMax.Y) / 2f : (gunMin.X + gunMax.X) / 2f;
        var moved = new Dictionary<PreviewPart, PreviewPart>();
        for (var i = 0; i < magazines.Count; i++)
        {
            var (min, max) = boxes[i];
            var along = (alongX ? min.X : min.Y) - cursor;
            var side = ((alongX ? min.Y + max.Y : min.X + max.X) / 2f) - cross;
            var offset = new Core.Mathematics.FVector(alongX ? -along : -side, alongX ? -side : -along, gunMin.Z - ExplodedGap - max.Z);
            moved[magazines[i]] = magazines[i] with { Transform = magazines[i].Transform * new Core.Mathematics.FTransform(offset) };
            cursor += (alongX ? max.X - min.X : max.Y - min.Y) + ExplodedGap;
        }

        return model with { Parts = model.Parts.Select(p => moved.GetValueOrDefault(p) ?? p).ToList() };
    }

    /// <summary>The UE-space bounds of <paramref name="parts"/>: the vertices each part's triangles use, where the part sits.</summary>
    internal static (Core.Mathematics.FVector Min, Core.Mathematics.FVector Max) UeBounds(IEnumerable<PreviewPart> parts)
    {
        var min = new Core.Mathematics.FVector(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Core.Mathematics.FVector(float.MinValue, float.MinValue, float.MinValue);
        foreach (var part in parts)
        {
            var (a, b) = UeBounds(part);
            min = new(MathF.Min(min.X, a.X), MathF.Min(min.Y, a.Y), MathF.Min(min.Z, a.Z));
            max = new(MathF.Max(max.X, b.X), MathF.Max(max.Y, b.Y), MathF.Max(max.Z, b.Z));
        }

        return (min, max);
    }

    /// <summary>The UE-space bounds of the vertices <paramref name="part"/>'s triangles use (a section of a shared mesh: its own, not the mesh's).</summary>
    private static (Core.Mathematics.FVector Min, Core.Mathematics.FVector Max) UeBounds(PreviewPart part)
    {
        var min = new Core.Mathematics.FVector(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Core.Mathematics.FVector(float.MinValue, float.MinValue, float.MinValue);
        var positions = part.Mesh.Positions;
        foreach (var index in part.Mesh.Indices)
        {
            var v = part.Transform.TransformPosition(new Core.Mathematics.FVector(positions[index * 3], positions[(index * 3) + 1], positions[(index * 3) + 2]));
            min = new(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
            max = new(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
        }

        return (min, max);
    }

    /// <summary>A gun material's stock look and its even finish (baked when first shown).</summary>
    private sealed class WeaponLookPair(WeaponLook stock, Func<WeaponLook> flat)
    {
        private WeaponLook? _flat;

        public WeaponLook Stock { get; } = stock;

        public WeaponLook Flat => _flat ??= flat();
    }
}
