using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Core.Sharing;

namespace ScumStudio.Tests.App;

/// <summary>
/// "Share your mod" (owner, 2026-10-12: upload your work, others browse the gallery and press Get, the owner removes bad
/// uploads with a key file only on his PC): the gallery client against <see cref="FakeModShareServer"/>, the executable
/// spec of the Worker. Uploads, the gallery, size caps that hold when the server lies, offline and unconfigured as error
/// codes, the rate limit, signed removals that cannot be replayed, and no secret inside the app.
/// </summary>
public sealed class ModShareServiceTests
{
    [Fact]
    public async Task UploadReturnsTheId()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer(key.PublicSpkiBase64);
        var service = Client(fake);
        Assert.True(service.IsConfigured);
        var zip = FakeModShareServer.Package();

        var upload = await service.UploadAsync(zip);
        Assert.True(upload.Ok);
        Assert.True(ModShareService.IsValidId(upload.Value));
        var post = Assert.Single(fake.Requests);
        Assert.Equal(("POST", "/upload"), (post.Method, post.Path));
        Assert.Equal("application/zip", post.Headers["Content-Type"]);
        Assert.StartsWith("SCUM-Modding-Studio/", post.Headers["User-Agent"], StringComparison.Ordinal);
        Assert.Equal(zip, post.Body);

        var mod = Assert.Single((await service.GetGalleryAsync()).Value!);
        Assert.Equal(new ModListing(upload.Value!, "North airfield", "Allawi", "Hangars and a trader.", mod.Sectors, zip.Length, fake.Now, "3.1.0", "1.0.2.3", 3), mod);
        Assert.Equal(["B2", "B3"], mod.Sectors);
        Assert.Equal(zip, (await service.DownloadAsync(mod.Id)).Value);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, (await service.GetThumbnailAsync(mod.Id)).Value![..3]);

        // The newest comes first.
        fake.Now += TimeSpan.FromMinutes(1);
        var second = await service.UploadAsync(FakeModShareServer.Package("South coast"));
        Assert.Equal([second.Value!, upload.Value!], (await service.GetGalleryAsync()).Value!.Select(m => m.Id));
    }

    [Fact]
    public async Task ATooLargePackageIsRefusedBeforeSending()
    {
        var fake = new FakeModShareServer();
        var result = await Client(fake).UploadAsync(new byte[ModShareService.MaxPackageBytes + 1]);
        Assert.Equal(ModShareError.TooLarge, result.Error);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task TheFakeAnswersAsTheWorkerMust()
    {
        var fake = new FakeModShareServer();
        using var raw = new HttpClient(fake, disposeHandler: false);
        async Task<int> Post(byte[] body)
        {
            fake.ClientIp = Guid.NewGuid().ToString("N"); // one upload per address: the rate limit is its own test
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await raw.PostAsync(FakeModShareServer.Url + "/upload", content);
            return (int)response.StatusCode;
        }

        Assert.Equal(413, await Post(new byte[FakeModShareServer.MaxUploadBytes + 1]));
        Assert.Equal(400, await Post("not a zip"u8.ToArray()));
        Assert.Equal(400, await Post(FakeModShareServer.Package(extra: [("../escape.json", "{}"u8.ToArray())])));
        Assert.Equal(400, await Post(FakeModShareServer.Package(extra: [("Content/Island.uasset", new byte[] { 0xC1, 0x83, 0x2A, 0x9E })])));
        Assert.Equal(400, await Post(FakeModShareServer.Package(extra: [("mods/pakchunk99-WindowsNoEditor.pak", new byte[64])])));
        Assert.Equal(400, await Post(FakeModShareServer.Package(name: "  ")));
        Assert.Equal(200, await Post(FakeModShareServer.Package()));
        fake.Full = true;
        Assert.Equal(507, await Post(FakeModShareServer.Package()));

        Assert.Equal(HttpStatusCode.NotFound, (await raw.GetAsync(FakeModShareServer.Url + "/mods/zzzzzzzzzzzz.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await raw.GetAsync(FakeModShareServer.Url + "/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.DeleteAsync(FakeModShareServer.Url + "/mods/" + fake.Ids[0])).StatusCode);
        Assert.Single(fake.Ids);
    }

    [Fact]
    public async Task TheGalleryIsReadAndBadRowsAreSkipped()
    {
        var fake = new FakeModShareServer();
        var index = string.Empty;
        fake.Override = request => request.RequestUri!.AbsolutePath == "/index.json"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index, Encoding.UTF8, "application/json") }
            : null;
        var service = Client(fake);
        var longName = new string('n', 70) + "\u202E" + "end";
        index = $$"""
            {"mods":[
              {"id":"aaaaaaaaaaa1","name":"  Good  ","author":"Me","description":"{{new string('d', 1000)}}","sectors":["B2",5,"  ","B2","C1"],
               "size":1234,"created":"2026-10-12T10:00:00Z","appVersion":"3.1.0","gameBuild":"b","edits":7},
              "not an object",
              {"id":"short","name":"x","created":1760270000},
              {"id":"aaaaaaaaaaa2","created":1760270000},
              {"id":"aaaaaaaaaaa3","name":"No date"},
              {"id":"aaaaaaaaaaa1","name":"Same id again","created":1760270000},
              {"id":"aaaaaaaaaaa4","name":"{{longName}}","author":"A\tB\u0000","description":"line 1\nline 2","created":1760270000000,"size":"big","edits":-5}
            ]}
            """;

        var gallery = await service.GetGalleryAsync();
        Assert.True(gallery.Ok);
        Assert.Equal(["aaaaaaaaaaa1", "aaaaaaaaaaa4"], gallery.Value!.Select(m => m.Id));
        var good = gallery.Value![0];
        Assert.Equal("Good", good.Name);
        Assert.Equal(600, good.Description.Length);
        Assert.Equal(["B2", "C1"], good.Sectors);
        Assert.Equal((1234L, 7, new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)), (good.Size, good.Edits, good.Created));
        var odd = gallery.Value![1];
        Assert.Equal(new string('n', 60), odd.Name); // capped, and the right-to-left override is gone
        Assert.Equal("A B", odd.Author);
        Assert.Equal("line 1\nline 2", odd.Description);
        Assert.Equal((0L, 0, DateTimeOffset.FromUnixTimeSeconds(1760270000)), (odd.Size, odd.Edits, odd.Created)); // milliseconds read as such

        // A bare array reads too; at most 1000 rows; not an index at all is a bad answer.
        index = "[" + string.Join(',', Enumerable.Range(0, 1200).Select(i => $$"""{"id":"{{i:D12}}","name":"Mod {{i}}","created":1760270000}""")) + "]";
        Assert.Equal(ModShareService.MaxListings, (await service.GetGalleryAsync()).Value!.Count);
        index = "{\"mods\": 5}";
        Assert.Equal(ModShareError.BadAnswer, (await service.GetGalleryAsync()).Error);
        index = "<html>";
        Assert.Equal(ModShareError.BadAnswer, (await service.GetGalleryAsync()).Error);
    }

    [Fact]
    public async Task ABadIdFromTheIndexNeverBuildsAUrl()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer(key.PublicSpkiBase64)
        {
            Override = request => request.RequestUri!.AbsolutePath == "/index.json"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [{"id":"../x","name":"a","created":1760270000},{"id":"ABC","name":"b","created":1760270000},
                         {"id":"abcdefghijkl\n","name":"c","created":1760270000},{"id":"abcdefgh/../","name":"d","created":1760270000},
                         {"id":"abcdefghijklm","name":"e","created":1760270000},{"id":7,"name":"f","created":1760270000}]
                        """),
                }
                : null,
        };
        var service = Client(fake);

        Assert.Empty((await service.GetGalleryAsync()).Value!);
        foreach (var id in new[] { "../x", "ABC", "abcdefghijkl\n", "abcdefgh/../", "../../index", string.Empty })
        {
            Assert.Equal(ModShareError.NotFound, (await service.DownloadAsync(id)).Error);
            Assert.Equal(ModShareError.NotFound, (await service.GetThumbnailAsync(id)).Error);
            Assert.Equal(ModShareError.NotFound, (await service.RemoveAsync(id, key)).Error);
        }

        Assert.Equal("/index.json", Assert.Single(fake.Requests).Path);
    }

    [Fact]
    public async Task DownloadStopsAtTheCapWhenTheServerLies()
    {
        var fake = new FakeModShareServer();
        var id = fake.Seed(FakeModShareServer.Package());
        var service = Client(fake);
        EndlessZip? served = null;
        long? claimed = null;
        fake.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath != $"/mods/{id}.zip")
            {
                return null;
            }

            served = new EndlessZip(50L * 1024 * 1024);
            var content = new StreamContent(served);
            content.Headers.ContentLength = claimed;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        // 50 MiB said to be 1000 bytes, and 50 MiB with no length: reading stops one byte past the cap.
        foreach (var lie in new long?[] { 1000, null })
        {
            claimed = lie;
            Assert.Equal(ModShareError.TooLarge, (await service.DownloadAsync(id)).Error);
            Assert.InRange(served!.Served, ModShareService.MaxPackageBytes + 1, ModShareService.MaxPackageBytes + 1);
        }

        // An honest length above the cap: nothing is read.
        claimed = 50L * 1024 * 1024;
        Assert.Equal(ModShareError.TooLarge, (await service.DownloadAsync(id)).Error);
        Assert.Equal(0, served!.Served);

        // The picture has its own cap, and must be a JPEG.
        var picture = new byte[ModShareService.MaxThumbBytes + 1];
        fake.Override = request => request.RequestUri!.AbsolutePath == $"/thumbs/{id}.jpg"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) }
            : null;
        picture[0] = 0xFF;
        picture[1] = 0xD8;
        picture[2] = 0xFF;
        Assert.Equal(ModShareError.TooLarge, (await service.GetThumbnailAsync(id)).Error);
        picture = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        Assert.Equal(ModShareError.BadAnswer, (await service.GetThumbnailAsync(id)).Error);
        fake.Override = null;
        Assert.True((await service.GetThumbnailAsync(id)).Ok);
        Assert.True((await service.DownloadAsync(id)).Ok);
    }

    [Fact]
    public async Task OfflineIsAnErrorNotACrash()
    {
        using var key = TestKey();
        foreach (var failure in new Func<Exception>[] { () => new HttpRequestException("No such host is known."), () => new TaskCanceledException("timeout") })
        {
            var service = new ModShareService(NullLogger.Instance, () => FakeModShareServer.Url, () => new Failing(failure));
            Assert.Equal(ModShareError.Offline, (await service.UploadAsync(FakeModShareServer.Package())).Error);
            Assert.Equal(ModShareError.Offline, (await service.GetGalleryAsync()).Error);
            Assert.Equal(ModShareError.Offline, (await service.GetThumbnailAsync("abcdefghijkl")).Error);
            Assert.Equal(ModShareError.Offline, (await service.DownloadAsync("abcdefghijkl")).Error);
            Assert.Equal(ModShareError.Offline, (await service.RemoveAsync("abcdefghijkl", key)).Error);
        }

        // The connection drops in the middle of a download.
        var fake = new FakeModShareServer();
        var id = fake.Seed(FakeModShareServer.Package());
        fake.Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessZip(1_000_000, failAfter: 100_000)) };
        Assert.Equal(ModShareError.Offline, (await Client(fake).DownloadAsync(id)).Error);

        // The caller's own cancel is not "offline": it ends the call.
        var hanging = new ModShareService(NullLogger.Instance, () => FakeModShareServer.Url, () => new Hanging());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.GetGalleryAsync(cancel.Token));
    }

    [Fact]
    public async Task NoUrlMeansNotConfiguredAndNoRequest()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer();
        var unusable = new List<string?> { "ftp://modshare.example/", "http://modshare.example", "https://user:secret@modshare.example",
            "https://modshare.example/?x=1", "not an address", "file:///C:/mods" };
        if (Environment.GetEnvironmentVariable(ModShareService.UrlVariable) is null)
        {
            unusable.AddRange([null, string.Empty, "   "]); // the built-in address is empty until the owner deploys the Worker
        }

        foreach (var url in unusable)
        {
            var service = new ModShareService(NullLogger.Instance, () => url, () => fake);
            Assert.False(service.IsConfigured, url);
            Assert.Null(service.BaseUrl());
            Assert.Equal(ModShareError.NotConfigured, (await service.UploadAsync(FakeModShareServer.Package())).Error);
            Assert.Equal(ModShareError.NotConfigured, (await service.GetGalleryAsync()).Error);
            Assert.Equal(ModShareError.NotConfigured, (await service.GetThumbnailAsync("abcdefghijkl")).Error);
            Assert.Equal(ModShareError.NotConfigured, (await service.DownloadAsync("abcdefghijkl")).Error);
            Assert.Equal(ModShareError.NotConfigured, (await service.RemoveAsync("abcdefghijkl", key)).Error);
        }

        Assert.Empty(fake.Requests);
        Assert.Empty(ModShareService.DefaultUrl);

        // https anywhere (a path prefix is kept), plain http only to this PC.
        Assert.Equal("https://modshare.example.workers.dev", new ModShareService(NullLogger.Instance, () => " https://modshare.example.workers.dev/ ").BaseUrl());
        Assert.Equal("https://example.com/modshare", new ModShareService(NullLogger.Instance, () => "https://example.com/modshare/").BaseUrl());
        Assert.Equal("http://localhost:8787", new ModShareService(NullLogger.Instance, () => "http://localhost:8787").BaseUrl());
        Assert.Empty(fake.Requests); // creating a client sends nothing
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        var fake = new FakeModShareServer();
        var clock = new FixedClock(fake.Now);
        var service = new ModShareService(NullLogger.Instance, () => FakeModShareServer.Url, () => fake, clock);
        for (var i = 0; i < FakeModShareServer.UploadsPerHour; i++)
        {
            Assert.True((await service.UploadAsync(FakeModShareServer.Package($"Mod {i}"))).Ok);
        }

        var limited = await service.UploadAsync(FakeModShareServer.Package());
        Assert.Equal((ModShareError.RateLimited, 3600), (limited.Error, limited.RetryAfterSeconds));
        fake.Now += TimeSpan.FromMinutes(30);
        Assert.Equal(1800, (await service.UploadAsync(FakeModShareServer.Package())).RetryAfterSeconds);

        // Another address is not held back; the first is free again after the hour.
        fake.ClientIp = "198.51.100.20";
        Assert.True((await service.UploadAsync(FakeModShareServer.Package())).Ok);
        fake.ClientIp = "203.0.113.7";
        fake.Now += TimeSpan.FromMinutes(31);
        Assert.True((await service.UploadAsync(FakeModShareServer.Package())).Ok);

        // Retry-After as a date is read against the clock.
        fake.Override = _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.Now + TimeSpan.FromSeconds(120));
            return response;
        };
        Assert.Equal(120, (await service.UploadAsync(FakeModShareServer.Package())).RetryAfterSeconds);
    }

    [Fact]
    public async Task ServerAnswersBecomeErrorCodes()
    {
        var fake = new FakeModShareServer();
        var service = Client(fake);
        Assert.Equal(ModShareError.Rejected, (await service.UploadAsync(FakeModShareServer.Package(extra: [("Content/Island.uexp", new byte[8])]))).Error);
        fake.Full = true;
        Assert.Equal(ModShareError.Full, (await service.UploadAsync(FakeModShareServer.Package())).Error);
        fake.Full = false;

        var status = HttpStatusCode.OK;
        var body = string.Empty;
        fake.Override = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };
        foreach (var (code, answer, expected) in new[]
                 {
                     (413, "", ModShareError.TooLarge), (422, "", ModShareError.Rejected), (403, "", ModShareError.Unauthorized),
                     (404, "", ModShareError.NotFound), (500, "", ModShareError.BadAnswer), (302, "", ModShareError.BadAnswer),
                     (200, "<html>", ModShareError.BadAnswer), (200, """{"id":"../x"}""", ModShareError.BadAnswer), (200, "{}", ModShareError.BadAnswer),
                 })
        {
            (status, body) = ((HttpStatusCode)code, answer);
            var result = await service.UploadAsync(FakeModShareServer.Package());
            Assert.Equal(expected, result.Error);
            Assert.Null(result.Value);
        }
    }

    [Fact]
    public async Task RemoveIsSignedAndTheFakeAccepts()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer(key.PublicSpkiBase64);
        var service = Client(fake);
        var id = fake.Seed(FakeModShareServer.Package());
        var kept = fake.Seed(FakeModShareServer.Package("Keep me"));

        var removed = await service.RemoveAsync(id, key);
        Assert.True(removed.Ok);
        var delete = fake.Requests[^1];
        Assert.Equal(("DELETE", $"/mods/{id}"), (delete.Method, delete.Path));
        var canonical = ModeratorKey.Canonical("DELETE", delete.Path, long.Parse(delete.Headers["X-ModShare-Time"]), delete.Headers["X-ModShare-Nonce"]);
        Assert.True(ModeratorKey.Verify(key.PublicSpkiBase64, canonical, delete.Headers["X-ModShare-Signature"]));
        Assert.Equal([kept], fake.Ids);
        Assert.Equal(ModShareError.NotFound, (await service.DownloadAsync(id)).Error);
        Assert.Equal(ModShareError.NotFound, (await service.GetThumbnailAsync(id)).Error);
        Assert.Equal(ModShareError.NotFound, (await service.RemoveAsync(id, key)).Error);

        // Another key is not the moderator.
        using var stranger = TestKey();
        Assert.Equal(ModShareError.Unauthorized, (await service.RemoveAsync(kept, stranger)).Error);
        Assert.Equal([kept], fake.Ids);
    }

    [Fact]
    public async Task AReplayedRemoveIsRefused()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer(key.PublicSpkiBase64);
        var first = fake.Seed(FakeModShareServer.Package());
        var second = fake.Seed(FakeModShareServer.Package("Second"));
        Assert.True((await Client(fake).RemoveAsync(first, key)).Ok);
        var captured = fake.Requests[^1];

        // The same headers again, on the same mod or on another one: refused, nothing removed.
        using var raw = new HttpClient(fake, disposeHandler: false);
        foreach (var path in new[] { $"/mods/{first}", $"/mods/{second}" })
        {
            using var replay = new HttpRequestMessage(HttpMethod.Delete, FakeModShareServer.Url + path);
            foreach (var name in new[] { "X-ModShare-Time", "X-ModShare-Nonce", "X-ModShare-Signature" })
            {
                replay.Headers.Add(name, captured.Headers[name]);
            }

            using var response = await raw.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal([second], fake.Ids);
    }

    [Fact]
    public async Task AStaleRemoveIsRefused()
    {
        using var key = TestKey();
        var fake = new FakeModShareServer(key.PublicSpkiBase64);
        var id = fake.Seed(FakeModShareServer.Package());
        foreach (var off in new[] { -301, 301, -3600 })
        {
            var service = new ModShareService(NullLogger.Instance, () => FakeModShareServer.Url, () => fake, new FixedClock(fake.Now + TimeSpan.FromSeconds(off)));
            Assert.Equal(ModShareError.Unauthorized, (await service.RemoveAsync(id, key)).Error);
        }

        Assert.Equal([id], fake.Ids);
        var late = new ModShareService(NullLogger.Instance, () => FakeModShareServer.Url, () => fake, new FixedClock(fake.Now - TimeSpan.FromSeconds(299)));
        Assert.True((await late.RemoveAsync(id, key)).Ok);
    }

    [Fact]
    public void TheAppCarriesNoModeratorSecret()
    {
        // F029: the report webhook is embedded by the release build (report-webhook.txt). It may stay there until it is
        // replaced, but no other copy may appear, and no private key may ever ship.
        foreach (var assembly in new[] { typeof(ModShareService).Assembly, typeof(ModeratorKey).Assembly })
        {
            var bytes = File.ReadAllBytes(assembly.Location);
            Assert.Equal(0, Count(bytes, "PRIVATE KEY"));
            byte[] embedded = [];
            if (assembly.GetManifestResourceStream("report-webhook.txt") is { } resource)
            {
                using (resource)
                using (var copy = new MemoryStream())
                {
                    resource.CopyTo(copy);
                    embedded = copy.ToArray();
                }
            }

            Assert.Equal(Count(embedded, "discord.com/api/webhooks"), Count(bytes, "discord.com/api/webhooks"));
        }

        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, ModeratorKey.FileName)));
    }

    private static ModShareService Client(FakeModShareServer fake) =>
        new(NullLogger.Instance, () => FakeModShareServer.Url, () => fake, new FixedClock(fake.Now));

    private static ModeratorKey TestKey() => ModeratorKey.FromPem(ModeratorKey.Generate().PrivatePem)!;

    /// <summary>Occurrences of <paramref name="text"/> as UTF-8 and as UTF-16 (how .NET stores string literals).</summary>
    private static int Count(byte[] haystack, string text)
    {
        var count = 0;
        foreach (var needle in new[] { Encoding.UTF8.GetBytes(text), Encoding.Unicode.GetBytes(text) })
        {
            ReadOnlySpan<byte> rest = haystack;
            for (var at = rest.IndexOf(needle); at >= 0; at = rest.IndexOf(needle))
            {
                count++;
                rest = rest[(at + 1)..];
            }
        }

        return count;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A network that fails every request.</summary>
    private sealed class Failing(Func<Exception> failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure());
    }

    /// <summary>A network that never answers.</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>A body that starts like a zip and goes on for <paramref name="length"/> bytes (or breaks at <paramref name="failAfter"/>), counting what was read.</summary>
    private sealed class EndlessZip(long length, long failAfter = -1) : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter >= 0 && Served >= failAfter)
            {
                throw new IOException("The connection was reset.");
            }

            var n = (int)Math.Min(count, length - Served);
            Array.Clear(buffer, offset, n);
            if (Served == 0 && n >= 2)
            {
                buffer[offset] = (byte)'P';
                buffer[offset + 1] = (byte)'K';
            }

            Served += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
