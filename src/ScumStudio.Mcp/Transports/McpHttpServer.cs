using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Transports;

/// <summary>Settings of <see cref="McpHttpServer"/>.</summary>
public sealed record McpHttpServerOptions
{
    /// <summary>Default port of the ScumStudio app's MCP endpoint.</summary>
    public const int DefaultPort = 47130;

    /// <summary>TCP port on 127.0.0.1 (0 = any free port; see <see cref="McpHttpServer.Port"/>).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Endpoint path.</summary>
    public string Path { get; init; } = "/mcp";

    /// <summary>Bearer token every request must carry (<c>Authorization: Bearer …</c>); null disables the check.</summary>
    public string? Token { get; init; }

    /// <summary>Largest accepted request body.</summary>
    public int MaxBodyBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Creates a random token (URL-safe, 32 bytes of entropy).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// MCP Streamable HTTP transport on the loopback interface only, implemented on a plain <see cref="TcpListener"/> (no
/// http.sys URL reservation, no ASP.NET Core): <c>POST /mcp</c> with one JSON-RPC message (or a batch) answers
/// <c>application/json</c> (or <c>202 Accepted</c> for notifications); <c>GET</c> answers 405 (no server-initiated
/// stream); <c>DELETE</c> ends the session. Sessions are assigned on <c>initialize</c> (<c>Mcp-Session-Id</c>); requests
/// without a session id use a shared session, unknown ids get 404. Security: binds 127.0.0.1, checks the Host and
/// Origin headers against localhost (DNS-rebinding protection) and, when configured, a bearer token.
/// </summary>
public sealed class McpHttpServer : IAsyncDisposable
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];
    private readonly McpServer _server;
    private readonly McpHttpServerOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly McpSession _sharedSession = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>Creates the transport (call <see cref="Start"/>).</summary>
    public McpHttpServer(McpServer server, McpHttpServerOptions? options = null, ILogger? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = options ?? new McpHttpServerOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The bound port (after <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    /// <summary>Endpoint URL, e.g. <c>http://127.0.0.1:47130/mcp</c>.</summary>
    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}{_options.Path}");

    /// <summary>True while listening.</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>Open sessions.</summary>
    public IReadOnlyCollection<McpSession> Sessions => _sessions.Values.ToList();

    /// <summary>Starts listening on 127.0.0.1.</summary>
    /// <exception cref="SocketException">The port is in use.</exception>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, _options.Port);
        if (OperatingSystem.IsWindows())
        {
            // Another process must not be able to bind the same port next to us.
            listener.ExclusiveAddressUse = true;
        }

        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _logger.LogInformation("MCP server listening on {Url}{Auth}.", Url, _options.Token is null ? " (no token)" : " (bearer token required)");
    }

    /// <summary>Stops listening and closes connections.</summary>
    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        _listener = null;
        _cts?.Cancel();
        listener.Stop();
        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _sessions.Clear();
        _logger.LogInformation("MCP server stopped.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeConnectionAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var buffer = new HttpReadBuffer(stream);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = await HttpRequest.ReadAsync(buffer, stream, _options.MaxBodyBytes, cancellationToken).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
                    var keepAlive = request.KeepAlive && response.Status < 500;
                    await response.WriteAsync(stream, keepAlive, cancellationToken).ConfigureAwait(false);
                    if (!keepAlive)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                // Client went away or sent garbage.
            }
        }
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var origin = request.Header("Origin");
        var cors = origin is not null && IsLocalOrigin(origin) ? origin : null;
        HttpResponse Respond(HttpResponse r) => cors is null ? r : r.WithCors(cors);

        if (!IsLocalHost(request.Header("Host")))
        {
            return HttpResponse.Text(403, "Forbidden: only localhost requests are served.");
        }

        if (origin is not null && cors is null)
        {
            return HttpResponse.Text(403, "Forbidden: cross-origin requests from non-local pages are refused.");
        }

        var path = request.Path;
        if (!string.Equals(path, _options.Path, StringComparison.Ordinal) && !string.Equals(path, _options.Path + "/", StringComparison.Ordinal))
        {
            return Respond(HttpResponse.Text(404, "Not found. The MCP endpoint is " + _options.Path));
        }

        if (request.Method == "OPTIONS")
        {
            return Respond(new HttpResponse(204, "No Content"));
        }

        if (_options.Token is { } token && !TokenMatches(request.Header("Authorization"), token))
        {
            return Respond(HttpResponse.Text(401, "Unauthorized: send 'Authorization: Bearer <token>' (ScumStudio → Settings → AI control).")
                .WithHeader("WWW-Authenticate", "Bearer realm=\"scumstudio\""));
        }

        if (request.Header("MCP-Protocol-Version") is { } protocol && !McpServer.SupportedProtocolVersions.Contains(protocol, StringComparer.Ordinal))
        {
            return Respond(HttpResponse.Text(400, "Unsupported MCP-Protocol-Version " + protocol));
        }

        switch (request.Method)
        {
            case "GET":
                return Respond(HttpResponse.Text(405, "This server does not open a server-to-client stream; use POST.").WithHeader("Allow", "POST, DELETE, OPTIONS"));
            case "DELETE":
                return Respond(request.Header("Mcp-Session-Id") is { } ended && _sessions.TryRemove(ended, out _)
                    ? new HttpResponse(200, "OK")
                    : HttpResponse.Text(404, "Session not found."));
            case "POST":
                break;
            default:
                return Respond(HttpResponse.Text(405, "Method not allowed.").WithHeader("Allow", "POST, DELETE, OPTIONS"));
        }

        var body = Encoding.UTF8.GetString(request.Body);
        bool isInitialize;
        try
        {
            using var doc = JsonDocument.Parse(body);
            isInitialize = McpServer.IsInitializeRequest(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return Respond(HttpResponse.Json(400, McpServer.Error(null, McpServer.ErrorCodes.ParseError, "Parse error: " + ex.Message).ToJsonString(McpJson.Compact)));
        }

        McpSession session;
        if (isInitialize)
        {
            session = new McpSession();
            _sessions[session.Id] = session;
        }
        else if (request.Header("Mcp-Session-Id") is { } sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out session!))
            {
                return Respond(HttpResponse.Json(404, McpServer.Error(null, -32001, "Session not found; initialize again.").ToJsonString(McpJson.Compact)));
            }
        }
        else
        {
            session = _sharedSession;
        }

        var reply = await _server.HandleMessageAsync(body, session, cancellationToken).ConfigureAwait(false);
        var response = reply is null ? new HttpResponse(202, "Accepted") : HttpResponse.Json(200, reply);
        if (isInitialize)
        {
            response = response.WithHeader("Mcp-Session-Id", session.Id);
        }

        return Respond(response);
    }

    private static bool TokenMatches(string? header, string token)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(header[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static bool IsLocalHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            host = close > 0 ? host[..(close + 1)] : host;
        }
        else if (host.LastIndexOf(':') is var colon and > 0)
        {
            host = host[..colon];
        }

        return LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && LocalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Buffered reader over the connection stream.</summary>
    private sealed class HttpReadBuffer(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start;
        private int _end;

        public async Task<int> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0 && _start == _end)
            {
                _start = _end = 0;
            }

            if (_end == _buffer.Length)
            {
                if (_start == 0)
                {
                    throw new InvalidDataException("HTTP header too large.");
                }

                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            _end += read;
            return read;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                    _start = newline + 1;
                    return line;
                }

                if (await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    return null;
                }
            }
        }

        public async Task ReadExactAsync(byte[] target, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                if (_start == _end && await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    throw new IOException("Connection closed inside the body.");
                }

                var n = Math.Min(count, _end - _start);
                Buffer.BlockCopy(_buffer, _start, target, offset, n);
                _start += n;
                offset += n;
                count -= n;
            }
        }
    }

    /// <summary>A parsed HTTP/1.1 request.</summary>
    private sealed class HttpRequest
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string Method { get; private init; } = string.Empty;

        public string Path { get; private init; } = "/";

        public bool KeepAlive { get; private set; }

        public byte[] Body { get; private set; } = [];

        public string? Header(string name) => _headers.TryGetValue(name, out var v) ? v : null;

        public static async Task<HttpRequest?> ReadAsync(HttpReadBuffer buffer, Stream stream, int maxBody, CancellationToken cancellationToken)
        {
            string? requestLine;
            do
            {
                requestLine = await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (requestLine is null)
                {
                    return null;
                }
            }
            while (requestLine.Length == 0);

            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Bad request line.");
            }

            var target = parts[1];
            var query = target.IndexOf('?');
            var request = new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = query >= 0 ? target[..query] : target };
            var headerCount = 0;
            while (true)
            {
                var line = await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("Connection closed inside the headers.");
                if (line.Length == 0)
                {
                    break;
                }

                if (++headerCount > 100)
                {
                    throw new InvalidDataException("Too many headers.");
                }

                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    request._headers[name] = request._headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
                }
            }

            var connection = request.Header("Connection") ?? string.Empty;
            request.KeepAlive = parts[2] == "HTTP/1.1"
                ? !connection.Contains("close", StringComparison.OrdinalIgnoreCase)
                : connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(cont, cancellationToken).ConfigureAwait(false);
            }

            if (request.Header("Transfer-Encoding") is { } te && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var body = new MemoryStream();
                while (true)
                {
                    var sizeLine = await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("Connection closed inside a chunk.");
                    var semicolon = sizeLine.IndexOf(';');
                    var size = int.Parse(semicolon >= 0 ? sizeLine[..semicolon] : sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        while ((await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Length > 0)
                        {
                        }

                        break;
                    }

                    if (body.Length + size > maxBody)
                    {
                        throw new InvalidDataException("Body too large.");
                    }

                    var chunk = new byte[size];
                    await buffer.ReadExactAsync(chunk, 0, size, cancellationToken).ConfigureAwait(false);
                    body.Write(chunk);
                    await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }

                request.Body = body.ToArray();
            }
            else if (request.Header("Content-Length") is { } lengthText)
            {
                if (!int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0 || length > maxBody)
                {
                    throw new InvalidDataException("Bad Content-Length.");
                }

                var body = new byte[length];
                await buffer.ReadExactAsync(body, 0, length, cancellationToken).ConfigureAwait(false);
                request.Body = body;
            }

            return request;
        }
    }

    /// <summary>An HTTP response.</summary>
    private sealed record HttpResponse(int Status, string Reason)
    {
        public string? ContentType { get; init; }

        public byte[] Body { get; init; } = [];

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

        public static HttpResponse Text(int status, string text) =>
            new(status, ReasonOf(status)) { ContentType = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(text) };

        public static HttpResponse Json(int status, string json) =>
            new(status, ReasonOf(status)) { ContentType = "application/json", Body = Encoding.UTF8.GetBytes(json) };

        public HttpResponse WithHeader(string name, string value) => this with { Headers = [.. Headers, new(name, value)] };

        public HttpResponse WithCors(string origin) => this with
        {
            Headers =
            [
                .. Headers,
                new("Access-Control-Allow-Origin", origin),
                new("Vary", "Origin"),
                new("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS"),
                new("Access-Control-Allow-Headers", "Content-Type, Authorization, Mcp-Session-Id, MCP-Protocol-Version, Last-Event-ID"),
                new("Access-Control-Expose-Headers", "Mcp-Session-Id"),
            ],
        };

        public async Task WriteAsync(Stream stream, bool keepAlive, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason).Append("\r\n");
            if (ContentType is not null)
            {
                sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            }

            sb.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            foreach (var (name, value) in Headers)
            {
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            sb.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), cancellationToken).ConfigureAwait(false);
            if (Body.Length > 0)
            {
                await stream.WriteAsync(Body, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string ReasonOf(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
    }
}
