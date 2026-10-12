using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScumStudio.Core;
using ScumStudio.Core.Sharing;

namespace ScumStudio.App.Services;

/// <summary>One mod of the gallery, as the index lists it (strings trimmed and capped, the id checked).</summary>
public sealed record ModListing(
    string Id, string Name, string Author, string Description, IReadOnlyList<string> Sectors, long Size,
    DateTimeOffset Created, string AppVersion, string GameBuild, int Edits);

/// <summary>Why a mod share call did not succeed; the page turns it into a sentence in the user's language.</summary>
public enum ModShareError
{
    /// <summary>It worked.</summary>
    None,

    /// <summary>No gallery address is set (or it is not https).</summary>
    NotConfigured,

    /// <summary>The gallery could not be reached or did not answer in time.</summary>
    Offline,

    /// <summary>The package is above the size limit (refused here or by the server).</summary>
    TooLarge,

    /// <summary>Too many uploads from this address; try again after <see cref="ModShareResult{T}.RetryAfterSeconds"/>.</summary>
    RateLimited,

    /// <summary>The server refused the package (not a valid mod package).</summary>
    Rejected,

    /// <summary>The mod is not (or no longer) in the gallery.</summary>
    NotFound,

    /// <summary>The server answered something this version does not understand.</summary>
    BadAnswer,

    /// <summary>The moderator signature was refused (wrong key, clock off by more than five minutes, or replayed).</summary>
    Unauthorized,

    /// <summary>The gallery's storage is full.</summary>
    Full,
}

/// <summary>A value, or why there is none.</summary>
/// <param name="Value">The result when <see cref="Ok"/>.</param>
/// <param name="Error">What went wrong, or <see cref="ModShareError.None"/>.</param>
/// <param name="RetryAfterSeconds">With <see cref="ModShareError.RateLimited"/>: when to try again, when the server said.</param>
public sealed record ModShareResult<T>(T? Value, ModShareError Error, int? RetryAfterSeconds = null)
{
    /// <summary>True when the call worked.</summary>
    public bool Ok => Error == ModShareError.None;
}

/// <summary>
/// "Share your mod" (owner: upload your work, others browse the gallery in the app and press Get): the client of the mod
/// gallery, a Cloudflare Worker (<c>tools/modshare-worker</c>). Upload a package, read the gallery, fetch a picture or a
/// package, and, with the moderator key on the owner's PC, remove a mod. The app holds no secret: uploading needs none,
/// and a removal is signed with <see cref="ModeratorKey"/>, which never ships (F029 is the lesson). Every answer is an
/// error code, never a sentence; nothing is sent before a call, and no answer is read past its size limit.
/// </summary>
public sealed class ModShareService
{
    /// <summary>Environment variable with the gallery address when the settings have none (tests, the owner's own builds).</summary>
    public const string UrlVariable = "SCUMSTUDIO_MODSHARE_URL";

    /// <summary>The built-in gallery address: none until the owner deploys the Worker and pastes its address here.</summary>
    public const string DefaultUrl = "";

    /// <summary>Largest package (the Worker refuses more with 413).</summary>
    public const int MaxPackageBytes = 3 * 1024 * 1024;

    /// <summary>Largest gallery picture.</summary>
    public const int MaxThumbBytes = 120 * 1024;

    /// <summary>Most gallery rows read.</summary>
    public const int MaxListings = 1_000;

    private const int MaxIndexBytes = 8 * 1024 * 1024;
    private const int MaxSmallAnswerBytes = 64 * 1024;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly Func<string?> _configuredUrl;
    private readonly Func<HttpMessageHandler> _handler;
    private readonly TimeProvider _clock;

    /// <summary>Creates the client; <paramref name="configuredUrl"/> is read at every call (the settings may change).</summary>
    public ModShareService(ILogger logger, Func<string?> configuredUrl, Func<HttpMessageHandler>? handler = null, TimeProvider? clock = null)
    {
        _logger = logger;
        _configuredUrl = configuredUrl;
        _handler = handler ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when a usable gallery address is set.</summary>
    public bool IsConfigured => BaseUrl() is not null;

    /// <summary>True for a gallery id: 12 characters of <c>a-z0-9</c>. Nothing else ever goes into a URL.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 12 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>
    /// The gallery address: the settings, else <see cref="UrlVariable"/>, else <see cref="DefaultUrl"/>; null unless it is
    /// https (or http to this PC, for tests) without a query or a user name. No trailing slash.
    /// </summary>
    public string? BaseUrl()
    {
        var text = _configuredUrl()?.Trim() is { Length: > 0 } configured ? configured
            : Environment.GetEnvironmentVariable(UrlVariable)?.Trim() is { Length: > 0 } fromEnvironment ? fromEnvironment
            : DefaultUrl;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return null;
        }

        var local = uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1");
        return uri.Scheme == Uri.UriSchemeHttps || local ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/') : null;
    }

    /// <summary>Uploads a package (<c>POST /upload</c>); the value is the new mod's id. A package above <see cref="MaxPackageBytes"/> is refused here.</summary>
    public Task<ModShareResult<string>> UploadAsync(byte[] zip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (!IsConfigured)
        {
            return Task.FromResult(new ModShareResult<string>(null, ModShareError.NotConfigured));
        }

        if (zip.Length > MaxPackageBytes)
        {
            return Task.FromResult(new ModShareResult<string>(null, ModShareError.TooLarge));
        }

        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return SendAsync(HttpMethod.Post, "/upload", content, TransferTimeout, null, cancellationToken, async (response, ct) =>
        {
            if (await ReadCappedAsync(response.Content, MaxSmallAnswerBytes, ct).ConfigureAwait(false) is not { } body)
            {
                return new ModShareResult<string>(null, ModShareError.BadAnswer);
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && IsValidId(id.GetString())
                ? new ModShareResult<string>(id.GetString(), ModShareError.None)
                : new ModShareResult<string>(null, ModShareError.BadAnswer);
        });
    }

    /// <summary>The gallery (<c>GET /index.json</c>), in the server's order (newest first). Rows that do not read are skipped.</summary>
    public Task<ModShareResult<IReadOnlyList<ModListing>>> GetGalleryAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ModListing>>(HttpMethod.Get, "/index.json", null, ShortTimeout, null, cancellationToken, async (response, ct) =>
        {
            var body = await ReadCappedAsync(response.Content, MaxIndexBytes, ct).ConfigureAwait(false);
            return body is null ? new(null, ModShareError.TooLarge)
                : ParseIndex(body) is { } listings ? new(listings, ModShareError.None)
                : new(null, ModShareError.BadAnswer);
        });

    /// <summary>A mod's picture (<c>GET /thumbs/&lt;id&gt;.jpg</c>), JPEG bytes of at most <see cref="MaxThumbBytes"/>.</summary>
    public Task<ModShareResult<byte[]>> GetThumbnailAsync(string id, CancellationToken cancellationToken = default) =>
        GetBytesAsync(id, $"/thumbs/{id}.jpg", MaxThumbBytes, [0xFF, 0xD8, 0xFF], ShortTimeout, cancellationToken);

    /// <summary>A mod's package (<c>GET /mods/&lt;id&gt;.zip</c>), read until <see cref="MaxPackageBytes"/> whatever the server says its length is.</summary>
    public Task<ModShareResult<byte[]>> DownloadAsync(string id, CancellationToken cancellationToken = default) =>
        GetBytesAsync(id, $"/mods/{id}.zip", MaxPackageBytes, "PK"u8.ToArray(), TransferTimeout, cancellationToken);

    /// <summary>
    /// Removes a mod (<c>DELETE /mods/&lt;id&gt;</c>), signed with the moderator key: <c>X-ModShare-Time</c>,
    /// <c>X-ModShare-Nonce</c> and <c>X-ModShare-Signature</c> over <see cref="ModeratorKey.Canonical"/> of the request's
    /// method and URL path.
    /// </summary>
    public Task<ModShareResult<bool>> RemoveAsync(string id, ModeratorKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsValidId(id))
        {
            return Task.FromResult(new ModShareResult<bool>(false, IsConfigured ? ModShareError.NotFound : ModShareError.NotConfigured));
        }

        return SendAsync(HttpMethod.Delete, $"/mods/{id}", null, ShortTimeout, request =>
        {
            var (time, nonce, signature) = key.Sign("DELETE", request.RequestUri!.AbsolutePath, _clock.GetUtcNow());
            request.Headers.Add("X-ModShare-Time", time.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-ModShare-Nonce", nonce);
            request.Headers.Add("X-ModShare-Signature", signature);
        }, cancellationToken, (_, _) => Task.FromResult(new ModShareResult<bool>(true, ModShareError.None)));
    }

    /// <summary>
    /// Reads <c>index.json</c>: an array of rows, or <c>{"mods":[…]}</c>. A row needs a valid id, a name and a creation
    /// time (ISO 8601, Unix seconds or milliseconds); the rest is optional. Strings are trimmed, cleared of control characters and capped
    /// (name 60, author 32, description 600); a repeated id and everything past <see cref="MaxListings"/> rows are dropped.
    /// </summary>
    public static IReadOnlyList<ModListing>? ParseIndex(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array ? mods
                : default;
            if (rows.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<ModListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (list.Count == MaxListings)
                {
                    break;
                }

                if (ParseRow(row) is { } listing && seen.Add(listing.Id))
                {
                    list.Add(listing);
                }
            }

            return list;
        }
    }

    private static ModListing? ParseRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || idValue.GetString() is not { } id || !IsValidId(id)
            || Text(row, "name", 60, false) is not { Length: > 0 } name || Created(row) is not { } created)
        {
            return null;
        }

        var sectors = row.TryGetProperty("sectors", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Clean(e.GetString()!, 16, false))
                .Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).Take(64).ToList()
            : [];
        return new ModListing(id, name, Text(row, "author", 32, false) ?? string.Empty, Text(row, "description", 600, true) ?? string.Empty,
            sectors, Math.Max(0, Number(row, "size")), created, Text(row, "appVersion", 32, false) ?? string.Empty,
            Text(row, "gameBuild", 32, false) ?? string.Empty, (int)Math.Clamp(Number(row, "edits"), 0, int.MaxValue));

        static string? Text(JsonElement row, string name, int max, bool lines) =>
            row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString()!, max, lines) : null;

        static long Number(JsonElement row, string name) =>
            row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        static DateTimeOffset? Created(JsonElement row)
        {
            if (!row.TryGetProperty("created", out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var number) && number is > 0 and < 253_402_300_800_000)
            {
                // Unix seconds, or milliseconds (JavaScript's Date.now()) for anything past the year 5138 in seconds.
                return number < 100_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(number) : DateTimeOffset.FromUnixTimeMilliseconds(number);
            }

            return v.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at : null;
        }
    }

    /// <summary>
    /// Trims, turns control characters into spaces (keeps line breaks when <paramref name="lines"/>), drops the bidi
    /// overrides that make a name read backwards, and caps without splitting a surrogate pair.
    /// </summary>
    private static string Clean(string value, int max, bool lines)
    {
        var builder = new StringBuilder(Math.Min(value.Length, max + 1));
        foreach (var c in value.AsSpan().Trim())
        {
            if (builder.Length == max + 1)
            {
                break;
            }

            if (c is not ('‪' or '‫' or '‭' or '‮' or '\r'))
            {
                builder.Append(lines && c == '\n' ? c : char.IsControl(c) ? ' ' : c);
            }
        }

        var text = builder.ToString().TrimEnd();
        if (text.Length > max)
        {
            text = text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)].TrimEnd();
        }

        return text;
    }

    private Task<ModShareResult<byte[]>> GetBytesAsync(string id, string path, int cap, byte[] magic, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsValidId(id))
        {
            return Task.FromResult(new ModShareResult<byte[]>(null, IsConfigured ? ModShareError.NotFound : ModShareError.NotConfigured));
        }

        return SendAsync<byte[]>(HttpMethod.Get, path, null, timeout, null, cancellationToken, async (response, ct) =>
        {
            var bytes = await ReadCappedAsync(response.Content, cap, ct).ConfigureAwait(false);
            return bytes is null ? new(null, ModShareError.TooLarge)
                : bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)magic) ? new(bytes, ModShareError.None)
                : new(null, ModShareError.BadAnswer);
        });
    }

    /// <summary>
    /// One request on its own <see cref="HttpClient"/>: an error code for no address, no network, a timeout or a refusal;
    /// <paramref name="read"/> for a success. The caller's own cancellation is not an error: it is thrown.
    /// </summary>
    private async Task<ModShareResult<T>> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan timeout,
        Action<HttpRequestMessage>? prepare, CancellationToken cancellationToken, Func<HttpResponseMessage, CancellationToken, Task<ModShareResult<T>>> read)
    {
        using var owned = content;
        if (BaseUrl() is not { } baseUrl)
        {
            return new ModShareResult<T>(default, ModShareError.NotConfigured);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            using var http = new HttpClient(_handler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SCUM-Modding-Studio/{CoreInfo.Version.Split('+')[0]}");
            using var request = new HttpRequestMessage(method, new Uri(baseUrl + path)) { Content = content };
            prepare?.Invoke(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await read(response, timer.Token).ConfigureAwait(false);
            }

            var error = ErrorOf(response.StatusCode);
            _logger.LogWarning("Mod share {Method} {Path}: the server answered {Status}.", method.Method, path, (int)response.StatusCode);
            return new ModShareResult<T>(default, error, error == ModShareError.RateLimited ? RetryAfter(response) : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Mod share {Method} {Path}: the gallery cannot be reached ({Kind}).", method.Method, path, ex.GetType().Name);
            return new ModShareResult<T>(default, ModShareError.Offline);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Mod share {Method} {Path}: the answer is not JSON.", method.Method, path);
            return new ModShareResult<T>(default, ModShareError.BadAnswer);
        }
    }

    private static ModShareError ErrorOf(HttpStatusCode status) => (int)status switch
    {
        413 => ModShareError.TooLarge,
        429 => ModShareError.RateLimited,
        400 or 422 => ModShareError.Rejected,
        401 or 403 => ModShareError.Unauthorized,
        404 => ModShareError.NotFound,
        507 => ModShareError.Full,
        _ => ModShareError.BadAnswer,
    };

    private int? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var wait = retry?.Delta ?? (retry?.Date is { } at ? at - _clock.GetUtcNow() : null);
        return wait is { } w ? (int)Math.Clamp(Math.Ceiling(w.TotalSeconds), 0, 86_400) : null;
    }

    /// <summary>The body, or null when it is longer than <paramref name="cap"/> (said or found): reading stops at cap + 1 bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int cap, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > cap)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, cap + 1L - body.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            body.Write(chunk, 0, read);
            if (body.Length > cap)
            {
                return null;
            }
        }

        return body.ToArray();
    }
}
