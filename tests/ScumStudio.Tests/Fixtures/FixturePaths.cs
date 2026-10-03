namespace ScumStudio.Tests.Fixtures;

/// <summary>
/// Locations inside the (optional) SCUM fixture archive. The archive is never part of the repository:
/// point the <c>SCUM_FIXTURES</c> environment variable at an extracted copy (the folder that contains
/// <c>orig/</c>, <c>orig_extra/</c>, <c>build_sep*/</c> and the mod <c>*.pak</c> files).
/// Tests that need it use <see cref="FixturesFactAttribute"/> / <see cref="FixturesTheoryAttribute"/>
/// so they are skipped, not failed, when it is missing.
/// </summary>
public static class FixturePaths
{
    /// <summary>Name of the environment variable holding the fixture root.</summary>
    public const string EnvironmentVariable = "SCUM_FIXTURES";

    /// <summary>The fixture root from <c>SCUM_FIXTURES</c>, or null when unset/empty.</summary>
    public static string? Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim().Trim('"'));
        }
    }

    /// <summary>True when <c>SCUM_FIXTURES</c> is set and the directory exists.</summary>
    public static bool IsAvailable => Root is { } root && Directory.Exists(root);

    /// <summary>
    /// Reason used as xunit Skip text when fixtures are unavailable; null when they are available.
    /// </summary>
    public static string? SkipReason =>
        Root is null
            ? $"{EnvironmentVariable} is not set (optional SCUM fixture archive; see docs/BUILDING.md)."
            : !Directory.Exists(Root)
                ? $"{EnvironmentVariable} points to a missing directory."
                : null;

    /// <summary>Fixture root; throws when unavailable (only call from fixture-gated tests).</summary>
    public static string RequireRoot() =>
        IsAvailable ? Root! : throw new InvalidOperationException(SkipReason);

    /// <summary>Combines segments below the fixture root.</summary>
    public static string Combine(params string[] segments) =>
        Path.Combine([RequireRoot(), .. segments]);

    /// <summary><c>orig/</c>: stock cooked packages as extracted from the game (project root containing <c>SCUM/</c>).</summary>
    public static string OrigRoot => Combine("orig");

    /// <summary><c>orig/SCUM/Content</c>.</summary>
    public static string OrigContent => Path.Combine(OrigRoot, "SCUM", "Content");

    /// <summary><c>orig_extra/</c>: stock presets and <c>SCUM/AssetRegistry.bin</c>.</summary>
    public static string OrigExtraRoot => Combine("orig_extra");

    /// <summary>Stock <c>orig_extra/SCUM/AssetRegistry.bin</c>.</summary>
    public static string StockAssetRegistry => Path.Combine(OrigExtraRoot, "SCUM", "AssetRegistry.bin");

    /// <summary><c>tools/</c>: the Python toolchain (read-only reference; never executed by tests).</summary>
    public static string ToolsRoot => Combine("tools");

    /// <summary>Converts a virtual path such as <c>SCUM/Content/...</c> to a file under <see cref="OrigRoot"/>.</summary>
    public static string OrigFile(string virtualPath) =>
        Path.Combine([OrigRoot, .. virtualPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Client mod paks at the fixture root (<c>pakchunk*_P.pak</c>, excluding <c>server_</c> ones), sorted.</summary>
    public static IReadOnlyList<string> ClientPaks() =>
        Directory.EnumerateFiles(RequireRoot(), "pakchunk*_P.pak", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Server mod paks at the fixture root (<c>server_pakchunk*_P.pak</c>), sorted.</summary>
    public static IReadOnlyList<string> ServerPaks() =>
        Directory.EnumerateFiles(RequireRoot(), "server_pakchunk*_P.pak", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Path of a pak at the fixture root by file name (e.g. <c>pakchunk97-BMW_P.pak</c>).</summary>
    public static string Pak(string fileName) => Combine(fileName);

    /// <summary>
    /// Unpacked mod trees <c>build_sep*/</c> (each a project root containing <c>SCUM/Content/...</c>), sorted.
    /// </summary>
    public static IReadOnlyList<string> BuildSepTrees() =>
        Directory.EnumerateDirectories(RequireRoot(), "build_sep*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>A specific unpacked tree, e.g. <c>BuildSepTree("BMW")</c> = <c>build_sep_BMW</c>; null name = <c>build_sep</c>.</summary>
    public static string BuildSepTree(string? suffix = null) =>
        Combine(string.IsNullOrEmpty(suffix) ? "build_sep" : "build_sep_" + suffix);

    /// <summary>
    /// Enumerates files below <see cref="OrigContent"/> with the given extension (e.g. ".uasset"), sorted.
    /// </summary>
    public static IReadOnlyList<string> OrigFiles(string extension) =>
        Directory.EnumerateFiles(OrigContent, "*" + extension, SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
