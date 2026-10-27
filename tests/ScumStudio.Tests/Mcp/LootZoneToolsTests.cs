using System.Text.Json.Nodes;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Studio;
using ScumStudio.Modding.ServerMods;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Mcp;

/// <summary>The loot zone tools: a preset's groups, chance and counts changed inside a rectangle, written to the server's loot customization.</summary>
public sealed class LootZoneToolsTests
{
    private static readonly string[] Names = ["list_loot_tags", "read_loot_zones", "write_loot_zone", "remove_loot_zone"];

    private static async Task<JsonNode> Call(McpServer server, McpSession session, string tool, object arguments, bool expectError = false)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) },
        };
        var result = JsonNode.Parse((await server.HandleMessageAsync(request.ToJsonString(), session))!)!["result"]!;
        Assert.Equal(expectError, result["isError"]!.GetValue<bool>());
        return result;
    }

    [Fact]
    public void TheToolsAreListedAndNamedInTheSkillAndTheInstructions()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        foreach (var name in Names)
        {
            Assert.Single(server.Tools, t => t.Name == name);
            Assert.Contains("`" + name, StudioSkill.Text, StringComparison.Ordinal);
            Assert.Contains(name, StudioTools.Instructions, StringComparison.Ordinal);
        }

        Assert.Equal(["serverFolder", "zone", "preset", "probability", "groups"], Assert.Single(server.Tools, t => t.Name == "write_loot_zone").InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task AZoneIsWrittenReadAndRemovedAndABadPresetIsRefused()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();
        var win64 = temp.Combine("srv", "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), string.Empty);
        var paks = temp.Combine("srv", "SCUM", "Content", "Paks");
        var store = new LootOverrideStore(ServerInstall.Find(win64)!);

        var overview = (await Call(server, session, "read_loot_zones", new { serverFolder = paks }))["structuredContent"]!;
        Assert.False(overview["gameDefaultsExported"]!.GetValue<bool>());
        Assert.Empty(overview["zones"]!.AsArray());

        var written = (await Call(server, session, "write_loot_zone", new
        {
            serverFolder = paks,
            zone = "Dungeon",
            cornerA = new[] { 452300.0, -238600.0, 0.0 },
            cornerB = new[] { 460300.0, -230600.0, 0.0 },
            preset = "Street-Military-Examine_Crate",
            probability = 100,
            quantityMin = 1,
            quantityMax = 3,
            groups = new object[]
            {
                new { rarity = "Common", entries = new[] { "ItemLootTreeNodes.Workshop.Other.FoodAndDrink" } },
                new { rarity = "Rare", entries = new[] { "ItemLootTreeNodes.Military.Weapons.Rifles", "KeyCard" } },
            },
        }))["structuredContent"]!;
        Assert.EndsWith("Street-Military-Examine_Crate.json", written["written"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("#ReloadLootCustomizationsAndResetSpawners", written["next"]!.GetValue<string>(), StringComparison.Ordinal);

        var file = store.ReadOverride("Dungeon", "Street-Military-Examine_Crate")!;
        Assert.Equal((100d, 1, 3, false), (file.Probability, file.QuantityMin, file.QuantityMax, file.FilterByZone));
        Assert.Equal(3, file.Groups.Count);
        Assert.Equal(new LootZone(460300, -230600, 452300, -238600), store.ReadZone("Dungeon"));

        var zone = (await Call(server, session, "read_loot_zones", new { serverFolder = paks, zone = "Dungeon", preset = "Street-Military-Examine_Crate" }))["structuredContent"]!;
        Assert.Equal(100, zone["probability"]!.GetValue<double>());

        // A rarity the game does not have, a missing rectangle and an unknown zone are refused with a reason.
        await Call(server, session, "write_loot_zone", new { serverFolder = paks, zone = "Dungeon", preset = "Street-Military-Examine_Crate", probability = 100, groups = new object[] { new { rarity = "Mythic", entries = new[] { "KeyCard" } } } }, expectError: true);
        await Call(server, session, "write_loot_zone", new { serverFolder = paks, zone = "Other", preset = "A-B", probability = 100, groups = new object[] { new { rarity = "Common", entries = new[] { "KeyCard" } } } }, expectError: true);
        await Call(server, session, "remove_loot_zone", new { serverFolder = paks, zone = "Nope" }, expectError: true);

        await Call(server, session, "remove_loot_zone", new { serverFolder = paks, zone = "Dungeon" });
        Assert.Empty(store.Zones());
    }
}
