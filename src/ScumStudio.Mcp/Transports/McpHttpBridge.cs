using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Transports;

/// <summary>
/// Bridges an MCP client that only speaks stdio (e.g. Claude Desktop's <c>command</c> servers) to the running
/// ScumStudio app's Streamable HTTP endpoint: each stdin line is POSTed, JSON (or SSE) replies are written to stdout, the
/// <c>Mcp-Session-Id</c> and protocol version are carried along. When the app is not reachable, requests get a JSON-RPC
/// error explaining how to enable it, so the client never hangs.
/// </summary>
public sealed class McpHttpBridge : IDisposable
{
    private readonly Uri _endpoint;
    private readonly string? _token;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string? _sessionId;
    private string? _protocolVersion;

    /// <summary>Creates the bridge.</summary>
    public McpHttpBridge(Uri endpoint, string? token, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Forwards messages until stdin ends.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(input, utf8, detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(output, utf8) { NewLine = "\n" };
        using var writeLock = new SemaphoreSlim(1, 1);
        var pending = new List<Task>();

        async Task WriteAsync(string line)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await writer.WriteAsync("\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var message = line;
            var task = ForwardAsync(message, WriteAsync, cancellationToken);
            if (message.Contains("\"initialize\"", StringComparison.Ordinal))
            {
                await task.ConfigureAwait(false);
                continue;
            }

            pending.Add(task);
            pending.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        if (_sessionId is not null)
        {
            try
            {
                using var end = NewRequest(HttpMethod.Delete, null);
                using var _ = await _http.SendAsync(end, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
        }
    }

    /// <summary>Forwards one message and writes the replies through <paramref name="write"/>.</summary>
    public async Task ForwardAsync(string message, Func<string, Task> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        JsonNode? id = null;
        var isInitialize = false;
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    id = JsonNode.Parse(idElement.GetRawText());
                }

                isInitialize = McpServer.IsInitializeRequest(doc.RootElement);
            }
        }
        catch (JsonException)
        {
            await write(McpServer.Error(null, McpServer.ErrorCodes.ParseError, "Parse error").ToJsonString(McpJson.Compact)).ConfigureAwait(false);
            return;
        }

        try
        {
            using var request = NewRequest(HttpMethod.Post, message);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (isInitialize && response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            {
                _sessionId = sessions.FirstOrDefault();
            }

            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
            {
                return;
            }

            if (response.StatusCode == HttpStatusCode.NotFound && _sessionId is not null && !isInitialize)
            {
                _sessionId = null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (response.IsSuccessStatusCode && string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await ForwardEventStreamAsync(response, write, isInitialize, cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) && TryCompact(body, out var compact))
            {
                if (isInitialize)
                {
                    RememberProtocol(compact);
                }

                await write(compact).ConfigureAwait(false);
                return;
            }

            if (id is not null)
            {
                var hint = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "ScumStudio refused the token: copy it again from Settings → AI control (SCUMSTUDIO_MCP_TOKEN)."
                    : $"ScumStudio answered HTTP {(int)response.StatusCode}: {body.Trim()}";
                await write(McpServer.Error(id, -32000, hint).ToJsonString(McpJson.Compact)).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("MCP bridge: {Endpoint} not reachable ({Message}).", _endpoint, ex.Message);
            if (id is not null)
            {
                await write(McpServer.Error(id, -32000,
                    $"ScumStudio is not reachable at {_endpoint}: start ScumStudio and turn on Settings → AI control (MCP server).").ToJsonString(McpJson.Compact)).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private HttpRequestMessage NewRequest(HttpMethod method, string? body)
    {
        var request = new HttpRequestMessage(method, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        if (_protocolVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        }

        return request;
    }

    private async Task ForwardEventStreamAsync(HttpResponseMessage response, Func<string, Task> write, bool isInitialize, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart()).Append('\n');
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (TryCompact(data.ToString(), out var compact))
                {
                    if (isInitialize)
                    {
                        RememberProtocol(compact);
                    }

                    await write(compact).ConfigureAwait(false);
                }

                data.Clear();
            }
        }
    }

    private void RememberProtocol(string initializeResponse)
    {
        try
        {
            var node = JsonNode.Parse(initializeResponse);
            _protocolVersion = node?["result"]?["protocolVersion"]?.GetValue<string>() ?? _protocolVersion;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
    }

    private static bool TryCompact(string json, out string compact)
    {
        try
        {
            compact = JsonNode.Parse(json)?.ToJsonString(McpJson.Compact) ?? "null";
            return true;
        }
        catch (JsonException)
        {
            compact = string.Empty;
            return false;
        }
    }
}
