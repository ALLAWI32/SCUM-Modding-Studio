using System.Text.Json;
using System.Text.Json.Nodes;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Mcp;

/// <summary>
/// The MCP delete tools take what belongs to an object (owner, 2026-10-12: "If I delete a fire place, the fire stays burning
/// in game ... When I delete it, EVERYTHING of it must be deleted"): one journal edit with the fire's light, emitter, sound
/// and decal, a shared ambience kept for the house it belongs to, one undo for all of it. Headless host on the synthetic
/// camp level.
/// </summary>
public sealed class DeleteCompanionsToolTests(ITestOutputHelper output)
{
    private const string Level = SyntheticCampLevel.LevelPath;
    private const string CampfireName = "A_0_Campfire";
    private const string Campfire = "/Game/ConZ_Files/Maps/The_Island/" + CampfireName;

    [Fact]
    public async Task DeletingTheFireplaceJournalsOneBatchWithItsFireAndKeepsTheHousesAmbience()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp);
        var tools = StudioTools.CreateServer(host);

        var result = await CallAsync(tools, "delete_actors", new { level = Level, actors = new[] { "SM_Fireplace_1" } });

        var batch = Assert.IsType<BatchOp>(LastOp(host));
        var deleted = batch.Ops.OfType<DeleteActorOp>().Select(o => o.Target.Actor).ToList();
        Assert.Equal("SM_Fireplace_1", deleted[0]);
        foreach (var fire in new[] { "FireLight_1", "P_Fire_1", "Fire_Sound_1", "Ash_Decal_1" })
        {
            Assert.Contains(fire, deleted);
            Assert.True(host.Project!.State.IsDeleted(new ActorRef(Level, fire)), fire);
        }

        Assert.DoesNotContain("Camp_Ambience", deleted);
        Assert.DoesNotContain("BP_House_1", deleted);
        Assert.DoesNotContain("Lamp_1", deleted);
        Assert.DoesNotContain("DirectionalLight_1", deleted);
        Assert.DoesNotContain("WorldSettings", deleted);

        var structured = result["structuredContent"]!;
        Assert.Contains(structured["companions"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Fire_Sound_1" && c["rule"]!.GetValue<string>() == "AtTheSpot");
        var ambience = Assert.Single(structured["kept"]!.AsArray(), k => k!["name"]!.GetValue<string>() == "Camp_Ambience")!;
        Assert.Equal("BP_House_1", ambience["belongsTo"]!.GetValue<string>());
        Assert.Contains(structured["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("running server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheExportedLevelHoldsNoneOfTheFiresPieces()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp);
        await CallAsync(StudioTools.CreateServer(host), "delete_actors", new { level = Level, actors = new[] { "SM_Fireplace_1" } });

        var result = await new ProjectExporter().ExportAsync(host.Project!, host.Catalog!, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });

        using var overlay = AssetCatalog.OpenLoose(temp.Combine("game"), new AssetCatalogOptions { LooseOverlays = [result.StagingDirectory] });
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay), Level);
        foreach (var gone in new[] { "SM_Fireplace_1", "FireLight_1", "P_Fire_1", "Fire_Sound_1", "Ash_Decal_1", "Glow_1" })
        {
            Assert.Null(after.FindActor(gone));
        }

        foreach (var kept in new[] { "Camp_Ambience", "BP_House_1", "Lamp_1", "DirectionalLight_1" })
        {
            Assert.NotNull(after.FindActor(kept));
        }
    }

    [Fact]
    public async Task WithoutCompanionsOnlyTheNamedActorIsDeleted()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp);
        var tools = StudioTools.CreateServer(host);

        await CallAsync(tools, "delete_actors", new { level = Level, actors = new[] { "SM_Fireplace_1" }, companions = false });

        var op = Assert.IsType<DeleteActorOp>(LastOp(host));
        Assert.Equal("SM_Fireplace_1", op.Target.Actor);
        Assert.False(host.Project!.State.IsDeleted(new ActorRef(Level, "FireLight_1")));
    }

    [Fact]
    public async Task OneUndoRestoresTheFireplaceAndEverythingThatWentWithIt()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp);
        var tools = StudioTools.CreateServer(host);
        await CallAsync(tools, "delete_actors", new { level = Level, actors = new[] { "SM_Fireplace_1" } });
        var gone = ((BatchOp)LastOp(host)).Ops.OfType<DeleteActorOp>().Select(o => o.Target).ToList();
        Assert.True(gone.Count >= 5);

        await CallAsync(tools, "undo");

        Assert.All(gone, a => Assert.False(host.Project!.State.IsDeleted(a), a.Actor));
    }

    [Fact]
    public async Task DeleteAllOfKindTakesTheCompanionsInTheSameEdit()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp);
        var tools = StudioTools.CreateServer(host);

        await CallAsync(tools, "delete_all_of_kind", new { levels = new[] { Level }, by = "mesh", value = SyntheticCampLevel.FireplaceMesh + ".SM_Fireplace_01" });

        var batch = Assert.IsType<BatchOp>(LastOp(host));
        Assert.IsType<DeleteAllOfKindOp>(batch.Ops[0]);
        var deleted = batch.Ops.OfType<DeleteActorOp>().Select(o => o.Target.Actor).ToList();
        Assert.Contains("FireLight_1", deleted);
        Assert.Contains("Fire_Sound_1", deleted);
        Assert.DoesNotContain("Camp_Ambience", deleted);
    }

    [Fact]
    public async Task InTheAppTheOtherShownLevelsOfTheCellAreSearchedToo()
    {
        using var temp = new LevelTempDirectory();
        const string lights = "/Game/ConZ_Files/Maps/The_Island/A_0_Camp_Lights";
        const string other = "/Game/ConZ_Files/Maps/The_Island/B_1_Elsewhere";
        using var headless = await OpenCampAsync(temp, root =>
        {
            // The fire's light in the cell's other sublevel, and a light at the same spot of another cell's level.
            SyntheticLevels.WriteBlueprintOnlyLevel(root, "A_0_Camp_Lights", "/Game/ConZ_Files/Blueprints/Lights/MBP_Fire_PointLight_Child",
                "MBP_Fire_PointLight_Child_1", new FVector(0, 0, 80), FRotator.Zero);
            SyntheticLevels.WriteBlueprintOnlyLevel(root, "B_1_Elsewhere", "/Game/ConZ_Files/Blueprints/Lights/MBP_Fire_PointLight_Child",
                "MBP_Fire_PointLight_Child_2", new FVector(0, 0, 80), FRotator.Zero);
        });
        var tools = StudioTools.CreateServer(new ShowingHost(headless, [Level, lights, other]));

        await CallAsync(tools, "delete_actors", new { level = Level, actors = new[] { "SM_Fireplace_1" } });

        var deleted = ((BatchOp)LastOp(headless)).Ops.OfType<DeleteActorOp>().Select(o => o.Target).ToList();
        Assert.Contains(deleted, a => a.Level == lights && a.Actor == "MBP_Fire_PointLight_Child_1");
        Assert.DoesNotContain(deleted, a => a.Level == other);
    }

    [Fact]
    public async Task AStoredChildActorIsNotJournaledOnItsOwnAndComesBackWithItsParent()
    {
        using var temp = new LevelTempDirectory();
        using var host = await OpenCampAsync(temp, WriteCampfireLevel);
        var tools = StudioTools.CreateServer(host);

        var result = await CallAsync(tools, "delete_actors", new { level = Campfire, actors = new[] { "BP_Campfire_1" } });

        // Listed as the campfire's, but the export takes it along: a journaled delete would outlive a restore of the campfire.
        IReadOnlyList<EditOp> journaled = LastOp(host) is BatchOp batch ? batch.Ops : [LastOp(host)];
        Assert.Equal(["BP_Campfire_1"], journaled.OfType<DeleteActorOp>().Select(o => o.Target.Actor));
        Assert.Contains(result["structuredContent"]!["companions"]!.AsArray(),
            c => c!["name"]!.GetValue<string>() == "Campfire_Sound_1" && c["rule"]!.GetValue<string>() == "ChildActor");
        var exported = await new ProjectExporter().ExportAsync(host.Project!, host.Catalog!, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        using (var overlay = AssetCatalog.OpenLoose(temp.Combine("game"), new AssetCatalogOptions { LooseOverlays = [exported.StagingDirectory] }))
        {
            var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay), Campfire);
            Assert.Null(after.FindActor("BP_Campfire_1"));
            Assert.Null(after.FindActor("Campfire_Sound_1"));
        }

        await CallAsync(tools, "restore_actors", new { level = Campfire, actors = new[] { "BP_Campfire_1" } });

        Assert.False(host.Project!.State.IsDeleted(new ActorRef(Campfire, "Campfire_Sound_1")));
    }

    [Fact]
    public void AnEmitterHungOnTheHousesFireLightGoesAndTheFireComponentsGoWithIt()
    {
        // The camp's house with an embers emitter attached to its FireLight: deleting the fireplace part takes the emitter
        // (locked: it would lose its place), so the fire, light and sound components can go too.
        const string level = "/Game/ConZ_Files/Maps/The_Island/A_0_House";
        var b = new FakeLevelBuilder(level);
        var house = b.Actor("BP_House_1", "/Game/ConZ_Files/Buildings/House/BP_House.BP_House_C", blueprint: true);
        var root = b.Component(house, "DefaultSceneRoot");
        b.Root(house, root);
        b.Component(house, "Walls", "StaticMeshComponent", attachTo: root, mesh: SyntheticCampLevel.WallsMesh + ".SM_House_Walls");
        var fireplace = b.Component(house, "Fireplace", "StaticMeshComponent", new FVector(0, 300, 0), attachTo: root, mesh: SyntheticCampLevel.FireplaceMesh + ".SM_Fireplace_01");
        b.Component(house, "Fire", "ParticleSystemComponent", attachTo: fireplace);
        var fireLight = b.Component(house, "FireLight", "PointLightComponent", new FVector(0, 300, 60), attachTo: root);
        b.Component(house, "FireSound", "AkComponent", new FVector(0, 300, 0), attachTo: root);
        var embers = b.Actor("P_Embers_1", "/Script/Engine.Emitter");
        b.Root(embers, b.Component(embers, "ParticleSystemComponent0", "ParticleSystemComponent", attachTo: fireLight));
        var document = LevelDocument.FromData(b.Build());
        var record = document.FindActor("BP_House_1")!;
        var houseRef = new ActorRef(level, record.Name);
        var state = new EditState();
        var part = EditOpFactory.SetTransform(document, record, record.FindComponent("Fireplace")!.Relative with { Scale = FVector.Zero }, state, "Fireplace");

        var plan = StudioTools.PlanCompanions([document], [new DeleteSeed(houseRef, "Fireplace")], [part], state, "Delete BP_House_1.Fireplace", many: false,
            meshBounds: null, CancellationToken.None);

        Assert.Null(state.Validate(plan.Edit));
        state.Apply(plan.Edit);
        Assert.True(state.IsDeleted(new ActorRef(level, "P_Embers_1")));
        foreach (var effect in new[] { "Fire", "FireLight", "FireSound" })
        {
            Assert.True(state.IsDeleted(houseRef, effect), effect);
        }

        Assert.False(state.IsDeleted(houseRef, "Walls"));
        Assert.Empty(JsonSerializer.SerializeToNode(plan.Kept)!.AsArray());
        Assert.Contains(JsonSerializer.SerializeToNode(plan.Taken)!.AsArray(),
            t => t!["name"]!.GetValue<string>() == "P_Embers_1" && t["rule"]!.GetValue<string>() == "Attached");
    }

    /// <summary>
    /// A campfire whose <c>ChildActorComponent</c> spawned a Wwise ambient sound the level stores (its <c>ParentComponent</c>
    /// is the spawner), the way cooked buildings store their doors.
    /// </summary>
    private static void WriteCampfireLevel(string root)
    {
        const string engine = "/Script/Engine";
        const string akAudio = "/Script/AkAudio";
        var p = new SyntheticLevelPackage();
        var world = p.Export(CampfireName, p.ScriptClass(engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(engine, "Level"), world);
        var logsMesh = p.ObjectImport(SyntheticCampLevel.FireplaceMesh, engine, "StaticMesh", "SM_Fireplace_01");
        var fireEvent = p.ObjectImport(SyntheticCampLevel.FireEvent, akAudio, "AkAudioEvent", "Play_Fire_Loop");
        var ambientClass = p.ScriptClass(akAudio, "AkAmbientSound");
        var campfire = p.Export("BP_Campfire_1", p.ScriptClass(engine, "Actor"), level);
        var campfireRoot = p.Export("DefaultSceneRoot", p.ScriptClass(engine, "SceneComponent"), campfire);
        var logs = p.Export("Logs", p.ScriptClass(engine, "StaticMeshComponent"), campfire);
        var spawner = p.Export("SoundSpawner", p.ScriptClass(engine, "ChildActorComponent"), campfire);
        var sound = p.Export("Campfire_Sound_1", ambientClass, level);
        var soundRoot = p.Export("AkComponent0", p.ScriptClass(akAudio, "AkComponent"), sound);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => ReaderEffectsTests.LevelNative(w, [campfire, sound], CampfireName)));
        p.SetPayload(campfire, p.Properties(t => t.Object("RootComponent", campfireRoot)));
        p.SetPayload(campfireRoot, p.Properties(t => t.Vector("RelativeLocation", 3000, 0, 0)));
        p.SetPayload(logs, p.Properties(t =>
        {
            t.Object("AttachParent", campfireRoot);
            t.Object("StaticMesh", logsMesh);
        }, w => w.I32(0)));
        p.SetPayload(spawner, p.Properties(t =>
        {
            t.Object("AttachParent", campfireRoot);
            t.Vector("RelativeLocation", 0, 0, 40);
            t.Object("ChildActorClass", ambientClass);
            t.Object("ChildActor", sound);
        }));
        p.SetPayload(sound, p.Properties(t =>
        {
            t.Object("RootComponent", soundRoot);
            t.Object("ParentComponent", spawner);
        }));
        p.SetPayload(soundRoot, p.Properties(t =>
        {
            t.Object("AttachParent", spawner);
            t.Object("AkAudioEvent", fireEvent);
        }));
        p.Build().WriteAsync(Path.Combine([root, .. (SyntheticLevels.MapsFolder + "/" + CampfireName).Split('/')]), ".umap").GetAwaiter().GetResult();
    }

    private static async Task<HeadlessStudioHost> OpenCampAsync(LevelTempDirectory temp, Action<string>? more = null)
    {
        SyntheticCampLevel.WriteCampContent(temp.Combine("game"));
        more?.Invoke(temp.Combine("game"));
        var host = new HeadlessStudioHost();
        await host.OpenSourceAsync(temp.Combine("game"), CancellationToken.None);
        await host.CreateProjectAsync(temp.Combine("projects"), "Camp", CancellationToken.None);
        return host;
    }

    private static EditOp LastOp(HeadlessStudioHost host) => host.Project!.History.MaxBy(h => h.Seq)!.Op;

    /// <summary>The headless host as the app would be, with <paramref name="shown"/> loaded in the map.</summary>
    private sealed class ShowingHost(HeadlessStudioHost inner, IReadOnlyList<string> shown) : IStudioHost, IStudioUi
    {
        public string Kind => "app";

        public AssetCatalog? Catalog => inner.Catalog;

        public WorldIndex? World => inner.World;

        public Project? Project => inner.Project;

        public IStudioUi? Ui => this;

        public Task<string> OpenSourceAsync(string? path, CancellationToken cancellationToken) => inner.OpenSourceAsync(path, cancellationToken);

        public Task<Project> CreateProjectAsync(string parentFolder, string name, CancellationToken cancellationToken) => inner.CreateProjectAsync(parentFolder, name, cancellationToken);

        public Task<Project> OpenProjectAsync(string path, CancellationToken cancellationToken) => inner.OpenProjectAsync(path, cancellationToken);

        public JournalEntry Apply(EditOp op) => inner.Apply(op);

        public JournalEntry? Undo() => inner.Undo();

        public JournalEntry? Redo() => inner.Redo();

        public Task<IReadOnlyList<ExportResult>> ExportAsync(ExportOptions options, bool includeServer, CancellationToken cancellationToken) =>
            inner.ExportAsync(options, includeServer, cancellationToken);

        public void ReportActivity(string tool, string summary, bool isError)
        {
        }

        public Task<StudioUiState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StudioUiState("map", shown, null, null, 0, 0));

        public Task NavigateAsync(string page, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ShowLevelsAsync(IReadOnlyList<string> packagePaths, int landscapeStep, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SelectActorAsync(ActorRef actor, bool frame, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SetCameraAsync(FVector? location, FVector? lookAt, float? yaw, float? pitch, bool frameAll, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]?> CaptureViewportAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]?> CaptureWindowAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ShowItemAsync(string packagePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task<JsonNode> CallAsync(ScumStudio.Mcp.Protocol.McpServer server, string tool, object? arguments = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments is null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) },
        };
        var reply = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), new ScumStudio.Mcp.Protocol.McpSession()))!)!;
        Assert.Null(reply["error"]);
        var result = reply["result"]!;
        var text = string.Join('\n', result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>()));
        output.WriteLine($"> {tool}\n{text}");
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        return result;
    }
}
