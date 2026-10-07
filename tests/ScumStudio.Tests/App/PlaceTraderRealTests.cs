using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "place a trader anywhere". An Armory placed on a farm is a copy of a stock armory post with a personality of its
/// own (its economy section), in a new outpost whose manager lists it; one in an outpost level joins that outpost's manager.
/// The export is read back from the staged files. Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class PlaceTraderRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";

    [Fact]
    public async Task AnArmoryOnAFarmGetsItsOwnTraderOutpostAndEconomySection()
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
        await map.LoadLevelsAsync([Farm]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(800f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // The Add object box's defaults come from the cell and type: A_3_Armory_1 (the farm's first) in a new outpost Outpost_A_3.
        map.PrepareTraders();
        map.SelectedTraderType = map.TraderTypes.First(t => t.Type == "Armorer");
        Assert.Equal("A_3_Armory_1", map.TraderName);
        Assert.Equal("Outpost_A_3", map.TraderOutpost);
        var before = project.Journal.Applied.Count;
        Assert.True(await map.AddTraderAsync("Armorer", map.TraderName, map.TraderOutpost));
        Assert.Equal(before + 1, project.Journal.Applied.Count); // one history row
        Assert.True(ctx.Services.Economy.Economy.HasSection("A_3_Armory_1")); // its economy section is there at once

        var op = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(Farm, op.Level);
        Assert.EndsWith("BP_Outpost_Armory_NPCInteractionBox_C", op.ClassPath, StringComparison.Ordinal);
        Assert.Equal(new TraderPost("A_3_Armory_1", "Armorer", "Outpost_A_3") { Personality = op.Trader!.Personality }, op.Trader);
        Assert.Contains("_Armory_Personality_01", op.Trader.Personality, StringComparison.Ordinal);
        Assert.Contains("Add trader A_3_Armory_1 (Armorer, Outpost_A_3)", op.Describe(), StringComparison.Ordinal);

        // Drawn where the camera aims with its trader (the NPC stand-in and its pin), selected, with "Edit stock".
        var clone = Assert.Single(map.Clones, c => c.Name == op.NewName);
        Assert.True((clone.RootWorld.Translation - aim).Size() < 1f);
        Assert.Contains(clone.Placements!, p => SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Trader);
        Assert.Equal(op.NewName, map.SelectedActor?.Name);
        Assert.Equal("A_3_Armory_1", map.SelectedTraderName);
        Assert.Contains(map.ActorProperties, r => r.Value.StartsWith("Trade post: A_3_Armory_1 (Armorer). NPC: BP_ArmsDealer_01.", StringComparison.Ordinal));
        string? opened = null;
        map.SetEconomyOpener(name => opened = name);
        map.EditStockCommand.Execute(null);
        Assert.Equal("A_3_Armory_1", opened);

        // The next armory of the cell is numbered on; the same name again is refused (it would share the economy section), a stock name too.
        map.SelectedTraderType = null;
        map.SelectedTraderType = map.TraderTypes.First(t => t.Type == "Armorer");
        Assert.Equal("A_3_Armory_2", map.TraderName);
        Assert.False(await map.AddTraderAsync("Armorer", "A_3_Armory_1", "Outpost_A_3"));
        Assert.False(await map.AddTraderAsync("Armorer", "B_4_Armory", "Outpost_A_3"));
        Assert.Equal(before + 1, project.Journal.Applied.Count);

        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });

        // The level: the post with its outpost and its own personality, and the new outpost's manager listing it.
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var level = ModdableAssets.ReadPackage(written, Farm);
        var actors = LevelPackageEditor.ReadActorList(level).Select(a => a.Name).ToList();
        Assert.Contains(op.NewName, actors);
        Assert.Contains("BP_TradeOutpostManager_Outpost_A_3", actors);
        var post = Export(level, op.NewName);
        var props = level.ReadProperties(post);
        Assert.Equal("Outpost_A_3", Assert.IsType<NameValue>(Assert.IsType<StructValue>(props.Find("_outpost")!.Value).Find("OutpostName")!.Value).Value);
        var marker = Assert.IsType<StructValue>(Assert.Single(Assert.IsType<ArrayValue>(props.Find("_traderMarkers")!.Value).Items));
        var personality = Assert.IsType<ObjectValue>(marker.Find("TraderPersonality")!.Value);
        Assert.Equal("/Game/ConZ_Files/Economy/TraderPersonalities/ScumStudio/A_3_Armory_1_Personality.A_3_Armory_1_Personality", ImportPath(level, personality.Index));
        Assert.Equal(0, (props.Find("_questBook")?.Value as ObjectValue)?.Index ?? 0); // the source outpost's quest book stays there
        var source = ModdableAssets.ReadPackage(ctx.Services.Workspace.Catalog!, op.Source.Level);
        Assert.NotEqual(QuestGiverId(source, Export(source, op.Source.Actor)), QuestGiverId(level, post)); // a quest giver id of its own
        var manager = level.ReadProperties(Export(level, "BP_TradeOutpostManager_Outpost_A_3"));
        Assert.Equal("Outpost_A_3", Assert.IsType<NameValue>(manager.Find("_outpostName")!.Value).Value);
        Assert.Equal([post + 1], Assert.IsType<ArrayValue>(manager.Find("_assignedTradePosts")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index));
        Assert.Equal("/Game/ConZ_Files/Economy/OutpostDescriptions/A_3_TradeOutpostDescription.A_3_TradeOutpostDescription",
            ImportPath(level, Assert.IsType<ObjectValue>(manager.Find("_outpostDescription")!.Value).Index));

        // The new data assets: the trader's name and type, and stable ids of their own; the personality is registered.
        var created = ModdableAssets.ReadPackage(written, "/Game/ConZ_Files/Economy/TraderPersonalities/ScumStudio/A_3_Armory_1_Personality").ReadProperties(0);
        Assert.Equal("A_3_Armory_1", Assert.IsType<StrValue>(created.Find("HumanReadableTraderName")!.Value).Value);
        Assert.Equal("ETraderType::Armorer", Assert.IsType<EnumValue>(created.Find("TraderType")!.Value).Value);
        Assert.Equal(FGuid.FromBytes(TraderPosts.PersistentId("Trader", "A_3_Armory_1")), Guid(created));
        var description = ModdableAssets.ReadPackage(written, "/Game/ConZ_Files/Economy/OutpostDescriptions/A_3_TradeOutpostDescription").ReadProperties(0);
        Assert.Equal(FGuid.FromBytes(TraderPosts.PersistentId("Outpost", "Outpost_A_3")), Guid(description));
        var registry = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
        var record = Assert.Single(registry.Assets, a => a.ObjectPath == "/Game/ConZ_Files/Economy/TraderPersonalities/ScumStudio/A_3_Armory_1_Personality.A_3_Armory_1_Personality");
        Assert.Contains(registry.GetTags(record), t => t.Key == "PrimaryAssetType" && t.Value.Text == "TraderPersonalityDataAsset");

        // CUE4Parse reads the trader through the new personality (the game's files with the export on top).
        using var game = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.Normalize(AesKeyText.FromEnvironmentOrStore()!), LooseOverlays = [result.StagingDirectory] });
        var document = LevelDocument.Load(new Cue4ParseLevelReader(game, new Cue4ParseLevelReaderOptions()), Farm, CancellationToken.None);
        var trader = Assert.Single(document.FindActor(op.NewName)!.TraderMarkers);
        Assert.Equal(("A_3_Armory_1", "Armorer"), (trader.Name, trader.Type));

        // The economy goes next to the pak with the trader's section; the report says where it goes and what the server needs.
        var economy = EconomyOverride.Parse(File.ReadAllText(result.EconomyPath!));
        Assert.True(economy.HasSection("A_3_Armory_1"));
        var report = File.ReadAllText(result.ReportPath!);
        Assert.Contains("## Traders", report, StringComparison.Ordinal);
        Assert.Contains("A_3_Armory_1 (Armorer)", report, StringComparison.Ordinal);
        Assert.Contains("new outpost manager BP_TradeOutpostManager_Outpost_A_3", report, StringComparison.Ordinal);
        Assert.Contains(@"Saved\Config\WindowsServer\EconomyOverride.json", report, StringComparison.Ordinal);

        // Deleting the trader is the plain delete: it is not created, nor its outpost's manager.
        map.SelectedActor = map.AllActors.Single(a => a.Name == op.NewName);
        map.DeleteSelectedCommand.Execute(null);
        Assert.Empty(TraderPosts.Placed(project.State));
        Assert.False(ctx.Services.Economy.Economy.HasSection("A_3_Armory_1")); // and so is its section
    }

    [Fact]
    public async Task ATraderInAnOutpostLevelJoinsThatOutpostsManager()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Outpost");
        await map.LoadLevelsAsync([Outpost]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(500f, 500f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // B_4's own outpost by default: new traders join it, the bank too (no personality, no economy section). The game's
        // B_4_Trader counts, so the default name is B_4_Trader_2; the game's own name is refused.
        Assert.False(await map.AddTraderAsync("GeneralGoods", "B_4_Trader"));
        Assert.True(await map.AddTraderAsync("GeneralGoods"));
        var trader = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(("B_4_Trader_2", "Outpost_B_4"), (trader.Trader!.Name, trader.Trader.Outpost));
        Assert.True(await map.AddTraderAsync("Armorer", "B_4_Armory_2"));
        var second = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.True(await map.AddTraderAsync(TraderPosts.BankType));
        var bank = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(string.Empty, bank.Trader!.Name);
        Assert.Null(bank.Trader.Personality);

        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var level = ModdableAssets.ReadPackage(written, Outpost);
        var manager = level.ReadProperties(Export(level, "BP_TradeOutpostManager_B_4"));
        var posts = Assert.IsType<ArrayValue>(manager.Find("_assignedTradePosts")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index).ToList();
        Assert.Equal(9, posts.Count); // the 7 stock posts and the 2 new traders (the bank is in no list, like the game's)
        Assert.Contains(Export(level, trader.NewName) + 1, posts);
        Assert.Contains(Export(level, second.NewName) + 1, posts);
        Assert.DoesNotContain(Export(level, bank.NewName) + 1, posts);
        Assert.Equal("Outpost_B_4", Assert.IsType<NameValue>(Assert.IsType<StructValue>(level.ReadProperties(Export(level, bank.NewName)).Find("_outpost")!.Value).Find("OutpostName")!.Value).Value);
        Assert.DoesNotContain(LevelPackageEditor.ReadActorList(level), a => a.Name?.StartsWith("BP_TradeOutpostManager_Outpost", StringComparison.Ordinal) == true);
        Assert.Contains("listed in _assignedTradePosts of the outpost's manager BP_TradeOutpostManager_B_4", File.ReadAllText(result.ReportPath!), StringComparison.Ordinal);
        var economy = EconomyOverride.Parse(File.ReadAllText(result.EconomyPath!));
        Assert.Equal(["B_4_Armory_2", "B_4_Trader_2"], economy.Traders.Order(StringComparer.Ordinal).ToList()); // the bank has no section
    }

    private static int Export(CookedPackage package, string name) =>
        Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == name);

    private static string ImportPath(CookedPackage package, int index)
    {
        var import = package.Imports[-index - 1];
        return package.ResolveName(package.Imports[-import.OuterIndex - 1].ObjectName) + "." + package.ResolveName(import.ObjectName);
    }

    private static ulong QuestGiverId(CookedPackage package, int actor) =>
        Enumerable.Range(0, package.Exports.Count)
            .Where(i => package.Exports[i].OuterIndex == actor + 1 && package.GetExportClassName(i) == "QuestGiverComponent")
            .Select(i => ((UInt64Value)package.ReadProperties(i).Find("_gameUniqueId")!.Value).Value)
            .Single();

    private static FGuid Guid(PropertyBlock block) => ((GuidValue)block.Properties.First(t => t.Value is GuidValue).Value).Value;
}
