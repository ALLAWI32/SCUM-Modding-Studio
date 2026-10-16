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

    /// <summary>The paint material whose textures are listed.</summary>
    [ObservableProperty]
    private PaintMaterialViewModel? _textureMaterial;

    /// <summary>The texture slots of <see cref="TextureMaterial"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextureSlots))]
    private IReadOnlyList<TextureSlotViewModel> _textureSlots = [];

    /// <summary>The Textures panel has something to show.</summary>
    public bool HasTextureSlots => TextureSlots.Count > 0;

    partial void OnPaintsChanged(IReadOnlyList<PaintMaterialViewModel> value) =>
        TextureMaterial = value.FirstOrDefault(p => p.MaterialPath == TextureMaterial?.MaterialPath) ?? value.FirstOrDefault();

    partial void OnTextureMaterialChanged(PaintMaterialViewModel? value) => _ = LoadTextureSlotsAsync(value);

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

    /// <summary>The texture slots of a material instance (name, stock texture, size, format, sRGB), in the material's order.</summary>
    internal static IReadOnlyList<TextureSlot> ReadTextureSlots(AssetCatalog catalog, string materialPath)
    {
        if (!catalog.TryLoadObject<UMaterialInstanceConstant>(materialPath, out var mi))
        {
            return [];
        }

        var slots = new List<TextureSlot>();
        foreach (var texture in new MaterialInspector(catalog).Inspect(mi).Textures.Where(t => t.TexturePath is { Length: > 0 }))
        {
            var info = catalog.TryLoadObject<UTexture2D>(texture.TexturePath!, out var t) ? TextureDecoder.Describe(t) : null;
            slots.Add(new TextureSlot(texture.Name, texture.TexturePath!, info?.Width ?? 0, info?.Height ?? 0, info?.PixelFormat ?? string.Empty, info?.IsSrgb ?? false));
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

    private async Task LoadTextureSlotsAsync(PaintMaterialViewModel? paint)
    {
        var load = ++_textureLoads;
        if (paint is null || _catalog is not { } catalog)
        {
            TextureSlots = [];
            return;
        }

        try
        {
            var slots = await Task.Run(() => ReadTextureSlots(catalog, paint.MaterialPath)).ConfigureAwait(true);
            if (load != _textureLoads)
            {
                return;
            }

            var state = _services.Projects.Current?.State;
            TextureSlots = slots.Select(s =>
            {
                var replaced = state?.GetAssetValue(paint.ColourValue.Package, paint.ColourValue.Tunable.Export, MaterialParameters.PathOf("TextureParameterValues", s.Parameter))?.Current;
                var pixels = replaced is null ? null : state!.GetAssetValue(replaced[..replaced.LastIndexOf('.')], replaced[(replaced.LastIndexOf('.') + 1)..], TextureWrites.PixelsPath)?.Current;
                return new TextureSlotViewModel(s, TextureWrites.Parse(pixels)?.File, ReplaceSlotAsync, SaveSlotPngAsync, ResetSlotAsync);
            }).ToList();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
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
public sealed class TextureSlotViewModel
{
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

    /// <summary>The stock texture.</summary>
    public string TexturePath => Slot.TexturePath;

    /// <summary>The project picture that replaces it, or null.</summary>
    public string? ReplacedFile { get; }

    /// <summary>The clone has its own picture here.</summary>
    public bool IsReplaced => ReplacedFile is not null;

    /// <summary>Size and format, e.g. <c>1024 x 1024, PF_DXT1</c>.</summary>
    public string Info => Slot.Width > 0 ? Loc.F("Studio.Textures.Info", Slot.Width, Slot.Height, Slot.PixelFormat) : string.Empty;

    /// <summary>Pick a picture for this slot.</summary>
    public IAsyncRelayCommand ReplaceCommand { get; }

    /// <summary>Save the slot's texture as a PNG.</summary>
    public IAsyncRelayCommand SavePngCommand { get; }

    /// <summary>The game's texture again.</summary>
    public IAsyncRelayCommand ResetCommand { get; }
}
