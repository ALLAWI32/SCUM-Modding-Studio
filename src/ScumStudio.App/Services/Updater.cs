using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ScumStudio.Core;

namespace ScumStudio.App.Services;

/// <summary>A published release: its version, notes ("what's new") and the Windows zip.</summary>
/// <param name="Version">Version from the tag (<c>v0.2.0</c> → 0.2.0).</param>
/// <param name="Tag">The tag.</param>
/// <param name="Notes">Release notes (Markdown).</param>
/// <param name="ZipUrl">Download URL of the <c>-win-x64.zip</c> asset, or null.</param>
/// <param name="ZipSize">Its size in bytes.</param>
/// <param name="PageUrl">The release page.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string? ZipUrl, long ZipSize, string PageUrl);

/// <summary>
/// Updates from the GitHub releases: the app asks for the latest release when it starts; a newer one shows an Update
/// button. Installing downloads the release zip and swaps the files in place: Windows lets a running program's files be
/// renamed (not overwritten), so each is renamed to <c>.old</c>, the new one is put there, the new version is started and
/// this one closes; the next start deletes the <c>.old</c> files and shows the release's notes once.
/// </summary>
public static class Updater
{
    /// <summary>The public repository.</summary>
    public const string Repository = "ALLAWI32/SCUM-Modding-Studio";

    /// <summary>GitHub API URL of the latest release.</summary>
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>Suffix of the release asset the app installs.</summary>
    public const string AssetSuffix = "-win-x64.zip";

    /// <summary>Notes kept across the restart, shown once by the new version.</summary>
    public const string WhatsNewFile = "whats-new.json";

    private const string OldSuffix = ".old";

    /// <summary>This build's version (informational version without a <c>-suffix</c> or <c>+commit</c>).</summary>
    public static Version Current { get; } = ParseVersion(CoreInfo.Version) ?? new Version(0, 0, 0);

    /// <summary>
    /// True for a published build (single-file, nothing but the exe next to it): a development build runs from the
    /// <c>bin</c> folder with its DLLs, where an update must not write.
    /// </summary>
    public static bool CanInstall => !File.Exists(Path.Combine(AppContext.BaseDirectory, "ScumStudio.App.dll"));

    /// <summary>The latest release when it is newer than <see cref="Current"/>, else null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        // SCUMSTUDIO_UPDATE_URL points the check at a test server (a release JSON) instead of GitHub.
        var url = Environment.GetEnvironmentVariable("SCUMSTUDIO_UPDATE_URL") is { Length: > 0 } test ? test : LatestUrl;
        var json = await http.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        return Parse(json) is { } release && release.Version > Current ? release : null;
    }

    /// <summary>Reads a GitHub release JSON object; null when it is a draft, a prerelease or has no version tag.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Flag(root, "draft") || Flag(root, "prerelease") || !root.TryGetProperty("tag_name", out var tag)
            || ParseVersion(tag.GetString()) is not { } version)
        {
            return null;
        }

        string? zipUrl = null;
        long zipSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() is { } n && n.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    zipSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
                    break;
                }
            }
        }

        return new ReleaseInfo(version, tag.GetString()!,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            zipUrl, zipSize,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? string.Empty : $"https://github.com/{Repository}/releases");
    }

    /// <summary>Downloads the release zip into <paramref name="folder"/>, reporting 0..1.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var url = release.ZipUrl ?? throw new InvalidOperationException("The release has no Windows zip.");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"SCUM-Modding-Studio-{release.Version}{AssetSuffix}");
        using var http = Client(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.ZipSize;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        return target;
    }

    /// <summary>
    /// Puts the files of <paramref name="zipPath"/> into <paramref name="installFolder"/>: every file that exists is renamed
    /// to <c>.old</c> first (allowed while it runs), then the new one is written. Any failure puts the old files back.
    /// </summary>
    public static void Install(string zipPath, string installFolder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        if (!entries.Any(e => e.Name.Equals("ScumStudio.App.exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The download is not a SCUM Modding Studio release (no ScumStudio.App.exe).");
        }

        var root = Path.GetFullPath(installFolder);
        var moved = new List<string>();
        var written = new List<string>();
        try
        {
            foreach (var entry in entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The download has a file outside the app folder: {entry.FullName}.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    File.Delete(target + OldSuffix); // left from an earlier update that could not clean up
                    File.Move(target, target + OldSuffix);
                    moved.Add(target);
                }

                written.Add(target);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch
        {
            foreach (var file in written.Where(File.Exists))
            {
                TryDelete(file);
            }

            foreach (var file in moved)
            {
                File.Move(file + OldSuffix, file, overwrite: true);
            }

            throw;
        }
    }

    /// <summary>Deletes the <c>.old</c> files an update left in <paramref name="installFolder"/> (best effort).</summary>
    public static int CleanUp(string installFolder)
    {
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(installFolder, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            n += TryDelete(file) ? 1 : 0;
        }

        return n;
    }

    /// <summary>Keeps <paramref name="release"/>'s notes for the new version to show after the restart.</summary>
    public static void SaveWhatsNew(string dataDirectory, ReleaseInfo release) =>
        File.WriteAllText(Path.Combine(dataDirectory, WhatsNewFile),
            JsonSerializer.Serialize(new WhatsNew(release.Version.ToString(3), release.Notes)));

    /// <summary>The notes kept for this version (removed once read), or null.</summary>
    public static string? TakeWhatsNew(string dataDirectory, Version current)
    {
        var path = Path.Combine(dataDirectory, WhatsNewFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var kept = JsonSerializer.Deserialize<WhatsNew>(File.ReadAllText(path));
            return kept is not null && ParseVersion(kept.Version) == current ? kept.Notes : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>Starts the program at <paramref name="exePath"/> (the new version).</summary>
    public static void Start(string exePath) => Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) });

    /// <summary>Parses <c>v1.2.3</c>, <c>1.2.3-rc1</c>, <c>1.2.3+abc</c> into 1.2.3 (missing parts 0); null when not a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core.Contains('.', StringComparison.Ordinal) ? core : core + ".0", out var v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : null;
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SCUM-Modding-Studio/{Current}");
        return http;
    }

    private static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record WhatsNew(string Version, string Notes);
}
