using System.Diagnostics;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Decals and stickers on a vehicle clone (owner: "tattoos and stickers like drift cars"; m7ammed: "tools for editing
/// materials and importing textures"): a design of the library or a picture is dropped where the user clicks on the car
/// (<see cref="PlaceDecal"/>, through <see cref="MeshRay"/>), dragged (<see cref="DragDecal"/>), sized, turned, coloured;
/// the 3D view shows it baked into the material's textures (<see cref="ComposeDecals"/>). Every step is one undo step and
/// is saved at once in the project's <c>decals/&lt;clone&gt;.json</c>.
/// </summary>
public abstract partial class ModulePageViewModel
{
    /// <summary>Width a dropped decal starts with, cm.</summary>
    private const float DecalStartWidth = 40f;

    /// <summary>Pixels across a design is drawn at for the bakes.</summary>
    private const int DecalRasterSize = 512;

    private readonly Stack<DecalSet> _decalHistory = new();
    private readonly Dictionary<string, DecalRaster> _decalRasters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecalImage?> _stickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _decalClock = Stopwatch.StartNew();
    private DecalSet _decals = new(string.Empty, []);
    private (PreviewModel Model, string Clone)? _decalTarget;
    private DecalSet? _decalDragStart;
    private string? _decalLastEdited;
    private TimeSpan _decalLastTime;
    private string? _armedSticker;
    private HashSet<string> _decalBaked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The decals changed (the 3D view bakes them again); <see cref="DecalsDragging"/> tells a drag from a finished step.</summary>
    public event EventHandler? DecalsChanged;

    /// <summary>The library category shown in the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecalDesigns))]
    private DecalCategoryItem _decalCategory = new(string.Empty);

    /// <summary>The design the next click on the car puts there (null: a click picks or drags a decal).</summary>
    [ObservableProperty]
    private DecalDesignItem? _armedDecal;

    /// <summary>The decals of the selected clone, top first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecals))]
    private IReadOnlyList<DecalLayerViewModel> _decalLayers = [];

    /// <summary>The decal the settings below the list edit.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDecalUpCommand), nameof(MoveDecalDownCommand), nameof(DuplicateDecalCommand), nameof(DeleteDecalCommand), nameof(ShuffleDecalCommand))]
    private DecalLayerViewModel? _selectedDecal;

    /// <summary>A decal is being dragged: the 3D view bakes at half size.</summary>
    public bool DecalsDragging => _decalDragStart is not null;

    /// <summary>The clone has decals.</summary>
    public bool HasDecals => DecalLayers.Count > 0;

    /// <summary>The saved decals of the selected clone.</summary>
    public DecalSet Decals => _decals;

    /// <summary>A decal step Undo can take back.</summary>
    public bool CanUndoDecal => _decalHistory.Count > 0;

    /// <summary>The category chips (all first, stickers last).</summary>
    public static IReadOnlyList<DecalCategoryItem> DecalCategories { get; } =
        [new(string.Empty), .. DecalLibrary.Categories.Select(c => new DecalCategoryItem(c)), new("Stickers")];

    private static readonly IReadOnlyList<DecalDesignItem> AllDecalDesigns =
        [.. DecalLibrary.All.GroupBy(d => d.Category).SelectMany(g => g.Select((d, i) => new DecalDesignItem(d, i + 1)))];

    /// <summary>The designs of <see cref="DecalCategory"/>.</summary>
    public IReadOnlyList<DecalDesignItem> DecalDesigns => DecalCategory.Key.Length == 0
        ? AllDecalDesigns
        : [.. AllDecalDesigns.Where(d => d.Design.Category == DecalCategory.Key)];

    /// <summary>Uses <paramref name="model"/> and the clone <paramref name="cloneLeaf"/> for the decals instead of the selected vehicle (tests).</summary>
    internal void UseDecalTarget(PreviewModel model, string cloneLeaf)
    {
        _decalTarget = (model, cloneLeaf);
        SyncDecals();
    }

    /// <summary>
    /// Puts the armed design or sticker where <paramref name="ray"/> meets the car (projected along the surface there),
    /// or, with nothing armed, selects the decal under the cursor. True when something was placed or selected.
    /// </summary>
    public bool PlaceDecal(SurfaceRay ray)
    {
        ArgumentNullException.ThrowIfNull(ray);
        if (DecalTarget() is not { } target || MeshRay.Hit(target.Model, ray) is not { } found)
        {
            return false;
        }

        var part = target.Model.Parts[found.PartIndex];
        if (ArmedDecal is null && _armedSticker is null)
        {
            return SelectDecalAt(part, found.Hit);
        }

        var design = ArmedDecal?.Design;
        var aspect = design?.Aspect ?? StickerAspect(_armedSticker!);
        var layer = new DecalLayer
        {
            DesignId = design?.Id,
            StickerFile = design is null ? _armedSticker : null,
            PartKey = DecalPartKey(part.Attachment),
            MaterialPath = part.Material,
            Position = found.Hit.Position,
            Normal = found.Hit.Normal,
            Up = UpOn(part.Transform, found.Hit.Normal),
            WidthCm = DecalStartWidth,
            HeightCm = DecalStartWidth / aspect,
            Order = _decals.Layers.Count == 0 ? 0 : _decals.Layers.Max(l => l.Order) + 1,
            Colours = design is null ? [] : [.. design.Regions.Select(r => DecalLayer.Hex(DecalLayer.FromRgb(r.Colour)))],
        };
        Step(_decals with { Layers = [.. _decals.Layers, layer] }, layer.Id);
        return true;
    }

    /// <summary>
    /// Drags the selected decal to where <paramref name="ray"/> meets the same material; <paramref name="commit"/> ends the
    /// drag (one undo step for the whole drag). True when it moved.
    /// </summary>
    public bool DragDecal(SurfaceRay ray, bool commit)
    {
        ArgumentNullException.ThrowIfNull(ray);
        if (DecalTarget() is not { } target || SelectedDecal?.Layer is not { } layer)
        {
            return false;
        }

        _decalDragStart ??= _decals;
        var moved = false;
        if (MeshRay.Hit(target.Model, ray with { PartIndex = -1 }) is { } found
            && string.Equals(target.Model.Parts[found.PartIndex].Material, layer.MaterialPath, StringComparison.OrdinalIgnoreCase))
        {
            var part = target.Model.Parts[found.PartIndex];
            var next = layer with { PartKey = DecalPartKey(part.Attachment), Position = found.Hit.Position, Normal = found.Hit.Normal, Up = UpOn(part.Transform, found.Hit.Normal) };
            _decals = _decals with { Layers = [.. _decals.Layers.Select(l => l.Id == layer.Id ? next : l)] };
            SelectedDecal.Show(next);
            moved = true;
        }

        if (commit)
        {
            var start = _decalDragStart;
            _decalDragStart = null;
            if (!ReferenceEquals(start, _decals))
            {
                _decalHistory.Push(start);
                SaveDecals();
                OnPropertyChanged(nameof(CanUndoDecal));
            }
        }

        OnDecalsChanged();
        return moved;
    }

    /// <summary>
    /// <paramref name="diffuse"/> and <paramref name="mask"/> of the material <paramref name="materialPath"/> with the clone's
    /// decals baked in (half size while one is dragged); the same images when it has none. The 3D view paints over the result.
    /// </summary>
    public (TextureImage Diffuse, TextureImage Mask) ComposeDecals(string materialPath, TextureImage diffuse, TextureImage mask)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(mask);
        if (DecalTarget() is not { } target || DecalStamps(target.Model, materialPath) is not { Count: > 0 } stamps)
        {
            return (diffuse, mask);
        }

        var parts = target.Model.Parts.Where(p => string.Equals(p.Material, materialPath, StringComparison.OrdinalIgnoreCase)).ToList();
        var size = DecalsDragging ? Math.Max(1, Math.Max(diffuse.Width, diffuse.Height) / 2) : 0;
        var baked = DecalBaker.Bake(diffuse, mask, parts, stamps, size);
        return (baked.Diffuse, baked.Mask);
    }

    /// <summary>
    /// Every decalled material of the clone baked at its diffuse's full size on a worker (what Apply decals writes), with
    /// the parts whose shared UVs show a decal elsewhere too.
    /// </summary>
    internal async Task<IReadOnlyList<(string Material, DecalBakeResult Result)>> BakeDecalsAsync(Func<string, (TextureImage Diffuse, TextureImage Mask)?> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        if (DecalTarget() is not { } target)
        {
            return [];
        }

        var jobs = _decals.Layers.Select(l => l.MaterialPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => (Material: m, Stamps: DecalStamps(target.Model, m), Textures: textures(m)))
            .Where(j => j.Stamps.Count > 0 && j.Textures is not null).ToList();
        return await Task.Run(() => jobs.Select(j => (j.Material, DecalBaker.Bake(j.Textures!.Value.Diffuse, j.Textures.Value.Mask,
            [.. target.Model.Parts.Where(p => string.Equals(p.Material, j.Material, StringComparison.OrdinalIgnoreCase))], j.Stamps))).ToList()).ConfigureAwait(true);
    }

    /// <summary>
    /// Apply decals: every decalled material of the clone baked at full size over the game's own colour texture and colour
    /// mask, written as the clone's <c>Diffuse Map</c> and <c>Color Mask</c> (<see cref="TextureReplaceOp"/>), all as one
    /// journal step that Ctrl+Z takes back. A picture put on those slots in the Textures tab is replaced.
    /// </summary>
    [RelayCommand]
    private async Task ApplyDecalsAsync()
    {
        if (_catalog is not { } catalog || _services.Projects.Current is not { } project || SelectedItem is not { IsClone: true } item || DecalTarget() is null)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Apply"), Loc.T("Studio.Textures.CloneFirst"));
            return;
        }

        var baked = await BakeDecalsAsync(material => GameTextures(catalog, material)).ConfigureAwait(true);
        var ops = new List<EditOp>();
        var written = 0;
        try
        {
            foreach (var (material, result) in baked)
            {
                foreach (var (parameter, image) in new[] { (DecalDiffuse, result.Diffuse), (DecalMask, result.Mask) })
                {
                    if (TexturePlan(material, parameter) is not { } plan)
                    {
                        continue; // checked by GameTextures: both slots exist
                    }

                    var file = await ProjectTextures.SaveAsync(project.DirectoryPath, image.Rgba, image.Width, image.Height).ConfigureAwait(true);
                    ops.AddRange(TextureReplaceOp(project.State, plan with { File = file }).Ops);
                    written++;
                }

                if (result.SharedUv.Count > 0)
                {
                    _services.Notifications.Warning(Loc.T("Studio.Decals.Applied"), Loc.F("Studio.Decals.SharedUv", string.Join(", ", result.SharedUv)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
            return;
        }

        if (ops.Count > 0)
        {
            _services.Projects.Apply(new BatchOp(Loc.T("Studio.Decals.Applied"), ops));
        }

        if (written == 0)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Apply"), Loc.T(_decals.Layers.Count == 0 ? "Studio.Decals.Empty" : "Studio.Decals.NoMask"));
            return;
        }

        _services.Notifications.Success(Loc.T("Studio.Decals.Applied"), Loc.F("Studio.Decals.AppliedDetail", written, item.Name));
        await LoadTextureSlotsAsync(TextureMaterial).ConfigureAwait(true);
    }

    /// <summary>Another vehicle was picked: the Decals panel lists that clone's saved decals (none for a stock vehicle).</summary>
    partial void OnSelectedItemChanged(ModuleItemViewModel? oldValue, ModuleItemViewModel? newValue)
    {
        var clone = newValue is { IsClone: true } item ? item.PackagePath[(item.PackagePath.LastIndexOf('/') + 1)..] : string.Empty;
        if (_decalTarget is null && _decals.CloneLeaf != clone)
        {
            ArmedDecal = null;
            _armedSticker = null;
            SyncDecals(clone.Length == 0 ? null : clone);
            OnDecalsChanged();
        }
    }

    /// <summary>Escape during a decal drag: the decal goes back where the drag began (nothing is recorded).</summary>
    private void CancelDecalDrag()
    {
        if (_decalDragStart is { } start)
        {
            _decals = start;
            _decalDragStart = null;
            ShowDecals(SelectedDecal?.Layer.Id);
            OnDecalsChanged();
        }
    }

    /// <summary>The decals changed: the 3D view's paint is baked again with them (<see cref="Repaint"/>), then <see cref="DecalsChanged"/>.</summary>
    private void OnDecalsChanged()
    {
        // Only the materials that had or have a decal are baked again (a bake at 2048 px takes about half a second).
        var decalled = _decals.Layers.Select(l => l.MaterialPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var material in _decalBaked.Concat(decalled))
        {
            _paintBakes.Remove(material);
        }

        _decalBaked = decalled;
        Repaint();
        DecalsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when the clone has a decal on <paramref name="materialPath"/> (the 3D view bakes that material even unpainted).</summary>
    private bool HasDecalsOn(string materialPath) =>
        _decals.Layers.Count > 0 && _decals.Layers.Any(l => string.Equals(l.MaterialPath, materialPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour texture slot a decal is baked into.</summary>
    internal const string DecalDiffuse = "Diffuse Map";

    /// <summary>The colour mask slot the decal takes the paint away in.</summary>
    internal const string DecalMask = "Color Mask";

    /// <summary>
    /// The game's own colour texture and colour mask of <paramref name="material"/> at full size, or null when the material
    /// has no texture of its own in one of the two slots (then nothing is written for it).
    /// </summary>
    private (TextureImage Diffuse, TextureImage Mask)? GameTextures(AssetCatalog catalog, string material)
    {
        var slots = ReadTextureSlots(catalog, material);
        TextureImage? Load(string parameter) =>
            slots.FirstOrDefault(s => s.Parameter == parameter)?.TexturePath is { } path && catalog.TryLoadObject<UTexture2D>(path, out var texture)
                ? TextureDecoder.Decode(texture)
                : null;
        try
        {
            return Load(DecalDiffuse) is { } diffuse && Load(DecalMask) is { } mask ? (diffuse, mask) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Decals on {Material}: {Message}", material, ex.Message);
            return null;
        }
    }

    /// <summary>The decal settings changed in the panel: one undo step per pause (a slider dragged is one step).</summary>
    private void OnDecalEdited(DecalLayer layer)
    {
        var now = _decalClock.Elapsed;
        var together = _decalLastEdited == layer.Id && now - _decalLastTime <= PaintUndoPause;
        (_decalLastEdited, _decalLastTime) = (layer.Id, now);
        var next = _decals with { Layers = [.. _decals.Layers.Select(l => l.Id == layer.Id ? layer : l)] };
        if (together)
        {
            _decals = next;
            SaveDecals();
            OnDecalsChanged();
            return;
        }

        Step(next, layer.Id, rebuild: false);
    }

    /// <summary>Takes the last decal step back.</summary>
    [RelayCommand]
    private void UndoDecal()
    {
        if (_decalHistory.TryPop(out var previous))
        {
            _decals = previous;
            _decalLastEdited = null;
            SaveDecals();
            ShowDecals(SelectedDecal?.Layer.Id);
            OnPropertyChanged(nameof(CanUndoDecal));
            OnDecalsChanged();
        }
    }

    /// <summary>Arms a design: the next click on the car puts it there.</summary>
    [RelayCommand]
    private void ArmDecal(DecalDesignItem? design)
    {
        _armedSticker = null;
        ArmedDecal = design;
    }

    /// <summary>Picks a PNG or JPG, copies it into the project and arms it as a sticker.</summary>
    [RelayCommand]
    private async Task AddStickerAsync()
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Module.NoProjectChanges"));
            return;
        }

        var file = await _services.Dialogs.OpenFileAsync(Loc.T("Studio.Decals.AddSticker"), "png;jpg;jpeg", Loc.T("Studio.Decals.Category.Stickers")).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        try
        {
            var folder = Path.Combine(project.DirectoryPath, "decals", "stickers");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, copy);
            var relative = Path.GetRelativePath(project.DirectoryPath, copy).Replace('\\', '/');
            if (Sticker(relative) is null)
            {
                File.Delete(copy);
                return;
            }

            ArmedDecal = null;
            _armedSticker = relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
    }

    private bool CanEditDecal() => SelectedDecal is not null;

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void MoveDecalUp() => SwapDecal(1);

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void MoveDecalDown() => SwapDecal(-1);

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void DuplicateDecal()
    {
        if (SelectedDecal?.Layer is { } layer)
        {
            var copy = layer with { Id = new DecalLayer().Id, Order = _decals.Layers.Max(l => l.Order) + 1 };
            Step(_decals with { Layers = [.. _decals.Layers, copy] }, copy.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void DeleteDecal()
    {
        if (SelectedDecal?.Layer is { } layer)
        {
            Step(_decals with { Layers = [.. _decals.Layers.Where(l => l.Id != layer.Id)] }, null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void ShuffleDecal() => SelectedDecal?.Shuffle();

    /// <summary>Swaps the selected decal's order with the next one above (+1) or below (−1).</summary>
    private void SwapDecal(int direction)
    {
        var ordered = _decals.Ordered;
        var i = SelectedDecal is { } s ? ordered.ToList().FindIndex(l => l.Id == s.Layer.Id) : -1;
        var j = i + direction;
        if (i < 0 || j < 0 || j >= ordered.Count)
        {
            return;
        }

        var (a, b) = (ordered[i], ordered[j]);
        Step(_decals with { Layers = [.. _decals.Layers.Select(l => l.Id == a.Id ? l with { Order = b.Order } : l.Id == b.Id ? l with { Order = a.Order } : l)] }, a.Id);
    }

    /// <summary>One undo step: remembers the decals as they were, takes <paramref name="next"/>, saves and shows it.</summary>
    private void Step(DecalSet next, string? select, bool rebuild = true)
    {
        _decalHistory.Push(_decals);
        _decals = next;
        SaveDecals();
        if (rebuild)
        {
            ShowDecals(select);
        }

        OnPropertyChanged(nameof(CanUndoDecal));
        OnDecalsChanged();
    }

    /// <summary>The clone whose decals are edited and the model they sit on; null for a stock vehicle or without a project.</summary>
    private (PreviewModel Model, string Clone)? DecalTarget()
    {
        if (_decalTarget is null && SelectedItem is { IsClone: true } item && (_previewBase ?? Preview) is { } model)
        {
            var clone = item.PackagePath[(item.PackagePath.LastIndexOf('/') + 1)..];
            if (_decals.CloneLeaf != clone)
            {
                _decals = new DecalSet(clone, []);
                SyncDecals(clone);
            }

            return (model, clone);
        }

        return _decalTarget;
    }

    /// <summary>Reads the clone's saved decals (a new clone selected, or a test target).</summary>
    private void SyncDecals(string? clone = null)
    {
        clone ??= _decalTarget?.Clone;
        _decalHistory.Clear();
        _decalDragStart = null;
        try
        {
            _decals = clone is null || _services.Projects.Current is not { } project ? new DecalSet(clone ?? string.Empty, []) : DecalSet.Load(project.DirectoryPath, clone);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Decals of {Clone}: {Message}", clone, ex.Message);
            _decals = new DecalSet(clone ?? string.Empty, []);
        }

        ShowDecals(null);
        OnPropertyChanged(nameof(CanUndoDecal));
    }

    private void SaveDecals()
    {
        if (_services.Projects.Current is not { } project || _decals.CloneLeaf.Length == 0)
        {
            return;
        }

        try
        {
            _decals.Save(project.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
    }

    /// <summary>Rebuilds the layer list (top first) and selects <paramref name="select"/>.</summary>
    private void ShowDecals(string? select)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = _decals.Ordered.Reverse().Select(l =>
        {
            var design = DecalLibrary.Find(l.DesignId);
            var category = design?.Category ?? "Stickers";
            counts[category] = counts.GetValueOrDefault(category) + 1;
            var name = Loc.F("Studio.Decals.Design", DecalCategoryItem.LabelOf(category), counts[category]);
            return new DecalLayerViewModel(l, name, design?.Regions.Count ?? 1, [.. design?.Regions.Select(r => r.Colour) ?? [0xFFFFFFu]], OnDecalEdited);
        }).ToList();
        DecalLayers = items;
        SelectedDecal = items.FirstOrDefault(i => i.Layer.Id == select);
    }

    /// <summary>Selects the decal whose box holds the hit point (the top one).</summary>
    private bool SelectDecalAt(PreviewPart part, SurfaceHit hit)
    {
        var world = part.Transform.TransformPosition(FVector.FromVector3(hit.Position)).ToVector3();
        var target = DecalTarget()!.Value;
        foreach (var layer in _decals.Ordered.Reverse())
        {
            if (DecalPart(target.Model, layer) is { } on)
            {
                var centre = on.Transform.TransformPosition(FVector.FromVector3(layer.Position)).ToVector3();
                if (Vector3.Distance(centre, world) <= MathF.Max(layer.WidthCm, layer.HeightCm) / 2)
                {
                    SelectedDecal = DecalLayers.FirstOrDefault(l => l.Layer.Id == layer.Id);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The stamps of the decals on <paramref name="material"/>, bottom first (a decal whose part or picture is gone is left out).</summary>
    private List<DecalStamp> DecalStamps(PreviewModel model, string material)
    {
        var stamps = new List<DecalStamp>();
        foreach (var layer in _decals.Ordered.Where(l => string.Equals(l.MaterialPath, material, StringComparison.OrdinalIgnoreCase)))
        {
            var image = DecalLibrary.Find(layer.DesignId) is { } design
                ? layer.Render(_decalRasters.TryGetValue(design.Id, out var r) ? r : _decalRasters[design.Id] = design.Rasterize(DecalRasterSize), design)
                : layer.StickerFile is { } file ? Sticker(file) : null;
            if (image is not null && DecalPart(model, layer) is { } part)
            {
                stamps.AddRange(DecalBaker.Stamps(layer, image, part.Transform));
            }
        }

        return stamps;
    }

    private static PreviewPart? DecalPart(PreviewModel model, DecalLayer layer) => model.Parts.FirstOrDefault(p =>
        string.Equals(p.Material, layer.MaterialPath, StringComparison.OrdinalIgnoreCase) && string.Equals(DecalPartKey(p.Attachment), layer.PartKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>A sticker's picture (relative to the project), read once; null when it cannot be read.</summary>
    private DecalImage? Sticker(string file)
    {
        if (_stickers.TryGetValue(file, out var known))
        {
            return known;
        }

        try
        {
            var path = _services.Projects.Current is { } project ? Path.Combine(project.DirectoryPath, file) : file;
            return _stickers[file] = DecalLibrary.LoadSticker(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SixLabors.ImageSharp.ImageFormatException or NotSupportedException)
        {
            _services.Notifications.Error(Loc.T("Studio.Decals.AddSticker"), $"{file}: {ex.Message}");
            return _stickers[file] = null;
        }
    }

    private float StickerAspect(string file) => Sticker(file) is { } image ? image.Width / (float)image.Height : 1f;

    /// <summary><c>/Game/…/BPC_WolfsWagen_Door_FrontLeft</c> → <c>Door_FrontLeft</c>; empty for the vehicle's own mesh.</summary>
    internal static string DecalPartKey(string attachment)
    {
        if (attachment.Length == 0)
        {
            return string.Empty;
        }

        var leaf = attachment[(attachment.LastIndexOf('/') + 1)..];
        leaf = leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? leaf[4..] : leaf;
        var token = leaf.IndexOf('_');
        return token > 0 ? leaf[(token + 1)..] : leaf;
    }

    /// <summary>Up on a decal at <paramref name="normal"/> (part space): the world's up, or its forward on a roof or a bonnet.</summary>
    private static Vector3 UpOn(FTransform transform, Vector3 normal)
    {
        var up = transform.InverseTransformVector(FVector.Up).ToVector3();
        return MathF.Abs(Vector3.Dot(Vector3.Normalize(up), normal)) > 0.8f ? transform.InverseTransformVector(FVector.Forward).ToVector3() : up;
    }
}
