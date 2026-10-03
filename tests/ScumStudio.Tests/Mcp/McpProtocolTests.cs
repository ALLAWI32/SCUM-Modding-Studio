using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Transports;

namespace ScumStudio.Tests.Mcp;

/// <summary>
/// MCP protocol core and transports: initialize/version negotiation, tools/list, tools/call (results, tool errors,
/// protocol errors), notifications, batches, stdio framing, the Streamable HTTP endpoint (sessions, token, Host/Origin
/// checks, CORS) and the stdio→HTTP bridge.
/// </summary>
public sealed class McpProtocolTests
{
    private static McpServer CreateServer()
    {
        var echo = new McpTool("echo", "Echoes 'text'.", ToolSchema.Object().String("text", "Text.", required: true).Integer("times", "Repeat count.").Build(),
            (call, _) => Task.FromResult(McpToolResult.Text(string.Concat(Enumerable.Repeat(call.RequireString("text"), call.GetInt("times", 1))))))
        { ReadOnly = true, Title = "Echo" };
        var json = new McpTool("info", "Structured result.", ToolSchema.Object().Vector("at", "A point.").Build(),
            (call, _) => Task.FromResult(McpToolResult.Json(new { at = call.GetVector3("at") is { } v ? new[] { v.X, v.Y, v.Z } : null, ok = true })));
        var image = new McpTool("pixel", "A PNG.", ToolSchema.Object().Build(),
            (_, _) => Task.FromResult(new McpToolResult().AddImage([0x89, 0x50, 0x4E, 0x47]).AddText("1 pixel")));
        var boom = new McpTool("boom", "Fails.", ToolSchema.Object().Build(), (_, _) => throw new InvalidOperationException("It broke."));
        return new McpServer(new McpServerInfo { Name = "test", Title = "Test", Version = "1.2.3", Instructions = "Be nice." }, [echo, json, image, boom]);
    }

    private static async Task<JsonNode> Send(McpServer server, McpSession session, string json) =>
        JsonNode.Parse((await server.HandleMessageAsync(json, session))!)!;

    [Fact]
    public async Task InitializeNegotiatesTheVersionAndDescribesTheServer()
    {
        var server = CreateServer();
        var session = new McpSession();
        var reply = await Send(server, session, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1"}}}""");
        Assert.Equal(1, reply["id"]!.GetValue<int>());
        var result = reply["result"]!;
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("test", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", result["serverInfo"]!["version"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("Be nice.", result["instructions"]!.GetValue<string>());
        Assert.Equal("claude-code", session.ClientName);

        var newer = await Send(server, new McpSession(), """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal(McpServer.SupportedProtocolVersions[0], newer["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("a", newer["id"]!.GetValue<string>());

        Assert.Null(await server.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session));
        Assert.True(session.Initialized);
        var ping = await Send(server, session, """{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Assert.Empty(ping["result"]!.AsObject());
    }

    [Fact]
    public async Task ToolsAreListedWithSchemasAndAnnotations()
    {
        var reply = await Send(CreateServer(), new McpSession(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = reply["result"]!["tools"]!.AsArray();
        Assert.Equal(["echo", "info", "pixel", "boom"], tools.Select(t => t!["name"]!.GetValue<string>()));
        var echo = tools[0]!;
        Assert.Equal("object", echo["inputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal("string", echo["inputSchema"]!["properties"]!["text"]!["type"]!.GetValue<string>());
        Assert.Equal("text", echo["inputSchema"]!["required"]![0]!.GetValue<string>());
        Assert.True(echo["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Equal("Echo", echo["title"]!.GetValue<string>());
        var info = tools[1]!;
        Assert.Equal(3, info["inputSchema"]!["properties"]!["at"]!["minItems"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolCallsReturnContentErrorsAndStructuredResults()
    {
        var server = CreateServer();
        var session = new McpSession();
        var calls = new List<McpToolCalled>();
        server.ToolCalled += (_, c) => calls.Add(c);

        var echo = await Send(server, session, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"ab","times":3}}}""");
        Assert.False(echo["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("ababab", echo["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var info = await Send(server, session, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"info","arguments":{"at":[1,2,3]}}}""");
        Assert.True(info["result"]!["structuredContent"]!["ok"]!.GetValue<bool>());
        Assert.Equal(2f, info["result"]!["structuredContent"]!["at"]![1]!.GetValue<float>());
        Assert.Contains("\"ok\": true", info["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var image = await Send(server, session, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"pixel"}}""");
        Assert.Equal("image", image["result"]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("iVBORw==", image["result"]!["content"]![0]!["data"]!.GetValue<string>());
        Assert.Equal("image/png", image["result"]!["content"]![0]!["mimeType"]!.GetValue<string>());

        // Missing argument and a failing handler are tool errors (the AI can correct itself), not protocol errors.
        var missing = await Send(server, session, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("'text' is required", missing["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var boom = await Send(server, session, """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"boom"}}""");
        Assert.True(boom["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("It broke.", boom["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(boom["result"]!["structuredContent"]);

        // Unknown tools and methods are protocol errors.
        var unknown = await Send(server, session, """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"nope"}}""");
        Assert.Equal(McpServer.ErrorCodes.InvalidParams, unknown["error"]!["code"]!.GetValue<int>());
        var method = await Send(server, session, """{"jsonrpc":"2.0","id":9,"method":"sampling/createMessage"}""");
        Assert.Equal(McpServer.ErrorCodes.MethodNotFound, method["error"]!["code"]!.GetValue<int>());
        var parse = await Send(server, session, "{not json");
        Assert.Equal(McpServer.ErrorCodes.ParseError, parse["error"]!["code"]!.GetValue<int>());
        var invalid = await Send(server, session, """{"jsonrpc":"1.0","id":10,"method":"ping"}""");
        Assert.Equal(McpServer.ErrorCodes.InvalidRequest, invalid["error"]!["code"]!.GetValue<int>());

        Assert.Equal(5, server.CallCount);
        Assert.Equal(5, calls.Count);
        Assert.Contains(calls, c => c.Tool == "boom" && c.IsError && c.Summary == "It broke.");

        var batch = JsonNode.Parse((await server.HandleMessageAsync("""[{"jsonrpc":"2.0","id":11,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":12,"method":"tools/list"}]""", session))!)!.AsArray();
        Assert.Equal(2, batch.Count);
        var empty = await Send(server, session, """{"jsonrpc":"2.0","id":13,"method":"resources/list"}""");
        Assert.Empty(empty["result"]!["resources"]!.AsArray());
    }

    [Fact]
    public async Task StdioTransportFramesOneMessagePerLine()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"صقر"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""") + "\n"));
        var output = new MemoryStream();
        await McpStdioTransport.RunAsync(CreateServer(), input, output);
        var text = new UTF8Encoding(false).GetString(output.ToArray());
        Assert.False(text.StartsWith('﻿'));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var byId = lines.Select(l => JsonNode.Parse(l)!).ToDictionary(n => n["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", byId[1]["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("صقر", byId[2]["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(byId[3]["result"]);
    }

    [Fact]
    public async Task HttpEndpointHandlesSessionsTokenAndLocalOnlyChecks()
    {
        await using var http = new McpHttpServer(CreateServer(), new McpHttpServerOptions { Port = 0, Token = "secret-token" });
        http.Start();
        Assert.True(http.Port > 0);
        Assert.StartsWith("http://127.0.0.1:", http.Url, StringComparison.Ordinal);
        using var client = new HttpClient();

        HttpRequestMessage Post(string json, string? token = "secret-token", string? session = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (session is not null)
            {
                request.Headers.Add("Mcp-Session-Id", session);
            }

            return request;
        }

        const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"t","version":"1"}}}""";
        using (var noToken = await client.SendAsync(Post(Init, token: null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        }

        using (var wrongToken = await client.SendAsync(Post(Init, token: "nope")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        }

        string session;
        using (var init = await client.SendAsync(Post(Init)))
        {
            Assert.Equal(HttpStatusCode.OK, init.StatusCode);
            Assert.Equal("application/json", init.Content.Headers.ContentType?.MediaType);
            session = Assert.Single(init.Headers.GetValues("Mcp-Session-Id"));
            var body = JsonNode.Parse(await init.Content.ReadAsStringAsync())!;
            Assert.Equal("2025-06-18", body["result"]!["protocolVersion"]!.GetValue<string>());
        }

        using (var notification = await client.SendAsync(Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session: session)))
        {
            Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
        }

        using (var call = await client.SendAsync(Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""", session: session)))
        {
            Assert.Equal(HttpStatusCode.OK, call.StatusCode);
            Assert.Equal("hi", JsonNode.Parse(await call.Content.ReadAsStringAsync())!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }

        Assert.Contains(http.Sessions, s => s.Id == session && s.Initialized);
        using (var unknown = await client.SendAsync(Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", session: "deadbeef")))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var noSession = await client.SendAsync(Post("""{"jsonrpc":"2.0","id":4,"method":"ping"}""")))
        {
            Assert.Equal(HttpStatusCode.OK, noSession.StatusCode); // lenient: a shared session
        }

        var get = new HttpRequestMessage(HttpMethod.Get, http.Url);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        using (var getResponse = await client.SendAsync(get))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, getResponse.StatusCode);
        }

        // DNS rebinding / browser protection.
        var rebound = Post("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");
        rebound.Headers.Host = "evil.example:" + http.Port;
        using (var reboundResponse = await client.SendAsync(rebound))
        {
            Assert.Equal(HttpStatusCode.Forbidden, reboundResponse.StatusCode);
        }

        var foreign = Post("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");
        foreign.Headers.Add("Origin", "https://evil.example");
        using (var foreignResponse = await client.SendAsync(foreign))
        {
            Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
        }

        var inspector = Post("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        inspector.Headers.Add("Origin", "http://localhost:6274");
        using (var inspectorResponse = await client.SendAsync(inspector))
        {
            Assert.Equal(HttpStatusCode.OK, inspectorResponse.StatusCode);
            Assert.Equal("http://localhost:6274", Assert.Single(inspectorResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        var preflight = new HttpRequestMessage(HttpMethod.Options, http.Url);
        preflight.Headers.Add("Origin", "http://localhost:6274");
        using (var preflightResponse = await client.SendAsync(preflight))
        {
            Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
            Assert.Contains("Mcp-Session-Id", string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.Ordinal);
        }

        var unsupported = Post("""{"jsonrpc":"2.0","id":8,"method":"ping"}""", session: session);
        unsupported.Headers.Add("MCP-Protocol-Version", "1999-01-01");
        using (var unsupportedResponse = await client.SendAsync(unsupported))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedResponse.StatusCode);
        }

        var delete = new HttpRequestMessage(HttpMethod.Delete, http.Url);
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        delete.Headers.Add("Mcp-Session-Id", session);
        using (var deleted = await client.SendAsync(delete))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        using (var afterDelete = await client.SendAsync(Post("""{"jsonrpc":"2.0","id":9,"method":"ping"}""", session: session)))
        {
            Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        }

        await http.StopAsync();
        Assert.False(http.IsRunning);
    }

    [Fact]
    public async Task BridgeForwardsStdioToTheHttpEndpointAndExplainsWhenTheAppIsDown()
    {
        await using var http = new McpHttpServer(CreateServer(), new McpHttpServerOptions { Port = 0, Token = "t0k3n" });
        http.Start();
        using var bridge = new McpHttpBridge(new Uri(http.Url), "t0k3n");
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"via bridge"}}}""") + "\n"));
        var output = new MemoryStream();
        await bridge.RunAsync(input, output);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("via bridge", lines.Single(l => l["id"]!.GetValue<int>() == 2)["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(http.Sessions); // the bridge ends its session when stdin closes

        var url = http.Url;
        await http.StopAsync();
        using var offline = new McpHttpBridge(new Uri(url), "t0k3n");
        var replies = new List<string>();
        await offline.ForwardAsync("""{"jsonrpc":"2.0","id":"x","method":"tools/list"}""", line => { replies.Add(line); return Task.CompletedTask; });
        var error = JsonNode.Parse(Assert.Single(replies))!;
        Assert.Equal("x", error["id"]!.GetValue<string>());
        Assert.Contains("not reachable", error["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
