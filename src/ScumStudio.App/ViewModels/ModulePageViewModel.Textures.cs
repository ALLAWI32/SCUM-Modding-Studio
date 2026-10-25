using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Textures;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Textures (vehicles; m7ammed: "tools for editing materials and importing textures"): the texture slots of a clone's paint
/// materials. Replace writes the user's picture as the clone's own texture at export (<see cref="TextureWriter"/>, the stock
/// texture's format and layout, under the clone's folder) and points the clone's material and its streaming data at it: one
/// journal step, so Ctrl+Z takes all of it back. The game's texture never changes.
/// </summary>
public abstract partial class ModulePageViewModel
{
    private int _textureLoads;

    /// <summary>The material whose textures are listed.</summary>
    [ObservableProperty]
    private TextureMaterialViewModel? _textureMaterial;

    /// <summary>The materials the picker offers: the paint slots of a vehicle or weapon, else every material of the model's parts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextureMaterials), nameof(ShowStudioPanel), nameof(ShowMaterialsPanel), nameof(ShowTexturesPanel), nameof(IsTexturesOnly))]
    private IReadOnlyList<TextureMaterialViewModel> _textureMaterials = [];

    /// <summary>There is a material to list.</summary>
    public bool HasTextureMaterials => TextureMaterials.Count > 0;

    /// <summary>The texture slots of <see cref="TextureMaterial"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextureSlots))]
    private IReadOnlyList<TextureSlotViewModel> _textureSlots = [];

    /// <summary>The Textures panel has something to show.</summary>
    public bool HasTextureSlots => TextureSlots.Count > 0;

    partial void OnPaintsChanged(IReadOnlyList<PaintMaterialViewModel> value) =>
        TextureMaterials = [.. value.Select(p => new TextureMaterialViewModel(p.MaterialPath, p.Name))];

    partial void OnTextureMaterialsChanged(IReadOnlyList<TextureMaterialViewModel> value) =>
        TextureMaterial = value.FirstOrDefault(p => p.MaterialPath == TextureMaterial?.MaterialPath) ?? value.FirstOrDefault();

    partial void OnTextureMaterialChanged(TextureMaterialViewModel? value) => _ = LoadTextureSlotsAsync(value);

    /// <summary>
    /// Replaces texture <paramref name="parameter"/> of <paramref name="materialPath"/> (the stock material a preview part
    /// wears) on the selected clone with <paramref name="image"/>. False, with a warning, for a stock item.
    /// </summary>
    public async Task<bool> ReplaceTextureAsync(string materialPath, string parameter, TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (TexturePlan(materialPath, parameter) is not { } plan || _services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("Studio.Textures.Title"), Loc.T("Studio.Textures.CloneFirst"));
            return false;
        }

        try
        {
            var file = await ProjectTextures.SaveAsync(project.DirectoryPath, image.Rgba, image.Width, image.Height).ConfigureAwait(true);
            if (TextureReplaceOp(project.State, plan with { File = file }) is { Ops.Count: > 0 } op) // the same picture again records nothing
            {
                _services.Projects.Apply(op);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), $"{parameter}: {ex.Message}");
            return false;
        }

        _services.Notifications.Success(Loc.T("Studio.Textures.Replaced"),
            Loc.F("Studio.Textures.ReplacedDetail", parameter, image.Name, plan.Width, plan.Height)
            + (image.Width != plan.Width || image.Height != plan.Height ? " " + Loc.F("Studio.Textures.Scaled", image.Width, image.Height, plan.Width, plan.Height) : string.Empty));
        await LoadTextureSlotsAsync(TextureMaterial).ConfigureAwait(true);
        return true;
    }

    /// <summary>Texture <paramref name="parameter"/> back to the game's own (one journal step); false when it was not replaced.</summary>
    public async Task<bool> ResetTextureAsync(string materialPath, string parameter)
    {
        if (TexturePlan(materialPath, parameter) is not { } plan || _services.Projects.Current is not { } project
            || TextureResetOp(project.State, plan) is not { } op)
        {
            return false;
        }

        _services.Projects.Apply(op);
        await LoadTextureSlotsAsync(TextureMaterial).ConfigureAwait(true);
        return true;
    }

    /// <summary>The three value edits of a replace, as one step: the pixels, the material's parameter, its streaming entry.</summary>
    internal static BatchOp TextureReplaceOp(EditState state, TextureReplacePlan plan)
    {
        string Current(string package, string export, string path, string stock) => state.GetAssetValue(package, export, path)?.Current ?? stock;
        var pixels = TextureWrites.Value(plan.TemplatePath, plan.File);
        var parameterPath = MaterialParameters.PathOf("TextureParameterValues", plan.Parameter);
        var streamingPath = MaterialTextureStreaming.PathOf(plan.TemplateLeaf);
        SetAssetValueOp[] ops =
        [
            new(plan.TexturePackage, plan.TextureLeaf, TextureWrites.PixelsPath, "Texture",
                Current(plan.TexturePackage, plan.TextureLeaf, TextureWrites.PixelsPath, string.Empty), pixels),
            new(plan.MaterialPackage, plan.MaterialExport, parameterPath, "Text",
                Current(plan.MaterialPackage, plan.MaterialExport, parameterPath, plan.TemplatePath), plan.TexturePackage + "." + plan.TextureLeaf),
            new(plan.MaterialPackage, plan.MaterialExport, streamingPath, "Text",
                Current(plan.MaterialPackage, plan.MaterialExport, streamingPath, plan.TemplateLeaf), plan.TextureLeaf),
        ];
        // A second picture for the same slot changes only the pixels: the material already points at the clone's texture.
        return new BatchOp(Loc.T("Studio.Textures.Replaced"), ops.Where(o => o.Old != o.New).ToList<EditOp>());
    }

    /// <summary>The edits of <see cref="TextureReplaceOp"/> set back to their starting values, or null when none is set.</summary>
    internal static BatchOp? TextureResetOp(EditState state, TextureReplacePlan plan)
    {
        var keys = new (string Package, string Export, string Path)[]
        {
            (plan.TexturePackage, plan.TextureLeaf, TextureWrites.PixelsPath),
            (plan.MaterialPackage, plan.MaterialExport, MaterialParameters.PathOf("TextureParameterValues", plan.Parameter)),
            (plan.MaterialPackage, plan.MaterialExport, MaterialTextureStreaming.PathOf(plan.TemplateLeaf)),
        };
        var ops = keys.Select(k => state.GetAssetValue(k.Package, k.Export, k.Path))
            .OfType<AssetValueOverride>()
            .Select(v => (EditOp)new SetAssetValueOp(v.Package, v.Export, v.Path, v.ValueKind, v.Current, v.Base))
            .ToList();
        return ops.Count == 0 ? null : new BatchOp(Loc.T("Studio.Textures.Reset"), ops);
    }

    /// <summary>
    /// The texture slots of a material instance (name, stock texture, size, format, sRGB), in the material's order. Only
    /// slots that hold a 2D texture: a car paint's reflection slot holds a cube map (<c>T_CubeMap_Dummy_01</c>, a
    /// <c>TextureCube</c>) that no picture can replace, and loading it as a 2D texture threw and closed the panel (owner,
    /// 2026-10-19, debugging 3.5.1 on his Rager clone).
    /// </summary>
    internal static IReadOnlyList<TextureSlot> ReadTextureSlots(AssetCatalog catalog, string materialPath)
    {
        if (!catalog.TryLoadObject<UMaterialInstanceConstant>(materialPath, out var mi))
        {
            return [];
        }

        var slots = new List<TextureSlot>();
        foreach (var texture in new MaterialInspector(catalog).Inspect(mi).Textures.Where(t => t.TexturePath is { Length: > 0 }))
        {
            if (!catalog.TryLoadObject<UTexture2D>(texture.TexturePath!, out var t))
            {
                continue; // a cube map, a texture array, or a texture the game files do not have
            }

            var info = TextureDecoder.Describe(t);
            slots.Add(new TextureSlot(texture.Name, texture.TexturePath!, info.Width, info.Height, info.PixelFormat, info.IsSrgb));
        }

        return slots;
    }

    /// <summary>Where a replace of <paramref name="parameter"/> goes for the selected clone; null for a stock item or an unknown slot.</summary>
    private TextureReplacePlan? TexturePlan(string materialPath, string parameter)
    {
        if (SelectedItem is not { IsClone: true } item || _catalog is not { } catalog
            || _services.Projects.Current?.State.FindCloneOf(item.PackagePath) is not { } clone)
        {
            return null;
        }

        var stockPackage = materialPath[..materialPath.LastIndexOf('.')];
        var material = clone.Packages.FirstOrDefault(p => string.Equals(p.Old, stockPackage, StringComparison.OrdinalIgnoreCase))?.New;
        var slot = ReadTextureSlots(catalog, materialPath).FirstOrDefault(s => s.Parameter == parameter);
        if (material is null || slot is null)
        {
            return null;
        }

        var texture = TextureWrites.NewPackage(clone.NewPrimary, material, parameter);
        var export = TunableReader.ExportKeys(ReadForEditing(catalog, material))[0];
        return new TextureReplacePlan(texture, texture[(texture.LastIndexOf('/') + 1)..], slot.TexturePath, slot.TexturePath[(slot.TexturePath.LastIndexOf('.') + 1)..],
            material, export, parameter, string.Empty, slot.Width, slot.Height);
    }

    /// <summary>Largest side of a slot's picture in the Textures panel.</summary>
    private const int SlotPictureSize = 128;

    /// <summary>The parts of the vehicle that wear the material whose textures are listed ("Body, Door FrontLeft …").</summary>
    [ObservableProperty]
    private string _textureMaterialParts = string.Empty;

    /// <summary>The pictures replaced on the selected clone, as the 3D view shows them: (material, stock texture) → picture.</summary>
    private Dictionary<(string Material, string Texture), TextureImage> _replacedShown = [];

    /// <summary>Pictures of the project read once each (file → image).</summary>
    private readonly Dictionary<string, TextureImage> _replacedFiles = new(StringComparer.OrdinalIgnoreCase);

    private int _replacedLoads;

    /// <summary>The names of the preview parts drawn with <paramref name="materialPath"/>, joined for the panel.</summary>
    private string PartsWearing(string materialPath)
    {
        var names = (Preview?.Parts ?? []).Where(p => string.Equals(p.Material, materialPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Attachment.Length > 0 ? VehicleParts.Label(p.Attachment + "." + p.Attachment[(p.Attachment.LastIndexOf('/') + 1)..] + "_C") : Loc.T("Studio.Textures.Body"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? string.Empty : Loc.F("Studio.Textures.WornBy", string.Join(", ", names.Take(8)) + (names.Count > 8 ? " …" : string.Empty));
    }

    /// <summary>The slot's picture at most <see cref="SlotPictureSize"/> px: the project's picture when replaced, else the game's texture.</summary>
    private static TextureImage? SlotPicture(AssetCatalog catalog, string? projectDirectory, TextureSlotViewModel slot)
    {
        try
        {
            if (slot.ReplacedFile is { } file && projectDirectory is not null)
            {
                var image = ProjectTextures.Load(projectDirectory, file);
                var (rgba, w, h) = ThumbnailService.Shrink(image.Rgba, image.Width, image.Height, SlotPictureSize);
                return image with { Rgba = rgba, Width = w, Height = h, CompressedMips = null };
            }

            if (!catalog.TryLoadObject<UTexture2D>(slot.TexturePath, out var texture))
            {
                return null; // not a 2D texture any more, or gone: the slot shows no picture
            }

            var game = TextureDecoder.Decode(texture, SlotPictureSize);
            var (r, gw, gh) = ThumbnailService.Shrink(game.Rgba, game.Width, game.Height, SlotPictureSize);
            var opaque = r.ToArray();
            for (var i = 3; i < opaque.Length; i += 4)
            {
                opaque[i] = 255; // a mask or roughness map keeps data in alpha: show its colour
            }

            return game with { Rgba = opaque, Width = gw, Height = gh, CompressedMips = null };
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// After a step, an undo or a redo: the Textures panel and the 3D view show the clone's pictures as the project has them
    /// now (a replaced texture appears on the car at once; Ctrl+Z takes it off again).
    /// </summary>
    internal Task RefreshTexturesAsync()
    {
        _ = LoadTextureSlotsAsync(TextureMaterial);
        return ReplacedTexturesCompletion = ShowReplacedTexturesAsync();
    }

    /// <summary>Completes when the 3D view's replaced pictures are read (tests).</summary>
    internal Task ReplacedTexturesCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>The pictures replaced on the selected clone's materials (its own copies), read from the project: (material, stock texture) → picture.</summary>
    private async Task ShowReplacedTexturesAsync()
    {
        var load = ++_replacedLoads;
        var project = _services.Projects.Current;
        var clone = SelectedItem is { IsClone: true } item ? project?.State.FindCloneOf(item.PackagePath) : null;
        var wanted = new List<(string Material, string Texture, string File)>();
        if (project is not null && clone is not null)
        {
            foreach (var pair in clone.Packages)
            {
                foreach (var value in project.State.GetAssetValues(pair.New).Where(v => v.Path.StartsWith("TextureParameterValues", StringComparison.Ordinal)))
                {
                    var now = value.Current;
                    var dot = now.LastIndexOf('.');
                    var pixels = dot > 0 ? project.State.GetAssetValue(now[..dot], now[(dot + 1)..], TextureWrites.PixelsPath)?.Current : null;
                    if (TextureWrites.Parse(pixels) is { } written && value.Base.Length > 0)
                    {
                        var leaf = pair.Old[(pair.Old.LastIndexOf('/') + 1)..];
                        wanted.Add((pair.Old + "." + leaf, value.Base, written.File));
                    }
                }
            }
        }

        var directory = project?.DirectoryPath;
        var missing = wanted.Select(w => w.File).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !_replacedFiles.ContainsKey(f)).ToList();
        if (missing.Count > 0 && directory is not null)
        {
            var read = await Task.Run(() => missing.Select(f =>
            {
                try
                {
                    return (File: f, Image: (TextureImage?)ProjectTextures.Load(directory, f));
                }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return (File: f, Image: null);
                }
            }).ToList()).ConfigureAwait(true);
            foreach (var (file, image) in read.Where(r => r.Image is not null))
            {
                _replacedFiles[file] = image!;
            }
        }

        if (load != _replacedLoads)
        {
            return;
        }

        var shown = wanted.Where(w => _replacedFiles.ContainsKey(w.File))
            .GroupBy(w => (w.Material, w.Texture))
            .ToDictionary(g => g.Key, g => _replacedFiles[g.Last().File]);
        if (shown.Count == _replacedShown.Count && shown.All(s => _replacedShown.TryGetValue(s.Key, out var was) && ReferenceEquals(was, s.Value)))
        {
            return;
        }

        foreach (var material in shown.Keys.Concat(_replacedShown.Keys).Select(k => k.Material).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _paintBakes.Remove(material); // painted again over the new picture
        }

        _replacedShown = shown;
        if (_previewBase is not null)
        {
            Repaint();
        }
    }

    /// <summary>
    /// The picture the 3D view draws for <paramref name="texture"/> on a part of <paramref name="material"/>: the user's
    /// replacement, or null for the game's. A material with decals shows its colour and mask from the game's textures (the
    /// decals bake over them live; "Apply decals" writes those two slots).
    /// </summary>
    private TextureImage? ReplacedFor(string material, string? texture) =>
        texture is not null && _replacedShown.TryGetValue((material, texture), out var image) && !(HasDecalsOn(material) && IsDecalSlot(material, texture)) ? image : null;

    /// <summary>The material's colour or colour-mask texture (the slots Apply decals writes).</summary>
    private bool IsDecalSlot(string material, string texture) =>
        _paintPixels.ContainsKey(texture) || (_paintMasks.TryGetValue(material, out var mask) && string.Equals(mask.Name, PathLeaf(texture), StringComparison.OrdinalIgnoreCase));

    private static string PathLeaf(string objectPath) => objectPath[(objectPath.LastIndexOf('.') + 1)..];

    /// <summary>
    /// The package and export key the material's values are edited in: the selected clone's own copy of it, else the game's
    /// (null when it cannot be read).
    /// </summary>
    private (string? Package, string? Export) MaterialKey(AssetCatalog catalog, string materialPath)
    {
        var stock = materialPath[..materialPath.LastIndexOf('.')];
        var own = SelectedItem is { IsClone: true } item && _services.Projects.Current?.State.FindCloneOf(item.PackagePath) is { } clone
            ? clone.Packages.FirstOrDefault(p => string.Equals(p.Old, stock, StringComparison.OrdinalIgnoreCase))?.New
            : null;
        var package = own ?? stock;
        try
        {
            return (package, TunableReader.ExportKeys(ReadForEditing(catalog, package))[0]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return (null, null);
        }
    }

    private async Task LoadTextureSlotsAsync(TextureMaterialViewModel? paint)
    {
        var load = ++_textureLoads;
        if (paint is null || _catalog is not { } catalog)
        {
            TextureSlots = [];
            return;
        }

        try
        {
            var (slots, package, export) = await Task.Run(() =>
            {
                var (pkg, exp) = MaterialKey(catalog, paint.MaterialPath);
                return (ReadTextureSlots(catalog, paint.MaterialPath), pkg, exp);
            }).ConfigureAwait(true);
            if (load != _textureLoads)
            {
                return;
            }

            var state = _services.Projects.Current?.State;
            var list = slots.Select(s =>
            {
                var replaced = package is null || export is null ? null
                    : state?.GetAssetValue(package, export, MaterialParameters.PathOf("TextureParameterValues", s.Parameter))?.Current;
                var pixels = replaced is null ? null : state!.GetAssetValue(replaced[..replaced.LastIndexOf('.')], replaced[(replaced.LastIndexOf('.') + 1)..], TextureWrites.PixelsPath)?.Current;
                return new TextureSlotViewModel(s, TextureWrites.Parse(pixels)?.File, ReplaceSlotAsync, SaveSlotPngAsync, ResetSlotAsync);
            }).ToList();
            TextureSlots = list;
            TextureMaterialParts = PartsWearing(paint.MaterialPath);

            // A small picture of what each slot shows now (the user's picture, or the game's texture).
            var directory = _services.Projects.Current?.DirectoryPath;
            foreach (var slot in list)
            {
                slot.Picture = await Task.Run(() => SlotPicture(catalog, directory, slot)).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or InvalidCastException or NotSupportedException)
        {
            _services.Logger.LogWarning("Textures of {Material}: {Message}", paint.MaterialPath, ex.Message);
            TextureSlots = [];
        }
    }

    private async Task ReplaceSlotAsync(TextureSlotViewModel slot)
    {
        if (TextureMaterial is not { } paint)
        {
            return;
        }

        if (SelectedItem is not { IsClone: true })
        {
            _services.Notifications.Warning(Loc.T("Studio.Textures.Title"), Loc.T("Studio.Textures.CloneFirst"));
            return;
        }

        var path = await _services.Dialogs.OpenFileAsync(Loc.T("Studio.Textures.Replace"), ImageFiles.Extensions, "PNG / JPG").ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        TextureImage image;
        try
        {
            image = await Task.Run(() => ImageFiles.Load(path)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Studio.Textures.Title"), Loc.F("Studio.Textures.Unreadable", Path.GetFileName(path)));
            _services.Logger.LogDebug("Picture {Path}: {Message}", path, ex.Message);
            return;
        }

        await ReplaceTextureAsync(paint.MaterialPath, slot.Parameter, image).ConfigureAwait(true);
    }

    private async Task SaveSlotPngAsync(TextureSlotViewModel slot)
    {
        if (_catalog is not { } catalog)
        {
            return;
        }

        var leaf = slot.TexturePath[(slot.TexturePath.LastIndexOf('.') + 1)..];
        var target = await _services.Dialogs.SaveFileAsync(Loc.T("Studio.Textures.SavePng"), leaf + ".png", "png", "PNG").ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        try
        {
            var project = _services.Projects.Current;
            var image = await Task.Run(() => slot.ReplacedFile is { } file && project is not null
                ? ProjectTextures.Load(project.DirectoryPath, file)
                : TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(slot.TexturePath))).ConfigureAwait(true);
            await PngWriter.SaveAsync(image, target).ConfigureAwait(true);
            _services.Notifications.Success(Loc.T("Studio.Textures.SavePng"), target);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Studio.Textures.Title"), ex.Message);
        }
    }

    private async Task ResetSlotAsync(TextureSlotViewModel slot)
    {
        if (TextureMaterial is { } paint)
        {
            await ResetTextureAsync(paint.MaterialPath, slot.Parameter).ConfigureAwait(true);
        }
    }
}

/// <summary>One texture parameter of a material and the stock texture it holds.</summary>
/// <param name="Parameter">Parameter name (<c>Diffuse Map</c>, <c>Normal Map</c>, …).</param>
/// <param name="TexturePath">Object path of the stock texture.</param>
/// <param name="Width">Its width (0 when unreadable).</param>
/// <param name="Height">Its height.</param>
/// <param name="PixelFormat">Its cooked format.</param>
/// <param name="IsSrgb">Its sRGB flag.</param>
public sealed record TextureSlot(string Parameter, string TexturePath, int Width, int Height, string PixelFormat, bool IsSrgb);

/// <summary>Where a texture replace writes: the clone's new texture, the clone's material and the stock texture it replaces.</summary>
/// <param name="TexturePackage">The new texture's package.</param>
/// <param name="TextureLeaf">Its object name.</param>
/// <param name="TemplatePath">The stock texture (object path) whose layout and format it takes.</param>
/// <param name="TemplateLeaf">The stock texture's object name (in the material's streaming data).</param>
/// <param name="MaterialPackage">The clone's material instance.</param>
/// <param name="MaterialExport">Its export key.</param>
/// <param name="Parameter">The texture parameter.</param>
/// <param name="File">The picture in the project (<see cref="ProjectTextures"/>).</param>
/// <param name="Width">The stock texture's width (the picture is fitted to it).</param>
/// <param name="Height">The stock texture's height.</param>
public sealed record TextureReplacePlan(string TexturePackage, string TextureLeaf, string TemplatePath, string TemplateLeaf, string MaterialPackage,
    string MaterialExport, string Parameter, string File, int Width, int Height);

/// <summary>A texture slot as the Textures panel shows it.</summary>
public sealed partial class TextureSlotViewModel : ObservableObject
{
    /// <summary>A small picture of what the slot shows now (the user's picture or the game's texture), null until read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private TextureImage? _picture;

    /// <summary>Creates a slot; the actions are the page's.</summary>
    public TextureSlotViewModel(TextureSlot slot, string? replacedFile, Func<TextureSlotViewModel, Task> replace, Func<TextureSlotViewModel, Task> save,
        Func<TextureSlotViewModel, Task> reset)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Slot = slot;
        ReplacedFile = replacedFile;
        ReplaceCommand = new AsyncRelayCommand(() => replace(this));
        SavePngCommand = new AsyncRelayCommand(() => save(this));
        ResetCommand = new AsyncRelayCommand(() => reset(this), () => IsReplaced);
    }

    /// <summary>The slot.</summary>
    public TextureSlot Slot { get; }

    /// <summary>The parameter name.</summary>
    public string Parameter => Slot.Parameter;

    /// <summary>
    /// What the slot is in plain words ("Colour (what you see)", "Bumps (normal map)", "Paint mask" …), with the game's
    /// parameter name after it; the parameter name alone for a slot the studio does not know.
    /// </summary>
    public string Label => KindKey(Slot.Parameter) is { } key ? Loc.F("Studio.Textures.SlotLabel", Loc.T(key), Slot.Parameter) : Slot.Parameter;

    /// <summary>The stock texture.</summary>
    public string TexturePath => Slot.TexturePath;

    /// <summary>The project picture that replaces it, or null.</summary>
    public string? ReplacedFile { get; }

    /// <summary>The clone has its own picture here.</summary>
    public bool IsReplaced => ReplacedFile is not null;

    /// <summary>A picture is shown.</summary>
    public bool HasPicture => Picture is not null;

    /// <summary>Whose picture the slot shows: "Your picture: x.png" or "The game's texture".</summary>
    public string Status => IsReplaced ? Loc.F("Studio.Textures.Yours", Path.GetFileName(ReplacedFile!)) : Loc.T("Studio.Textures.Games");

    /// <summary>Size and format, e.g. <c>1024 x 1024, PF_DXT1</c>.</summary>
    public string Info => Slot.Width > 0 ? Loc.F("Studio.Textures.Info", Slot.Width, Slot.Height, Slot.PixelFormat) : string.Empty;

    /// <summary>Pick a picture for this slot.</summary>
    public IAsyncRelayCommand ReplaceCommand { get; }

    /// <summary>Save the slot's texture as a PNG.</summary>
    public IAsyncRelayCommand SavePngCommand { get; }

    /// <summary>The game's texture again.</summary>
    public IAsyncRelayCommand ResetCommand { get; }

    /// <summary>The string key that says what a texture parameter is, by the words SCUM's materials name them with; null when unknown.</summary>
    internal static string? KindKey(string parameter)
    {
        var p = parameter.ToUpperInvariant();
        return p.Contains("NORMAL", StringComparison.Ordinal) ? "Studio.Textures.Kind.Normal"
            : p.Contains("MASK", StringComparison.Ordinal) && (p.Contains("COLO", StringComparison.Ordinal) || p.Contains("PAINT", StringComparison.Ordinal)) ? "Studio.Textures.Kind.PaintMask"
            : p.Contains("DIFFUSE", StringComparison.Ordinal) || p.Contains("ALBEDO", StringComparison.Ordinal) || p.Contains("BASE", StringComparison.Ordinal) || p.Contains("COLOR", StringComparison.Ordinal) ? "Studio.Textures.Kind.Colour"
            : p.Contains("ROUGH", StringComparison.Ordinal) || p.Contains("SPEC", StringComparison.Ordinal) || p.Contains("METAL", StringComparison.Ordinal) || p.Contains("ORM", StringComparison.Ordinal) ? "Studio.Textures.Kind.Shine"
            : p.Contains("DIRT", StringComparison.Ordinal) || p.Contains("DAMAGE", StringComparison.Ordinal) || p.Contains("SCRATCH", StringComparison.Ordinal) || p.Contains("RUST", StringComparison.Ordinal) ? "Studio.Textures.Kind.Wear"
            : p.Contains("MASK", StringComparison.Ordinal) ? "Studio.Textures.Kind.Mask"
            : null;
    }
}
