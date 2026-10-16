using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.ViewModels;

/// <summary>A tab of the Vehicles studio beside the 3D view.</summary>
public enum StudioTab
{
    /// <summary>Colours, shine and finish of each material slot.</summary>
    Materials,

    /// <summary>The pictures the car is painted with.</summary>
    Textures,

    /// <summary>Tattoos and stickers on the body.</summary>
    Decals,

    /// <summary>Move, hide, swap and add parts.</summary>
    Parts,
}

/// <summary>
/// The Vehicles studio (owner: "full control like Blender"): the paint panel beside the 3D view becomes Materials | Textures |
/// Decals | Parts for a vehicle (a weapon keeps its materials), and each tab gives the view its own mouse: the decals point
/// at the body, the parts get a gizmo, the others turn the view.
/// </summary>
public abstract partial class ModulePageViewModel
{
    /// <summary>The studio tab shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioTool), nameof(IsStudioMaterials), nameof(IsStudioTextures), nameof(IsStudioDecals), nameof(IsStudioParts),
        nameof(ShowMaterialsPanel), nameof(StudioToolHint))]
    private StudioTab _studioTab;

    /// <summary>The studio's tabs are offered (vehicles; a weapon has its materials only).</summary>
    public bool HasStudioTabs => IsVehicleModule;

    /// <summary>The panel beside the 3D view is shown: a model with paint, or any vehicle (its parts and decals need no paint).</summary>
    public bool ShowStudioPanel => HasPaints || (IsVehicleModule && HasPreview);

    /// <summary>What the left mouse button does on the tab shown, over the 3D view.</summary>
    public string StudioToolHint => StudioTool switch
    {
        PreviewTool.Surface => Loc.T("Preview.Tool.Surface.Tip"),
        PreviewTool.Gizmo => Loc.T("Preview.Tool.Gizmo.Tip"),
        _ => Loc.T("Preview.Tool.Orbit.Tip"),
    };

    /// <summary>The tool hint is shown (vehicles with a model).</summary>
    public bool ShowStudioToolHint => IsVehicleModule && HasPreview;

    /// <summary>What the left mouse button does in the 3D view for the tab shown.</summary>
    public PreviewTool StudioTool => !IsVehicleModule ? PreviewTool.Orbit : StudioTab switch
    {
        StudioTab.Decals => PreviewTool.Surface,
        StudioTab.Parts => IsAddingPart ? PreviewTool.Surface : PreviewTool.Gizmo,
        _ => PreviewTool.Orbit,
    };

    /// <summary>The paint list is shown (the Materials tab, or a weapon).</summary>
    public bool ShowMaterialsPanel => !IsVehicleModule || StudioTab == StudioTab.Materials;

    /// <summary>The Materials tab is shown (a radio button's two-way binding).</summary>
    public bool IsStudioMaterials
    {
        get => StudioTab == StudioTab.Materials;
        set => Pick(value, StudioTab.Materials);
    }

    /// <summary>The Textures tab is shown.</summary>
    public bool IsStudioTextures
    {
        get => StudioTab == StudioTab.Textures;
        set => Pick(value, StudioTab.Textures);
    }

    /// <summary>The Decals tab is shown.</summary>
    public bool IsStudioDecals
    {
        get => StudioTab == StudioTab.Decals;
        set => Pick(value, StudioTab.Decals);
    }

    /// <summary>The Parts tab is shown.</summary>
    public bool IsStudioParts
    {
        get => StudioTab == StudioTab.Parts;
        set => Pick(value, StudioTab.Parts);
    }

    /// <summary>The handles the part gizmo shows for the Parts tab's tool (move, turn, scale).</summary>
    public PreviewGizmoMode StudioGizmoMode => PartTool switch
    {
        PartTool.Rotate => PreviewGizmoMode.Rotate,
        PartTool.Scale => PreviewGizmoMode.Scale,
        _ => PreviewGizmoMode.Move,
    };

    /// <summary>The parts moved or hidden on the Parts tab, as the 3D view draws them (by preview part index).</summary>
    public IReadOnlyDictionary<int, PreviewPartOverride> StudioPartOverrides =>
        PartOverrides.ToDictionary(p => p.Key, p => new PreviewPartOverride(p.Value.Delta, p.Value.Hidden));

    partial void OnPartToolChanged(PartTool value) => OnPropertyChanged(nameof(StudioGizmoMode));

    partial void OnIsAddingPartChanged(bool value)
    {
        OnPropertyChanged(nameof(StudioTool));
        OnPropertyChanged(nameof(StudioToolHint));
    }

    /// <summary>A surface drag of the 3D view is moving a decal.</summary>
    private bool _studioSurfaceDrag;

    /// <summary>
    /// A click, drag step or release on the car in the 3D view (<see cref="PreviewTool.Surface"/>), as a ray in UE space: on
    /// the Decals tab a press puts the armed design there (then disarms it) or picks the decal under the cursor, a drag moves
    /// it and the release ends the step; Escape puts it back. On the Parts tab with "Add part…" armed, a press bolts the
    /// chosen model where the ray meets the car.
    /// </summary>
    public void OnStudioSurface(int partIndex, Vector3 origin, Vector3 direction, bool drag, bool release, bool cancel)
    {
        var ray = new SurfaceRay(partIndex, origin, direction);
        if (StudioTab == StudioTab.Parts)
        {
            if (!drag && !release && !cancel && IsAddingPart && Preview is { } model && MeshRay.Hit(model, ray) is { } found)
            {
                var hit = found.Hit.Position;
                var point = model.Parts[found.PartIndex].Transform.TransformPosition(new FVector(hit.X, hit.Y, hit.Z));
                AddPartAt(new Vector3(point.X, point.Y, point.Z));
            }

            return;
        }

        if (StudioTab != StudioTab.Decals)
        {
            return;
        }

        if (cancel)
        {
            _studioSurfaceDrag = false;
            CancelDecalDrag();
        }
        else if (!drag && !release)
        {
            var armed = ArmedDecal is not null || _armedSticker is not null;
            _studioSurfaceDrag = PlaceDecal(ray);
            if (armed && _studioSurfaceDrag)
            {
                ArmDecal(null); // one click, one decal: the next click picks or drags
            }
        }
        else if (_studioSurfaceDrag)
        {
            if (drag || DecalsDragging)
            {
                DragDecal(ray, commit: release); // a click without a move records nothing
            }

            _studioSurfaceDrag = !release;
        }
    }

    /// <summary>A gizmo drag of preview part <paramref name="partIndex"/> from <paramref name="start"/> to <paramref name="now"/> (Parts tab); <paramref name="committed"/> journals it.</summary>
    public void OnStudioGizmo(int partIndex, FTransform start, FTransform now, bool committed)
    {
        if (StudioTab == StudioTab.Parts)
        {
            MovePart(partIndex, GizmoDelta(start, now), committed);
        }
    }

    /// <summary>A part picked in the 3D view (Parts tab): its socket shows in the panel.</summary>
    public void OnStudioPick(int partIndex)
    {
        if (StudioTab == StudioTab.Parts && Preview is not null)
        {
            SelectStudioPart(partIndex);
        }
    }

    /// <summary>
    /// The change of a gizmo drag in vehicle space, as <see cref="PartsStudio.Apply"/> takes it: the move, the turn applied
    /// after the start's (now = delta · start) and the scale ratio.
    /// </summary>
    internal static FTransform GizmoDelta(FTransform start, FTransform now)
    {
        static float Ratio(float a, float b) => MathF.Abs(b) < 1e-6f ? 1f : a / b;
        return new FTransform(now.Rotation * start.Rotation.Inverse(), now.Translation - start.Translation,
            new FVector(Ratio(now.Scale3D.X, start.Scale3D.X), Ratio(now.Scale3D.Y, start.Scale3D.Y), Ratio(now.Scale3D.Z, start.Scale3D.Z)));
    }

    /// <summary>A tab's toggle was checked (unchecking the tab shown does nothing: the view checks it again).</summary>
    private void Pick(bool on, StudioTab tab)
    {
        if (on)
        {
            StudioTab = tab;
        }
    }
}
