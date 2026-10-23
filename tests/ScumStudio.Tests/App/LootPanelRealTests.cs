using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The loot panel shows the loot a crate gives in game when the crate's only loot is its model's (Discord, redriver6155,
/// 3.5: removed loot still spawned), and taking it out there is what the exported crate gives. Real game files only.
/// </summary>
public sealed class LootPanelRealTests(ITestOutputHelper output)
{
    private const string MilitaryBase = "/Game/ConZ_Files/Maps/The_Island/A_4_Military_Base";
    private const string Tarp = "SM_Crate_Tarp_88";

    [PaksFact]
    public async Task ACrateWhoseLootIsItsModelsShowsItAndLosesItWhenTakenOut()
    {
        var paks = RealPaks.Folder!;
        using var ctx = AppTestContext.Create();
        var key = AesKeyText.FromEnvironmentOrStore()!;
        ctx.Services.Keys.Set(key);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Loot");
        await map.LoadLevelsAsync([MilitaryBase]);

        var tarp = map.AllActors.Single(a => a.Level.PackagePath == MilitaryBase && a.Name == Tarp);
        Assert.Empty(tarp.Actor.Root!.LootPresets); // nothing stored on the crate itself
        map.SelectedInstanceKey = null;
        map.SelectedActorId = tarp.SelectableId;
        Assert.True(map.HasLootEditor);
        Assert.True(map.IsLootable);
        var shown = map.LootPresets.Select(p => p.Name).ToList();
        output.WriteLine("shown: " + string.Join(", ", shown));
        Assert.Equal(new[] { "Examine_Crate_Tarp_Camo" }, shown);

        map.IsLootable = false;
        var op = Assert.IsType<SetLootOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.False(op.New.Lootable); // the presets stay in the setting, for ticking it again
        Assert.False(map.IsLootable);

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key, LooseOverlays = [result.StagingDirectory] });
        var root = LevelDocument.Load(new Cue4ParseLevelReader(written, new Cue4ParseLevelReaderOptions { ReadInstances = false }), MilitaryBase).FindActor(Tarp)!.Root!;
        output.WriteLine($"exported: {root.StaticMeshPath} [{string.Join(", ", root.LootPresets)}]");
        Assert.Empty(root.LootPresets);
        Assert.True(MeshLoot.IsCopy(root.StaticMeshPath!));
        Assert.True(MeshLoot.FromCatalog(written).Of(MeshLoot.StockPath(root.StaticMeshPath!)).HasExamineData); // the stock model keeps it
        Assert.False(MeshLoot.Read(ScumStudio.Modding.Catalog.ModdableAssets.ReadPackage(written, root.StaticMeshPath![..root.StaticMeshPath!.IndexOf('.')])).HasExamineData);
    }
}
