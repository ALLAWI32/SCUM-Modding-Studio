using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Fit to ground" (owner: a house on a hillside should follow the slope, not stand half in the ground and half in the
/// air; houses tilt, they do not bend like bridges; Hektor: "fit a forest to the terrain, it's not aligning multiple
/// objects"): the selected objects, or every member of the multi-selection, are set down on the ground. A building is
/// tilted to the slope under its footprint; a tree, bush or rock (a copy, or one instance of the game's foliage) stays
/// upright with its foot on the ground, the way the game plants it. "The ground" is whatever is right under the object
/// (owner: "a roof, a floor, a road, a rock, not only the landscape"): the highest drawn surface below its bottom, or the
/// landscape.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>A floor or road this far above an object's bottom still holds it (it was sunk a little into it, cm); a ceiling higher up does not.</summary>
    private const float SupportReach = 30f;

    /// <summary>Size of the map cells the supports are sorted into for a fit, cm.</summary>
    private const float SupportCell = 2000f;

    private PreparedLevelScene? _supportScene;
    private BoundingBox[] _supportBoxes = [];
    private readonly Dictionary<string, MeshSurface?> _surfaces = new(StringComparer.OrdinalIgnoreCase);

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
        var supports = Supports(
            members.Where(m => m.Instance is null).Select(m => m.Item.SelectableId).ToHashSet(),
            members.Where(m => m.Instance?.Instance is not null).Select(m => InstanceKey.Of(m.Item.SelectableId, m.Instance!.Instance!.ComponentName, m.Instance.Instance.InstanceIndex)).ToHashSet());
        var ops = new List<EditOp>();
        var missed = 0;
        foreach (var (item, sel) in members)
        {
            if (sel is { Instance: { } instance })
            {
                // One tree, bush or rock of the game's foliage: its foot on the ground, upright as it is.
                var space = SpaceOf(sel);
                var world = CurrentInstanceTransform(sel).ToTransform() * space;
                if (Planted(world, MeshBounds(instance.StaticMeshPath), IsPlant(instance.StaticMeshPath), supports) is not { } placed)
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
                ? Planted(WorldOf(item, null), MeshBounds(item.Actor.StaticMeshPath), true, supports)
                : Grounded(item, supports);
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
    /// <paramref name="world"/> set down on what is under it, upright as it is: a plant with its pivot (where the game puts
    /// its foot) on it, anything else with the bottom of its box on it. Null with nothing under it.
    /// </summary>
    private FTransform? Planted(FTransform world, BoundingBox? bounds, bool plant, SupportGrid supports)
    {
        var foot = plant || bounds is not { IsEmpty: false }
            ? world.Translation
            : world.TransformPosition(new FVector(bounds.Value.Center.X, bounds.Value.Center.Y, bounds.Value.Min.Z));
        if (SupportAt(supports, foot) is not { } ground)
        {
            return null;
        }

        return world with { Translation = world.Translation with { Z = world.Translation.Z + (ground - foot.Z) } };
    }

    /// <summary>
    /// Where <paramref name="item"/>'s root goes standing on what is under it: along the slope (or roof) under the four
    /// corners of its footprint, the middle of its bottom on it where it is now. Null without a footprint or ground there.
    /// </summary>
    private FTransform? Grounded(ActorItemViewModel item, SupportGrid supports)
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
            if (SupportAt(supports, corners[i]) is not { } z)
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

    /// <summary>
    /// What holds up something whose bottom is at <paramref name="bottom"/>: the highest drawn surface under it no higher
    /// than <see cref="SupportReach"/> above it (a roof, a floor, a road, a rock), or the landscape when that is higher (as
    /// before: something sunk into the ground comes up onto it). Null with neither.
    /// </summary>
    private float? SupportAt(SupportGrid supports, FVector bottom)
    {
        var best = GroundAt(bottom.X, bottom.Y);
        var top = bottom.Z + SupportReach;
        if (!supports.TryGetValue(SupportCellOf(bottom.X, bottom.Y), out var here))
        {
            return best;
        }

        foreach (var s in here)
        {
            if (bottom.X < s.Box.Min.X || bottom.X > s.Box.Max.X || bottom.Y < s.Box.Min.Y || bottom.Y > s.Box.Max.Y || s.Box.Min.Z > top || best >= s.Box.Max.Z)
            {
                continue;
            }

            if (SurfaceOf(s.Mesh)?.HighestBelow(s.World, bottom.X, bottom.Y, top) is { } z && !(best >= z))
            {
                best = z;
            }
        }

        return best;
    }

    /// <summary>
    /// The drawn meshes something can stand on, where they are drawn now (moved, copied and deleted ones as the project
    /// has them), by map cell; the objects being set down (<paramref name="skipIds"/>, <paramref name="skipKeys"/>), spawn
    /// pins and stand-ins are left out. With <paramref name="cells"/> only those map cells are filled.
    /// </summary>
    private SupportGrid Supports(HashSet<uint> skipIds, HashSet<InstanceKey> skipKeys, (int X0, int Y0, int X1, int Y1)? cells = null)
    {
        var grid = new SupportGrid();
        if (PreparedScene is not { } scene)
        {
            return grid;
        }

        if (!ReferenceEquals(_supportScene, scene))
        {
            _supportScene = scene;
            _surfaces.Clear();
            _supportBoxes = scene.Placements.Select(p => Holds(p) && BoxOf(p.MeshPath, p.World) is { } b ? b : BoundingBox.Empty).ToArray();
        }

        // Only actors with a moved, deleted or set-down instance need their instances' keys (a forest is thousands).
        var owners = InstanceTransforms.Keys.Concat(HiddenInstanceKeys).Concat(skipKeys).Select(k => k.SelectableId).ToHashSet();
        var copied = Clones.Where(c => c.SourceId != 0).Select(c => c.SourceId).ToHashSet();
        var sources = new Dictionary<uint, List<ScenePlacement>>();
        for (var i = 0; i < scene.Placements.Count; i++)
        {
            var p = scene.Placements[i];
            if (copied.Contains(p.SelectableId))
            {
                (sources.TryGetValue(p.SelectableId, out var list) ? list : sources[p.SelectableId] = []).Add(p);
            }

            if (_supportBoxes[i].IsEmpty || HiddenActorIds.Contains(p.SelectableId) || skipIds.Contains(p.SelectableId))
            {
                continue;
            }

            if (owners.Contains(p.SelectableId) && p.InstanceKey is { } key)
            {
                if (HiddenInstanceKeys.Contains(key) || skipKeys.Contains(key))
                {
                    continue;
                }

                if (InstanceTransforms.TryGetValue(key, out var moved))
                {
                    Add(p.MeshPath, moved);
                    continue;
                }
            }

            if (ActorTransforms.TryGetValue(p.SelectableId, out var root))
            {
                Add(p.MeshPath, p.World.GetRelativeTransform(p.Actor.WorldTransform) * root);
            }
            else
            {
                Add(p.MeshPath, p.World, _supportBoxes[i]);
            }
        }

        foreach (var clone in Clones)
        {
            if (skipIds.Contains(clone.Id) || HiddenActorIds.Contains(clone.Id))
            {
                continue;
            }

            if (clone.SourceId == 0 && clone.Placements is null)
            {
                if (clone.MeshPath is { } mesh)
                {
                    Add(mesh, clone.RootWorld);
                }

                continue;
            }

            foreach (var p in clone.Placements ?? sources.GetValueOrDefault(clone.SourceId) ?? [])
            {
                if (Holds(p))
                {
                    Add(p.MeshPath, p.World.GetRelativeTransform(p.Actor.WorldTransform) * clone.RootWorld);
                }
            }
        }

        return grid;

        void Add(string mesh, FTransform world, BoundingBox? known = null)
        {
            if ((known ?? BoxOf(mesh, world)) is not { IsEmpty: false } box)
            {
                return;
            }

            var (x0, y0) = SupportCellOf(box.Min.X, box.Min.Y);
            var (x1, y1) = SupportCellOf(box.Max.X, box.Max.Y);
            if (cells is { } only)
            {
                (x0, y0, x1, y1) = (Math.Max(x0, only.X0), Math.Max(y0, only.Y0), Math.Min(x1, only.X1), Math.Min(y1, only.Y1));
            }

            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    (grid.TryGetValue((x, y), out var cell) ? cell : grid[(x, y)] = []).Add(new Support(box, world, mesh));
                }
            }
        }
    }

    /// <summary>False for what nothing stands on: spawn pins and the items drawn for spawners, volumes the game never draws.</summary>
    private bool Holds(ScenePlacement p) =>
        p.SpawnPoint is null && p.LootMarker is null && p.Spawner is null && !p.MeshPath.StartsWith(SpawnMarkers.Prefix, StringComparison.Ordinal)
        && MeshAsset(p.MeshPath) is { Shimmer: false, Billboard: false, IsEditorOnly: false };

    /// <summary>The world box of <paramref name="mesh"/> placed at <paramref name="world"/>; null when the mesh is not loaded or is scaled to nothing.</summary>
    private BoundingBox? BoxOf(string mesh, FTransform world)
    {
        if (MeshAsset(mesh)?.Mesh.Bounds is not { IsEmpty: false } b || world.Scale3D.X * world.Scale3D.Y * world.Scale3D.Z == 0f)
        {
            return null;
        }

        var box = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var p = world.TransformPosition(new FVector((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            box = box.Include(new Vector3(p.X, p.Y, p.Z));
        }

        return box;
    }

    /// <summary>A loaded mesh by its key in the prepared scene, or one loaded later for an added actor.</summary>
    private PreparedMeshAsset? MeshAsset(string mesh) =>
        PreparedScene?.Meshes.GetValueOrDefault(mesh) ?? ExtraMeshes.FirstOrDefault(m => string.Equals(m.Asset.MeshPath, mesh, StringComparison.OrdinalIgnoreCase))?.Asset;

    /// <summary>The cached surface of a mesh (built on first use).</summary>
    private MeshSurface? SurfaceOf(string mesh)
    {
        if (!_surfaces.TryGetValue(mesh, out var surface) && MeshAsset(mesh) is { } asset)
        {
            _surfaces[mesh] = surface = MeshSurface.Of(asset);
        }

        return surface;
    }

    private static (int X, int Y) SupportCellOf(float x, float y) => ((int)MathF.Floor(x / SupportCell), (int)MathF.Floor(y / SupportCell));

    /// <summary>A drawn mesh something can stand on: its world box, where it stands and its mesh key.</summary>
    private readonly record struct Support(BoundingBox Box, FTransform World, string Mesh);

    /// <summary>The supports of one fit by map cell (<see cref="SupportCellOf"/>).</summary>
    private sealed class SupportGrid : Dictionary<(int X, int Y), List<Support>>
    {
    }
}
