using System.Diagnostics;
using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: the brush took nothing while the cursor was over empty ground. On the real farm A_3_Farm_01, a 15 m brush on open
/// ground next to the greenhouse takes the greenhouse (its pivot is outside the circle, its wall inside) and every prop
/// drawn in the circle, and nothing farther. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class BrushRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const float Radius = 1500f;

    private readonly ITestOutputHelper _output;

    public BrushRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AFifteenMetreBrushOnOpenGroundTakesTheGreenhouseAndThePropsInTheCircleOnly()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Brush");
        await map.LoadLevelsAsync([Farm]);
        var scene = map.PreparedScene!;
        var greenhouse = map.AllActors.Single(a => a.Name == "BP_Greenhouse_A_EXT_Ruined_01_2");
        var pivot = greenhouse.Actor.WorldTransform.Translation;

        // Open ground 18.5 m north of the greenhouse's pivot (under the power line): the pivot is outside the circle, the
        // greenhouse's glass wall inside it.
        var centre = new FVector(pivot.X, pivot.Y + 1850f, pivot.Z);
        var shell = scene.Placements.Where(p => p.SelectableId == greenhouse.SelectableId).MinBy(p => Distance(centre, Box(scene, p)))!;
        Assert.InRange(Distance(centre, Box(scene, shell)), 1f, Radius - 50f);
        map.BrushSelect = true;
        map.PickParts = false; // whole objects first
        map.BrushRadius = Radius / 100f;
        var watch = Stopwatch.StartNew();
        map.BrushAt(centre);
        var first = watch.Elapsed.TotalMilliseconds;
        var ids = map.KindSelectionIds.ToHashSet();
        var keys = map.KindSelectionInstances.ToHashSet();
        _output.WriteLine($"{ids.Count} actors and {keys.Count} pieces in the circle; first dab (with the index) {first:0.0} ms");
        Assert.Contains(greenhouse.SelectableId, ids);

        // Every prop whose middle is drawn in the circle is taken (an instance or road piece on its own, anything else whole).
        foreach (var p in scene.Placements)
        {
            var box = Box(scene, p);
            var middle = Middle(scene, p);
            if (p.SelectableId == 0 || p.SpawnPoint is not null || p.LootMarker is not null || p.Spawner is not null
                || (p.MeshPath.StartsWith(SpawnMarkers.Prefix, StringComparison.Ordinal) && !SpawnMarkers.IsPinOnly(p.Actor, out _))
                || p.Actor.ClassName is "InstancedFoliageActor" || p.Actor.ClassName.StartsWith("Landscape", StringComparison.Ordinal)
                || box.Max.Z < centre.Z - Radius || box.Min.Z > centre.Z + Radius
                || Vector2.Distance(new Vector2(centre.X, centre.Y), new Vector2(middle.X, middle.Y)) > Radius - 50f)
            {
                continue;
            }

            var taken = p.InstanceKey is { InstanceIndex: >= 0 or InstanceKey.Segment } key ? keys.Contains(key) : ids.Contains(p.SelectableId);
            Assert.True(taken, $"{p.Name} ({p.MeshPath}) with its middle {Vector2.Distance(new Vector2(centre.X, centre.Y), new Vector2(middle.X, middle.Y)):0} cm from the centre is not taken");
        }

        // Nothing beyond: each one taken draws something within the circle.
        foreach (var id in ids)
        {
            var nearest = scene.Placements.Where(p => p.SelectableId == id).Min(p => Distance(centre, Box(scene, p)));
            Assert.True(nearest <= Radius + 1f, $"{map.AllActors.First(a => a.SelectableId == id).Name} is {nearest:0} cm away");
        }

        foreach (var key in keys)
        {
            var nearest = scene.Placements.Where(p => p.InstanceKey == key).Min(p => Distance(centre, Box(scene, p)));
            Assert.True(nearest <= Radius + 1f, $"{key} is {nearest:0} cm away");
        }

        // Part mode (the default): the greenhouse's own glass shell is a part, taken by what it draws, not by its pivot.
        map.ClearKindSelection();
        map.PickParts = true;
        map.BrushAt(centre);
        Assert.Contains(shell.InstanceKey!.Value, map.KindSelectionInstances);
        Assert.DoesNotContain(greenhouse.SelectableId, map.KindSelectionIds);
        Assert.True(Vector2.Distance(new Vector2(centre.X, centre.Y), new Vector2(shell.World.Translation.X, shell.World.Translation.Y)) > Radius);

        // Sweeping: a lap round the farm in 1 m steps (each dab a capsule from the last) stays well under a frame.
        var times = new List<double>();
        var previous = centre;
        for (var step = 1; step <= 400; step++)
        {
            var angle = step * MathF.Tau / 400f;
            var next = new FVector(pivot.X + (MathF.Cos(angle) * 6000f), pivot.Y + (MathF.Sin(angle) * 6000f), pivot.Z);
            watch.Restart();
            map.BrushAt(next, previous);
            times.Add(watch.Elapsed.TotalMilliseconds);
            previous = next;
        }

        times.Sort();
        _output.WriteLine($"sweep: {map.KindSelectionIds.Count} actors, {map.KindSelectionInstances.Count} pieces; per move median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, max {times[^1]:0.00} ms");
        Assert.True(times[times.Count / 2] < 5.0, $"median {times[times.Count / 2]:0.00} ms");
    }

    /// <summary>The world box of what a placement draws (its place without the mesh).</summary>
    private static BoundingBox Box(PreparedLevelScene scene, ScenePlacement p)
    {
        if (!scene.Meshes.TryGetValue(p.MeshPath, out var mesh) || mesh.Mesh.Bounds.IsEmpty)
        {
            var at = new Vector3(p.World.Translation.X, p.World.Translation.Y, p.World.Translation.Z);
            return new BoundingBox(at, at);
        }

        var b = mesh.Mesh.Bounds;
        var box = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var c = p.World.TransformPosition(new FVector((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            box = box.Include(new Vector3(c.X, c.Y, c.Z));
        }

        return box;
    }

    /// <summary>The middle of what a placement draws (inside its mesh's own box, wherever that is turned).</summary>
    private static FVector Middle(PreparedLevelScene scene, ScenePlacement p) =>
        scene.Meshes.TryGetValue(p.MeshPath, out var mesh) && !mesh.Mesh.Bounds.IsEmpty
            ? p.World.TransformPosition(new FVector(mesh.Mesh.Bounds.Center.X, mesh.Mesh.Bounds.Center.Y, mesh.Mesh.Bounds.Center.Z))
            : p.World.Translation;

    /// <summary>Distance on the ground from <paramref name="at"/> to the box (0 inside it).</summary>
    private static float Distance(FVector at, BoundingBox box)
    {
        var p = new Vector2(at.X, at.Y);
        return Vector2.Distance(p, Vector2.Clamp(p, new Vector2(box.Min.X, box.Min.Y), new Vector2(box.Max.X, box.Max.Y)));
    }
}
