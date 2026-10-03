using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.App;

/// <summary>
/// The editor's built-in MCP server ("AI control"): the Settings card starts/stops it and copies client configurations,
/// tool calls reach the live pages (values refresh, toasts, undo), and the UI tools drive the main window (show levels,
/// select, navigate, window screenshot). Screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class AppMcpTests
{
    [Fact]
    public async Task SettingsCardTurnsTheServerOnAndOff()
    {
        var dialogs = new ScriptedDialogService();
        using var ctx = AppTestContext.Create(dialogs: dialogs);
        using var page = new SettingsPageViewModel(ctx.Services);
        var mcp = ctx.Services.Mcp;
        Assert.False(mcp.IsRunning);
        Assert.Equal("Off", page.McpStatus);

        page.McpPort = "80";
        Assert.NotNull(page.McpPortError);
        page.McpPort = FreePort().ToString(CultureInfo.InvariantCulture);
        Assert.Null(page.McpPortError);
        page.SaveMcpPortCommand.Execute(null);
        await page.McpApplyTask;
        Assert.False(mcp.IsRunning);

        page.McpEnabled = true;
        await page.McpApplyTask;
        Assert.True(mcp.IsRunning);
        Assert.True(page.McpRunning);
        var settings = ctx.Services.Settings.Load().Mcp;
        Assert.True(settings.Enabled);
        var token = settings.Token!;
        Assert.True(token.Length >= 32);
        Assert.StartsWith("Listening on http://127.0.0.1:" + page.McpPort + "/mcp", page.McpStatus, StringComparison.Ordinal);
        Assert.EndsWith(token[^4..], page.McpTokenMasked, StringComparison.Ordinal);
        Assert.DoesNotContain(token, page.McpCommandPreview, StringComparison.Ordinal);

        // The copy buttons put the real token on the clipboard.
        await page.CopyClaudeCodeCommandCommand.ExecuteAsync(null);
        Assert.Equal($"claude mcp add --transport http scumstudio {mcp.Url} --header \"Authorization: Bearer {token}\"", dialogs.Clipboard[^1]);
        await page.CopyClaudeDesktopConfigCommand.ExecuteAsync(null);
        var entry = JsonNode.Parse(dialogs.Clipboard[^1])!["mcpServers"]!["scumstudio"]!;
        Assert.Equal(["mcp", "--connect", mcp.Url], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(token, entry["env"]!["SCUMSTUDIO_MCP_TOKEN"]!.GetValue<string>());
        await page.CopyMcpTokenCommand.ExecuteAsync(null);
        Assert.Equal(token, dialogs.Clipboard[^1]);

        // A client with the token talks to the app host (no window attached here: no UI tools).
        using (var client = new McpTestClient(mcp.Url, token))
        {
            var init = await client.InitializeAsync();
            Assert.Equal("ScumStudio (app)", init["serverInfo"]!["title"]!.GetValue<string>());
            var tools = await client.ListToolsAsync();
            Assert.Contains("set_item_values", tools);
            Assert.DoesNotContain("screenshot", tools);
            var status = await client.CallAsync("get_status");
            Assert.Equal("app", status["structuredContent"]!["host"]!.GetValue<string>());
            Assert.Equal("No game files open: call open_source.", status["structuredContent"]!["hint"]!.GetValue<string>());
            Assert.Equal(1, mcp.CallCount);
            Assert.Contains("get_status", mcp.LastActivity, StringComparison.Ordinal);
            Assert.StartsWith("1 tool call(s)", page.McpActivity, StringComparison.Ordinal);

            // A new token locks the old one out; the client works again with the new one.
            page.NewMcpTokenCommand.Execute(null);
            await page.McpApplyTask;
            Assert.Equal(HttpStatusCode.Unauthorized, await client.PingStatusAsync());
            var renewed = ctx.Services.Settings.Load().Mcp.Token!;
            Assert.NotEqual(token, renewed);
            using var again = new McpTestClient(mcp.Url, renewed);
            await again.InitializeAsync();
        }

        page.McpEnabled = false;
        await page.McpApplyTask;
        Assert.False(mcp.IsRunning);
        Assert.Equal("Off", page.McpStatus);
        Assert.False(ctx.Services.Settings.Load().Mcp.Enabled);
    }

    [FixturesFact]
    public async Task AiEditsReachTheWeaponsPageAndUndo()
    {
        using var ctx = AppTestContext.Create();
        using var page = new WeaponsPageViewModel(ctx.Services);
        var mcp = ctx.Services.Mcp;
        mcp.PortOverride = 0;
        mcp.TokenOverride = "test-token";
        await mcp.ApplySettingsAsync();
        Assert.True(mcp.IsRunning);
        using var client = new McpTestClient(mcp.Url, "test-token");
        await client.InitializeAsync();

        var opened = await client.CallAsync("open_source", new { path = FixturePaths.OrigRoot });
        Assert.False(McpTestClient.IsError(opened), McpTestClient.Text(opened));
        Assert.NotNull(ctx.Services.Workspace.Catalog);
        await page.LoadCompletion;
        Assert.Contains(page.AllItems, i => i.Name == "Weapon_RPG7");

        var created = await client.CallAsync("create_project", new { folder = ctx.Combine("projects"), name = "AI guns" });
        Assert.False(McpTestClient.IsError(created), McpTestClient.Text(created));
        Assert.Equal("AI guns", ctx.Services.Projects.DisplayName);

        Assert.True(await page.SelectAsync("Weapon_RPG7"));
        var damage = page.FindRow("DamagePerShot")!;
        Assert.Equal("2.664", damage.CommittedValue);
        var values = await client.CallAsync("get_item_values", new { item = "Weapon_RPG7", filter = "damage" });
        var key = values["structuredContent"]!["values"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "DamagePerShot")!["key"]!.GetValue<string>();
        var set = await client.CallAsync("set_item_values", new { item = "Weapon_RPG7", values = new[] { new { key, value = "5" } } });
        Assert.False(McpTestClient.IsError(set), McpTestClient.Text(set));

        // The page shows the AI's value at once; the edit is a normal journal entry with a toast.
        Assert.Equal("5", damage.CommittedValue);
        Assert.True(damage.IsOverridden);
        Assert.True(ctx.Services.Projects.CanUndo);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title.StartsWith("AI: ", StringComparison.Ordinal));

        var undo = await client.CallAsync("undo");
        Assert.Contains("Undone", McpTestClient.Text(undo), StringComparison.Ordinal);
        Assert.Equal("2.664", damage.CommittedValue);
        Assert.False(ctx.Services.Projects.CanUndo);

        await mcp.StopAsync();
        Assert.False(mcp.IsRunning);
    }

    [AvaloniaMapSliceFact]
    public async Task AiDrivesTheMapPageAndTakesScreenshots()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            var mcp = ctx.Services.Mcp;
            mcp.AttachShell(vm, () => window);
            ctx.Services.UpdateSettings(s => s with { Mcp = s.Mcp with { Enabled = true, Port = FreePort(), Token = "ui-token-0123456789abcdef" } });
            await mcp.ApplySettingsAsync();
            HeadlessUi.Pump();
            Assert.Equal(PillState.Ok, vm.AiPill.State);
            Assert.Equal("on", vm.AiPill.Value);

            using var client = new McpTestClient(mcp.Url, "ui-token-0123456789abcdef");
            await client.InitializeAsync();
            var tools = await client.ListToolsAsync();
            Assert.Contains("screenshot", tools);
            Assert.Contains("show_levels", tools);

            Assert.False(McpTestClient.IsError(await client.CallAsync("open_source", new { path = AvaloniaMapSliceFactAttribute.Root })));
            Assert.False(McpTestClient.IsError(await client.CallAsync("create_project", new { folder = ctx.Combine("projects"), name = "AI outpost" })));

            var shown = await client.CallAsync("show_levels", new { levels = new[] { "A_0_Outpost_Exterior" } });
            Assert.False(McpTestClient.IsError(shown), McpTestClient.Text(shown));
            var map = Assert.IsType<MapPageViewModel>(vm.CurrentPage);
            Assert.Equal(510, map.AllActors.Count);
            Assert.Equal("A_0_Outpost_Exterior", map.LoadedLevelsCaption);

            var listed = await client.CallAsync("list_actors", new { level = "A_0_Outpost_Exterior", filter = "Barricade", limit = 5 });
            var actor = listed["structuredContent"]!["actors"]!.AsArray()[0]!["name"]!.GetValue<string>();
            var selected = await client.CallAsync("select_actor", new { level = "A_0_Outpost_Exterior", actor, frame = false });
            Assert.False(McpTestClient.IsError(selected), McpTestClient.Text(selected));
            Assert.Equal(actor, map.SelectedActor?.Name);

            var deleted = await client.CallAsync("delete_actors", new { level = "A_0_Outpost_Exterior", actors = new[] { actor } });
            Assert.False(McpTestClient.IsError(deleted), McpTestClient.Text(deleted));
            HeadlessUi.Pump();
            Assert.True(map.SelectedActor!.IsDeleted);
            Assert.Contains(map.SelectedActor.SelectableId, map.HiddenActorIds);

            var status = (await client.CallAsync("get_status"))["structuredContent"]!["ui"]!;
            Assert.Equal("map", status["page"]!.GetValue<string>());
            Assert.Equal("A_0_Outpost_Exterior", status["loadedLevels"]![0]!.GetValue<string>());
            Assert.Equal(actor, status["selectedActor"]!["actor"]!.GetValue<string>());

            // A window screenshot comes back as an MCP image block.
            var shot = await client.CallAsync("screenshot", new { target = "window" });
            Assert.False(McpTestClient.IsError(shot), McpTestClient.Text(shot));
            var image = shot["content"]!.AsArray().First(c => c!["type"]!.GetValue<string>() == "image")!;
            Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
            var png = Convert.FromBase64String(image["data"]!.GetValue<string>());
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
            SaveBytes(png, "mcp-window-map");
            HeadlessUi.Pump();
            Assert.EndsWith(" calls", vm.AiPill.Value, StringComparison.Ordinal);

            // Undo through MCP brings the actor back in the open page.
            Assert.Contains("Undone", McpTestClient.Text(await client.CallAsync("undo")), StringComparison.Ordinal);
            HeadlessUi.Pump();
            Assert.False(map.SelectedActor!.IsDeleted);

            // The Settings page reports the connection.
            Assert.False(McpTestClient.IsError(await client.CallAsync("navigate", new { page = "settings" })));
            var settings = Assert.IsType<SettingsPageViewModel>(vm.CurrentPage);
            HeadlessUi.Pump();
            Assert.True(settings.McpRunning);
            Assert.True(settings.McpEnabled);
            Assert.Contains("tool call(s)", settings.McpActivity, StringComparison.Ordinal);
            var card = HeadlessUi.FindNamed<Border>(window, "AiControlCard")!;
            card.BringIntoView();
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "settings-ai-control");
        }
        finally
        {
            await ctx.Services.Mcp.StopAsync();
            window.Close();
            vm.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void SaveBytes(byte[] png, string name)
    {
        if (HeadlessUi.ScreenshotDirectory is { } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), png);
        }
    }
}

/// <summary>A minimal Streamable-HTTP MCP client for tests (JSON responses, session header, bearer token).</summary>
internal sealed class McpTestClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _url;
    private readonly string _token;
    private string? _session;
    private int _id;

    public McpTestClient(string url, string token)
    {
        _url = url;
        _token = token;
    }

    public static bool IsError(JsonNode result) => result["isError"]?.GetValue<bool>() == true;

    public static string Text(JsonNode result) =>
        string.Join('\n', result["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));

    public async Task<JsonNode> InitializeAsync()
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "scumstudio-tests", version = "1.0" },
        });
        await PostAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return result;
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync() =>
        (await RequestAsync("tools/list", new { }))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

    public Task<JsonNode> CallAsync(string tool, object? arguments = null) =>
        RequestAsync("tools/call", new { name = tool, arguments = arguments ?? new { } });

    public async Task<HttpStatusCode> PingStatusAsync()
    {
        using var response = await PostAsync(new { jsonrpc = "2.0", id = ++_id, method = "ping" });
        return response.StatusCode;
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode> RequestAsync(string method, object parameters)
    {
        using var response = await PostAsync(new { jsonrpc = "2.0", id = ++_id, method, @params = parameters });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        {
            _session = ids.First();
        }

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(body["error"] is null, body["error"]?.ToJsonString());
        return body["result"]!;
    }

    private async Task<HttpResponseMessage> PostAsync(object message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (_session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", _session);
        }

        return await _http.SendAsync(request);
    }
}
