using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>A UE4SS release asset found on GitHub.</summary>
/// <param name="Name">File name of the zip.</param>
/// <param name="Url">Download address.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">The SHA-256 GitHub published for the file (lower-case hex), or null.</param>
public sealed record Ue4ssRelease(string Name, string Url, long Size, string? Sha256);

/// <summary>Installs UE4SS into a server folder and sets it up for a dedicated server (no window, nothing that hangs SCUM).</summary>
public static class Ue4ssInstaller
{
    /// <summary>The latest experimental release of the official repository, the build SCUM servers run.</summary>
    public const string ReleasesApi = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/tags/experimental-latest";

    /// <summary>Settings a server needs, checked on SCUM Server 2026-10-25: without the forced iteration the game thread hangs, with the stock hooks it crashes when a player joins.</summary>
    public static IReadOnlyDictionary<string, string> ServerSettings { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bForceGUObjectArrayForIteration"] = "true",
        ["bUseUObjectArrayCache"] = "false",
        ["EnableAutoReloadingLuaMods"] = "1",
        ["EnableHotReloadSystem"] = "0",
        ["ConsoleEnabled"] = "0",
        ["GuiConsoleEnabled"] = "0",
        ["GuiConsoleVisible"] = "0",

        // With UE4SS's stock hooks SCUM Server 1.3.4.2 crashes on every player join (access violation at SCUMServer.exe+0x4295f3f,
        // found here and in the Takaro SCUM connector, gettakaro/connectors games/scum). Only the two hooks RegisterHook and
        // ExecuteInGameThread need stay on.
        ["HookProcessInternal"] = "0",
        ["HookProcessLocalScriptFunction"] = "0",
        ["HookInitGameState"] = "0",
        ["HookLoadMap"] = "0",
        ["HookCallFunctionByNameWithArguments"] = "0",
        ["HookBeginPlay"] = "0",
        ["HookEndPlay"] = "0",
        ["HookLocalPlayerExec"] = "0",
        ["HookAActorTick"] = "0",
        ["HookEngineTick"] = "1",
        ["HookGameViewportClientTick"] = "0",
        ["HookUObjectProcessEvent"] = "1",
        ["HookProcessConsoleExec"] = "0",
        ["HookUStructLink"] = "0",
    };

    /// <summary>The standard (not zDEV) UE4SS zip of the latest experimental release.</summary>
    public static async Task<Ue4ssRelease> FindLatestAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("ScumStudio");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.StartsWith("UE4SS_v", StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal))
            {
                continue;
            }

            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new Ue4ssRelease(name, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToLowerInvariant() : null);
        }

        throw new InvalidDataException("The latest UE4SS release has no standard zip.");
    }

    /// <summary>Downloads <paramref name="release"/> into <paramref name="folder"/> and checks it against the published SHA-256.</summary>
    /// <exception cref="InvalidDataException">The file's SHA-256 is not the published one.</exception>
    public static async Task<string> DownloadAsync(HttpClient http, Ue4ssRelease release, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(release.Name));
        using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (release.Sha256 is not null && !string.Equals(Sha256Of(path), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded UE4SS file does not match the SHA-256 GitHub published, so it was deleted.");
        }

        return path;
    }

    /// <summary>SHA-256 of a file as lower-case hex.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Unpacks a UE4SS zip (<c>dwmapi.dll</c> and <c>ue4ss/...</c>) into the server folder. An existing settings file and the existing
    /// mods are kept; <paramref name="forServer"/> then applies <see cref="ServerSettings"/> and switches the stock mods off.
    /// </summary>
    /// <returns>The number of files written.</returns>
    /// <exception cref="InvalidDataException">The zip is not a UE4SS package or has paths that leave the folder.</exception>
    public static int InstallFromZip(ServerInstall server, string zipPath, bool forServer = true)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var zip = ZipFile.OpenRead(zipPath);
        if (!zip.Entries.Any(e => e.FullName == "dwmapi.dll") || !zip.Entries.Any(e => e.FullName == "ue4ss/UE4SS.dll"))
        {
            throw new InvalidDataException("This zip is not a UE4SS package (dwmapi.dll and ue4ss/UE4SS.dll are missing).");
        }

        var root = Path.GetFullPath(server.Win64) + Path.DirectorySeparatorChar;
        var written = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(server.Win64, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The zip has a path outside the server folder: {entry.FullName}");
            }

            var keep = File.Exists(target) && (entry.FullName == "ue4ss/UE4SS-settings.ini" || entry.FullName.StartsWith("ue4ss/Mods/", StringComparison.Ordinal));
            if (keep)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (entry.FullName == "dwmapi.dll" && File.Exists(server.ParkedProxyDll))
            {
                File.Delete(server.ParkedProxyDll);
            }

            entry.ExtractToFile(target, overwrite: true);
            written++;
        }

        if (forServer)
        {
            ApplyServerSettings(server);
            new ServerModStore(server).DisableStockMods();
        }

        return written;
    }

    /// <summary>Writes <see cref="ServerSettings"/> into UE4SS-settings.ini (other lines, comments and line ends stay).</summary>
    public static void ApplyServerSettings(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var text = File.ReadAllText(server.SettingsFile);
        foreach (var (key, value) in ServerSettings)
        {
            text = Regex.Replace(text, $@"^(\s*{Regex.Escape(key)}\s*=\s*).*$", m => m.Groups[1].Value + value, RegexOptions.Multiline);
        }

        File.WriteAllText(server.SettingsFile, text);
    }

    /// <summary>Switches UE4SS off (the proxy DLL is parked) or on, so the server can run without it.</summary>
    public static void SetActive(ServerInstall server, bool active)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (active && File.Exists(server.ParkedProxyDll) && !File.Exists(server.ProxyDll))
        {
            File.Move(server.ParkedProxyDll, server.ProxyDll);
        }
        else if (!active && File.Exists(server.ProxyDll))
        {
            File.Move(server.ProxyDll, server.ParkedProxyDll, overwrite: true);
        }
    }
}
