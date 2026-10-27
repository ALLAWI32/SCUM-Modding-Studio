using System.Text.Json.Nodes;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Mcp;

/// <summary>
/// Owner request: an AI places zombie spawn points and armed NPCs over the MCP server, as the Spawns page does (a dungeon
/// the AI had built needed its zombies and bots). The tools are listed, named in the skill and the instructions, need a
/// project, and on the game's own files (<c>SCUM_PAKS</c>) place one group of posts with an area of its own in one undo step.
/// </summary>
public sealed class SpawnToolsTests(ITestOutputHelper output)
{
    private static readonly string[] Names = ["list_npc_kinds", "place_npcs", "place_zombie_spawns", "set_guard_spawns", "list_guard_spawns", "remove_guard_spawns", "write_guard_mod"];

    private async Task<JsonNode> Call(McpServer server, McpSession session, string tool, object arguments, bool expectError = false)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) },
        };
        var result = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), session))!)!["result"]!;
        output.WriteLine($"> {tool}: {result["content"]![0]!["text"]!.GetValue<string>()}");
        Assert.Equal(expectError, result["isError"]!.GetValue<bool>());
        return result;
    }

    [Fact]
    public void TheSpawnToolsAreListedAndNamedInTheSkillAndTheInstructions()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        foreach (var name in Names)
        {
            var tool = Assert.Single(server.Tools, t => t.Name == name);
            Assert.True(tool.Description.Length > 30, name);
            Assert.Contains("`" + name, StudioSkill.Text, StringComparison.Ordinal);
            Assert.Contains(name, StudioTools.Instructions, StringComparison.Ordinal);
        }

        Assert.True(Assert.Single(server.Tools, t => t.Name == "list_npc_kinds").ReadOnly);
        Assert.Equal(["kind"], Assert.Single(server.Tools, t => t.Name == "place_npcs").InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["points"], Assert.Single(server.Tools, t => t.Name == "place_zombie_spawns").InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>The guard sets need no game files: made, listed, written as the server mod, removed, undone.</summary>
    [Fact]
    public async Task GuardSetsAreMadeListedWrittenAsTheServerModAndRemoved()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        await Call(server, session, "create_project", new { folder = temp.Combine("projects"), name = "Guards" });

        var made = await Call(server, session, "set_guard_spawns", new
        {
            name = "Dungeon",
            posts = new[] { new { x = 456300.0, y = -234600.0, z = 8301.0, level = 5 }, new { x = 456800.0, y = -234600.0, z = 8301.0, level = 3 } },
            idleSeconds = 120,
        });
        Assert.Contains("2 post(s)", made["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var set = Assert.Single(host.Project!.State.GuardSets).Value;
        Assert.Equal((2, 120, 300), (set.Posts.Count, set.IdleSeconds, set.RespawnSeconds));

        var listed = (await Call(server, session, "list_guard_spawns", new { }))["structuredContent"]!["sets"]!.AsArray();
        Assert.Equal("Dungeon", listed[0]!["name"]!.GetValue<string>());
        Assert.Equal(5, listed[0]!["posts"]![0]!["level"]!.GetValue<int>());

        // Written for a server the app cannot reach, and into a server folder (UE4SS mods list switched on).
        await Call(server, session, "write_guard_mod", new { outputFolder = temp.Combine("out") });
        Assert.Contains("{ 456300, -234600, 8301, 5 },", File.ReadAllText(temp.Combine("out", GuardSpawns.ModName, "Scripts", "main.lua")), StringComparison.Ordinal);
        Directory.CreateDirectory(temp.Combine("srv", "SCUM", "Binaries", "Win64"));
        File.WriteAllText(temp.Combine("srv", "SCUM", "Binaries", "Win64", "SCUMServer.exe"), string.Empty);
        await Call(server, session, "write_guard_mod", new { serverFolder = temp.Combine("srv", "SCUM", "Content", "Paks") });
        Assert.Contains("GuardSpawns : 1", File.ReadAllText(temp.Combine("srv", "SCUM", "Binaries", "Win64", "ue4ss", "Mods", "mods.txt")), StringComparison.Ordinal);
        await Call(server, session, "write_guard_mod", new { serverFolder = temp.Combine("nowhere") }, expectError: true);

        // A bad set is refused with a reason; a set can be removed and undone.
        await Call(server, session, "set_guard_spawns", new { name = "bad name", posts = new[] { new { x = 1.0, y = 1.0, z = 1.0 } } }, expectError: true);
        await Call(server, session, "set_guard_spawns", new { name = "Other", posts = new[] { new { x = 1.0, y = 1.0, z = 1.0, level = 9 } } }, expectError: true);
        await Call(server, session, "remove_guard_spawns", new { name = "Dungeon" });
        Assert.Empty(host.Project.State.GuardSets);
        await Call(server, session, "write_guard_mod", new { outputFolder = temp.Combine("out2") }, expectError: true); // nothing to write
        await Call(server, session, "undo", new { });
        Assert.Single(host.Project.State.GuardSets);
    }

    [Fact]
    public async Task WithoutAProjectNothingIsPlacedAndTheAnswerSaysWhy()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        var zombies = await Call(server, session, "place_zombie_spawns", new { points = new[] { new { x = 1.0, y = 2.0, z = 3.0 } } }, expectError: true);
        Assert.Contains("project", zombies["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var npcs = await Call(server, session, "place_npcs", new { kind = "military base", x = 1.0, y = 2.0 }, expectError: true);
        Assert.Contains("project", npcs["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnTheGamesFilesNpcsAndZombiesAreOneUndoStepEach()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        await Call(server, session, "open_source", new { path = paks });
        await Call(server, session, "create_project", new { folder = temp.Combine("projects"), name = "Spawns" });

        // The kinds read like the wizard lists them.
        var kinds = (await Call(server, session, "list_npc_kinds", new { filter = "military base" }))["structuredContent"]!["kinds"]!.AsArray();
        var guard = kinds.First(k => k!["name"]!.GetValue<string>() == "MTZ_POI_Military_Base_NPC_Encounter")!;
        Assert.Equal("Military Base", guard["place"]!.GetValue<string>());
        Assert.Contains("BP_Guard_Lvl_3", guard["characters"]!.AsArray().Select(c => c!.GetValue<string>()));

        // Three posts on a built floor (z is the floor): one group, an area over it and the zone settings' own copy.
        var posts = new[] { new { x = 456000.0, y = -234000.0, z = 8301.0 }, new { x = 456600.0, y = -234600.0, z = 8301.0 }, new { x = 455400.0, y = -235200.0, z = 8301.0 } };
        var before = host.Project!.Journal.Applied.Count;
        var placed = (await Call(server, session, "place_npcs", new { kind = "military base npc", posts, title = "Dungeon guards" }))["structuredContent"]!;
        Assert.Equal(before + 1, host.Project.Journal.Applied.Count);
        var batch = Assert.IsType<BatchOp>(host.Project.Journal.Applied[^1].Op);
        var group = Assert.Single(batch.Ops.OfType<AddSpawnPlaceOp>(), o => o.Kind == SpawnPlaceKind.CharacterGroup);
        Assert.Equal(3, group.Points!.Count);
        Assert.InRange(group.Transform.Location.Z, 8305.0, 8306.0); // the floor and the lift
        Assert.Contains(batch.Ops, o => o is CloneAssetOp);
        Assert.Equal(3, placed["posts"]!.AsArray().Count);

        // An own copy of one character: the whole chain is cloned, the stock one is not touched.
        await Call(server, session, "place_npcs", new { kind = "MTZ_POI_Military_Base_NPC_Encounter", x = 456300.0, y = -234600.0, z = 8301.0, count = 2, ownCopyOf = "BP_Guard_Lvl_3" });
        var own = Assert.IsType<BatchOp>(host.Project.Journal.Applied[^1].Op);
        Assert.Contains(own.Ops.OfType<CloneAssetOp>(), c => c.Packages.Any(p => p.New.Contains("BP_Guard_Lvl_3_Own", StringComparison.Ordinal))); // a later copy may carry a number

        // Zombie points: one op per point, one step.
        var step = host.Project.Journal.Applied.Count;
        await Call(server, session, "place_zombie_spawns", new { points = posts, type = "Interior" });
        var zombies = Assert.IsType<BatchOp>(host.Project.Journal.Applied[^1].Op);
        Assert.Equal(step + 1, host.Project.Journal.Applied.Count);
        Assert.All(zombies.Ops.Cast<AddSpawnPlaceOp>(), o =>
        {
            Assert.Equal(SpawnPlaceKind.Character, o.Kind);
            Assert.Equal("Interior", o.Setting);
        });
        Assert.Equal(3, zombies.Ops.Count);

        // An unknown kind and too many posts are refused with a reason.
        await Call(server, session, "place_npcs", new { kind = "no such kind", x = 1.0, y = 1.0 }, expectError: true);
        await Call(server, session, "place_npcs", new { kind = "MTZ_POI_Military_Base_NPC_Encounter", posts = Enumerable.Range(0, 25).Select(i => new { x = 456000.0 + i, y = -234000.0, z = 8301.0 }).ToArray() }, expectError: true);

        // One undo takes a whole step back.
        await Call(server, session, "undo", new { steps = 3 });
        Assert.Equal(before, host.Project.Journal.Applied.Count);
    }
}
