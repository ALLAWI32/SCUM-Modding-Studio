using System.Text.Json.Nodes;
using ScumStudio.Level.Editing;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Mcp;

/// <summary>
/// Owner request: an AI building through the MCP server (a village, a dense forest) without objects floating in the air:
/// everything rests on the terrain or on what stands there, stacked objects land on each other, planted meshes sit as deep
/// as the game plants them, and one undo removes a whole build step.
/// </summary>
public sealed class BuildToolsTests(ITestOutputHelper output)
{
    private static readonly double[] Outpost = [-622000, -556000];

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
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        output.WriteLine($"> {tool}: {(text.Length > 800 ? text[..800] + " …" : text)}");
        Assert.Equal(expectError, result["isError"]!.GetValue<bool>());
        return result;
    }

    [MapSliceFact]
    public async Task AnAiBuildsWithoutFloatingObjectsAndUndoesAStepAtOnce()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        await Call(server, session, "open_source", new { path = MapSlice.Root });
        await Call(server, session, "create_project", new { folder = temp.Combine("projects"), name = "Builder" });

        var categories = (await Call(server, session, "list_object_categories", new { depth = 1 }))["structuredContent"]!;
        Assert.NotNull(categories);
        var ground = (await Call(server, session, "ground_height", new { x = Outpost[0], y = Outpost[1] }))["structuredContent"]!;
        var terrainZ = ground["terrainZ"]!.GetValue<double>();

        // Two crates at the same spot: the second lands on the first.
        var crates = (await Call(server, session, "list_objects", new { category = "buildings", type = "mesh", withSize = true, limit = 60 }))["structuredContent"]!;
        var crate = crates["objects"]!.AsArray().First(o => o!["sizeCm"] is JsonArray { Count: 3 } s && s[2]!.GetValue<double>() is > 40 and < 400)!;
        var crateHeight = crate["sizeCm"]![2]!.GetValue<double>();
        var spot = new { x = Outpost[0] + 2500, y = Outpost[1] + 2500 };
        var placed = (await Call(server, session, "place_objects", new
        {
            title = "Stack two crates",
            objects = new[] { new { @object = crate["path"]!.GetValue<string>(), spot.x, spot.y }, new { @object = crate["path"]!.GetValue<string>(), spot.x, spot.y } },
        }))["structuredContent"]!["placed"]!.AsArray();
        var z0 = placed[0]!["location"]![2]!.GetValue<double>();
        var z1 = placed[1]!["location"]![2]!.GetValue<double>();
        Assert.InRange(z1 - z0, crateHeight * 0.8, crateHeight * 1.2); // on top of the first, not inside it, not floating

        // A small forest: planted on the terrain, one journal step.
        // The A_0 slice holds buildings and terrain only; any mesh shows the planting rules.
        var trees = crates["objects"]!.AsArray().Where(o => o!["sizeCm"] is JsonArray { Count: 3 } s && s[2]!.GetValue<double>() is > 40 and < 400).Take(2).ToList();
        var before = host.Project!.Journal.Applied.Count;
        await Call(server, session, "scatter_objects", new
        {
            objects = trees.Select(t => t!["path"]!.GetValue<string>()).ToArray(),
            x = Outpost[0] - 8000,
            y = Outpost[1] - 8000,
            radius = 3000,
            count = 25,
            seed = 7,
            title = "Plant a grove",
        });
        var grove = Assert.IsType<BatchOp>(host.Project.Journal.Applied[^1].Op);
        Assert.Equal(before + 1, host.Project.Journal.Applied.Count);
        Assert.All(grove.Ops.Cast<AddStaticMeshActorOp>(), a => Assert.InRange(a.Transform.Location.Z, terrainZ - 30_000, terrainZ + 30_000));

        // One undo removes the whole grove.
        await Call(server, session, "undo", new { });
        Assert.All(grove.Ops.Cast<AddStaticMeshActorOp>(), a => Assert.False(host.Project.State.IsAdded(a.Created)));

        // A whole game-made place copied elsewhere: its Blueprint actors come along.
        await Call(server, session, "copy_place", new { sourceLevel = "A_0_Outpost_Ext_Saloon", x = Outpost[0] - 6000, y = Outpost[1] + 4000, yaw = 90 });
        var copy = Assert.IsType<BatchOp>(host.Project.Journal.Applied[^1].Op);
        Assert.Contains(copy.Ops, o => o is AddBlueprintActorOp);
    }
}
