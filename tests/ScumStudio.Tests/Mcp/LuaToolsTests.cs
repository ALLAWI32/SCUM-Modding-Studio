using System.Text.Json.Nodes;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Skill;
using ScumStudio.Mcp.Studio;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Mcp;

/// <summary>The Lua tools: the catalog, the game function list, the check and writing a mod to a server folder.</summary>
public sealed class LuaToolsTests
{
    private static readonly string[] Names = ["lua_catalog", "lua_game_functions", "check_lua", "write_lua_mod"];

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
    public void TheLuaToolsAreListedAndNamedInTheSkillAndTheInstructions()
    {
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        foreach (var name in Names)
        {
            Assert.Single(server.Tools, t => t.Name == name);
            Assert.Contains("`" + name, StudioSkill.Text, StringComparison.Ordinal);
            Assert.Contains(name, StudioTools.Instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheCatalogIsSearchedAndAScriptIsCheckedAndWrittenOnlyWithoutErrors()
    {
        using var temp = new LevelTempDirectory();
        using var host = new HeadlessStudioHost();
        var server = StudioTools.CreateServer(host);
        var session = new McpSession();

        var found = (await Call(server, session, "lua_catalog", new { filter = "nobody", group = "Messages" }))["structuredContent"]!;
        Assert.Equal("SendHUDMessageToClient", found["entries"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tested", found["entries"]![0]!["status"]!.GetValue<string>());

        var win64 = temp.Combine("srv", "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(Path.Combine(win64, "ue4ss"));
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), string.Empty);
        File.WriteAllLines(Path.Combine(win64, "ue4ss", "scum_dump.txt"), ["Function /Script/SCUM.Item:DropAround"]);
        var paks = temp.Combine("srv", "SCUM", "Content", "Paks");

        var functions = (await Call(server, session, "lua_game_functions", new { serverFolder = paks, filter = "drop" }))["structuredContent"]!;
        Assert.Equal(1, functions["total"]!.GetValue<int>());

        var bad = (await Call(server, session, "check_lua", new { code = "RegisterHook(\"/Script/SCUM.Item:Nope\", function() end", serverFolder = paks }))["structuredContent"]!;
        Assert.False(bad["ok"]!.GetValue<bool>());

        var refused = (await Call(server, session, "write_lua_mod", new { serverFolder = paks, name = "Bad", code = "if then" }))["structuredContent"]!;
        Assert.False(refused["written"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(win64, "ue4ss", "Mods", "Bad", "Scripts", "main.lua")));

        var written = (await Call(server, session, "write_lua_mod", new { serverFolder = paks, name = "Good", code = "print('ok')\n" }))["structuredContent"]!;
        Assert.True(written["written"]!.GetValue<bool>());
        Assert.Contains("Good : 1", File.ReadAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt")), StringComparison.Ordinal);
        await Call(server, session, "write_lua_mod", new { serverFolder = paks, name = "bad name", code = "print(1)" }, expectError: true);
    }
}
