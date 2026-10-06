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
}
