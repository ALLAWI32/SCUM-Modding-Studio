using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScumStudio.Core.Sharing;

namespace ScumStudio.Tests.App;

/// <summary>One request the fake gallery received.</summary>
public sealed record FakeModShareRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[]? Body);

/// <summary>
/// The mod gallery in process: the executable spec of <c>tools/modshare-worker/worker.js</c>, with its routes, limits and
/// answers. <c>POST /upload</c> (413 above 3 MiB, 400 for a body that is not a zip of whitelisted entries with a
/// manifest, 429 after five uploads per address per hour with Retry-After, 507 when full; else <c>{"id"}</c>),
/// <c>GET /index.json</c> (<c>{"mods":[…]}</c>, newest first), <c>GET /mods/&lt;id&gt;.zip</c>,
/// <c>GET /thumbs/&lt;id&gt;.jpg</c> and <c>DELETE /mods/&lt;id&gt;</c>, which needs a moderator signature
/// (<see cref="ModeratorKey.Verify"/>) at most 300 s from the server's clock with a nonce not seen before; anything else
/// is 401. Requests are recorded; <see cref="Override"/> answers in its place (a lying or broken server).
/// </summary>
public sealed class FakeModShareServer : HttpMessageHandler
{
    /// <summary>The address the tests give the client.</summary>
    public const string Url = "http://127.0.0.1:8787";

    /// <summary>Largest upload.</summary>
    public const int MaxUploadBytes = 3 * 1024 * 1024;

    /// <summary>Uploads per address per hour.</summary>
    public const int UploadsPerHour = 5;

    /// <summary>How far a signed time may be from the server's clock.</summary>
    public static readonly TimeSpan SignatureWindow = TimeSpan.FromSeconds(300);

    private const long MaxUnpackedBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 256;

    private readonly object _gate = new();
    private readonly string? _moderatorPublicKey;
    private readonly List<JsonObject> _index = [];
    private readonly Dictionary<string, byte[]> _mods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _thumbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly List<FakeModShareRequest> _requests = [];

    /// <summary>Creates an empty gallery; <paramref name="moderatorPublicKey"/> is the Worker's <c>MODERATOR_PUBLIC_KEY</c> (none: every removal is 401).</summary>
    public FakeModShareServer(string? moderatorPublicKey = null) => _moderatorPublicKey = moderatorPublicKey;

    /// <summary>The server's clock.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The caller's address (the Worker reads <c>CF-Connecting-IP</c>).</summary>
    public string ClientIp { get; set; } = "203.0.113.7";

    /// <summary>True: the storage is full and uploads get 507.</summary>
    public bool Full { get; set; }

    /// <summary>Answers a request instead of the gallery when it returns non-null.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<FakeModShareRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The ids in the gallery, newest first.</summary>
    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return [.. _index.Select(row => row["id"]!.GetValue<string>())];
            }
        }
    }

    /// <summary>Puts a package in the gallery as an upload would, without the rate limit; returns its id.</summary>
    public string Seed(byte[] zip)
    {
        lock (_gate)
        {
            return Store(zip, ReadPackage(zip) ?? throw new ArgumentException("Not a valid package.", nameof(zip)));
        }
    }

    /// <summary>
    /// A small valid package: <c>manifest.json</c>, <c>journal.json</c> and a <c>preview.jpg</c> that starts like a JPEG.
    /// <paramref name="extra"/> adds entries (name, bytes) as they are, valid or not.
    /// </summary>
    public static byte[] Package(string name = "North airfield", string author = "Allawi", string description = "Hangars and a trader.",
        string[]? sectors = null, int edits = 3, (string Name, byte[] Data)[]? extra = null)
    {
        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["name"] = name,
            ["author"] = author,
            ["description"] = description,
            ["sectors"] = new JsonArray((sectors ?? ["B2", "B3"]).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["appVersion"] = "3.1.0",
            ["gameBuild"] = "1.0.2.3",
            ["edits"] = edits,
            ["created"] = "2026-10-12T11:00:00Z",
        };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add(zip, "journal.json", """{"ops":[]}"""u8.ToArray());
            Add(zip, "preview.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9]);
            foreach (var (entry, data) in extra ?? [])
            {
                Add(zip, entry, data);
            }
        }

        return buffer.ToArray();

        static void Add(ZipArchive zip, string name, byte[] data)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            stream.Write(data);
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            _requests.Add(new FakeModShareRequest(request.Method.Method, path, headers, body));
        }

        if (Override?.Invoke(request) is { } overridden)
        {
            return overridden;
        }

        lock (_gate)
        {
            return Route(request.Method.Method, path, headers, body);
        }
    }

    /// <summary>The fake is shared by every per-call HttpClient; their disposal must not end it.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private HttpResponseMessage Route(string method, string path, IReadOnlyDictionary<string, string> headers, byte[]? body)
    {
        switch (method, path)
        {
            case ("POST", "/upload"):
                return Upload(body ?? []);
            case ("GET", "/index.json"):
                return Json(HttpStatusCode.OK, new JsonObject { ["mods"] = new JsonArray(_index.Select(row => (JsonNode?)row.DeepClone()).ToArray()) });
            case ("GET", _) when Id(path, "/mods/", ".zip") is { } id:
                return _mods.TryGetValue(id, out var zip) ? Bytes(zip, "application/zip") : Status(HttpStatusCode.NotFound);
            case ("GET", _) when Id(path, "/thumbs/", ".jpg") is { } id:
                return _thumbs.TryGetValue(id, out var jpg) ? Bytes(jpg, "image/jpeg") : Status(HttpStatusCode.NotFound);
            case ("DELETE", _) when Id(path, "/mods/", string.Empty) is { } id:
                return Remove(method, path, id, headers);
            default:
                return Status(HttpStatusCode.NotFound);
        }
    }

    private HttpResponseMessage Upload(byte[] body)
    {
        var recent = _uploads.TryGetValue(ClientIp, out var times) ? times : _uploads[ClientIp] = [];
        recent.RemoveAll(t => Now - t >= TimeSpan.FromHours(1));
        if (recent.Count >= UploadsPerHour)
        {
            var response = Status((HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(Math.Ceiling((recent[0] + TimeSpan.FromHours(1) - Now).TotalSeconds)));
            return response;
        }

        recent.Add(Now);
        if (body.Length > MaxUploadBytes)
        {
            return Status(HttpStatusCode.RequestEntityTooLarge);
        }

        if (ReadPackage(body) is not { } manifest)
        {
            return Status(HttpStatusCode.BadRequest);
        }

        return Full ? Status((HttpStatusCode)507) : Json(HttpStatusCode.OK, new JsonObject { ["id"] = Store(body, manifest) });
    }

    private HttpResponseMessage Remove(string method, string path, string id, IReadOnlyDictionary<string, string> headers)
    {
        if (_moderatorPublicKey is null
            || !headers.TryGetValue("X-ModShare-Time", out var timeText) || !long.TryParse(timeText, out var time)
            || !headers.TryGetValue("X-ModShare-Nonce", out var nonce) || nonce.Length != 22 || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !headers.TryGetValue("X-ModShare-Signature", out var signature)
            || Math.Abs(Now.ToUnixTimeSeconds() - time) > SignatureWindow.TotalSeconds
            || _nonces.ContainsKey(nonce)
            || !ModeratorKey.Verify(_moderatorPublicKey, ModeratorKey.Canonical(method, path, time, nonce), signature))
        {
            return Status(HttpStatusCode.Unauthorized);
        }

        foreach (var old in _nonces.Where(n => Now - n.Value > SignatureWindow * 2).Select(n => n.Key).ToList())
        {
            _nonces.Remove(old);
        }

        _nonces[nonce] = Now;
        if (!_mods.Remove(id))
        {
            return Status(HttpStatusCode.NotFound);
        }

        _thumbs.Remove(id);
        _index.RemoveAll(row => row["id"]!.GetValue<string>() == id);
        return Json(HttpStatusCode.OK, new JsonObject { ["removed"] = id });
    }

    private string Store(byte[] zip, JsonObject manifest)
    {
        var id = string.Concat(Enumerable.Range(0, 12).Select(_ => "abcdefghijklmnopqrstuvwxyz0123456789"[RandomNumberGenerator.GetInt32(36)]));
        _mods[id] = zip;
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            if (archive.GetEntry("preview.jpg") is { } preview)
            {
                using var stream = preview.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _thumbs[id] = copy.ToArray();
            }
        }

        _index.Insert(0, new JsonObject
        {
            ["id"] = id,
            ["name"] = manifest["name"]?.DeepClone(),
            ["author"] = manifest["author"]?.DeepClone(),
            ["description"] = manifest["description"]?.DeepClone(),
            ["sectors"] = manifest["sectors"]?.DeepClone(),
            ["size"] = zip.Length,
            ["created"] = Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["appVersion"] = manifest["appVersion"]?.DeepClone(),
            ["gameBuild"] = manifest["gameBuild"]?.DeepClone(),
            ["edits"] = manifest["edits"]?.DeepClone(),
        });
        return id;
    }

    /// <summary>
    /// The light check of an upload (the deep rules live in the package reader): a zip of at most 256 entries and 16 MiB
    /// unpacked, every entry a relative <c>.json</c> or <c>.jpg</c> path without <c>..</c>, and a <c>manifest.json</c>
    /// object with a name. Returns the manifest, or null.
    /// </summary>
    private static JsonObject? ReadPackage(byte[] body)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries || archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes
                || archive.Entries.Any(e => !AllowedEntry(e.FullName)))
            {
                return null;
            }

            if (archive.GetEntry("manifest.json") is not { Length: < 64 * 1024 } entry)
            {
                return null;
            }

            using var stream = entry.Open();
            return JsonNode.Parse(stream) is JsonObject manifest && manifest["name"] is JsonValue name
                   && name.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? manifest : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }

    private static bool AllowedEntry(string name) =>
        name.Length is > 0 and <= 128 && !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':')
        && !name.Split('/').Any(part => part is "" or "." or "..")
        && (name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal));

    private static string? Id(string path, string prefix, string suffix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = path[prefix.Length..^suffix.Length];
        return id.Length == 12 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ? id : null;
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] data, string type)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
