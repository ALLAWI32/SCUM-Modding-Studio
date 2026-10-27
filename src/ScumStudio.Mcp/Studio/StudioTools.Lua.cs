using ScumStudio.Mcp.Protocol;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Lua tools for an AI: the catalog of what a server Lua mod can call (with what was really tried on the owner's server), the
/// game's own function list from the class dump, a script check and "write to the server" (the Lua editor window's functions).
/// </summary>
public sealed partial class StudioTools
{
    private const int LuaGameMost = 200;

    private IEnumerable<McpTool> LuaTools()
    {
        yield return new McpTool("lua_catalog",
            "The catalog of what a server Lua mod (UE4SS inside SCUM Server) can use: functions, events (hooks), ready pieces and rules, each " +
            "with a status (tested = worked on the owner's server, seen = exists but effect not tried, unsafe = does not work or crashes), " +
            "a description and an example. READ IT BEFORE WRITING a server script: it lists what crashed the server before.",
            ToolSchema.Object()
                .String("filter", "Words that must all appear in the name, call or description (e.g. 'private message').")
                .String("group", "One group.", choices: LuaApiCatalog.Groups)
                .Build(), LuaCatalog)
        { Title = "Lua catalog", ReadOnly = true, Idempotent = true };

        yield return new McpTool("lua_game_functions",
            "Searches the game's own functions (/Script/SCUM.Class:Function) in the server's class dump (ue4ss/scum_dump.txt, written once by the " +
            "ScumStudioDump mod). Names only: no arguments. At most 200 are returned.",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server (its Paks folder, SCUM, Win64 or the install folder).", required: true)
                .String("filter", "Words that must all appear in the path (e.g. 'HUDMessage').")
                .Build(), LuaGameFunctions)
        { Title = "Game functions", ReadOnly = true, Idempotent = true, OpenWorld = true };

        yield return new McpTool("check_lua",
            "Checks a server Lua script without running it: syntax errors (with the line), game functions that are not in the server's class dump " +
            "(when 'serverFolder' is given) and the mistakes that crashed or froze the owner's server before.",
            ToolSchema.Object()
                .String("code", "The script (main.lua text).", required: true)
                .String("serverFolder", "A folder of the dedicated server, to check the game functions against its class dump.")
                .Build(), CheckLua)
        { Title = "Check Lua", ReadOnly = true, Idempotent = true, OpenWorld = true };

        yield return new McpTool("write_lua_mod",
            "Checks a script and, when it has no errors, writes it as the server Lua mod <name> (Mods/<name>/Scripts/main.lua + mods.txt/mods.json) " +
            "and switches it on. A running server reloads a changed mod by itself; a new mod needs one restart.",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server (its Paks folder, SCUM, Win64 or the install folder).", required: true)
                .String("name", "Mod name: letters, digits, _ and - (at most 40).", required: true)
                .String("code", "The script (main.lua text).", required: true)
                .Build(), WriteLuaMod)
        { Title = "Write Lua mod", OpenWorld = true };
    }

    private static ServerInstall RequireServer(ToolCall call) =>
        ServerInstall.Find(call.RequireString("serverFolder"))
        ?? throw new ToolArgumentException($"No SCUMServer.exe found at or above {call.GetString("serverFolder")}.");

    private static HashSet<string>? ReadServerDump(ServerInstall server)
    {
        var dump = Path.Combine(server.Ue4ssFolder, "scum_dump.txt");
        return File.Exists(dump) ? LuaCheck.ReadDump(File.ReadLines(dump)) : null;
    }

    private Task<McpToolResult> LuaCatalog(ToolCall call, CancellationToken cancellationToken)
    {
        var tokens = Tokens(call.GetString("filter")).ToList();
        var group = call.GetString("group");
        var entries = LuaApiCatalog.All
            .Where(e => string.IsNullOrEmpty(group) || string.Equals(e.Group, group, StringComparison.OrdinalIgnoreCase))
            .Where(e => MatchesAll(tokens, e.Name, e.Signature, e.Text))
            .Select(e => new
            {
                id = e.Id,
                group = e.Group,
                kind = e.Kind.ToString().ToLowerInvariant(),
                name = e.Name,
                call = e.Signature,
                status = e.Status.ToString().ToLowerInvariant(),
                description = e.Text,
                example = e.Snippet,
            }).ToList();
        return Task.FromResult(McpToolResult.Json(new { total = entries.Count, entries }));
    }

    private Task<McpToolResult> LuaGameFunctions(ToolCall call, CancellationToken cancellationToken)
    {
        var server = RequireServer(call);
        var known = ReadServerDump(server)
            ?? throw new ToolArgumentException("The server has no class dump yet (ue4ss/scum_dump.txt): switch the ScumStudioDump mod on once while the server is empty.");
        var tokens = Tokens(call.GetString("filter")).ToList();
        var found = known.Where(path => MatchesAll(tokens, path)).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(McpToolResult.Json(new { total = found.Count, shown = Math.Min(found.Count, LuaGameMost), functions = found.Take(LuaGameMost) }));
    }

    private Task<McpToolResult> CheckLua(ToolCall call, CancellationToken cancellationToken)
    {
        var known = call.GetString("serverFolder") is { Length: > 0 } ? ReadServerDump(RequireServer(call)) : null;
        var findings = LuaCheck.Run(call.RequireString("code"), known);
        return Task.FromResult(McpToolResult.Json(new
        {
            ok = findings.All(f => f.Level != LuaFindingLevel.Error),
            gameFunctionsChecked = known is not null,
            findings = findings.Select(f => new { level = f.Level.ToString().ToLowerInvariant(), line = f.Line, code = f.Code, detail = f.Detail }),
        }));
    }

    private Task<McpToolResult> WriteLuaMod(ToolCall call, CancellationToken cancellationToken)
    {
        var server = RequireServer(call);
        var name = call.RequireString("name");
        if (!ServerModStore.IsValidName(name))
        {
            throw new ToolArgumentException("'name' is letters, digits, _ and - (at most 40).");
        }

        var findings = LuaCheck.Run(call.RequireString("code"), ReadServerDump(server));
        if (findings.Any(f => f.Level == LuaFindingLevel.Error))
        {
            return Task.FromResult(McpToolResult.Json(new
            {
                written = false,
                findings = findings.Select(f => new { level = f.Level.ToString().ToLowerInvariant(), line = f.Line, code = f.Code, detail = f.Detail }),
            }));
        }

        var mod = new ServerModStore(server).Save(name, call.RequireString("code"));
        return Task.FromResult(McpToolResult.Json(new
        {
            written = true,
            path = mod.ScriptPath,
            warnings = findings.Select(f => new { line = f.Line, code = f.Code, detail = f.Detail }),
            note = "A running server reloads a changed mod by itself; a new mod needs one restart.",
        }));
    }
}
