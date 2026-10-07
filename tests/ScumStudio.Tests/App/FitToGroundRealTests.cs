using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "houses must not bend like bridges; on a hillside I want the house to follow the ground, not half of it inside
/// the ground". A real house on its real landscape tile: it cannot bend, and Fit to ground lays its four bottom corners on
/// the terrain. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class FitToGroundRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    private readonly ITestOutputHelper _output;

    public FitToGroundRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AHouseDoesNotBendAndIsLaidOnTheSlopeUnderIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Slope");
        await map.LoadLevelsAsync([Farm]);
        var name = map.AllActors.Where(a => a.Actor.Kind == ActorKind.StaticMeshActor && a.Actor.StaticMeshPath?.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase) == true && map.PreparedScene!.Meshes.ContainsKey(a.Actor.StaticMeshPath))
            .MaxBy(a => map.PreparedScene!.Meshes[a.Actor.StaticMeshPath!].Mesh.Bounds.Size.X * map.PreparedScene.Meshes[a.Actor.StaticMeshPath!].Mesh.Bounds.Size.Y)!.Name; // the biggest house

        // With the landscape tile it stands on.
        var at = map.AllActors.First(a => a.Name == name).Actor.WorldTransform.Translation;
        var world = WorldIndex.FromCatalog(ctx.Services.Workspace.Catalog!).WithTileInfo(ctx.Services.Workspace.Catalog!, null);
        var tiles = MapPageViewModel.LevelsAround(world, at, 2000f).Where(p => p.Contains("/Landscape_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(tiles.Count > 0, $"{name} at {at}; " + string.Join("; ", world.Packages.Where(p => p.Kind == WorldPackageKind.Landscape).Take(3).Select(p => $"{p.PackagePath} {p.Tile?.BoundsValid} {p.Tile?.BoundsMin} {p.Tile?.BoundsMax} cell {p.Cell} map {p.IsMap}")));
        await map.LoadLevelsAsync([Farm, .. tiles]);
        var scene = map.PreparedScene!;
        Assert.NotNull(scene.HeightField);
        var house = map.AllActors.First(a => a.Name == name && !a.IsAdded);
        var bounds = scene.Meshes[house.Actor.StaticMeshPath!].Mesh.Bounds;

        map.SelectedActor = house;
        Assert.False(map.CanBend);
        Assert.Equal(ScumStudio.App.Localization.Loc.T("Map.Shape.NoBendBuilding"), map.BendNote);

        // Put crooked first: tilted 12° and 1.5 m up.
        var root = house.Actor.Root!.Relative;
        ctx.Services.Projects.Apply(ScumStudio.Level.Editing.EditOpFactory.SetTransform(house.Level, house.Actor,
            root with { Location = root.Location + new FVector(0f, 0f, 150f), Rotation = root.Rotation with { Pitch = root.Rotation.Pitch + 12f } }, ctx.Services.Projects.Current!.State));
        var before = Offsets(map.ActorTransforms[house.SelectableId]);
        map.FitToGroundCommand.Execute(null);
        var after = Offsets(map.ActorTransforms[house.SelectableId]);
        _output.WriteLine($"{house.Name} ({house.Actor.StaticMeshPath}) corners above the ground before: {string.Join(", ", before.Select(d => d.ToString("0")))} cm, after: {string.Join(", ", after.Select(d => d.ToString("0")))} cm");
        Assert.Equal(2, ctx.Services.Projects.History.Count);
        Assert.All(after, d => Assert.InRange(d, -15f, 15f));
        Assert.True(before.Max(MathF.Abs) > 100f);

        // Hektor: "fit a forest to the terrain". One tree of the game's foliage, lifted 3 m: its foot comes back onto the
        // ground, upright as it was (a plant stands on its pivot, not tilted to the slope).
        var tile = map.AllActors.First(a => a.Actor.InstanceTransforms.Any(i => i.StaticMeshPath?.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase) == true));
        var tree = tile.Actor.InstanceTransforms.First(i => i.StaticMeshPath?.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase) == true);
        var lifted = ScumStudio.Level.Model.TransformValue.FromTransform(tree.LocalTransform);
        lifted = lifted with { Location = lifted.Location + new FVector(0f, 0f, 300f) };
        ctx.Services.Projects.Apply(ScumStudio.Level.Editing.EditOpFactory.SetInstanceTransform(tile.Level, tile.Actor, tree.ComponentName, tree.InstanceIndex, lifted, ctx.Services.Projects.Current!.State));
        map.RefreshEdits();
        map.SelectedActorId = tile.SelectableId;
        map.SelectedInstanceKey = ScumStudio.Viewport.InstanceKey.Of(tile.SelectableId, tree.ComponentName, tree.InstanceIndex);
        map.FitToGroundCommand.Execute(null);
        var placed = ctx.Services.Projects.Current!.State.GetInstanceOverride(new ScumStudio.Level.Editing.InstanceRef(tile.Level.PackagePath, tile.Name, tree.ComponentName, tree.InstanceIndex))!.Value;
        var foot = placed.ToTransform() * tile.Actor.FindComponent(tree.ComponentName)!.WorldTransform;
        var groundZ = scene.HeightField!.SampleHeight(foot.Translation.X, foot.Translation.Y)!.Value;
        _output.WriteLine($"tree {tree.StaticMeshPath}: foot {foot.Translation.Z:0} cm, ground {groundZ:0} cm");
        Assert.InRange(foot.Translation.Z - groundZ, -5f, 5f);
        Assert.True(placed.IsNearlyEqual(lifted with { Location = placed.Location }, 0.01f), $"{placed} vs {lifted}"); // only the height moved

        List<float> Offsets(FTransform root)
        {
            var result = new List<float>();
            foreach (var (x, y) in new[] { (bounds.Min.X, bounds.Min.Y), (bounds.Max.X, bounds.Min.Y), (bounds.Max.X, bounds.Max.Y), (bounds.Min.X, bounds.Max.Y) })
            {
                var p = root.TransformPosition(new FVector(x, y, bounds.Min.Z));
                result.Add(p.Z - scene.HeightField!.SampleHeight(p.X, p.Y)!.Value);
            }

            return result;
        }
    }

    /// <summary>
    /// Owner: "Fit to ground must put the object on whatever is directly under it: the ground, or a roof, a floor, a road,
    /// a rock". A crate 3 m over a farmhouse roof lands on the roof (not on the terrain under the house), a barrel over a
    /// road piece on the road, a copy over open ground on the terrain, and a multi-selection member by member; a 20-object
    /// group fits well under a second.
    /// </summary>
    [Fact]
    public async Task ThingsLandOnTheRoofOrRoadUnderThem()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        const string Crate = "/Game/ConZ_Files/Models/Objects/Outdoor/Crate/SM_Crate_02_A.SM_Crate_02_A";
        const string Barrel = "/Game/ConZ_Files/Models/Objects/Outdoor/Barrel/SM_Barrel_01.SM_Barrel_01";
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Supports");
        await map.LoadLevelsAsync([Farm]);
        var houseName = map.AllActors.First(a => a.Actor.ClassName.StartsWith("BP_CB_02_A_01_EXT", StringComparison.Ordinal)).Name; // a farmhouse with a shingle roof
        var world = WorldIndex.FromCatalog(ctx.Services.Workspace.Catalog!).WithTileInfo(ctx.Services.Workspace.Catalog!, null);
        var tiles = MapPageViewModel.LevelsAround(world, map.AllActors.First(a => a.Name == houseName).Actor.WorldTransform.Translation, 2000f)
            .Where(p => p.Contains("/Landscape_", StringComparison.OrdinalIgnoreCase)).ToList();
        await map.LoadLevelsAsync([Farm, .. tiles]);
        var scene = map.PreparedScene!;
        var house = map.AllActors.First(a => a.Name == houseName);
        var roof = new Surface(scene, scene.Placements.Where(p => p.SelectableId == house.SelectableId));
        var crateBox = scene.Meshes[Crate].Mesh.Bounds;
        var barrelBox = scene.Meshes[Barrel].Mesh.Bounds;
        float Terrain(float x, float y) => scene.HeightField!.SampleHeight(x, y)!.Value;

        // A flat stretch of the roof (the crate's four corners on one plane), well above the terrain.
        var spot = Spots(roof.Box, 100f).First(s => roof.Flat(s.X, s.Y, 60f) is { } z && z > Terrain(s.X, s.Y) + 250f);
        var roofZ = roof.Top(spot.X, spot.Y)!.Value;
        var crate = Add(Crate, new FVector(spot.X, spot.Y, roofZ + 300f - crateBox.Min.Z));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        map.FitToGroundCommand.Execute(null);
        var first = watch.Elapsed.TotalMilliseconds;
        var bottom = Bottom(crate, crateBox);
        _output.WriteLine($"crate over {house.Name}: bottom {bottom.Z:0.0} cm, roof {roof.Top(bottom.X, bottom.Y):0.0} cm, terrain {Terrain(bottom.X, bottom.Y):0.0} cm (first fit {first:0} ms)");
        Assert.InRange(bottom.Z - roof.Top(bottom.X, bottom.Y)!.Value, -5f, 5f);
        Assert.True(bottom.Z > Terrain(bottom.X, bottom.Y) + 200f);

        // A road piece (a bent spline mesh of the landscape tile): where it lies highest over the terrain.
        var roads = scene.Placements.Where(p => p.Component?.SplineMesh is not null && p.MeshPath.Contains("/Road/", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Placement: p, Surface: new Surface(scene, [p]))).ToList();
        var road = roads.Where(r => !r.Placement.MeshPath.Contains("Border", StringComparison.OrdinalIgnoreCase)).Take(80)
            .SelectMany(r => Spots(r.Surface.Box, 50f).Select(s => (s.X, s.Y, Z: r.Surface.Flat(s.X, s.Y, 40f))))
            .Where(c => c.Z is not null)
            .MaxBy(c => c.Z!.Value - Terrain(c.X, c.Y));
        var under = new Surface(scene, roads.Where(r => r.Surface.Box.Contains(r.Surface.Box.Center with { X = road.X, Y = road.Y })).Select(r => r.Placement)); // every piece there (crossings)
        var barrel = Add(Barrel, new FVector(road.X, road.Y, road.Z!.Value + 300f - barrelBox.Min.Z));
        map.FitToGroundCommand.Execute(null);
        bottom = Bottom(barrel, barrelBox);
        _output.WriteLine($"barrel over the road: bottom {bottom.Z:0.0} cm, road {under.Top(bottom.X, bottom.Y):0.0} cm, terrain {Terrain(bottom.X, bottom.Y):0.0} cm");
        Assert.InRange(bottom.Z - under.Top(bottom.X, bottom.Y)!.Value, -3f, 3f);
        Assert.True(under.Top(bottom.X, bottom.Y)!.Value > Terrain(bottom.X, bottom.Y) + 1f, "the road must lie above the terrain there to tell them apart");

        // A copy of a farm prop, lifted 3 m over open ground (no plant there, nothing else within 2 m): it lands on the terrain.
        var boxes = scene.Placements.Where(p => !p.MeshPath.StartsWith('#') && scene.Meshes.ContainsKey(p.MeshPath)) // not the spawn pins and rings
            .Select(p => (Plant: p.MeshPath.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase), Box: ScumStudio.Viewport.PieceSnap.WorldBox(new ScumStudio.Viewport.SnapPiece(p.MeshPath, p.World, scene.Meshes[p.MeshPath].Mesh.Bounds)))).ToList();
        var prop = map.AllActors.First(a => !a.IsAdded && a.Actor.Kind == ActorKind.StaticMeshActor && a.Level.PackagePath == Farm
                                            && a.Actor.StaticMeshPath is { } m && scene.Meshes.TryGetValue(m, out var mesh) && mesh.Mesh.Bounds.Size.Z is > 30f and < 300f && !m.Contains("/Foliage/", StringComparison.Ordinal));
        map.SelectedActor = prop;
        map.DuplicateSelectedCommand.Execute(null);
        var copy = map.SelectedActor!;
        Assert.True(copy.IsAdded && copy.SourceId == prop.SelectableId);
        var propBox = scene.Meshes[prop.Actor.StaticMeshPath!].Mesh.Bounds;
        var open = Enumerable.Range(0, 300).Select(i => new FVector(spot.X + (MathF.Cos(i * 0.7f) * (2000f + (i * 20f))), spot.Y + (MathF.Sin(i * 0.7f) * (2000f + (i * 20f))), 0f))
            .First(p => scene.HeightField!.SampleHeight(p.X, p.Y) is not null && !boxes.Any(b => b.Plant ? Near(b.Box, p, 0f) : Near(b.Box, p, 200f)));
        Lift(copy, new FVector(open.X, open.Y, Terrain(open.X, open.Y) + 300f - propBox.Min.Z));
        map.FitToGroundCommand.Execute(null);
        var corners = Corners(Now(copy).Actor.WorldTransform, propBox).Select(c => c.Z - Terrain(c.X, c.Y)).ToList();
        _output.WriteLine($"copy of {prop.Name} over open ground: corners above the terrain {string.Join(", ", corners.Select(d => d.ToString("0.0")))} cm");
        Assert.All(corners, d => Assert.InRange(d, -15f, 15f));

        // Both lifted again and fitted as one multi-selection: the crate onto the roof, the copy onto the grass.
        Lift(crate, new FVector(spot.X, spot.Y, roofZ + 300f - crateBox.Min.Z));
        Lift(copy, new FVector(open.X, open.Y, Terrain(open.X, open.Y) + 300f - propBox.Min.Z));
        map.SelectedActor = Now(crate);
        map.ToggleGroup(Now(copy).SelectableId, null);
        Assert.True(map.HasGroup);
        map.FitToGroundCommand.Execute(null);
        bottom = Bottom(crate, crateBox);
        corners = Corners(Now(copy).Actor.WorldTransform, propBox).Select(c => c.Z - Terrain(c.X, c.Y)).ToList();
        _output.WriteLine($"group: crate bottom {bottom.Z - roof.Top(bottom.X, bottom.Y)!.Value:0.0} cm off the roof, copy corners {string.Join(", ", corners.Select(d => d.ToString("0.0")))} cm off the terrain");
        Assert.InRange(bottom.Z - roof.Top(bottom.X, bottom.Y)!.Value, -5f, 5f);
        Assert.All(corners, d => Assert.InRange(d, -15f, 15f));

        // Twenty of the farm's objects (houses, sheds, props) fitted as one group.
        var twenty = map.AllActors.Where(a => !a.IsAdded && a.Level.PackagePath == Farm && a.Actor.Kind is ActorKind.StaticMeshActor or ActorKind.Blueprint
                                              && scene.Placements.Any(p => p.SelectableId == a.SelectableId)).Take(20).ToList();
        map.SelectedActor = twenty[0];
        foreach (var member in twenty.Skip(1))
        {
            map.ToggleGroup(member.SelectableId, null);
        }

        watch.Restart();
        map.FitToGroundCommand.Execute(null);
        _output.WriteLine($"20-object group: {watch.Elapsed.TotalMilliseconds:0} ms over {scene.Placements.Count:N0} placements");
        Assert.True(watch.Elapsed.TotalMilliseconds < 4000, $"{watch.Elapsed.TotalMilliseconds:0} ms"); // 60-260 ms alone; a loaded machine (parallel test hosts) took 1.1 s once

        ActorItemViewModel Add(string mesh, FVector at)
        {
            map.AimPointProvider = () => at;
            Assert.True(map.AddMeshActor(mesh));
            return map.SelectedActor!;
        }

        void Lift(ActorItemViewModel item, FVector to)
        {
            var now = Now(item).Actor.WorldTransform;
            map.ApplyDraggedTransform(Now(item).SelectableId, now with { Translation = to, Rotation = FQuat.Identity });
        }

        FVector Bottom(ActorItemViewModel item, ScumStudio.Core.Geometry.BoundingBox box) =>
            Now(item).Actor.WorldTransform.TransformPosition(new FVector(box.Center.X, box.Center.Y, box.Min.Z));

        ActorItemViewModel Now(ActorItemViewModel item) => map.AllActors.First(a => a.IsAdded && a.Name == item.Name);
    }

    private static bool Near(ScumStudio.Core.Geometry.BoundingBox b, FVector p, float margin) =>
        p.X >= b.Min.X - margin && p.X <= b.Max.X + margin && p.Y >= b.Min.Y - margin && p.Y <= b.Max.Y + margin;

    private static IEnumerable<FVector> Corners(FTransform world, ScumStudio.Core.Geometry.BoundingBox box) =>
        new[] { (box.Min.X, box.Min.Y), (box.Max.X, box.Min.Y), (box.Max.X, box.Max.Y), (box.Min.X, box.Max.Y) }.Select(c => world.TransformPosition(new FVector(c.Item1, c.Item2, box.Min.Z)));

    /// <summary>Points on a grid of <paramref name="step"/> cm over the middle 80% of a box, from the middle out.</summary>
    private static IEnumerable<(float X, float Y)> Spots(ScumStudio.Core.Geometry.BoundingBox box, float step)
    {
        var c = box.Center;
        var (hx, hy) = (box.Size.X * 0.4f, box.Size.Y * 0.4f);
        var spots = new List<(float X, float Y)>();
        for (var x = -hx; x <= hx; x += step)
        {
            for (var y = -hy; y <= hy; y += step)
            {
                spots.Add((c.X + x, c.Y + y));
            }
        }

        return spots.OrderBy(s => MathF.Abs(s.X - c.X) + MathF.Abs(s.Y - c.Y));
    }

    /// <summary>
    /// Brute-force reference: every triangle of some placements in world space, and the highest one a vertical line meets
    /// (independent of the app's binned <c>MeshSurface</c>).
    /// </summary>
    private sealed class Surface
    {
        private readonly List<(float[] P, uint[] I)> _meshes = [];

        public Surface(ScumStudio.Viewport.PreparedLevelScene scene, IEnumerable<ScumStudio.Viewport.ScenePlacement> placements)
        {
            var box = ScumStudio.Core.Geometry.BoundingBox.Empty;
            foreach (var p in placements)
            {
                if (p.MeshPath.StartsWith('#') || !scene.Meshes.TryGetValue(p.MeshPath, out var asset))
                {
                    continue; // spawn pins
                }

                var src = asset.Mesh.Positions;
                var world = new float[src.Length];
                for (var i = 0; i < src.Length; i += 3)
                {
                    var w = p.World.TransformPosition(new FVector(src[i], src[i + 1], src[i + 2]));
                    (world[i], world[i + 1], world[i + 2]) = (w.X, w.Y, w.Z);
                    box = box.Include(new System.Numerics.Vector3(w.X, w.Y, w.Z));
                }

                _meshes.Add((world, asset.Mesh.Indices));
            }

            Box = box;
        }

        public ScumStudio.Core.Geometry.BoundingBox Box { get; }

        /// <summary>The highest surface at (x, y), or null.</summary>
        public float? Top(float x, float y)
        {
            float? best = null;
            foreach (var (p, idx) in _meshes)
            {
                for (var t = 0; t + 2 < idx.Length; t += 3)
                {
                    var (i0, i1, i2) = ((int)idx[t] * 3, (int)idx[t + 1] * 3, (int)idx[t + 2] * 3);
                    var d = ((p[i1 + 1] - p[i2 + 1]) * (p[i0] - p[i2])) + ((p[i2] - p[i1]) * (p[i0 + 1] - p[i2 + 1]));
                    if (MathF.Abs(d) < 1e-6f)
                    {
                        continue;
                    }

                    var w0 = (((p[i1 + 1] - p[i2 + 1]) * (x - p[i2])) + ((p[i2] - p[i1]) * (y - p[i2 + 1]))) / d;
                    var w1 = (((p[i2 + 1] - p[i0 + 1]) * (x - p[i2])) + ((p[i0] - p[i2]) * (y - p[i2 + 1]))) / d;
                    if (w0 < 0f || w1 < 0f || w0 + w1 > 1f)
                    {
                        continue;
                    }

                    var z = (w0 * p[i0 + 2]) + (w1 * p[i1 + 2]) + ((1f - w0 - w1) * p[i2 + 2]);
                    best = best is { } b ? MathF.Max(b, z) : z;
                }
            }

            return best;
        }

        /// <summary>The height at (x, y) when the surface is one plane within <paramref name="half"/> cm around it, else null.</summary>
        public float? Flat(float x, float y, float half)
        {
            var c = Top(x, y);
            var around = new[] { Top(x - half, y - half), Top(x + half, y - half), Top(x + half, y + half), Top(x - half, y + half) };
            return c is { } z && around.All(a => a is not null) && MathF.Abs(around.Average(a => a!.Value) - z) < 1f
                   && MathF.Abs(around[0]!.Value + around[2]!.Value - around[1]!.Value - around[3]!.Value) < 2f ? z : null;
        }
    }
}
