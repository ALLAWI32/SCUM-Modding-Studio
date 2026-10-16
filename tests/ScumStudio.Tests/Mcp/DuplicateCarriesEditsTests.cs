using System.Text.Json.Nodes;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Mcp;

/// <summary>An AI's copies carry their source's edits as the Map's do (synthetic loose game files).</summary>
public sealed class DuplicateCarriesEditsTests
{
    [Fact]
    public async Task DuplicateAndCopyToLevelKeepTheBendAndThePaint()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        SyntheticLevels.WriteBlueprintOnlyLevel(game, "A_0_Other", SyntheticLevels.BlueprintPackage, "BP_Lamp_C_9", new FVector(10, 20, 30), new FRotator(0, 15, 0));
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        await CallAsync(server, session, "open_source", new { path = game });
        await CallAsync(server, session, "create_project", new { folder = temp.Combine("projects"), name = "AI copies" });
        var house = new ActorRef(SyntheticLevels.LevelPath, "StaticMeshActor_1");
        var bend = new BendValue(30f, Sway1: 50f, Legs: 120f);
        host.Apply(new BendActorOp(house, 0f, bend.Degrees, NewSway1: bend.Sway1, NewLegs: bend.Legs));

        var text = await CallAsync(server, session, "duplicate_actor", new { level = "A_0_TestLevel", actor = house.Actor });
        Assert.Contains("new actor 'StaticMeshActor_1_Copy'", text, StringComparison.Ordinal);
        var state = host.Project!.State;
        Assert.True(state.GetBendValue(house with { Actor = "StaticMeshActor_1_Copy" }).IsNearly(bend));

        // Into another level it is a new mesh actor: bent, and painted as the source.
        await CallAsync(server, session, "copy_actor_to_level", new { sourceLevel = "A_0_TestLevel", actor = house.Actor, targetLevel = "A_0_Other" });
        var added = Assert.Single(state.AddedActors.Values.OfType<AddStaticMeshActorOp>());
        Assert.True(state.GetBendValue(added.Created).IsNearly(bend));
        Assert.NotNull(added.OverrideMaterials);
        Assert.NotEmpty(added.OverrideMaterials);
    }

    [Fact]
    public async Task CopyToLevelKeepsTheLootAContainerStores()
    {
        // Review: an AI's copy of a stock locker into another level lost its loot (only a loot edit was carried), the Map's
        // Paste keeps what the locker stores; a plain mesh is decoration in both.
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        SyntheticLevels.WriteLockerLevel(game, "A_0_Locker");
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        await CallAsync(server, session, "open_source", new { path = game });
        await CallAsync(server, session, "create_project", new { folder = temp.Combine("projects"), name = "AI lockers" });

        await CallAsync(server, session, "copy_actor_to_level", new { sourceLevel = "A_0_Locker", actor = "Locker_1", targetLevel = "A_0_TestLevel" });
        await CallAsync(server, session, "copy_actor_to_level", new { sourceLevel = "A_0_TestLevel", actor = "StaticMeshActor_1", targetLevel = "A_0_Locker" });
        var added = host.Project!.State.AddedActors.Values.OfType<AddStaticMeshActorOp>().ToList();
        Assert.Equal([SyntheticLevels.LockerPreset], added.Single(a => a.Level == SyntheticLevels.LevelPath).Loot);
        var plain = added.Single(a => a.Level != SyntheticLevels.LevelPath).Loot;
        Assert.NotNull(plain);
        Assert.Empty(plain); // not lootable, as the Map's Paste writes it
    }

    private static async Task<string> CallAsync(McpServer server, McpSession session, string tool, object arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) },
        };
        var result = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), session))!)!["result"]!;
        var text = string.Join('\n', result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>()));
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        return text;
    }
}
