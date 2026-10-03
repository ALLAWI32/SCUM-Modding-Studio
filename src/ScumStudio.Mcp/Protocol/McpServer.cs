using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScumStudio.Mcp.Protocol;

/// <summary>JSON settings of the MCP server (camelCase, enums as strings, readable non-ASCII text).</summary>
public static class McpJson
{
    /// <summary>Single-line JSON (wire format).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Indented JSON (tool results shown to the AI).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Who the server is (the <c>serverInfo</c> of <c>initialize</c>) and what it tells the AI.</summary>
public sealed record McpServerInfo
{
    /// <summary>Programmatic name.</summary>
    public string Name { get; init; } = "scumstudio";

    /// <summary>Display name.</summary>
    public string Title { get; init; } = "ScumStudio";

    /// <summary>Version.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>Usage instructions for the AI (the <c>instructions</c> field of the initialize result).</summary>
    public string? Instructions { get; init; }
}

/// <summary>One client connection's state (stdio process, or an HTTP <c>Mcp-Session-Id</c>).</summary>
public sealed class McpSession
{
    /// <summary>Session id (sent as <c>Mcp-Session-Id</c> over HTTP).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Negotiated protocol version, after <c>initialize</c>.</summary>
    public string? ProtocolVersion { get; internal set; }

    /// <summary>Client name from <c>clientInfo</c>.</summary>
    public string? ClientName { get; internal set; }

    /// <summary>Client version from <c>clientInfo</c>.</summary>
    public string? ClientVersion { get; internal set; }

    /// <summary>True after <c>notifications/initialized</c>.</summary>
    public bool Initialized { get; internal set; }

    /// <summary>Last message time.</summary>
    public DateTimeOffset LastSeen { get; internal set; } = DateTimeOffset.UtcNow;
}

/// <summary>Raised after every tool call (activity log of the app).</summary>
/// <param name="Session">Calling session.</param>
/// <param name="Tool">Tool name.</param>
/// <param name="Arguments">Arguments as JSON text.</param>
/// <param name="IsError">True when the tool reported an error.</param>
/// <param name="Summary">The result's <see cref="McpToolResult.Summary"/>, else its compact JSON or first text line (≤ 200 characters).</param>
/// <param name="Duration">Run time.</param>
public sealed record McpToolCalled(McpSession Session, string Tool, string Arguments, bool IsError, string Summary, TimeSpan Duration);

/// <summary>
/// Model Context Protocol server core: JSON-RPC 2.0 dispatch of <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c> and the notifications, independent of the transport (stdio or Streamable HTTP). Tool calls run one at
/// a time (the studio's state is not built for concurrent edits); <c>ping</c> and cancellation are answered meanwhile.
/// Implements the tool subset of MCP revisions 2024-11-05 to 2025-11-25.
/// </summary>
public sealed class McpServer
{
    /// <summary>Protocol revisions this server speaks, newest first.</summary>
    public static IReadOnlyList<string> SupportedProtocolVersions { get; } = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>JSON-RPC error codes.</summary>
    public static class ErrorCodes
    {
        /// <summary>Invalid JSON.</summary>
        public const int ParseError = -32700;

        /// <summary>Not a valid request object.</summary>
        public const int InvalidRequest = -32600;

        /// <summary>Unknown method.</summary>
        public const int MethodNotFound = -32601;

        /// <summary>Invalid parameters (also: unknown tool).</summary>
        public const int InvalidParams = -32602;

        /// <summary>Internal error.</summary>
        public const int InternalError = -32603;
    }

    private readonly Dictionary<string, McpTool> _tools;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private int _callCount;

    /// <summary>Creates the server.</summary>
    /// <exception cref="ArgumentException">Two tools share a name.</exception>
    public McpServer(McpServerInfo info, IEnumerable<McpTool> tools, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(tools);
        Info = info;
        _tools = new Dictionary<string, McpTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new ArgumentException($"Two tools are called '{tool.Name}'.", nameof(tools));
            }
        }

        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Server identity and instructions.</summary>
    public McpServerInfo Info { get; }

    /// <summary>Registered tools, in registration order.</summary>
    public IReadOnlyCollection<McpTool> Tools => _tools.Values;

    /// <summary>Number of tool calls handled.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Raised after each tool call (on the calling thread).</summary>
    public event EventHandler<McpToolCalled>? ToolCalled;

    /// <summary>
    /// Handles one wire message (a JSON-RPC object, or a batch array) and returns the response text, or null when nothing
    /// is to be sent back (notifications, responses).
    /// </summary>
    public async Task<string?> HandleMessageAsync(string message, McpSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(session);
        session.LastSeen = DateTimeOffset.UtcNow;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException ex)
        {
            return Error(null, ErrorCodes.ParseError, "Parse error: " + ex.Message).ToJsonString(McpJson.Compact);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var responses = new JsonArray();
                foreach (var item in root.EnumerateArray())
                {
                    if (await HandleAsync(item, session, cancellationToken).ConfigureAwait(false) is { } response)
                    {
                        responses.Add(response);
                    }
                }

                return responses.Count == 0 ? null : responses.ToJsonString(McpJson.Compact);
            }

            return (await HandleAsync(root, session, cancellationToken).ConfigureAwait(false))?.ToJsonString(McpJson.Compact);
        }
    }

    /// <summary>True when <paramref name="message"/> is an <c>initialize</c> request (HTTP transport: starts a session).</summary>
    public static bool IsInitializeRequest(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "initialize";

    /// <summary>Handles one parsed message.</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message, McpSession session, CancellationToken cancellationToken = default)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return Error(null, ErrorCodes.InvalidRequest, "A JSON-RPC message must be an object.");
        }

        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;
        if (!message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
        {
            return hasId ? Error(id, ErrorCodes.InvalidRequest, "jsonrpc must be \"2.0\".") : null;
        }

        if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            // A response to a server request (this server sends none) or garbage: nothing to answer.
            return null;
        }

        var method = methodElement.GetString()!;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        try
        {
            switch (method)
            {
                case "initialize":
                    return hasId ? Result(id, Initialize(parameters, session)) : null;
                case "notifications/initialized":
                    session.Initialized = true;
                    return null;
                case "notifications/cancelled":
                    Cancel(session, parameters);
                    return null;
                case "ping":
                    return hasId ? Result(id, new JsonObject()) : null;
                case "tools/list":
                    return hasId ? Result(id, ListTools()) : null;
                case "tools/call":
                    if (!hasId)
                    {
                        return null;
                    }

                    var (result, error) = await CallToolAsync(parameters, session, idElement.GetRawText(), cancellationToken).ConfigureAwait(false);
                    return error ?? Result(id, result!);
                case "resources/list":
                    return hasId ? Result(id, new JsonObject { ["resources"] = new JsonArray() }) : null;
                case "resources/templates/list":
                    return hasId ? Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }) : null;
                case "prompts/list":
                    return hasId ? Result(id, new JsonObject { ["prompts"] = new JsonArray() }) : null;
                case "logging/setLevel":
                    return hasId ? Result(id, new JsonObject()) : null;
                default:
                    return hasId ? Error(id, ErrorCodes.MethodNotFound, $"Method not found: {method}") : null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "MCP {Method} failed.", method);
            return hasId ? Error(id, ErrorCodes.InternalError, ex.Message) : null;
        }
    }

    /// <summary>Picks the protocol version: the client's when supported, else the newest this server speaks.</summary>
    public static string NegotiateVersion(string? requested) =>
        requested is not null && SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal) ? requested : SupportedProtocolVersions[0];

    private JsonObject Initialize(JsonElement parameters, McpSession session)
    {
        string? requested = null;
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String)
            {
                requested = v.GetString();
            }

            if (parameters.TryGetProperty("clientInfo", out var client) && client.ValueKind == JsonValueKind.Object)
            {
                session.ClientName = client.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                session.ClientVersion = client.TryGetProperty("version", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
            }
        }

        session.ProtocolVersion = NegotiateVersion(requested);
        _logger.LogInformation("MCP client {Client} {Version} connected (protocol {Protocol}).", session.ClientName ?? "?", session.ClientVersion ?? string.Empty, session.ProtocolVersion);
        var result = new JsonObject
        {
            ["protocolVersion"] = session.ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = Info.Name,
                ["title"] = Info.Title,
                ["version"] = Info.Version,
            },
        };
        if (!string.IsNullOrWhiteSpace(Info.Instructions))
        {
            result["instructions"] = Info.Instructions;
        }

        return result;
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            tools.Add(tool.ToListEntry());
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<(JsonObject? Result, JsonObject? Error)> CallToolAsync(JsonElement parameters, McpSession session, string rawId, CancellationToken cancellationToken)
    {
        var id = JsonNode.Parse(rawId);
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return (null, Error(id, ErrorCodes.InvalidParams, "tools/call needs a tool name."));
        }

        var name = nameElement.GetString()!;
        if (!_tools.TryGetValue(name, out var tool))
        {
            return (null, Error(id, ErrorCodes.InvalidParams, $"Unknown tool: {name}"));
        }

        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : default;
        var argumentText = arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : "{}";
        var key = session.Id + "|" + rawId;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[key] = cts;
        var watch = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            await _toolLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                result = await tool.Handler(new ToolCall(name, arguments), cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _toolLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            result = McpToolResult.Error($"{name} was cancelled.");
        }
        catch (Exception ex) when (ex is ToolArgumentException or ArgumentException or InvalidOperationException or KeyNotFoundException
                                       or FileNotFoundException or DirectoryNotFoundException or IOException or InvalidDataException
                                       or FormatException or NotSupportedException or UnauthorizedAccessException or JsonException)
        {
            result = McpToolResult.Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "MCP tool {Tool} failed.", name);
            result = McpToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _running.TryRemove(key, out _);
        }

        watch.Stop();
        Interlocked.Increment(ref _callCount);
        var summary = result.Summary
                      ?? (result.Structured is { } structured && !result.IsError ? structured.ToJsonString(McpJson.Compact) : null)
                      ?? result.Content.Select(c => c["text"]?.GetValue<string>()).FirstOrDefault(t => t is not null)
                      ?? (result.Content.Count > 0 ? "[image]" : string.Empty);
        summary = summary.Split('\n')[0];
        if (summary.Length > 200)
        {
            summary = summary[..200] + "…";
        }

        _logger.LogInformation("MCP {Tool} {Outcome} in {Ms:0} ms.", name, result.IsError ? "failed" : "done", watch.Elapsed.TotalMilliseconds);
        try
        {
            ToolCalled?.Invoke(this, new McpToolCalled(session, name, argumentText, result.IsError, summary, watch.Elapsed));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "MCP activity listener failed.");
        }

        return (result.ToJson(), null);
    }

    private void Cancel(McpSession session, JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var requestId)
            && _running.TryGetValue(session.Id + "|" + requestId.GetRawText(), out var cts))
        {
            cts.Cancel();
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    /// <summary>A JSON-RPC error response.</summary>
    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
