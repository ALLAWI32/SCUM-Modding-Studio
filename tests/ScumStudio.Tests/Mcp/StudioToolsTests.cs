using System.Text.Json.Nodes;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Mcp;

/// <summary>
/// The ScumStudio MCP tools driven like an AI client would (JSON-RPC <c>tools/call</c>) over the headless host: map tools
/// on the A_0 slice (list, inspect, delete, move, duplicate, add mesh, copy between levels, delete-all-of-kind, undo,
/// export) and vehicle/weapon tools on the stock fixture packages.
/// </summary>
public sealed class StudioToolsTests(ITestOutputHelper output)
{
    private const string Saloon = "A_0_Outpost_Ext_Saloon";
    private const string Exterior = "A_0_Outpost_Exterior";

    private sealed class Client(McpServer server, ITestOutputHelper output)
    {
        private readonly McpSession _session = new();
        private int _id;

        public async Task<JsonNode> CallAsync(string tool, object? arguments = null, bool expectError = false)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = ++_id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments is null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) },
            };
            var reply = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), _session))!)!;
            Assert.Null(reply["error"]);
            var result = reply["result"]!;
            var text = string.Join('\n', result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>() ?? "[" + c!["type"] + "]"));
            output.WriteLine($"> {tool} {request["params"]!["arguments"]!.ToJsonString()}");
            output.WriteLine(text.Length > 1500 ? text[..1500] + " …" : text);
            Assert.Equal(expectError, result["isError"]!.GetValue<bool>());
            return result;
        }

        public static string Text(JsonNode result) => result["content"]![0]!["text"]!.GetValue<string>();

        public static JsonNode Structured(JsonNode result) => result["structuredContent"]!;
    }

    [Fact]
    public void EveryToolHasADescriptionAndAnObjectSchema()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        Assert.True(server.Tools.Count >= 25, $"{server.Tools.Count} tools");
        Assert.All(server.Tools, t =>
        {
            Assert.Matches("^[a-z][a-z_]*$", t.Name);
            Assert.True(t.Description.Length > 30, t.Name);
            Assert.Equal("object", t.InputSchema["type"]!.GetValue<string>());
        });
        Assert.DoesNotContain(server.Tools, t => t.Name is "screenshot" or "navigate"); // UI tools only in the app
        Assert.Contains(server.Tools, t => t.Name == "delete_actors" && t.Destructive);
        Assert.Contains(server.Tools, t => t.Name == "list_actors" && t.ReadOnly);
        Assert.Contains("never passes through this server", server.Info.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsExplainWhatIsMissing()
    {
        using var host = new HeadlessStudioHost();
        var client = new Client(StudioTools.CreateServer(host), output);
        var status = Client.Structured(await client.CallAsync("get_status"));
        Assert.Equal("cli", status["host"]!.GetValue<string>());
        Assert.Contains("open_source", status["hint"]!.GetValue<string>(), StringComparison.Ordinal);
        var noSource = await client.CallAsync("list_levels", expectError: true);
        Assert.Contains("open_source", Client.Text(noSource), StringComparison.Ordinal);
        var noProject = await client.CallAsync("undo", expectError: true);
        Assert.Contains("create_project", Client.Text(noProject), StringComparison.Ordinal);
    }

    [MapSliceFact]
    public async Task AnAiCanEditTheMapThroughTheTools()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var client = new Client(StudioTools.CreateServer(host), output);

        Assert.Contains("Opened", Client.Text(await client.CallAsync("open_source", new { path = MapSlice.Root })), StringComparison.Ordinal);
        var levels = Client.Structured(await client.CallAsync("list_levels", new { cell = "A_0", filter = "outpost ext" }));
        Assert.Contains(levels["levels"]!.AsArray(), l => l!["name"]!.GetValue<string>() == Saloon);

        var actors = Client.Structured(await client.CallAsync("list_actors", new { level = Saloon }));
        Assert.Contains(actors["actors"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "BP_SaloonOutpost_2");
        var saloon = Client.Structured(await client.CallAsync("get_actor", new { level = Saloon, actor = "BP_SaloonOutpost_2" }));
        Assert.Equal("Blueprint", saloon["kind"]!.GetValue<string>());
        Assert.EndsWith("_C", saloon["classPath"]!.GetValue<string>(), StringComparison.Ordinal);

        // Edits need a project.
        await client.CallAsync("delete_actors", new { level = Saloon, actors = new[] { "BP_SaloonOutpost_2" } }, expectError: true);
        var project = Client.Structured(await client.CallAsync("create_project", new { folder = temp.Combine("projects"), name = "AI test" }));
        Assert.Equal("AI test", project["name"]!.GetValue<string>());

        Assert.Contains("Delete", Client.Text(await client.CallAsync("delete_actors", new { level = Saloon, actors = new[] { "BP_SaloonOutpost_2", "NoSuchActor" } })), StringComparison.Ordinal);
        Assert.True(host.Project!.State.IsDeleted(new ActorRef(MapSlice.MapsPath + Saloon, "BP_SaloonOutpost_2")));
        var afterDelete = Client.Structured(await client.CallAsync("list_actors", new { level = Saloon, filter = "SaloonOutpost" }));
        Assert.Contains(afterDelete["actors"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "BP_SaloonOutpost_2" && a["deleted"]?.GetValue<bool>() == true);

        // Move, duplicate and add in the exterior level.
        var exterior = Client.Structured(await client.CallAsync("list_actors", new { level = Exterior, kind = "StaticMeshActor", limit = 5 }));
        var first = exterior["actors"]![0]!;
        var name = first["name"]!.GetValue<string>();
        var location = first["location"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        Assert.Contains("Transform", Client.Text(await client.CallAsync("move_actor", new { level = Exterior, actor = name, offset = new[] { 0, 0, 500 }, rotation = new[] { 0, 45, 0 } })), StringComparison.Ordinal);
        var moved = host.Project.State.GetTransformOverride(new ActorRef(MapSlice.MapsPath + Exterior, name))!.Value;
        Assert.Equal(location[2] + 500, moved.Location.Z, 1);
        Assert.Equal(45f, moved.Rotation.Yaw, 2);

        Assert.Contains("_Copy", Client.Text(await client.CallAsync("duplicate_actor", new { level = Exterior, actor = name })), StringComparison.Ordinal);
        var search = Client.Structured(await client.CallAsync("search_assets", new { query = "Outpost SM_", className = "StaticMesh", limit = 3 }));
        Assert.True(search["count"]!.GetValue<int>() > 0);
        var meshPath = search["results"]![0]!["objectPath"]!.GetValue<string>();
        Assert.Contains("new actor", Client.Text(await client.CallAsync("add_static_mesh", new { level = Exterior, mesh = meshPath, location = new[] { location[0] + 1000, location[1], location[2] } })), StringComparison.Ordinal);
        Assert.Contains("BP_SaloonOutpost", Client.Text(await client.CallAsync("copy_actor_to_level", new { sourceLevel = Saloon, actor = "BP_SaloonOutpost_2", targetLevel = Exterior, location = new[] { location[0], location[1] + 3000, location[2] } })), StringComparison.Ordinal);
        var added = Client.Structured(await client.CallAsync("list_actors", new { level = Exterior, filter = "Added" }));
        Assert.True(added["actors"]!.AsArray().Count(a => a!["added"]?.GetValue<bool>() == true) >= 2);

        var bulk = Client.Text(await client.CallAsync("delete_all_of_kind", new { levels = new[] { Exterior }, by = "mesh", likeActor = name }));
        Assert.Contains("actor(s)", bulk, StringComparison.Ordinal);

        // Undo / redo / history.
        Assert.Contains("Undone", Client.Text(await client.CallAsync("undo")), StringComparison.Ordinal);
        Assert.Contains("Redone", Client.Text(await client.CallAsync("redo")), StringComparison.Ordinal);
        var history = Client.Structured(await client.CallAsync("get_history", new { limit = 10 }));
        Assert.True(history["total"]!.GetValue<int>() >= 6);

        var references = Client.Structured(await client.CallAsync("level_references", new { levels = new[] { Saloon } }));
        Assert.True(references["present"]!.GetValue<int>() > 1);

        var export = Client.Structured(await client.CallAsync("export_mod", new { outputFolder = temp.Combine("out"), modName = "AiTest" }));
        var pak = export["exports"]![0]!["pak"]!.GetValue<string>();
        Assert.True(File.Exists(pak), pak);
        Assert.EndsWith("pakchunk900-AiTest_P.pak", pak, StringComparison.Ordinal);
        Assert.True(export["exports"]![0]!["levels"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public async Task ADoorCopiedToAnotherLevelLandsWhereTheDoorIs()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        // The saloon's door is a child actor: its root is placed relative to the building's component (all zero), so a copy
        // into another level without a location landed at the world origin.
        const string door = "BP_SingleDoorSaloonOutpost_Flip_GEN_VARIABLE_BP_SingleDoorSaloonOutpost_Flip_C_CAT_0";
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var client = new Client(StudioTools.CreateServer(host), output);
        await client.CallAsync("open_source", new { path = paks });
        await client.CallAsync("create_project", new { folder = temp.Combine("projects"), name = "Doors" });
        var saloon = LevelDocument.Load(new Cue4ParseLevelReader(host.Catalog!), MapSlice.MapsPath + Saloon);
        var world = saloon.FindActor(door)!.WorldTransform;
        Assert.True(world.Translation.Size() > 10_000f, world.ToString());

        await client.CallAsync("copy_actor_to_level", new { sourceLevel = Saloon, actor = door, targetLevel = Exterior });
        var copy = Assert.IsType<AddBlueprintActorOp>(host.Project!.Journal.Applied[^1].Op);
        Assert.True(FVector.Distance(world.Translation, copy.Transform.Location) < 1f, copy.Transform.ToString());

        // Moved first: the copy lands where the moved door is.
        await client.CallAsync("move_actor", new { level = Saloon, actor = door, offset = new[] { 0, 0, 100 } });
        var moved = host.Project.State.GetTransformOverride(new ActorRef(MapSlice.MapsPath + Saloon, door))!.Value;
        var slot = saloon.Actors.SelectMany(a => a.Components).Single(c => c.ExportIndex == saloon.FindActor(door)!.Root!.AttachParent);
        await client.CallAsync("copy_actor_to_level", new { sourceLevel = Saloon, actor = door, targetLevel = Exterior });
        var movedCopy = Assert.IsType<AddBlueprintActorOp>(host.Project.Journal.Applied[^1].Op);
        Assert.True(FVector.Distance((moved.ToTransform() * slot.WorldTransform).Translation, movedCopy.Transform.Location) < 1f, movedCopy.Transform.ToString());
        Assert.True(FVector.Distance(world.Translation, movedCopy.Transform.Location) > 50f);
    }

    [FixturesFact]
    public async Task AnAiCanTuneAndCloneWeaponsAndVehicles()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var client = new Client(StudioTools.CreateServer(host), output);
        await client.CallAsync("open_source", new { path = FixturePaths.OrigRoot });
        var items = Client.Structured(await client.CallAsync("list_items", new { kind = "Weapon" }));
        Assert.Contains(items["items"]!.AsArray(), i => i!["name"]!.GetValue<string>() == "Weapon_RPG7");

        var values = Client.Structured(await client.CallAsync("get_item_values", new { item = "Weapon_RPG7", filter = "Damage" }));
        var damage = values["values"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "DamagePerShot")!;
        Assert.Equal("2.664", damage["stock"]!.GetValue<string>());

        await client.CallAsync("create_project", new { folder = temp.Combine("p"), name = "Guns" });
        var set = await client.CallAsync("set_item_values", new
        {
            item = "Weapon_RPG7",
            values = new object[]
            {
                new { key = damage["key"]!.GetValue<string>(), value = "4.5" },
                new { key = "Default__Weapon_RPG7_C|MaxRange", value = 900 },
                new { key = "Default__Weapon_RPG7_C|WeaponCategory", value = "NotAnEnum" },
            },
        });
        Assert.Contains("Skipped", Client.Text(set), StringComparison.Ordinal);
        Assert.Equal(2, host.Project!.State.AssetValueOverrides.Count);
        var after = Client.Structured(await client.CallAsync("get_item_values", new { item = "Weapon_RPG7", filter = "MaxRange" }));
        Assert.Equal("900", after["values"]![0]!["current"]!.GetValue<string>());

        var clone = Client.Text(await client.CallAsync("clone_item", new { template = "Weapon_RPK-74", newName = "Weapon_RPK-74_Gold", caption = "RPK Gold" }));
        Assert.Contains("#SpawnItem Weapon_RPK-74_Gold", clone, StringComparison.Ordinal);
        var es = Client.Structured(await client.CallAsync("get_item_values", new { item = "Weapon_RPK-74_Gold_ES", filter = "Caption" }));
        Assert.True(es["isClone"]!.GetValue<bool>());
        Assert.Equal("RPK Gold", es["values"]![0]!["current"]!.GetValue<string>());
        var vehicle = Client.Text(await client.CallAsync("clone_item", new { template = "BPC_WolfsWagen", newName = "Hunter", includeSpawnPresets = false }));
        Assert.Contains("#SpawnVehicle BPC_Hunter", vehicle, StringComparison.Ordinal);
        await client.CallAsync("clone_item", new { template = "Weapon_RPK-74", newName = "Weapon_RPK-74_Gold" }, expectError: true); // already used

        Assert.Contains("Remove clone", Client.Text(await client.CallAsync("remove_item_clone", new { item = "Weapon_RPK-74_Gold" })), StringComparison.Ordinal);
        Assert.Single(host.Project.State.AssetClones);
        var list = Client.Structured(await client.CallAsync("list_items", new { kind = "Vehicle" }));
        Assert.Contains(list["items"]!.AsArray(), i => i!["name"]!.GetValue<string>() == "BPC_Hunter" && i["cloneOf"]!.GetValue<string>() == "BPC_WolfsWagen");

        var asset = Client.Structured(await client.CallAsync("get_asset", new { path = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPG7" }));
        Assert.Equal("BlueprintGeneratedClass", asset["mainClass"]!.GetValue<string>());
    }
}
