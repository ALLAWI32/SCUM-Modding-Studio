using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner and Hektor (Discord): "the traders stay when I delete everything in an outpost; I want to see them, move, copy
/// and delete them". A trade post (the armory's counter) places its trader where the class's marker says; the map pins the
/// trader there by name, the pin picks the trade post, and deleting it takes it off the outpost manager's list too.
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class TradersRealTests
{
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost";

    [Fact]
    public async Task TheOutpostTradersArePinnedByNameAndADeletedOneLeavesItsOutpost()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Traders");
        await map.LoadLevelsAsync([Outpost]);
        map.ShowSpawns = true;

        var traders = map.AllActors.SelectMany(a => a.Actor.TraderMarkers).Select(t => t.Name).ToList();
        Assert.Contains("A_0_Armory", traders);
        Assert.Contains("A_0_Mechanic", traders);
        var armory = map.AllActors.Single(a => a.Actor.TraderMarkers.Any(t => t.Name == "A_0_Armory"));
        var marker = armory.Actor.TraderMarkers.Single(t => t.Name == "A_0_Armory");
        Assert.Equal("Armorer", marker.Type);
        Assert.EndsWith("BP_ArmsDealer_01_C", marker.NpcClass, StringComparison.Ordinal);

        // The pin stands where the NPC does and picks the trade post.
        var pin = map.PreparedScene!.Placements.Single(p => p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Trader) && ReferenceEquals(p.Actor, armory.Actor));
        Assert.Equal(armory.SelectableId, pin.SelectableId);
        var where = (marker.Local * armory.Actor.WorldTransform).Translation;
        Assert.Equal(where.X, pin.World.Translation.X, 1f);
        Assert.Equal(where.Y, pin.World.Translation.Y, 1f);
        Assert.Contains(map.SpawnLayers, l => l.Key == "Traders" && l.IsVisible);

        map.SelectedActorId = armory.SelectableId;
        Assert.Contains(map.ActorProperties, r => r.Value.Contains("A_0_Armory", StringComparison.Ordinal));
        Assert.True(map.DeleteSelectedCommand.CanExecute(null));
        map.DeleteSelectedCommand.Execute(null);

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var package = ModdableAssets.ReadPackage(written, Outpost);
        var manager = Enumerable.Range(0, package.Exports.Count).Single(i => package.GetExportClassName(i).Contains("TradeOutpostManager", StringComparison.Ordinal));
        var posts = Assert.IsType<ArrayValue>(package.ReadProperties(manager).Find("_assignedTradePosts")!.Value).Items.OfType<ObjectValue>().ToList();
        Assert.Contains(posts, p => p.Index == 0); // the armory's entry is empty
        Assert.DoesNotContain(posts, p => p.Index > 0 && package.ResolveName(package.Exports[p.Index - 1].ObjectName) == armory.Name);
        Assert.True(posts.Count(p => p.Index > 0) >= 5); // the other traders stay
    }
}
