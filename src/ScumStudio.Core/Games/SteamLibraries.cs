namespace ScumStudio.Core.Games;

/// <summary>An installed Steam app read from <c>steamapps/appmanifest_&lt;id&gt;.acf</c>.</summary>
/// <param name="AppId">Steam app id.</param>
/// <param name="Name">Display name from the manifest (may be empty).</param>
/// <param name="InstallDirName">The manifest's <c>installdir</c> (folder name under <c>steamapps/common</c>).</param>
/// <param name="LibraryPath">Steam library containing the app.</param>
/// <param name="ManifestPath">Full path of the manifest file.</param>
public sealed record SteamAppManifest(string AppId, string Name, string InstallDirName, string LibraryPath, string ManifestPath)
{
    /// <summary><c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;</c> (may not exist if the app was moved).</summary>
    public string InstallPath => Path.Combine(LibraryPath, "steamapps", "common", InstallDirName);
}

/// <summary>
/// Pure file-system helpers for Steam libraries: <c>libraryfolders.vdf</c> and app manifests. No registry access.
/// </summary>
public static class SteamLibraries
{
    /// <summary>
    /// Returns the libraries of a Steam installation: <paramref name="steamRoot"/> itself plus every path listed in
    /// <c>steamapps/libraryfolders.vdf</c> (current format with <c>"path"</c> entries, and the pre-2021 format with
    /// numbered values; <c>config/libraryfolders.vdf</c> is read too). Only existing folders, de-duplicated. Malformed
    /// files are skipped and reported through <paramref name="onError"/>.
    /// </summary>
    /// <param name="steamRoot">Steam installation folder (the one containing <c>steam.exe</c> / <c>steamapps</c>).</param>
    /// <param name="onError">Optional callback for unreadable or malformed library files.</param>
    public static IReadOnlyList<string> ReadLibraryFolders(string steamRoot, Action<string, Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(steamRoot);
        var libraries = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (Directory.Exists(full) && seen.Add(full))
            {
                libraries.Add(full);
            }
        }

        Add(steamRoot);
        var candidates = new[]
        {
            FileSystemPaths.ChildFile(steamRoot, "steamapps", "libraryfolders.vdf"),
            FileSystemPaths.ChildFile(steamRoot, "config", "libraryfolders.vdf"),
        };
        foreach (var file in candidates)
        {
            if (file is null)
            {
                continue;
            }

            try
            {
                var root = KeyValuesNode.ParseFile(file);
                var folders = root.Child("libraryfolders");
                if (folders is null)
                {
                    continue;
                }

                foreach (var entry in folders.Children)
                {
                    if (entry.IsSection)
                    {
                        Add(entry.GetValue("path"));
                    }
                    else if (int.TryParse(entry.Key, out _))
                    {
                        Add(entry.Value);
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(file, ex);
            }
        }

        return libraries;
    }

    /// <summary>
    /// Reads <c>&lt;library&gt;/steamapps/appmanifest_&lt;appId&gt;.acf</c>; null when missing or without <c>installdir</c>.
    /// </summary>
    /// <exception cref="FormatException">The manifest is malformed.</exception>
    public static SteamAppManifest? ReadAppManifest(string libraryPath, string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var manifestPath = FileSystemPaths.ChildFile(libraryPath, "steamapps", $"appmanifest_{appId}.acf");
        if (manifestPath is null)
        {
            return null;
        }

        var state = KeyValuesNode.ParseFile(manifestPath).Child("AppState");
        var installDir = state?.GetValue("installdir");
        if (state is null || string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        var library = Path.GetDirectoryName(Path.GetDirectoryName(manifestPath)!)!;
        return new SteamAppManifest(state.GetValue("appid") ?? appId, state.GetValue("name") ?? string.Empty, installDir, library, manifestPath);
    }

    /// <summary>Comparer for file-system paths on the current OS.</summary>
    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>Case-tolerant path lookups (Linux file systems are case-sensitive; Steam and UE paths are not).</summary>
internal static class FileSystemPaths
{
    /// <summary>
    /// Resolves <paramref name="segments"/> below <paramref name="root"/> as existing directories, matching each segment
    /// exactly first and then case-insensitively. Returns null when any segment is missing.
    /// </summary>
    public static string? ChildDirectory(string root, params string[] segments)
    {
        var current = root;
        if (!Directory.Exists(current))
        {
            return null;
        }

        foreach (var segment in segments)
        {
            var next = Match(current, segment, directory: true);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Resolves an existing file below <paramref name="root"/>; the last segment is the file name. Returns null when missing.
    /// </summary>
    public static string? ChildFile(string root, params string[] segments)
    {
        if (segments.Length == 0)
        {
            return null;
        }

        var folder = ChildDirectory(root, segments[..^1]);
        return folder is null ? null : Match(folder, segments[^1], directory: false);
    }

    private static string? Match(string parent, string name, bool directory)
    {
        var exact = Path.Combine(parent, name);
        if (directory ? Directory.Exists(exact) : File.Exists(exact))
        {
            return exact;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
            var matches = directory
                ? Directory.EnumerateDirectories(parent, name, options)
                : Directory.EnumerateFiles(parent, name, options);
            return matches.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
