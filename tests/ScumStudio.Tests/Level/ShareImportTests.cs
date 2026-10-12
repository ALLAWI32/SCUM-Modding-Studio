using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using static ScumStudio.Tests.Level.EditOpTests;
using static ScumStudio.Tests.Level.ShareFileTests;

namespace ScumStudio.Tests.Level;

public sealed class ShareImportTests
{
    private const string TradePost = "/Game/ConZ_Files/Economy/BP_TradePost.BP_TradePost_C";

    [Fact]
    public void AnAddedActorWhoseNameIsTakenIsRenamedEverywhere()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        var mine = new ActorRef(Outpost, "SM_Rock_Added");
        project.Apply(new AddStaticMeshActorOp(Outpost, "SM_Rock_Added", Rock, Elsewhere));
        var content = Shared(
            new AddStaticMeshActorOp(Outpost, "SM_Rock_Added", Rock, Somewhere),
            new SetTransformOp(mine, Somewhere, JournalSliceTests.Moved),
            new SetClearGrassOp(mine, null, false));

        // Before: the mod's edits as they are clash with the rock the project added.
        var clash = Assert.Throws<InvalidOperationException>(() => project.Apply(content.Edits));
        Assert.Contains("already exists", clash.Message);

        var plan = ShareImport.Plan(project, content);
        ShareImport.Apply(project, plan, "mod1", content, EconomyOverride.CreateDefault());

        var theirs = new ActorRef(Outpost, "SM_Rock_Added2");
        Assert.Null(plan.ConflictDetail);
        Assert.Equal([("A_0_Outpost/SM_Rock_Added", "A_0_Outpost/SM_Rock_Added2")], plan.Renamed);
        Assert.True(project.State.IsAdded(mine));
        Assert.True(project.State.IsAdded(theirs));
        Assert.Null(project.State.GetTransformOverride(mine));
        Assert.Equal(JournalSliceTests.Moved, project.State.GetTransformOverride(theirs));
        Assert.Null(project.State.GetClearGrass(mine));
        Assert.False(project.State.GetClearGrass(theirs));
    }

    [Fact]
    public void AddedInstancesGetFreeIndices()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        var tree = new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 100);
        project.Apply(new AddInstanceOp(tree, Elsewhere));
        var content = Shared(
            new AddInstanceOp(tree, Somewhere),
            new AddInstanceOp(tree with { Index = 101 }, Somewhere),
            new SetInstanceTransformOp(tree, Somewhere, JournalSliceTests.Moved));

        var plan = ShareImport.Plan(project, content);
        ShareImport.Apply(project, plan, "trees", content, EconomyOverride.CreateDefault());

        Assert.Equal([(tree.ToString(), (tree with { Index = 102 }).ToString())], plan.Renamed);
        Assert.Equal(Elsewhere, project.State.GetAddedInstanceTransform(tree));
        Assert.Equal(Somewhere, project.State.GetAddedInstanceTransform(tree with { Index = 101 }));
        Assert.Equal(JournalSliceTests.Moved, project.State.GetAddedInstanceTransform(tree with { Index = 102 }));
    }

    [Fact]
    public void ApplyIsOneUndoableStep()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        project.Apply(new DeleteActorOp(new ActorRef(Outpost, "House_1")));
        var before = project.State.Describe();
        var history = project.History.Count;
        var content = ShareFile.Read(ShareFile.Write(Sample()));

        var install = ShareImport.Apply(project, ShareImport.Plan(project, content), "camp", content, EconomyOverride.CreateDefault());

        Assert.Equal(history + 1, project.History.Count);
        Assert.Equal("Crate camp (3 edit(s))", project.History[^1].Summary);
        Assert.Equal(project.Journal.Applied[^1].Seq, install.BatchSeq);
        Assert.NotEqual(before, project.State.Describe());
        project.Undo();
        Assert.Equal(before, project.State.Describe());
    }

    [Fact]
    public void RemoveTakesBackTheEditsCraftablesAndEconomy()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        new CraftablesFile { Items = [Table() with { Name = "Chair", Token = "SS_Chair" }] }.Save(project.DirectoryPath); // a stored token, as every load writes it
        var economy = EconomyOverride.CreateDefault();
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_M9") { PurchasePrice = 10 });
        var before = (State: project.State.Describe(), Craftables: File.ReadAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName)), Economy: economy.ToJson());
        var content = SharedWithFiles();

        var plan = ShareImport.Plan(project, content);
        var install = ShareImport.Apply(project, plan, "camp", content, economy);

        Assert.Equal(["Table"], plan.CraftablesAdded);
        Assert.Equal(["table", "Chair"], plan.CraftablesSkipped);
        Assert.Equal(["A_0_Armory", "A_0_Trader"], plan.EconomySections);
        Assert.Equal(["Chair", "Table"], CraftablesFile.Load(project.DirectoryPath).Items.Select(c => c.Name));
        Assert.Equal(10, economy.Find("A_0_Armory", "Weapon_M9")!.PurchasePrice);
        Assert.Equal(500, economy.Find("A_0_Armory", "Weapon_AK47")!.PurchasePrice);
        Assert.True(economy.HasSection("A_0_Trader"));
        var listed = Assert.Single(ShareImport.Installed(project));
        Assert.Equal((install.Id, install.BatchSeq, install.Name, install.Author), (listed.Id, listed.BatchSeq, listed.Name, listed.Author));
        Assert.Equal(["Table"], listed.CraftablesAdded);
        Assert.Equal(["A_0_Armory", "A_0_Trader"], listed.EconomyBefore.Select(b => b.Trader));
        Assert.Null(listed.EconomyBefore[1].Entries);

        ShareImport.Remove(project, install, economy, "Remove Crate camp");

        Assert.Equal(before.State, project.State.Describe());
        Assert.Equal("Remove Crate camp (3 edit(s))", project.History[^1].Summary);
        Assert.Equal(before.Craftables, File.ReadAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName)));
        Assert.Equal(before.Economy, economy.ToJson());
        Assert.Empty(ShareImport.Installed(project));
        Assert.Empty(Directory.GetFiles(Path.Combine(project.DirectoryPath, ShareImport.FolderName)));
    }

    [Fact]
    public void RemoveAfterCtrlZOnlyRestoresTheFiles()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        var economy = EconomyOverride.CreateDefault();
        var content = SharedWithFiles();
        var install = ShareImport.Apply(project, ShareImport.Plan(project, content), "camp", content, economy);

        project.Undo();
        var history = project.History.Count;
        ShareImport.Remove(project, install, economy);

        Assert.Equal(history, project.History.Count);
        Assert.Empty(project.Journal.Applied);
        Assert.True(project.State.IsEmpty);
        Assert.Empty(CraftablesFile.Load(project.DirectoryPath).Items);
        Assert.Empty(economy.Traders);
        Assert.Empty(ShareImport.Installed(project));
    }

    [Fact]
    public void AConflictIsReportedAndNothingIsWritten()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        project.Apply(new SetTransformOp(new ActorRef(Outpost, "StaticMeshActor_7"), TransformValue.Identity, JournalSliceTests.Moved));
        var economy = EconomyOverride.CreateDefault();
        var economyBefore = economy.ToJson();
        var journal = Path.Combine(project.DirectoryPath, Project.JournalFileName);
        var lines = Lines(journal);
        var content = SharedWithFiles();

        var plan = ShareImport.Plan(project, content);
        var refused = Assert.Throws<ShareException>(() => ShareImport.Apply(project, plan, "camp", content, economy));

        Assert.Contains("StaticMeshActor_7 is not at the expected transform", plan.ConflictDetail);
        Assert.Equal(ShareProblem.Conflicts, refused.Problem);
        Assert.Equal(plan.ConflictDetail, refused.Message);
        Assert.Equal(lines, Lines(journal));
        Assert.False(File.Exists(Path.Combine(project.DirectoryPath, CraftablesFile.FileName)));
        Assert.False(Directory.Exists(Path.Combine(project.DirectoryPath, ShareImport.FolderName)));
        Assert.Equal(economyBefore, economy.ToJson());
    }

    [Theory]
    [InlineData("1.3.3.4.149664", "1.3.4.0.150112", true)]
    [InlineData("1.3.3.4.149664", "1.3.3.4.149664", false)]
    [InlineData("1.3.3.4.149664", "unknown", false)]
    [InlineData("unknown", "1.3.3.4.149664", false)]
    public void ADifferentGameBuildIsFlagged(string author, string yours, bool differs)
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp, yours);
        var content = Sample() with { Manifest = SampleManifest() with { GameBuild = author } };

        var plan = ShareImport.Plan(project, content);

        Assert.Equal(differs, plan.GameBuildDiffers);
        Assert.Equal(author, plan.AuthorBuild);
        Assert.Equal(yours, plan.YourBuild);
        Assert.Null(plan.ConflictDetail);
    }

    [Fact]
    public void TraderNamesThatClashAreRenamedWithTheirEconomySection()
    {
        using var temp = new LevelTempDirectory();
        using var project = Receiver(temp);
        var source = new ActorRef(Outpost, "BP_TradePost_C_2");
        project.Apply(new AddBlueprintActorOp(Outpost, "BP_TradePost_Added", TradePost, source, Elsewhere) { Trader = new TraderPost("A_0_Armory_2", "Armorer", "Outpost_A_0") });
        var economy = EconomyOverride.CreateDefault();
        economy.Set("A_0_Armory_2", new TradeableOverride("Weapon_M9") { PurchasePrice = 10 }, keep: true);
        var post = new AddBlueprintActorOp(Outpost, "BP_TradePost_Added", TradePost, source, Somewhere) { Trader = new TraderPost("A_0_Armory_2", "Armorer", "Outpost_A_0") };
        var lift = new AddBlueprintActorOp(Outpost, "BP_CarLift_Added", "/Game/ConZ_Files/Economy/BP_CarLift.BP_CarLift_C", new ActorRef(Outpost, "BP_CarLift_C_1"), Somewhere) { Mechanic = "BP_TradePost_Added" };
        var sharedEconomy = EconomyOverride.Parse("""{"economy-override": {}}""");
        sharedEconomy.Set("A_0_Armory_2", new TradeableOverride("Weapon_AK47") { PurchasePrice = 900 }, keep: true);
        var content = Shared([post, lift], new Dictionary<string, byte[]> { [EconomyOverride.FileName] = Encoding.UTF8.GetBytes(sharedEconomy.ToJson()) });

        var plan = ShareImport.Plan(project, content, [("A_0_Armory", "Armorer")], economy);
        ShareImport.Apply(project, plan, "armory", content, economy);

        Assert.Equal("A_0_Armory_3", plan.TraderNames["A_0_Armory_2"]);
        Assert.Contains(("A_0_Armory_2", "A_0_Armory_3"), plan.Renamed);
        Assert.Equal(["A_0_Armory_3"], plan.EconomySections);
        var placed = Assert.IsType<AddBlueprintActorOp>(project.State.AddedActors[new ActorRef(Outpost, "BP_TradePost_Added2")]);
        Assert.Equal("A_0_Armory_3", placed.Trader!.Name);
        Assert.Equal("BP_TradePost_Added2", Assert.IsType<AddBlueprintActorOp>(project.State.AddedActors[new ActorRef(Outpost, "BP_CarLift_Added")]).Mechanic);
        Assert.Equal(900, economy.Find("A_0_Armory_3", "Weapon_AK47")!.PurchasePrice);
        Assert.Equal(["Weapon_M9"], economy.Entries("A_0_Armory_2").Select(e => e.Code));
    }

    [Fact]
    public void EveryEditOpTypeIsKnownToTheShareRemap()
    {
        var journalTypes = typeof(EditOp).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType).ToHashSet();

        Assert.Equal(journalTypes.OrderBy(t => t.Name), EditOpRemap.KnownTypes.OrderBy(t => t.Name));
        var empty = (actors: new Dictionary<ActorRef, ActorRef>(), instances: new Dictionary<InstanceRef, InstanceRef>(), traders: new Dictionary<string, string>());
        foreach (var op in AllOps().Select(o => (EditOp)o[0]))
        {
            Assert.Equal(op, EditOpRemap.Rename(op, empty.actors, empty.instances, empty.traders));
        }
    }

    /// <summary>Lines of the journal while the project holds it open for appending.</summary>
    private static int Lines(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static Project Receiver(LevelTempDirectory temp, string build = "1.0.0.0") =>
        Project.Create(temp.Combine("Receiver.ssproj"), "Receiver", build, clock: ManualClock.At2026());

    private static ShareContent Shared(params EditOp[] ops) => Shared(ops, new Dictionary<string, byte[]>());

    private static ShareContent Shared(EditOp[] ops, IReadOnlyDictionary<string, byte[]> files) =>
        ShareFile.Read(ShareFile.Write(Sample(files) with { Edits = new BatchOp("Shared", ops) }));

    /// <summary>The sample mod with a new craftable, one the receiver may have, and two trader sections.</summary>
    private static ShareContent SharedWithFiles()
    {
        var economy = EconomyOverride.Parse("""{"economy-override": {}}""");
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 500 });
        economy.EnsureSection("A_0_Trader");
        var craftables = new CraftablesFile { Items = [Table(), Table() with { Name = "table" }, Table() with { Name = "Chair" }] };
        return ShareFile.Read(ShareFile.Write(Sample(new Dictionary<string, byte[]>
        {
            [CraftablesFile.FileName] = Encoding.UTF8.GetBytes(craftables.ToJson()),
            [EconomyOverride.FileName] = Encoding.UTF8.GetBytes(economy.ToJson()),
        })));
    }
}
