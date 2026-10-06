using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Hektor (Discord): "if you delete a camp, fire animations remain behind, the tool doesn't show them; the traders of the
/// Wild Hunter packs stay put". Actors with nothing to draw get a pin that selects them, and the DLC plugins' sublevels
/// are in the world index, open with their cell and export to their own place in the pak. Real game files only.
/// </summary>
public sealed class PinOnlyActorsRealTests
{
    private const string Camp = "/Game/ConZ_Files/Maps/The_Island/C_3_Camp_01";
    private const string Grotto = "/SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/World/Maps/The_Island/C_2_Outpost_HuntersGrotto";

    private readonly ITestOutputHelper _output;

    public PinOnlyActorsRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ActorsWithoutAModelGetAPinThatSelectsThem()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Pins");
        await map.LoadLevelsAsync([Camp]);
        var scene = map.PreparedScene!;

        var pinOnly = map.AllActors.Where(a => SpawnMarkers.IsPinOnly(a.Actor, out _)).ToList();
        _output.WriteLine(string.Join(Environment.NewLine, pinOnly.Select(a => $"{a.Name} ({a.ClassName}): {string.Join(", ", a.Actor.Components.Select(c => c.ClassName).Distinct())}")));
        Assert.NotEmpty(pinOnly);
        foreach (var item in pinOnly)
        {
            var pin = Assert.Single(scene.Placements, p => p.SelectableId == item.SelectableId && SpawnMarkers.KindOfMesh(p.MeshPath) is SpawnKind.Effect or SpawnKind.Marker);
            Assert.Null(pin.InstanceKey); // the pin is the actor
            Assert.Equal(item.Actor.WorldTransform.Translation.X, pin.World.Translation.X, 0.01f);
        }

        // Nothing structural and nothing with a mesh gets one; a light or fire is an effect.
        Assert.DoesNotContain(map.AllActors, a => a.ClassName is "LevelBounds" or "ConZWorldSettings" && SpawnMarkers.IsPinOnly(a.Actor, out _));
        Assert.DoesNotContain(map.AllActors, a => a.Actor.Kind == ActorKind.StaticMeshActor && SpawnMarkers.IsPinOnly(a.Actor, out _));
        var effects = pinOnly.Where(a => SpawnMarkers.IsPinOnly(a.Actor, out var effect) && effect).ToList();
        _output.WriteLine($"{pinOnly.Count} pin-only actors, {effects.Count} effects");

        // The pin selects the actor; deleting it hides the pin and journals the actor.
        var first = pinOnly[0];
        map.SelectedActorId = first.SelectableId;
        Assert.Same(first, map.SelectedActor);
        Assert.True(map.DeleteSelectedCommand.CanExecute(null));
        map.DeleteSelectedCommand.Execute(null);
        Assert.Contains(first.SelectableId, map.HiddenActorIds);
        Assert.Contains(ctx.Services.Projects.Current!.State.DeletedActors, d => d.Actor == first.Name);
    }

    [Fact]
    public async Task ADlcPluginLevelIsIndexedOpenedAndExportedToItsOwnPlace()
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
        var grottos = world.Packages.Where(p => p.Name.EndsWith("_Outpost_HuntersGrotto", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, grottos.Count);
        var grotto = Assert.Single(grottos, p => p.PackagePath == Grotto);
        Assert.Equal(WorldPackageKind.Poi, grotto.Kind);
        Assert.Equal(new MapCell('C', 2), grotto.Cell);
        Assert.Contains(Grotto, MapPageViewModel.CellPackages(world, new MapCell('C', 2)));

        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Grotto");
        await map.LoadLevelsAsync([Grotto]);
        var npc = map.AllActors.Single(a => a.Name == "BP_Outpost_Hunter_01_C2_NPC");
        _output.WriteLine($"{npc.Name} ({npc.ClassName}): traders {npc.Actor.TraderMarkers.Count}, kind {SpawnMarkers.KindOf(npc.Actor)}, pin-only {SpawnMarkers.IsPinOnly(npc.Actor, out _)}, components {string.Join(", ", npc.Actor.Components.Select(c => c.ClassName).Distinct())}");
        Assert.Contains(map.PreparedScene!.Placements, p => p.SelectableId == npc.SelectableId && SpawnMarkers.IsMarker(p.MeshPath)); // the trader NPC has no mesh: a pin stands for it
        map.SelectedActorId = npc.SelectableId;
        map.DeleteSelectedCommand.Execute(null);
        Assert.Contains(npc.SelectableId, map.HiddenActorIds);

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        _output.WriteLine(string.Join(Environment.NewLine, result.Warnings));
        var written = Path.Combine(result.StagingDirectory, "SCUM", "Plugins", "GameFeatures", "WoodlandHunterPack", "Content", "World", "Maps", "The_Island", "C_2_Outpost_HuntersGrotto.umap");
        Assert.True(File.Exists(written), written);
        Assert.True(ScumStudio.Pak.PakPaths.IsModPakEntry(Path.GetRelativePath(result.StagingDirectory, written).Replace('\\', '/')));
        var package = CookedPackage.Parse(File.ReadAllBytes(written), File.ReadAllBytes(Path.ChangeExtension(written, ".uexp")), null, Grotto);
        Assert.DoesNotContain(LevelPackageEditor.ReadActorList(package), a => a.Name == "BP_Outpost_Hunter_01_C2_NPC");
        Assert.Contains(LevelPackageEditor.ReadActorList(package), a => a.Name == "BP_HuntersGrotto_INT_2");
    }
}
