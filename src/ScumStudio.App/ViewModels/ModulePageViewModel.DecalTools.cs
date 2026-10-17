using System.Numerics;
using Avalonia.Input;
using Keys = Avalonia.Input.Key;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Owner (2026-10-16): vehicle decals "like the game's tattoos, and more control from outside". The game's own tattoos
/// as a library category (read from the user's game files at run time, never shipped); tattoo-like handling in the 3D
/// view (Ctrl+wheel sizes, Shift+wheel turns, arrows move along the surface, Q/E turn, +/− size, X flips, M mirrors to
/// the car's other side, Delete removes, Ctrl+D copies); and the extra control of the panel: hide and lock per layer,
/// exact position and size fields, SVG drawings as stickers, presets of the project and copying from another clone.
/// Every action is one undo step (keys and wheel notches close together are one, like a slider dragged).
/// </summary>
public abstract partial class ModulePageViewModel
{
    private readonly Dictionary<string, DecalDesign> _tattooDesigns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tattooShapeUnknown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _tattooCuts = new(StringComparer.Ordinal);
    private IReadOnlyList<GameTattoo>? _gameTattoos;
    private Task? _gameTattoosLoad;
    private Task<TextureImage?>? _tattooAtlas;

    /// <summary>The game's tattoos in the library (empty until the game files are read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecalDesigns), nameof(ShowGameTattoosNote))]
    private IReadOnlyList<DecalDesignItem> _gameTattooItems = [];

    /// <summary>Why the game's tattoos are not listed (no game files, or they could not be read).</summary>
    [ObservableProperty]
    private string _gameTattoosNote = Loc.T("Studio.Decals.GameTattoos.None");

    /// <summary>The name the clone's decals are saved under as a preset.</summary>
    [ObservableProperty]
    private string _decalPresetName = string.Empty;

    /// <summary>The project's decal presets.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _decalPresets = [];

    /// <summary>The preset Add preset puts on the clone.</summary>
    [ObservableProperty]
    private string? _selectedDecalPreset;

    /// <summary>The project's other clones that have decals.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _decalSourceClones = [];

    /// <summary>The clone Copy from clone takes the decals of.</summary>
    [ObservableProperty]
    private string? _selectedDecalSource;

    /// <summary>The note in place of the game's tattoos is shown (their chip is picked and none are listed).</summary>
    public bool ShowGameTattoosNote => DecalCategory.Key == GameTattoos.Category && GameTattooItems.Count == 0;

    /// <summary>Uses <paramref name="tattoos"/> drawn in <paramref name="atlas"/> as the game's tattoos (tests).</summary>
    internal void UseGameTattoos(IReadOnlyList<GameTattoo> tattoos, TextureImage atlas)
    {
        _gameTattoos = tattoos;
        _tattooAtlas = Task.FromResult<TextureImage?>(atlas);
        _gameTattoosLoad = Task.CompletedTask;
        GameTattooItems = [.. tattoos.Select((t, i) => new DecalDesignItem(t, i + 1, () => Task.FromResult<string?>(null)))];
    }

    /// <summary>Reads the list of the game's tattoos (once; again after a failure once the game files are there).</summary>
    internal Task LoadGameTattoosAsync() => _gameTattoosLoad ??= LoadGameTattoosCoreAsync();

    private async Task LoadGameTattoosCoreAsync()
    {
        if (_catalog is not { } catalog)
        {
            _gameTattoosLoad = null;
            return;
        }

        try
        {
            var tattoos = await Task.Run(() => GameTattoos.Read(catalog)).ConfigureAwait(true);
            var small = new Lazy<TextureImage>(() => GameTattoos.LoadAtlas(catalog, 1024), LazyThreadSafetyMode.ExecutionAndPublication);
            _gameTattoos = tattoos;
            GameTattooItems = [.. tattoos.Select((t, i) => new DecalDesignItem(t, i + 1, () => _services.Thumbnails.GetDerivedAsync(catalog, GameTattoos.AtlasPath,
                "tattoo-" + t.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), () => GameTattoos.Picture(GameTattoos.Cut(small.Value, t), (226, 226, 226)))))];
            GameTattoosNote = tattoos.Count == 0 ? Loc.T("Studio.Decals.GameTattoos.None") : string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("The game's tattoos: {Message}", ex.Message);
            GameTattoosNote = Loc.F("Studio.Decals.GameTattoos.Failed", ex.Message);
            _gameTattoosLoad = null;
        }
    }

    /// <summary>Reads the game's tattoo <paramref name="id"/> out of the atlas (once; a second ask waits for the first), then shows the decals wearing it.</summary>
    internal Task EnsureGameTattooAsync(string id)
    {
        if (_decalRasters.ContainsKey(id) || GameTattoos.IndexOf(id) is null)
        {
            return Task.CompletedTask;
        }

        if (!_tattooCuts.TryGetValue(id, out var cutting))
        {
            cutting = _tattooCuts[id] = CutGameTattooAsync(id);
        }

        return cutting;
    }

    private async Task CutGameTattooAsync(string id)
    {
        if (GameTattoos.IndexOf(id) is not { } index || await TattooAtlasAsync().ConfigureAwait(true) is not { } atlas || _decalRasters.ContainsKey(id))
        {
            _tattooCuts.Remove(id); // asked again once the game files are there
            return;
        }

        var tattoo = _gameTattoos?.FirstOrDefault(t => t.Index == index) ?? new GameTattoo(index, 1f, []);
        var cut = await Task.Run(() => GameTattoos.Cut(atlas, tattoo)).ConfigureAwait(true);
        if (_decalRasters.ContainsKey(id))
        {
            return;
        }

        _decalRasters[id] = cut;
        var design = _tattooDesigns[id] = GameTattoos.Design(tattoo, cut);
        var reshaped = _decals.Layers.Where(l => l.DesignId == id && _tattooShapeUnknown.Remove(l.Id)).Select(l => l with { HeightCm = l.WidthCm / design.Aspect }).ToList();
        if (reshaped.Count > 0)
        {
            _decals = _decals with { Layers = [.. _decals.Layers.Select(l => reshaped.FirstOrDefault(r => r.Id == l.Id) ?? l)] };
            SaveDecals();
            foreach (var item in DecalLayers.Where(i => reshaped.Any(r => r.Id == i.Layer.Id)))
            {
                item.Show(reshaped.First(r => r.Id == item.Layer.Id));
            }
        }

        if (_decals.Layers.Any(l => l.DesignId == id))
        {
            OnDecalsChanged();
        }
    }

    /// <summary>Reads every game tattoo <paramref name="set"/> wears.</summary>
    private async Task EnsureGameTattoosOfAsync(DecalSet set)
    {
        foreach (var id in set.Layers.Select(l => l.DesignId).OfType<string>().Where(i => GameTattoos.IndexOf(i) is not null).Distinct(StringComparer.Ordinal).ToList())
        {
            await EnsureGameTattooAsync(id).ConfigureAwait(true);
        }
    }

    /// <summary>The game's tattoo atlas at full size, decoded once on a worker; null without game files.</summary>
    private Task<TextureImage?> TattooAtlasAsync()
    {
        if (_tattooAtlas is not null)
        {
            return _tattooAtlas;
        }

        if (_catalog is not { } catalog)
        {
            return Task.FromResult<TextureImage?>(null);
        }

        return _tattooAtlas = Task.Run(() =>
        {
            try
            {
                return (TextureImage?)GameTattoos.LoadAtlas(catalog);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _services.Logger.LogWarning("The game's tattoo atlas: {Message}", ex.Message);
                _tattooAtlas = null;
                return null;
            }
        });
    }

    /// <summary>A game tattoo's design: its true shape once the atlas is read, a square until then.</summary>
    private DecalDesign TattooDesign(string id) =>
        _tattooDesigns.GetValueOrDefault(id) ?? new DecalDesign(id, GameTattoos.Category, 1, 1, [new DecalRegion(string.Empty, GameTattoos.Ink)]);

    /// <summary>
    /// The wheel over the 3D view on the Decals tab: Ctrl sizes the selected decal, Shift turns it (a locked one stays).
    /// True when a decal took the wheel (the camera does not zoom then).
    /// </summary>
    public bool OnStudioWheel(float notches, KeyModifiers modifiers)
    {
        if (StudioTab != StudioTab.Decals || notches == 0f)
        {
            return false;
        }

        return modifiers.HasFlag(KeyModifiers.Control) ? ResizeDecal(MathF.Pow(1.1f, notches))
            : modifiers.HasFlag(KeyModifiers.Shift) && TurnDecal(5f * notches);
    }

    /// <summary>A key over the 3D view on the Decals tab, for the selected decal; true when it was used.</summary>
    public bool OnStudioKey(Key key, KeyModifiers modifiers)
    {
        if (StudioTab != StudioTab.Decals || SelectedDecal is null)
        {
            return false;
        }

        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var step = shift ? 10f : 1f;
        switch (key)
        {
            case Keys.Left:
                return NudgeDecal(-step, 0f);
            case Keys.Right:
                return NudgeDecal(step, 0f);
            case Keys.Up:
                return NudgeDecal(0f, step);
            case Keys.Down:
                return NudgeDecal(0f, -step);
            case Keys.Q:
                return TurnDecal(shift ? -15f : -5f);
            case Keys.E:
                return TurnDecal(shift ? 15f : 5f);
            case Keys.OemPlus or Keys.Add or Keys.PageUp:
                return ResizeDecal(shift ? 1.25f : 1.05f);
            case Keys.OemMinus or Keys.Subtract or Keys.PageDown:
                return ResizeDecal(shift ? 0.8f : 1f / 1.05f);
            case Keys.X:
                return EditSelectedDecal(l => l with { FlipX = !l.FlipX });
            case Keys.M:
                return MirrorToOtherSide();
            case Keys.Delete when !SelectedDecal.Layer.Locked:
                DeleteDecal();
                return true;
            case Keys.D when modifiers.HasFlag(KeyModifiers.Control):
                DuplicateDecal();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves the selected decal <paramref name="across"/> and <paramref name="up"/> cm along its own axes, onto the surface there.</summary>
    internal bool NudgeDecal(float across, float up)
    {
        if (SelectedDecal is not { Layer: { Locked: false } layer } || DecalTarget() is not { } target || DecalPart(target.Model, layer) is not { } part)
        {
            return false;
        }

        var (centre, normal, upWorld) = WorldFrame(part, layer);
        var (s, t, n) = DecalBaker.Axes(normal, upWorld, layer.RotationDeg);
        return OnSurface(target.Model, layer, centre + (s * across) + (t * up), n) is { } next && EditSelectedDecal(_ => next);
    }

    /// <summary>Turns the selected decal by <paramref name="degrees"/>.</summary>
    internal bool TurnDecal(float degrees) => EditSelectedDecal(l => l with { RotationDeg = Wrap(l.RotationDeg + degrees) });

    /// <summary>Sizes the selected decal by <paramref name="factor"/>, keeping its shape.</summary>
    internal bool ResizeDecal(float factor) => EditSelectedDecal(l =>
    {
        var width = Math.Clamp(l.WidthCm * factor, 1f, (float)DecalLayerViewModel.MaxSizeCm);
        return l with { WidthCm = width, HeightCm = l.HeightCm * (width / l.WidthCm) };
    });

    /// <summary>
    /// "Mirror to the other side": the selected decal's design put on the car's other side (across its Y = 0 plane), where
    /// the mirrored point lies on the same material, flipped and turned the other way, as a decal of its own. False (and a
    /// note) when there is no such surface there.
    /// </summary>
    internal bool MirrorToOtherSide()
    {
        if (SelectedDecal?.Layer is not { } layer || DecalTarget() is not { } target || DecalPart(target.Model, layer) is not { } part)
        {
            return false;
        }

        static Vector3 Reflect(Vector3 v) => new(v.X, -v.Y, v.Z);
        var (centre, normal, up) = WorldFrame(part, layer);
        var (mc, mn, mu) = (Reflect(centre), Reflect(normal), Reflect(up));
        var reach = MathF.Max(layer.WidthCm, layer.HeightCm) + 20f;
        if (HitMaterial(target.Model, new SurfaceRay(-1, mc + (mn * reach), -mn), layer.MaterialPath) is not { } found)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.MirrorSide"), Loc.T("Studio.Decals.MirrorSide.Missed"));
            return false;
        }

        var on = target.Model.Parts[found.PartIndex];
        var copy = layer with
        {
            Id = new DecalLayer().Id,
            PartKey = DecalPartKey(on.Attachment),
            Position = found.Hit.Position,
            Normal = found.Hit.Normal,
            Up = on.Transform.InverseTransformVector(FVector.FromVector3(mu)).ToVector3(),
            FlipX = !layer.FlipX,
            RotationDeg = -layer.RotationDeg,
            Mirror = false,
            Locked = false,
            Order = _decals.Layers.Max(l => l.Order) + 1,
        };
        Step(_decals with { Layers = [.. _decals.Layers, copy] }, copy.Id);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanEditDecal))]
    private void MirrorDecalToOtherSide() => MirrorToOtherSide();

    /// <summary>Shows or hides a decal (one undo step; a hidden decal is not baked).</summary>
    [RelayCommand]
    private void ToggleDecalHidden(DecalLayerViewModel? item)
    {
        if (item?.Layer is { } layer)
        {
            Step(Replace(layer with { Hidden = !layer.Hidden }), SelectedDecal?.Layer.Id ?? layer.Id);
        }
    }

    /// <summary>Locks or unlocks a decal against the 3D view (one undo step).</summary>
    [RelayCommand]
    private void ToggleDecalLocked(DecalLayerViewModel? item)
    {
        if (item?.Layer is { } layer)
        {
            Step(Replace(layer with { Locked = !layer.Locked }), SelectedDecal?.Layer.Id ?? layer.Id);
        }
    }

    [RelayCommand]
    private void SaveDecalPreset() => SaveDecalPresetAs(DecalPresetName);

    /// <summary>Saves the clone's decals as the project's preset <paramref name="name"/>; false when there is nothing to save.</summary>
    internal bool SaveDecalPresetAs(string? name)
    {
        if (_services.Projects.Current is not { } project || string.IsNullOrWhiteSpace(name) || _decals.Layers.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.SavePreset"), Loc.T(_decals.Layers.Count == 0 ? "Studio.Decals.Empty" : "Studio.Decals.PresetName.Tip"));
            return false;
        }

        try
        {
            _decals.SavePreset(project.DirectoryPath, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
            return false;
        }

        RefreshDecalSources();
        SelectedDecalPreset = DecalPresets.FirstOrDefault(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase));
        _services.Notifications.Success(Loc.T("Studio.Decals.PresetSaved"), Loc.F("Studio.Decals.PresetSavedDetail", name.Trim(), _decals.Layers.Count));
        return true;
    }

    [RelayCommand]
    private void ApplyDecalPreset() => AddDecalPreset(SelectedDecalPreset);

    /// <summary>Puts the preset <paramref name="name"/>'s decals on the clone, on top of its own (one undo step).</summary>
    internal bool AddDecalPreset(string? name) =>
        name is not null && _services.Projects.Current is { } project && AddDecals(() => DecalSet.LoadPreset(project.DirectoryPath, name));

    [RelayCommand]
    private void CopyDecalsFromClone() => CopyDecalsFrom(SelectedDecalSource);

    /// <summary>Puts the decals of the project's clone <paramref name="clone"/> on this clone, on top of its own (one undo step).</summary>
    internal bool CopyDecalsFrom(string? clone) =>
        clone is not null && _services.Projects.Current is { } project && AddDecals(() => DecalSet.Load(project.DirectoryPath, clone));

    private bool AddDecals(Func<DecalSet> read)
    {
        if (DecalTarget() is not { } target)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Title"), Loc.T("Studio.Textures.CloneFirst"));
            return false;
        }

        DecalSet source;
        try
        {
            source = read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _services.Notifications.Error(Loc.T("Studio.Decals.Title"), ex.Message);
            return false;
        }

        if (source.Layers.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Title"), Loc.T("Studio.Decals.Empty"));
            return false;
        }

        // A layer whose material path is not on this model goes to the part of the same name wearing a material of the
        // same name (a clone's own copy of it), so a preset follows the vehicle rather than one clone's paths.
        source = source with { Layers = [.. source.Layers.Select(l => DecalPart(target.Model, l) is null && SameNamedMaterial(target.Model, l) is { } m ? l with { MaterialPath = m } : l)] };
        var next = source.AddTo(_decals);
        Step(next, next.Ordered[^1].Id);
        var missing = source.Layers.Count(l => DecalPart(target.Model, l) is null);
        if (missing > 0)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Title"), Loc.F("Studio.Decals.PresetMissing", missing));
        }

        _ = EnsureGameTattoosOfAsync(_decals);
        return true;
    }

    /// <summary>Lists the project's presets and its other clones with decals.</summary>
    private void RefreshDecalSources()
    {
        if (_services.Projects.Current is not { } project)
        {
            (DecalPresets, DecalSourceClones) = ([], []);
            return;
        }

        DecalPresets = DecalSet.Presets(project.DirectoryPath);
        DecalSourceClones = [.. DecalSet.Clones(project.DirectoryPath).Where(c => !string.Equals(c, _decals.CloneLeaf, StringComparison.OrdinalIgnoreCase) && HasLayers(project.DirectoryPath, c))];

        static bool HasLayers(string directory, string clone)
        {
            try
            {
                return DecalSet.Load(directory, clone).Layers.Count > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>A centre typed in the position fields (vehicle space, cm): the decal goes there, onto the surface along its normal.</summary>
    private void MoveDecalTo(DecalLayerViewModel item, Vector3 centre)
    {
        if (DecalTarget() is not { } target || DecalPart(target.Model, item.Layer) is not { } part)
        {
            return;
        }

        var layer = item.Layer;
        var (_, normal, _) = WorldFrame(part, layer);
        var next = OnSurface(target.Model, layer, centre, normal)
            ?? layer with { Position = part.Transform.InverseTransformPosition(FVector.FromVector3(centre)).ToVector3() };
        item.Show(next);
        OnDecalEdited(next);
        ShowDecalPosition(item);
    }

    /// <summary>Shows the decal's centre (vehicle space) in its position fields.</summary>
    private void ShowDecalPosition(DecalLayerViewModel item)
    {
        if (DecalTarget() is { } target && DecalPart(target.Model, item.Layer) is { } part)
        {
            item.ShowPosition(WorldFrame(part, item.Layer).Centre);
        }
    }

    /// <summary>Applies <paramref name="change"/> to the selected decal (not a locked one) as an edit of the panel.</summary>
    private bool EditSelectedDecal(Func<DecalLayer, DecalLayer> change)
    {
        if (SelectedDecal is not { Layer: { Locked: false } layer } item)
        {
            return false;
        }

        var next = change(layer);
        item.Show(next);
        OnDecalEdited(next);
        ShowDecalPosition(item);
        return true;
    }

    /// <summary>The material of <paramref name="model"/>'s part <see cref="DecalLayer.PartKey"/> whose object name is the layer's material's, or null.</summary>
    private static string? SameNamedMaterial(PreviewModel model, DecalLayer layer)
    {
        var name = layer.MaterialPath[(layer.MaterialPath.LastIndexOf('.') + 1)..];
        return model.Parts.FirstOrDefault(p => string.Equals(DecalPartKey(p.Attachment), layer.PartKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Material[(p.Material.LastIndexOf('.') + 1)..], name, StringComparison.OrdinalIgnoreCase))?.Material;
    }

    private DecalSet Replace(DecalLayer layer) => _decals with { Layers = [.. _decals.Layers.Select(l => l.Id == layer.Id ? layer : l)] };

    /// <summary>
    /// <paramref name="layer"/> moved to where a ray from above <paramref name="centre"/> along −<paramref name="normal"/>
    /// meets its material (vehicle space in, the hit part's space out), its up kept; null when it meets none.
    /// </summary>
    private static DecalLayer? OnSurface(PreviewModel model, DecalLayer layer, Vector3 centre, Vector3 normal)
    {
        var part = DecalPart(model, layer);
        var upWorld = part is null ? Vector3.UnitZ : part.Transform.TransformVectorNoScale(FVector.FromVector3(layer.Up)).ToVector3();
        var reach = MathF.Max(layer.WidthCm, layer.HeightCm) + 20f;
        if (HitMaterial(model, new SurfaceRay(-1, centre + (normal * reach), -normal), layer.MaterialPath) is not { } found)
        {
            return null;
        }

        var on = model.Parts[found.PartIndex];
        return layer with
        {
            PartKey = DecalPartKey(on.Attachment),
            Position = found.Hit.Position,
            Normal = found.Hit.Normal,
            Up = on.Transform.InverseTransformVector(FVector.FromVector3(upWorld)).ToVector3(),
        };
    }

    /// <summary>The nearest hit of <paramref name="ray"/> on the parts drawn with <paramref name="material"/> (others are passed through).</summary>
    private static (int PartIndex, SurfaceHit Hit)? HitMaterial(PreviewModel model, SurfaceRay ray, string material)
    {
        (int, SurfaceHit)? best = null;
        for (var i = 0; i < model.Parts.Count; i++)
        {
            var part = model.Parts[i];
            if (string.Equals(part.Material, material, StringComparison.OrdinalIgnoreCase)
                && MeshRay.Hit(part.Mesh, part.Transform, ray.Origin, ray.Direction) is { } hit && (best is null || hit.Distance < best.Value.Item2.Distance))
            {
                best = (i, hit);
            }
        }

        return best;
    }

    /// <summary>The decal's centre, normal and up in vehicle space.</summary>
    private static (Vector3 Centre, Vector3 Normal, Vector3 Up) WorldFrame(PreviewPart part, DecalLayer layer) =>
        (part.Transform.TransformPosition(FVector.FromVector3(layer.Position)).ToVector3(),
         Vector3.Normalize(part.Transform.TransformVectorNoScale(FVector.FromVector3(layer.Normal)).ToVector3()),
         part.Transform.TransformVectorNoScale(FVector.FromVector3(layer.Up)).ToVector3());

    private static float Wrap(float degrees)
    {
        var d = degrees % 360f;
        return d > 180f ? d - 360f : d <= -180f ? d + 360f : d;
    }
}
