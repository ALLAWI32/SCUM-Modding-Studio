using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The owner deleted the B_4 outpost and the game kept the Wild Hunter stall (its class lives at the plugin's mount
/// point and had no meshes), the mechanic's lifts (skeletal meshes: nothing to pick), fires burning in the air (parts
/// scaled to nothing, the Blueprint's particle and light left) and invisible walls (zero-scaled parts keep a body). Brushing
/// the outpost away must journal every one of them as a whole actor and both paks must lose them. Real game files only.
/// </summary>
public sealed class OutpostLeftoversRealTests
{
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";
    private const string Exterior02 = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost_Exterior_02";
    private const string BossHouse = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost_Ext_BossHouse";
    private const string Grotto = "/SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/World/Maps/The_Island/B_4_Outpost_HuntersGrotto";

    private static readonly string[] Gone =
    [
        "BP_HuntersGrotto_INT3_2", "BP_HuntersGrotto_EXT_B_4", "BP_HuntersGrotto_LIGHT2_2", "BP_CarLift2_2", "BP_BikeLift_4",
        "BP_Barrel_FirePit24", "BP_Barrel_FirePit45", "BP_Debris_Tire_Stack_10", "BP_Debris_Tire_Stack_11",
    ];

    private readonly ITestOutputHelper _output;

    public OutpostLeftoversRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task DeletingTheOutpostTakesTheStallTheLiftsTheFiresAndTheCollisionBoxesOutOfBothPaks()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var catalog = ctx.Services.Workspace.Catalog!;
        var world = WorldIndex.FromCatalog(catalog);
        var levels = MapPageViewModel.CellPackages(world, new MapCell('B', 4)).Where(p => p.Contains("B_4_Outpost", StringComparison.Ordinal)).ToList();
        Assert.Contains(Grotto, levels);
        Assert.Contains(Outpost, levels);

        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Leftovers");
        await map.LoadLevelsAsync(levels);
        _output.WriteLine($"{levels.Count} levels, {map.AllActors.Count} actors");

        // The stall's class is at the plugin's mount point (/WoodlandHunterPack/...): its parts have their meshes now.
        var stall = map.AllActors.Single(a => a.Name == "BP_HuntersGrotto_INT3_2");
        var stallMeshes = stall.Actor.Components.Count(c => c.StaticMeshPath is not null);
        _output.WriteLine($"stall: {stallMeshes} mesh parts of {stall.Actor.Components.Count}, first {stall.Actor.Components.FirstOrDefault(c => c.StaticMeshPath is not null)?.StaticMeshPath}");
        Assert.True(stallMeshes > 400, stallMeshes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var lift = map.AllActors.Single(a => a.Name == "BP_CarLift2_2");
        Assert.Contains(lift.Actor.Components, c => c.StaticMeshPath?.Contains("/Models/", StringComparison.Ordinal) == true); // its skeletal mesh is drawn like any model
        Assert.False(SpawnMarkers.IsPinOnly(lift.Actor, out _));

        // The brush over the outpost, then Delete: one bulk entry that applies whole.
        map.BrushRadius = 150;
        map.BrushAt(new FVector(572000f, -221000f, 270f));
        Assert.True(map.HasGroup);
        await map.DeleteKindSelectionAsync();
        var state = project.State;
        _output.WriteLine($"{state.DeletedActors.Count} actors, {state.DeletedInstances.Count} instances deleted, {state.TransformOverrides.Count()} parts scaled");
        foreach (var name in Gone)
        {
            Assert.Contains(state.DeletedActors, d => d.Actor == name);
        }

        Assert.DoesNotContain(state.TransformOverrides, t => state.IsDeleted(t.Actor)); // nothing of a deleted actor is scaled instead
        Assert.DoesNotContain(state.DeletedInstances, i => state.IsDeleted(i.ActorRef));

        foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
        {
            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false }, role);
            _output.WriteLine($"{role}: {result.Levels.Count} levels; {string.Join(" | ", result.Warnings.Take(5))}");
            var grotto = Written(result, "SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/World/Maps/The_Island/B_4_Outpost_HuntersGrotto.umap", Grotto);
            Assert.DoesNotContain(grotto, a => a.Name == "BP_HuntersGrotto_INT3_2");
            var outpost = Written(result, "SCUM/Content/ConZ_Files/Maps/The_Island/B_4_Outpost.umap", Outpost);
            Assert.DoesNotContain(outpost, a => a.Name is "BP_CarLift2_2" or "BP_BikeLift_4");
            var exterior = Written(result, "SCUM/Content/ConZ_Files/Maps/The_Island/B_4_Outpost_Exterior_02.umap", Exterior02);
            Assert.DoesNotContain(exterior, a => a.Name is "BP_Barrel_FirePit24" or "BP_Debris_Tire_Stack_11" or "BP_HuntersGrotto_LIGHT2_2");
            var bossHouse = Written(result, "SCUM/Content/ConZ_Files/Maps/The_Island/B_4_Outpost_Ext_BossHouse.umap", BossHouse);
            Assert.DoesNotContain(bossHouse, a => a.Name == "BP_HuntersGrotto_EXT_B_4");
        }
    }

    private static IReadOnlyList<(int PackageIndex, string? Name)> Written(ExportResult result, string virtualPath, string packagePath)
    {
        var file = Path.Combine([result.StagingDirectory, .. virtualPath.Split('/')]);
        Assert.True(File.Exists(file), file);
        Assert.True(PakPaths.IsModPakEntry(virtualPath));
        var package = CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, packagePath);
        return LevelPackageEditor.ReadActorList(package);
    }
}
