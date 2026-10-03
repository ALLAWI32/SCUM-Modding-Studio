using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ScumStudio.Core.Games;

/// <summary>Options for <see cref="GameLocator"/>; null lists mean "detect automatically".</summary>
public sealed record GameLocatorOptions
{
    /// <summary>Steam installation folders to read; null = registry (Windows) plus well-known locations.</summary>
    public IReadOnlyList<string>? SteamRoots { get; init; }

    /// <summary>
    /// Folders searched for <c>SCUMServer.exe</c>; null = well-known locations such as <c>C:\SCUMServer</c> on every
    /// fixed drive (Windows only).
    /// </summary>
    public IReadOnlyList<string>? ServerSearchRoots { get; init; }

    /// <summary>How many folder levels below each server search root are scanned.</summary>
    public int ServerSearchDepth { get; init; } = 6;

    /// <summary>Upper bound on folders visited per server search root (keeps a scan of a big drive short).</summary>
    public int ServerSearchMaxDirectories { get; init; } = 20_000;
}

/// <summary>
/// Finds SCUM installations: the client through Steam (registry, <c>libraryfolders.vdf</c>, <c>appmanifest_513710.acf</c>)
/// and dedicated servers through Steam (app 3792580) or by searching folders for <c>SCUMServer.exe</c>.
/// </summary>
/// <remarks>
/// Layouts: client <c>&lt;library&gt;\steamapps\common\SCUM\SCUM\Content\Paks</c> with
/// <c>SCUM\Binaries\Win64\SCUM.exe</c>; server <c>&lt;root&gt;\SCUM\Binaries\Win64\SCUMServer.exe</c> with
/// <c>&lt;root&gt;\SCUM\Content\Paks</c> (e.g. <c>C:\SCUMServer\server</c>). Everything except the default Steam-root
/// discovery is plain file-system logic, testable with fake folder trees on any OS.
/// </remarks>
public sealed class GameLocator
{
    /// <summary>Steam app id of the SCUM client.</summary>
    public const string ScumAppId = "513710";

    /// <summary>Steam app id of the SCUM dedicated server.</summary>
    public const string ScumServerAppId = "3792580";

    /// <summary>Client executable name.</summary>
    public const string ClientExecutableName = "SCUM.exe";

    /// <summary>Dedicated server executable name.</summary>
    public const string ServerExecutableName = "SCUMServer.exe";

    private static readonly HashSet<string> SkippedSearchFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Saved", "Engine", "Logs", "~mods", ".git", "node_modules", "$RECYCLE.BIN",
        "System Volume Information", "Windows", "shadercache", "compatdata", "downloading", "temp",
    };

    private readonly GameLocatorOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a locator.</summary>
    public GameLocator(GameLocatorOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new GameLocatorOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Existing Steam installation folders (configured or detected), de-duplicated.</summary>
    public IReadOnlyList<string> GetSteamRoots() => DistinctExisting(_options.SteamRoots ?? DefaultSteamRoots());

    /// <summary>All Steam libraries of all Steam roots.</summary>
    public IReadOnlyList<string> GetSteamLibraries()
    {
        var libraries = new List<string>();
        foreach (var root in GetSteamRoots())
        {
            libraries.AddRange(SteamLibraries.ReadLibraryFolders(
                root,
                (file, ex) => _logger.LogWarning("Could not read Steam library list {File}: {Error}", file, ex.Message)));
        }

        return DistinctExisting(libraries);
    }

    /// <summary>The first SCUM client installation found, or null.</summary>
    public GameInstall? FindGame() => FindGames().FirstOrDefault();

    /// <summary>Every SCUM client installation found in the Steam libraries.</summary>
    public IReadOnlyList<GameInstall> FindGames()
    {
        var results = new List<GameInstall>();
        foreach (var library in GetSteamLibraries())
        {
            if (TryFindInLibrary(library, ScumAppId, GameInstallKind.Client) is { } install)
            {
                results.Add(install);
            }
        }

        if (_options.SteamRoots is null && OperatingSystem.IsWindows())
        {
            foreach (var location in UninstallLocationsFromRegistry(ScumAppId))
            {
                if (FromInstallDirectory(location, GameInstallKind.Client, "Windows uninstall entry") is { } install)
                {
                    results.Add(install);
                }
            }
        }

        var distinct = DistinctByPaks(results);
        _logger.LogDebug("Found {Count} SCUM client installation(s).", distinct.Count);
        return distinct;
    }

    /// <summary>
    /// Every SCUM dedicated server found: Steam app 3792580 in the Steam libraries, then <c>SCUMServer.exe</c> below
    /// the server search roots.
    /// </summary>
    public IReadOnlyList<GameInstall> FindDedicatedServers()
    {
        var results = new List<GameInstall>();
        foreach (var library in GetSteamLibraries())
        {
            if (TryFindInLibrary(library, ScumServerAppId, GameInstallKind.DedicatedServer) is { } install)
            {
                results.Add(install);
            }
        }

        foreach (var root in DistinctExisting(_options.ServerSearchRoots ?? DefaultServerSearchRoots()))
        {
            results.AddRange(FindDedicatedServersUnder(root, _options.ServerSearchDepth, _options.ServerSearchMaxDirectories));
        }

        return DistinctByPaks(results);
    }

    /// <summary>The first dedicated server found, or null.</summary>
    public GameInstall? FindDedicatedServer() => FindDedicatedServers().FirstOrDefault();

    /// <summary>
    /// Looks for a Steam app in one library: reads its app manifest (<c>installdir</c>), falling back for the client to
    /// <c>steamapps/common/SCUM</c>. Returns null when absent or when the Paks folder is missing.
    /// </summary>
    /// <exception cref="FormatException">The app manifest is malformed.</exception>
    public static GameInstall? FindInLibrary(string libraryPath, string appId, GameInstallKind kind)
    {
        var manifest = SteamLibraries.ReadAppManifest(libraryPath, appId);
        if (manifest is not null)
        {
            var installPath = FileSystemPaths.ChildDirectory(libraryPath, "steamapps", "common", manifest.InstallDirName);
            return installPath is null
                ? null
                : FromInstallDirectory(installPath, kind, $"Steam library {libraryPath} (appmanifest_{appId}.acf)");
        }

        if (kind == GameInstallKind.Client && appId == ScumAppId &&
            FileSystemPaths.ChildDirectory(libraryPath, "steamapps", "common", "SCUM") is { } folder)
        {
            return FromInstallDirectory(folder, kind, $"Steam library {libraryPath} (folder steamapps/common/SCUM)");
        }

        return null;
    }

    /// <summary>
    /// Builds an install from its root folder (the one containing <c>SCUM/Content/Paks</c>); null when that Paks
    /// folder does not exist.
    /// </summary>
    public static GameInstall? FromInstallDirectory(string installDirectory, GameInstallKind kind, string source)
    {
        var paks = FileSystemPaths.ChildDirectory(installDirectory, "SCUM", "Content", "Paks");
        if (paks is null)
        {
            return null;
        }

        var exe = FileSystemPaths.ChildFile(
            installDirectory, "SCUM", "Binaries", "Win64",
            kind == GameInstallKind.Client ? ClientExecutableName : ServerExecutableName);
        return new GameInstall(kind, Path.GetFullPath(installDirectory), Path.GetFullPath(paks), exe, source);
    }

    /// <summary>
    /// Breadth-first search below <paramref name="root"/> for <c>SCUMServer.exe</c> (case-insensitive), skipping
    /// <c>Content</c>, <c>Saved</c>, <c>Engine</c>, symbolic links and similar. Each hit is resolved to its Paks folder
    /// with <see cref="ResolvePaksFolder"/>.
    /// </summary>
    public static IReadOnlyList<GameInstall> FindDedicatedServersUnder(string root, int maxDepth = 6, int maxDirectories = 20_000)
    {
        var results = new List<GameInstall>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        var fileOptions = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };
        var dirOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((Path.GetFullPath(root), 0));
        var visited = 0;
        while (queue.Count > 0 && visited < maxDirectories)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, ServerExecutableName, fileOptions))
                {
                    if (ResolvePaksFolder(exe) is { } paks)
                    {
                        var install = InstallRootFromPaks(paks) ?? Path.GetDirectoryName(exe)!;
                        results.Add(new GameInstall(GameInstallKind.DedicatedServer, install, paks, exe, $"{ServerExecutableName} under {root}"));
                    }
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(dir, "*", dirOptions))
                {
                    if (!SkippedSearchFolders.Contains(Path.GetFileName(sub)))
                    {
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable folder: skip it.
            }
        }

        return DistinctByPaks(results);
    }

    /// <summary>
    /// Resolves a user-chosen file or folder to a SCUM <c>Content/Paks</c> folder. Accepts the install root, the
    /// <c>SCUM</c> project folder, <c>Content</c>, <c>Paks</c>, <c>Paks/~mods</c>, <c>Binaries/Win64</c>, or a file
    /// inside any of them (e.g. <c>SCUM.exe</c>, <c>SCUMServer.exe</c>, a <c>.pak</c>). Returns null when none is found
    /// within three parent levels.
    /// </summary>
    public static string? ResolvePaksFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var start = File.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (start is null || !Directory.Exists(start))
        {
            return null;
        }

        var current = new DirectoryInfo(start);
        for (var level = 0; current is not null && level <= 3; level++, current = current.Parent)
        {
            if (NameIs(current, GameInstall.ModsFolderName) && current.Parent is { } paksParent &&
                NameIs(paksParent, "Paks") && paksParent.Parent is { } contentOfMods && NameIs(contentOfMods, "Content"))
            {
                return paksParent.FullName;
            }

            if (NameIs(current, "Paks") && current.Parent is { } content && NameIs(content, "Content"))
            {
                return current.FullName;
            }

            if (NameIs(current, "Content") && FileSystemPaths.ChildDirectory(current.FullName, "Paks") is { } fromContent)
            {
                return Path.GetFullPath(fromContent);
            }

            if (FileSystemPaths.ChildDirectory(current.FullName, "Content", "Paks") is { } fromProject)
            {
                return Path.GetFullPath(fromProject);
            }

            if (FileSystemPaths.ChildDirectory(current.FullName, "SCUM", "Content", "Paks") is { } fromInstall)
            {
                return Path.GetFullPath(fromInstall);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="paksFolder"/> exists and contains at least one <c>.pak</c> file.</summary>
    public static bool ContainsPaks(string paksFolder)
    {
        try
        {
            return Directory.Exists(paksFolder) &&
                   Directory.EnumerateFiles(paksFolder, "*.pak", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Candidate Steam installation folders for this machine: on Windows the registry (<c>HKCU\Software\Valve\Steam
    /// SteamPath</c>, <c>HKLM\SOFTWARE\(WOW6432Node\)Valve\Steam InstallPath</c>) and Program Files; on Linux/macOS the
    /// usual Steam, Flatpak and Snap locations. Not filtered for existence.
    /// </summary>
    public static IReadOnlyList<string> DefaultSteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            roots.AddRange(SteamRootsFromRegistry());
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var programFiles = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(programFiles))
                {
                    roots.Add(Path.Combine(programFiles, "Steam"));
                }
            }

            return roots;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return roots;
        }

        if (OperatingSystem.IsMacOS())
        {
            roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }

        roots.Add(Path.Combine(home, ".steam", "steam"));
        roots.Add(Path.Combine(home, ".steam", "root"));
        roots.Add(Path.Combine(home, ".local", "share", "Steam"));
        roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        roots.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        return roots;
    }

    /// <summary>
    /// Candidate folders to search for a dedicated server: on Windows <c>SCUMServer</c>, <c>SCUM Server</c>,
    /// <c>SCUMDedicatedServer</c> and <c>steamcmd</c> at the root of every fixed drive. Empty elsewhere.
    /// </summary>
    public static IReadOnlyList<string> DefaultServerSearchRoots()
    {
        var roots = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return roots;
        }

        foreach (var drive in SafeFixedDrives())
        {
            foreach (var name in new[] { "SCUMServer", "SCUM Server", "SCUM_Server", "SCUMDedicatedServer", "steamcmd" })
            {
                roots.Add(Path.Combine(drive, name));
            }
        }

        return roots;
    }

    private GameInstall? TryFindInLibrary(string library, string appId, GameInstallKind kind)
    {
        try
        {
            return FindInLibrary(library, appId, kind);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read the app manifest {AppId} in {Library}: {Error}", appId, library, ex.Message);
            return null;
        }
    }

    private static string? InstallRootFromPaks(string paks)
    {
        // <install>/SCUM/Content/Paks
        var content = Path.GetDirectoryName(paks);
        var project = content is null ? null : Path.GetDirectoryName(content);
        return project is null ? null : Path.GetDirectoryName(project);
    }

    private static bool NameIs(DirectoryInfo directory, string name) =>
        string.Equals(directory.Name, name, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DistinctExisting(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(SteamLibraries.PathComparer);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!Directory.Exists(full))
                {
                    continue;
                }

                // ~/.steam/steam is usually a symlink to ~/.local/share/Steam: compare resolved targets.
                var resolved = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
                if (seen.Add(Path.TrimEndingDirectorySeparator(resolved)))
                {
                    result.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Invalid or unreadable candidate: ignore.
            }
        }

        return result;
    }

    private static IReadOnlyList<GameInstall> DistinctByPaks(IEnumerable<GameInstall> installs)
    {
        var seen = new HashSet<string>(SteamLibraries.PathComparer);
        return installs.Where(i => seen.Add(Path.TrimEndingDirectorySeparator(i.PaksDirectory))).ToList();
    }

    private static IEnumerable<string> SafeFixedDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool usable;
            try
            {
                usable = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                usable = false;
            }

            if (usable)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamRootsFromRegistry()
    {
        var values = new[]
        {
            ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Replace('/', '\\'));
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> UninstallLocationsFromRegistry(string appId)
    {
        var subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {appId}";
        var values = new[]
        {
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, subKey, "InstallLocation"),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, subKey, "InstallLocation"),
        };
        return values.Where(v => !string.IsNullOrWhiteSpace(v) && Directory.Exists(v)).Select(v => v!);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
