using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Fit to ground" (owner: a house on a hillside should follow the slope, not stand half in the ground and half in the
/// air; houses tilt, they do not bend like bridges): the selected objects are tilted to the slope under their footprint
/// and set down on it, which way they face kept.
/// </summary>
public sealed partial class MapPageViewModel
{
    [RelayCommand]
    private void FitToGround()
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Moves"));
            return;
        }

        var items = HasGroup
            ? GroupItems().Where(m => m.Instance is null).Select(m => m.Item).ToList()
            : SelectedActor is { } one && SelectedInstanceKey is null ? [one] : [];
        var ops = new List<EditOp>();
        var missed = 0;
        foreach (var item in items.Where(i => !IsImmovable(i)))
        {
            if (Grounded(item) is not { } world)
            {
                missed++;
                continue;
            }

            var value = RelativeOf(item, world) with { Scale = CurrentRootTransform(item).Scale };
            if (!value.IsNearlyEqual(CurrentRootTransform(item)))
            {
                ops.Add(item.IsAdded ? EditOpFactory.SetAddedActorTransform(item.Reference, value, project.State) : EditOpFactory.SetTransform(item.Level, item.Actor, value, project.State));
            }
        }

        if (missed > 0 && ops.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Map.Ground"), Loc.T("Map.Ground.NoGround"));
            return;
        }

        ApplyGroupOps(ops, Loc.T("Map.Ground"));
        if (HasGroup)
        {
            RefreshGroup();
        }
    }

    /// <summary>
    /// Where <paramref name="item"/>'s root goes standing on the ground: up along the slope under the four corners of its
    /// footprint, the middle of its bottom on the ground where it is now. Null without a footprint or ground there.
    /// </summary>
    private FTransform? Grounded(ActorItemViewModel item)
    {
        if (Footprint(item) is not { IsEmpty: false } box)
        {
            return null;
        }

        var now = WorldOf(item, null);
        var facing = new FRotator(0f, now.Rotator().Yaw, 0f).Quaternion();
        var level = now with { Rotation = facing };
        var corners = new[]
        {
            new FVector(box.Min.X, box.Min.Y, box.Min.Z), new FVector(box.Max.X, box.Min.Y, box.Min.Z),
            new FVector(box.Max.X, box.Max.Y, box.Min.Z), new FVector(box.Min.X, box.Max.Y, box.Min.Z),
        }.Select(level.TransformPosition).ToList();
        var ground = new FVector[4];
        for (var i = 0; i < 4; i++)
        {
            if (GroundAt(corners[i].X, corners[i].Y) is not { } z)
            {
                return null;
            }

            ground[i] = corners[i] with { Z = z };
        }

        // The slope: the normal of the two diagonals (exact for a plane, the average tilt otherwise).
        var normal = FVector.Cross(ground[2] - ground[0], ground[3] - ground[1]).GetSafeNormal();
        if (normal.Z < 0)
        {
            normal = -normal;
        }

        var rotation = FQuat.FindBetweenNormals(FVector.Up, normal) * facing;
        var bottom = new FVector(box.Center.X, box.Center.Y, box.Min.Z);
        var at = now.TransformPosition(bottom);
        var height = ground.Average(g => g.Z);
        return new FTransform(rotation, new FVector(at.X, at.Y, height) - rotation.RotateVector(now.Scale3D * bottom), now.Scale3D);
    }

    /// <summary>Bounds of what the actor draws in its root's space (unscaled): its mesh, or a building's parts together.</summary>
    private BoundingBox? Footprint(ActorItemViewModel item)
    {
        if (item.Actor.Kind == ActorKind.StaticMeshActor || item.Actor.Root is not { } root)
        {
            return MeshBounds(item.Actor.StaticMeshPath);
        }

        var box = BoundingBox.Empty;
        foreach (var component in item.Actor.Components)
        {
            if (component.StaticMeshPath is not { } mesh || component.IsInstanced || !component.IsVisible || MeshBounds(mesh) is not { IsEmpty: false } b)
            {
                continue;
            }

            for (var i = 0; i < 8; i++)
            {
                var corner = new FVector((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                var local = root.WorldTransform.InverseTransformPosition(component.WorldTransform.TransformPosition(corner));
                box = box.Include(new Vector3(local.X, local.Y, local.Z));
            }
        }

        return box.IsEmpty ? null : box;
    }

    /// <summary>Terrain height (UE cm) at a point: the loaded tiles, else the whole island's coarser ground.</summary>
    private float? GroundAt(float x, float y) =>
        PreparedScene?.HeightField?.SampleHeight(x, y) ?? WorldBackdrop?.HeightField?.SampleHeight(x, y);
}
