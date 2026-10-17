using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <content>
/// Owner (2026-10-12): "every part of the car editable like Blender: move it, scale it, hide it, add parts". The Parts
/// panel of the Vehicles 3D tab: a part picked on the car (<see cref="SelectStudioPart"/>, the 3D view forwards its pick)
/// is moved, turned or scaled by the gizmo (<see cref="MovePart"/>) or the numbers, hidden, removed from the spawn, given
/// another model, put back; "Add part…" bolts any model of the game where the user clicks (<see cref="AddPart"/>). Each
/// step is one journaled <see cref="BatchOp"/> (<see cref="PartsStudio"/>); clones only.
/// </content>
public abstract partial class ModulePageViewModel
{
    private readonly Dictionary<int, PartOverride> _partOverrides = [];
    private StudioPart? _studioPart;
    private int _studioPartIndex = -1;

    /// <summary>What the parts gizmo does (G move, R rotate, S scale).</summary>
    [ObservableProperty]
    private PartTool _partTool = PartTool.Move;

    /// <summary>The picked part's name, or the hint to pick one.</summary>
    [ObservableProperty]
    private string _studioPartName = Loc.T("Studio.Parts.Pick");

    /// <summary>Why the picked part cannot be edited (a wheel, a stock car, a shared mesh), or empty.</summary>
    [ObservableProperty]
    private string _studioPartNote = string.Empty;

    /// <summary>True when a part of a clone is picked and its place can be edited.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HidePartCommand), nameof(ResetPartCommand), nameof(ApplyPartPlaceCommand))]
    private bool _canEditPart;

    /// <summary>Position of the picked part (cm, socket space), as typed.</summary>
    [ObservableProperty]
    private string _partLocation = string.Empty;

    /// <summary>Rotation of the picked part (pitch, yaw, roll in degrees), as typed.</summary>
    [ObservableProperty]
    private string _partRotation = string.Empty;

    /// <summary>Scale of the picked part (1 = the game's size), as typed.</summary>
    [ObservableProperty]
    private string _partScale = string.Empty;

    /// <summary>Text of the model search ("Other model…", "Add part…").</summary>
    [ObservableProperty]
    private string _partMeshSearch = string.Empty;

    /// <summary>Models of the game matching <see cref="PartMeshSearch"/> (object paths).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _partMeshChoices = [];

    /// <summary>The model chosen in the list.</summary>
    [ObservableProperty]
    private string? _selectedPartMesh;

    /// <summary>The 3D view's live overrides: part index → place relative to the game's, or hidden.</summary>
    public IReadOnlyDictionary<int, PartOverride> PartOverrides => _partOverrides;

    /// <summary>Raised when <see cref="PartOverrides"/> changed (a drag, an undo, another part).</summary>
    public event EventHandler? PartOverridesChanged;

    /// <summary>The picked part, or null.</summary>
    public StudioPart? StudioPart => _studioPart;

    /// <summary>Picks the part drawn by preview part <paramref name="partIndex"/> (-1 clears).</summary>
    public void SelectStudioPart(int partIndex)
    {
        _studioPartIndex = partIndex;
        _studioPart = null;
        StudioPartNote = string.Empty;
        var attachment = Preview is { } model && partIndex >= 0 && partIndex < model.Parts.Count ? model.Parts[partIndex].Attachment : string.Empty;
        if (attachment.Length == 0)
        {
            StudioPartName = Loc.T("Studio.Parts.Pick");
            CanEditPart = false;
            return;
        }

        StudioPartName = VehicleParts.Label(attachment + "." + attachment[(attachment.LastIndexOf('/') + 1)..] + "_C");
        if (PartsStudioOf() is not { } studio)
        {
            StudioPartNote = Loc.T(SelectedItem?.IsClone == true ? "Studio.Parts.SharedMesh" : "Studio.Parts.CloneFirst");
            CanEditPart = false;
            return;
        }

        _studioPart = studio.Select(attachment);
        StudioPartNote = _studioPart is null ? Loc.T("Studio.Parts.CloneFirst")
            : studio.Mesh is null ? Loc.T("Studio.Parts.SharedMesh")
            : _studioPart.IsWheel ? Loc.T("Studio.Parts.WheelNote")
            : string.Empty;
        CanEditPart = _studioPart is { IsWheel: false } && studio.Mesh is not null;
        ShowPlace(_studioPart?.Place);
    }

    /// <summary>
    /// A gizmo drag of the picked part by <paramref name="worldDelta"/> (vehicle space, from where the drag started):
    /// the 3D view follows; <paramref name="commit"/> (mouse up) journals one step.
    /// </summary>
    /// <returns>False when the part cannot move (refused: see <see cref="StudioPartNote"/>).</returns>
    public bool MovePart(int partIndex, FTransform worldDelta, bool commit)
    {
        if (partIndex != _studioPartIndex)
        {
            SelectStudioPart(partIndex);
        }

        if (_studioPart is not { Place: { } place } part || !CanEditPart)
        {
            return false;
        }

        var (location, rotation, scale) = PartsStudio.Apply(place, worldDelta);
        if (!commit)
        {
            SetOverride(part, place with { Location = location, Rotation = rotation, Scale = scale });
            return true;
        }

        return Journal(studio => studio.Move(part, location, rotation, scale));
    }

    /// <summary>"Add part…" is armed: the next click on the car (<see cref="AddPartAt"/>) bolts <see cref="SelectedPartMesh"/> there.</summary>
    [ObservableProperty]
    private bool _isAddingPart;

    /// <summary>A click on the car's surface while "Add part…" is armed (vehicle space, cm): adds the chosen model there.</summary>
    /// <returns>True when a part was added.</returns>
    public bool AddPartAt(Vector3 point)
    {
        if (!IsAddingPart)
        {
            return false;
        }

        if (SelectedPartMesh is not { } mesh)
        {
            StudioPartNote = Loc.T("Studio.Parts.PickModel"); // armed without a model: say what is missing
            return false;
        }

        IsAddingPart = false;
        return AddPart(mesh, point);
    }

    /// <summary>Bolts <paramref name="meshObjectPath"/> onto the selected clone at <paramref name="point"/> (vehicle space, cm).</summary>
    public bool AddPart(string meshObjectPath, Vector3 point)
    {
        string? added = null;
        var ok = Journal(studio =>
        {
            var (step, package) = studio.Add(meshObjectPath, point, p => _catalog?.PackageExists(p) == true);
            added = package;
            return step;
        });
        if (ok && added is not null)
        {
            _services.Notifications.Success(Loc.T("Studio.Parts.Add"), added);
        }

        return ok;
    }

    /// <summary>Shows another model of the game for the picked part.</summary>
    public bool SwapPartMesh(string meshObjectPath) =>
        _studioPart is { } part && Journal(studio => studio.SwapMesh(part, meshObjectPath, ClassOfMesh));

    [RelayCommand]
    private void SetPartTool(PartTool tool) => PartTool = tool;

    [RelayCommand(CanExecute = nameof(CanEditPart))]
    private void HidePart()
    {
        if (_studioPart is { } part)
        {
            Journal(studio => studio.Hide(part, !(PartsStudio.OverrideOf(part)?.Hidden ?? false)));
        }
    }

    [RelayCommand]
    private void RemovePart()
    {
        if (_studioPart is not { } part)
        {
            StudioPartNote = Loc.T("Studio.Parts.PickFirst");
            return;
        }

        var studio = PartsStudioOf();
        if (studio is not null && !studio.IsAdded(part.Attachment) && studio.IsRemoved(part))
        {
            return; // already off the spawn: Ctrl+Z or Game position puts it back
        }

        // A part no cloned spawn preset holds cannot leave the spawn (the vehicle's own body, a clone without presets): say so.
        Journal(s =>
        {
            var step = s.Remove(part);
            if (step is null)
            {
                throw new PartRefusedException("Studio.Parts.NotSpawned", $"{part.Label} is in no cloned spawn preset.");
            }

            return step;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditPart))]
    private void ResetPart()
    {
        if (_studioPart is { } part)
        {
            Journal(studio => studio.Reset(part));
        }
    }

    /// <summary>Journals the typed position, rotation and scale (Enter in the boxes).</summary>
    [RelayCommand(CanExecute = nameof(CanEditPart))]
    private void ApplyPartPlace()
    {
        if (_studioPart is not { Game: { } game } part)
        {
            return;
        }

        try
        {
            var l = TunableValue.ParseFloats(PartLocation, 3);
            var r = TunableValue.ParseFloats(PartRotation, 3);
            var s = TunableValue.ParseFloats(PartScale, 3);
            Journal(studio => studio.Move(part, game.Location + new Vector3(l[0], l[1], l[2]), new Vector3(r[0], r[1], r[2]), game.Scale * new Vector3(s[0], s[1], s[2])));
        }
        catch (FormatException ex)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
    }

    [RelayCommand]
    private void SwapPartToSelectedMesh()
    {
        if (SelectedPartMesh is { } mesh)
        {
            SwapPartMesh(mesh);
        }
    }

    partial void OnPartMeshSearchChanged(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PartMeshChoices = _catalog is not { } catalog || tokens.Length == 0
            ? []
            : catalog.FindByClass("StaticMesh").Concat(catalog.FindByClass("SkeletalMesh"))
                .Select(e => e.ObjectPath)
                .Where(p => tokens.All(t => p.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Length)
                .Take(200)
                .ToList();
    }

    /// <summary>The parts studio of the selected item when it is a vehicle clone of the open project, else null.</summary>
    private PartsStudio? PartsStudioOf()
    {
        var state = _services.Projects.Current?.State;
        if (_catalog is not { } catalog || state is null || SelectedItem is not { IsClone: true } item || state.FindCloneOf(item.PackagePath) is not { } clone)
        {
            return null;
        }

        return new PartsStudio(clone, p => catalog.PackageExists(p) || state.FindCloneOf(p) is not null ? TryRead(catalog, p) : null, state);
    }

    private bool Journal(Func<PartsStudio, EditOp?> step)
    {
        if (!_services.Projects.HasProject || PartsStudioOf() is not { } studio)
        {
            StudioPartNote = Loc.T("Studio.Parts.CloneFirst");
            return false;
        }

        try
        {
            if (step(studio) is not { } op)
            {
                return true;
            }

            _services.Projects.Apply(op);
        }
        catch (PartRefusedException ex)
        {
            StudioPartNote = Loc.T(ex.Key);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
            return false;
        }

        if (_studioPartIndex >= 0)
        {
            SelectStudioPart(_studioPartIndex); // the journaled place is the new starting point
        }

        // The 3D view follows the project (moves, hides, parts taken off or added), as after an undo; the boxes show the place.
        ShowPlace(_studioPart?.Place);
        _ = RefreshStudioPartsAsync();

        return true;
    }

    private void SetOverride(StudioPart part, VehicleSocket? place)
    {
        var over = place is null ? null : PartsStudio.OverrideOf(part with { Place = place });
        var stock = Preview?.Parts.Select((p, i) => (p.Attachment, i))
            .Where(p => p.Attachment.Length > 0 && string.Equals(PartsStudioOf()?.CloneOf(p.Attachment) ?? p.Attachment, part.Attachment, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.i) ?? [];
        foreach (var i in stock.ToList())
        {
            if (over is null)
            {
                _partOverrides.Remove(i);
            }
            else
            {
                _partOverrides[i] = over;
            }
        }

        ShowPlace(place);
        OnPropertyChanged(nameof(StudioPartOverrides));
        PartOverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPlace(VehicleSocket? place)
    {
        // Position and scale read relative to the game's place (0 cm, 1 = where and how big the game has it); rotation as is.
        var game = _studioPart?.Game;
        PartLocation = place is null ? string.Empty : VehicleSockets.Format(place.Location - (game?.Location ?? Vector3.Zero));
        PartRotation = place is null ? string.Empty : VehicleSockets.Format(place.Rotation);
        PartScale = place is null ? string.Empty : VehicleSockets.Format(place.Scale / (game?.Scale ?? Vector3.One));
    }

    private string? ClassOfMesh(string objectPath)
    {
        var package = VehicleParts.PackageOf(objectPath);
        return _catalog is { } catalog && catalog.PackageExists(package)
            ? catalog.GetExports(package).FirstOrDefault(e => e.ClassName is "StaticMesh" or "SkeletalMesh")?.ClassName
            : null;
    }

    /// <summary>The project's added parts as the 3D view draws them (appended after the vehicle's own parts).</summary>
    private IReadOnlyList<PreviewPart> _addedPreviewParts = [];

    /// <summary>The textures of <see cref="_addedPreviewParts"/>.</summary>
    private IReadOnlyDictionary<string, TextureImage> _addedPreviewTextures = new Dictionary<string, TextureImage>();

    /// <summary>The models added parts show, loaded once each (object path → model, null when unreadable).</summary>
    private readonly Dictionary<string, PreviewModel?> _addedMeshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The preview as loaded from the template (before paint and added parts).</summary>
    private PreviewModel? _previewLoaded;

    private int _studioPartsLoads;

    /// <summary>Completes when the last <see cref="RefreshStudioPartsAsync"/> finished (tests).</summary>
    internal Task StudioPartsCompletion { get; private set; } = Task.CompletedTask;

    /// <summary><paramref name="model"/> with the project's added parts after its own (the same model when there are none).</summary>
    private PreviewModel WithAddedParts(PreviewModel model)
    {
        if (_addedPreviewParts.Count == 0)
        {
            return model;
        }

        var textures = new Dictionary<string, TextureImage>(model.Textures, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, image) in _addedPreviewTextures)
        {
            textures.TryAdd(path, image);
        }

        return model with { Parts = [.. model.Parts, .. _addedPreviewParts], Textures = textures };
    }

    /// <summary>
    /// Shows the clone's parts as the project has them now (after a step, an undo or a redo, or a new preview): a stock part
    /// moved or hidden is drawn there, one taken off the spawn is hidden, and every part "Add part…" bolted on is drawn on
    /// its socket with its model.
    /// </summary>
    internal Task RefreshStudioPartsAsync() => StudioPartsCompletion = RefreshStudioPartsCoreAsync();

    private async Task RefreshStudioPartsCoreAsync()
    {
        var load = ++_studioPartsLoads;
        if (!IsVehicleModule || Preview is not { } model)
        {
            return;
        }

        var studio = PartsStudioOf();
        _partOverrides.Clear();
        if (studio is not null)
        {
            var known = new Dictionary<string, PartOverride?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < model.Parts.Count; i++)
            {
                var attachment = model.Parts[i].Attachment;
                if (attachment.Length == 0 || studio.IsAdded(attachment))
                {
                    continue; // an added part is drawn where the project has it
                }

                if (!known.TryGetValue(attachment, out var over))
                {
                    var part = studio.Select(attachment);
                    known[attachment] = over = part is null ? null
                        : studio.IsRemoved(part) ? new PartOverride(FTransform.Identity, Hidden: true)
                        : studio.Mesh is null ? null
                        : PartsStudio.OverrideOf(part);
                }

                if (over is not null)
                {
                    _partOverrides[i] = over;
                }
            }
        }

        OnPropertyChanged(nameof(StudioPartOverrides));
        PartOverridesChanged?.Invoke(this, EventArgs.Empty);

        var added = studio?.AddedParts() ?? [];
        var catalog = _catalog;
        var missing = added.Select(p => p.Mesh).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !_addedMeshes.ContainsKey(m)).ToList();
        if (missing.Count > 0 && catalog is not null)
        {
            var logger = _services.Logger;
            var loaded = await Task.Run(() => missing.Select(m =>
            {
                try
                {
                    return (Mesh: m, Model: (PreviewModel?)new MeshPreviewLoader(catalog, logger) { TextureSize = 1024, ForGpu = true }.LoadMesh(m));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogInformation("Added part model {Mesh}: {Message}", m, ex.Message);
                    return (Mesh: m, Model: null);
                }
            }).ToList()).ConfigureAwait(true);
            foreach (var (mesh, part) in loaded)
            {
                _addedMeshes[mesh] = part;
            }
        }

        if (load != _studioPartsLoads)
        {
            return; // a newer refresh runs
        }

        var parts = new List<PreviewPart>();
        var textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in added)
        {
            if (part.Mesh is not { } mesh || _addedMeshes.GetValueOrDefault(mesh) is not { } shown || (part.Place ?? part.Game) is not { } place)
            {
                continue;
            }

            var at = PartsStudio.Transform(place);
            parts.AddRange(shown.Parts.Select(p => p with { Transform = Compose(p.Transform, at), Attachment = part.Attachment }));
            foreach (var (path, image) in shown.Textures)
            {
                textures.TryAdd(path, image);
            }
        }

        if (parts.Count == _addedPreviewParts.Count && parts.Zip(_addedPreviewParts).All(p => p.First == p.Second))
        {
            return;
        }

        _addedPreviewParts = parts;
        _addedPreviewTextures = textures;
        if (_previewBase is not null)
        {
            Repaint();
        }
        else if (_previewLoaded is { } loadedModel)
        {
            Preview = WithAddedParts(loadedModel);
        }

        static FTransform Compose(FTransform local, FTransform socket) =>
            new(socket.Rotation * local.Rotation, socket.TransformPosition(local.Translation), socket.Scale3D * local.Scale3D);
    }

    /// <summary>Forgets the added parts drawn (another vehicle selected).</summary>
    private void ForgetAddedParts()
    {
        _studioPartsLoads++;
        _addedPreviewParts = [];
        _addedPreviewTextures = new Dictionary<string, TextureImage>();
        _previewLoaded = null;
    }
}
