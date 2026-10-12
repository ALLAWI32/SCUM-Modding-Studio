using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// button. Installing downloads the release zip, stages and checks it, and swaps the files in place by renames: Windows
/// lets a running program's files be renamed (not overwritten or deleted), so each is renamed to a <c>.old</c> backup, the
/// new one is renamed into its place, the new version is started and this one closes; the next start deletes the backups
/// the update listed and shows the release's notes once.
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

    /// <summary>The backups an update left in the app folder (in the data folder), deleted by the next start.</summary>
    public const string CleanupListFile = "update-cleanup.json";

    /// <summary>A download that receives nothing for this long stops with a <see cref="TimeoutException"/>.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const string OldSuffix = ".old";

    private const string AppExe = "ScumStudio.App.exe";

    /// <summary>The release is extracted into <c>&lt;app folder&gt;/.update-&lt;ticks&gt;</c> (same drive: the swap is renames).</summary>
    private const string StagingPrefix = ".update-";

    /// <summary>What the release zip holds (scripts/build.ps1); older updaters left these as <c>.old</c> without a list.</summary>
    private static readonly string[] ReleaseFiles = [AppExe, "scumstudio.exe", "README.md", "CHANGELOG.md", "THIRD-PARTY-NOTICES.md", "LICENSE"];

    private static readonly TimeSpan[] MoveRetries = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900)];

    private static readonly Regex BackupName = new(@"^(?<name>.+)\.old(-\d+)?$", RegexOptions.CultureInvariant);

    private static readonly Regex StagingName = new(@"^\.update-\d+$", RegexOptions.CultureInvariant);

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

    /// <summary>
    /// Downloads the release zip into <paramref name="folder"/>, reporting 0..1. A connection that sends nothing for
    /// <paramref name="idleTimeout"/> (default <see cref="IdleTimeout"/>) ends in a <see cref="TimeoutException"/>; a slow
    /// one that keeps sending does not. Nothing is left behind when it fails or is cancelled.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string folder, IProgress<double>? progress, CancellationToken cancellationToken,
        HttpMessageHandler? handler = null, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        var url = release.ZipUrl ?? throw new InvalidOperationException("The release has no Windows zip.");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"SCUM-Modding-Studio-{release.Version}{AssetSuffix}");
        var idle = idleTimeout ?? IdleTimeout;
        using var http = Client(TimeSpan.FromMinutes(30), handler);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(idle); // restarted after every chunk
        try
        {
            using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.ZipSize;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(idle);
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total);
                    }
                }
            }

            return target;
        }
        catch (OperationCanceledException ex) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(target);
            throw new TimeoutException($"The download stopped: no data for {Math.Ceiling(idle.TotalSeconds)} seconds.", ex);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Puts the release in <paramref name="zipPath"/> in place of the files in <paramref name="installFolder"/>. Every file
    /// is first extracted into a staging folder beside them and checked (each size, and the app's version against
    /// <paramref name="expected"/>); only then is each one swapped by two renames, the current file to a backup (allowed
    /// while it runs) and the new one into its place, so no half-written file ever stands in the app folder. A backup
    /// name still held by a running program (a second window, Claude Desktop's bridge) gets a unique
    /// <c>.old-&lt;ticks&gt;</c> name instead. The backups are added to <paramref name="cleanupListPath"/> before the first
    /// rename, for the next start to delete. Any failure puts the old files back.
    /// </summary>
    /// <exception cref="InvalidDataException">The zip is not a release, is damaged or is another version.</exception>
    /// <exception cref="UpdateFileInUseException">A file could not be renamed: another program holds it.</exception>
    public static void Install(string zipPath, string installFolder, string cleanupListPath, Version? expected = null)
    {
        var root = Path.GetFullPath(installFolder);
        using var zip = ZipFile.OpenRead(zipPath);
        var staging = Path.Combine(root, StagingPrefix + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        var files = Plan(zip, root, staging);
        var started = new List<Swap>();
        try
        {
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Staged)!);
                Trace?.Invoke($"extract|{file.Staged}");
                file.Entry.ExtractToFile(file.Staged);
                if (new FileInfo(file.Staged).Length != file.Entry.Length)
                {
                    throw new InvalidDataException($"The download is damaged: {file.Entry.FullName} is incomplete.");
                }
            }

            var app = files.First(f => f.IsApp).Staged;
            if (expected is not null && ParseVersion(FileVersionInfo.GetVersionInfo(app).ProductVersion) is { } found && found != expected)
            {
                throw new InvalidDataException($"The download is version {found}, not {expected}.");
            }

            var held = new List<string>();
            foreach (var file in files.Where(f => File.Exists(f.Target)))
            {
                file.Backup = BackupFor(file.Target, held);
            }

            AddToList(cleanupListPath, [.. files.Where(f => f.Backup is not null).Select(f => f.Backup!), .. held]);
            foreach (var file in files)
            {
                started.Add(file);
                if (file.Backup is not null)
                {
                    Move(file.Target, file.Backup, file.Target);
                    file.BackedUp = true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                Move(file.Staged, file.Target, file.Target);
                file.InPlace = true;
            }
        }
        catch
        {
            RollBack(started);
            throw;
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    /// <summary>
    /// Deletes what updates left in <paramref name="installFolder"/>: the backups listed in <paramref name="cleanupListPath"/>,
    /// the <c>.old</c> files older updaters made of the release's own files, and unfinished staging folders. That folder
    /// only, never below it: the app may sit in a shared folder among the user's own files. A file still in use stays
    /// listed for the next start. Returns the number of files deleted.
    /// </summary>
    public static int CleanUp(string installFolder, string cleanupListPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder));
        var deleted = 0;
        var kept = new List<string>();
        foreach (var path in ReadList(cleanupListPath).Where(p => IsBackupIn(root, p) && File.Exists(p)))
        {
            if (TryDelete(path))
            {
                deleted++;
            }
            else
            {
                kept.Add(path);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*" + OldSuffix + "*", SearchOption.TopDirectoryOnly))
        {
            if (BackupName.Match(Path.GetFileName(file)) is { Success: true } match
                && ReleaseFiles.Contains(match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) && TryDelete(file))
            {
                deleted++;
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(root, StagingPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (StagingName.IsMatch(Path.GetFileName(folder)))
            {
                TryDeleteFolder(folder);
            }
        }

        WriteList(cleanupListPath, kept);
        return deleted;
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

    /// <summary>
    /// Starts the program at <paramref name="exePath"/> (the new version), telling it the id of this process (still closing,
    /// it may hold the project and its own <c>.old</c> image for a moment).
    /// </summary>
    public static void Start(string exePath, int afterUpdatePid) => Process.Start(StartInfo(exePath, afterUpdatePid));

    /// <summary>How <see cref="Start"/> starts the new version: <c>--after-update &lt;pid&gt;</c>.</summary>
    internal static ProcessStartInfo StartInfo(string exePath, int afterUpdatePid) =>
        new(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            Arguments = string.Create(CultureInfo.InvariantCulture, $"--after-update {afterUpdatePid}"),
        };

    /// <summary>Sees every file operation of <see cref="Install"/> ("extract|path", "move|from|to"); tests only.</summary>
    internal static Action<string>? Trace { get; set; }

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

    /// <summary>The files of the release zip, checked: the app at the top, nothing outside the folder, no name twice.</summary>
    private static List<Swap> Plan(ZipArchive zip, string root, string staging)
    {
        var files = new List<Swap>();
        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
        {
            var parts = entry.FullName.Split('/', '\\');
            if (Path.IsPathRooted(entry.FullName) || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException($"The download has a file outside the app folder: {entry.FullName}.");
            }

            var target = Path.Combine([root, .. parts]);
            if (Directory.Exists(target))
            {
                throw new InvalidDataException($"The download has a file where the app folder has a folder: {entry.FullName}.");
            }

            files.Add(new Swap(entry, target, Path.Combine([staging, .. parts]), parts.Length == 1 && parts[0].Equals(AppExe, StringComparison.OrdinalIgnoreCase)));
        }

        if (!files.Any(f => f.IsApp))
        {
            throw new InvalidDataException($"The download is not a SCUM Modding Studio release (no {AppExe}).");
        }

        if (files.DistinctBy(f => f.Target, StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        {
            throw new InvalidDataException("The download has a file twice.");
        }

        return files;
    }

    /// <summary><c>&lt;file&gt;.old</c>, or a unique <c>.old-&lt;ticks&gt;</c> when that one is still held (then added to <paramref name="held"/>).</summary>
    private static string BackupFor(string target, List<string> held)
    {
        var old = target + OldSuffix;
        if (!File.Exists(old) || TryDelete(old))
        {
            return old;
        }

        held.Add(old);
        var ticks = DateTime.UtcNow.Ticks;
        while (File.Exists(string.Create(CultureInfo.InvariantCulture, $"{old}-{ticks}")))
        {
            ticks++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{old}-{ticks}");
    }

    /// <summary>Renames <paramref name="from"/>, retrying while a scanner or indexer briefly holds it.</summary>
    /// <exception cref="UpdateFileInUseException">Still held after the retries.</exception>
    private static void Move(string from, string to, string appFile)
    {
        Trace?.Invoke($"move|{from}|{to}");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MoveRetries.Length)
                {
                    throw new UpdateFileInUseException(appFile, ex);
                }

                Thread.Sleep(MoveRetries[attempt]);
            }
        }
    }

    /// <summary>Puts the old files back, last first; a new file that cannot be moved out stays (complete) rather than none.</summary>
    private static void RollBack(List<Swap> started)
    {
        for (var i = started.Count - 1; i >= 0; i--)
        {
            var file = started[i];
            var free = !file.InPlace || TryMove(file.Target, file.Staged);
            if (file.BackedUp && free)
            {
                TryMove(file.Backup!, file.Target);
            }
        }

        static bool TryMove(string from, string to)
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static bool IsBackupIn(string root, string path) =>
        Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && BackupName.IsMatch(Path.GetFileName(path));

    private static string[] ReadList(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddToList(string path, IEnumerable<string> files) =>
        WriteList(path, [.. ReadList(path).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase)]);

    private static void WriteList(string path, IReadOnlyCollection<string> files)
    {
        if (files.Count == 0)
        {
            TryDelete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(files));
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the next start removes it
        }
    }

    private static HttpClient Client(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
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

    /// <summary>One file of the release: where it is staged, where it goes, and how far its swap got.</summary>
    private sealed record Swap(ZipArchiveEntry Entry, string Target, string Staged, bool IsApp)
    {
        public string? Backup { get; set; }

        public bool BackedUp { get; set; }

        public bool InPlace { get; set; }
    }
}

/// <summary>A file of the app could not be renamed during an update: another program has it open.</summary>
public sealed class UpdateFileInUseException : IOException
{
    /// <summary>Creates the exception for <paramref name="filePath"/>.</summary>
    public UpdateFileInUseException(string filePath, Exception innerException)
        : base($"{filePath} is in use by another program.", innerException)
    {
        FilePath = filePath;
        HResult = innerException.HResult;
    }

    /// <summary>The app file that could not be swapped.</summary>
    public string FilePath { get; }
}
