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
/// air; houses tilt, they do not bend like bridges; Hektor: "fit a forest to the terrain, it's not aligning multiple
/// objects"): the selected objects, or every member of the multi-selection, are set down on the ground. A building is
/// tilted to the slope under its footprint; a tree, bush or rock (a copy, or one instance of the game's foliage) stays
/// upright with its foot on the ground, the way the game plants it.
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

        var members = HasGroup
            ? GroupItems().Select(m => (m.Item, m.Instance)).ToList()
            : SelectedActor is { } one ? [(one, SelectedInstanceInfo())] : [];
        var ops = new List<EditOp>();
        var missed = 0;
        foreach (var (item, sel) in members)
        {
            if (sel is { Instance: { } instance })
            {
                // One tree, bush or rock of the game's foliage: its foot on the ground, upright as it is.
                var space = SpaceOf(sel);
                var world = CurrentInstanceTransform(sel).ToTransform() * space;
                if (Planted(world, MeshBounds(instance.StaticMeshPath), IsPlant(instance.StaticMeshPath)) is not { } placed)
                {
                    missed++;
                    continue;
                }

                var value = TransformValue.FromTransform(placed.GetRelativeTransform(space));
                if (!value.IsNearlyEqual(CurrentInstanceTransform(sel)))
                {
                    ops.Add(EditOpFactory.SetInstanceTransform(item.Level, item.Actor, instance.ComponentName, instance.InstanceIndex, value, project.State));
                }

                continue;
            }

            if (sel is not null || IsImmovable(item))
            {
                continue; // a road piece or a building's part: it belongs to its road or building
            }

            var grounded = IsPlant(item.Actor.StaticMeshPath) || item.Actor.Kind == ActorKind.StaticMeshActor && item.Actor.StaticMeshPath is { } m && IsPlant(m)
                ? Planted(WorldOf(item, null), MeshBounds(item.Actor.StaticMeshPath), true)
                : Grounded(item);
            if (grounded is not { } root)
            {
                missed++;
                continue;
            }

            var relative = RelativeOf(item, root) with { Scale = CurrentRootTransform(item).Scale };
            if (!relative.IsNearlyEqual(CurrentRootTransform(item)))
            {
                ops.Add(item.IsAdded ? EditOpFactory.SetAddedActorTransform(item.Reference, relative, project.State) : EditOpFactory.SetTransform(item.Level, item.Actor, relative, project.State));
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

    /// <summary>True for a mesh of the game's foliage (trees, bushes, cacti, grass): the game plants it with its pivot on the ground.</summary>
    private static bool IsPlant(string? meshPath) => meshPath is not null && meshPath.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="world"/> set down on the ground, upright as it is: a plant with its pivot (where the game puts its
    /// foot) on the ground, anything else with the bottom of its box on the ground. Null without ground under it.
    /// </summary>
    private FTransform? Planted(FTransform world, BoundingBox? bounds, bool plant)
    {
        var foot = plant || bounds is not { IsEmpty: false }
            ? world.Translation
            : world.TransformPosition(new FVector(bounds.Value.Center.X, bounds.Value.Center.Y, bounds.Value.Min.Z));
        if (GroundAt(foot.X, foot.Y) is not { } ground)
        {
            return null;
        }

        return world with { Translation = world.Translation with { Z = world.Translation.Z + (ground - foot.Z) } };
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
