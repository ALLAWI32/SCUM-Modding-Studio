using System.Text.Json.Nodes;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Studio;

namespace ScumStudio.Tests.Mcp;

/// <summary>The loot tools: listed, named in the skill and the instructions, and refused politely without a project.</summary>
public sealed class LootToolsTests
{
    private static readonly string[] Names = ["list_loot_presets", "set_loot"];

    [Fact]
    public void TheLootToolsAreListedAndNamedInTheSkillAndTheInstructions()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        foreach (var name in Names)
        {
            Assert.Single(server.Tools, t => t.Name == name);
            Assert.Contains("`" + name, StudioSkill.Text, StringComparison.Ordinal);
            Assert.Contains(name, StudioTools.Instructions, StringComparison.Ordinal);
        }

        Assert.True(Assert.Single(server.Tools, t => t.Name == "list_loot_presets").ReadOnly);
        Assert.Equal(["level", "actor", "presets"], Assert.Single(server.Tools, t => t.Name == "set_loot").InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task WithoutAProjectSetLootSaysWhy()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "set_loot", ["arguments"] = new JsonObject { ["level"] = "X", ["actor"] = "Y", ["presets"] = new JsonArray() } },
        };
        var result = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), new McpSession()))!)!["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("project", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}
