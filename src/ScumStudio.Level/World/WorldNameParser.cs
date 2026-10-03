using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Level.World;

/// <summary>
/// Classifies the packages of <c>SCUM/Content/ConZ_Files/Maps/The_Island/**</c> from their names and folders alone
/// (no package is opened). Naming rules from the pak index survey:
/// <list type="bullet">
/// <item><description><c>&lt;Cell&gt;_&lt;Row&gt;_&lt;Name&gt;...</c> POI/grid sublevels, cells <c>A-D, Z</c> x rows <c>0-4</c>;</description></item>
/// <item><description><c>Landscape_&lt;Cell&gt;_&lt;Row&gt;_&lt;Quadrant 1-4&gt;[b|c|d]</c> landscape tiles;</description></item>
/// <item><description><c>TV_Base_&lt;Cell&gt;_&lt;Row&gt;[_...]</c> (and compact <c>B2_TV_Base_...</c>) bunker sublevels;</description></item>
/// <item><description><c>AbandonedCity_PripyatLike/</c> abandoned city sublevels; <c>HLOD/</c> proxies; <c>*_BuiltData</c> lighting data;</description></item>
/// <item><description>compact <c>B3_Castle_Ruins</c> / trailing <c>Safe_Zone_D_2</c> cell tokens on special sublevels.</description></item>
/// </list>
/// </summary>
public static partial class WorldNameParser
{
    /// <summary>Name of the persistent level package.</summary>
    public const string PersistentLevelName = "The_Island";

    /// <summary>Folder of the abandoned city sublevels.</summary>
    public const string PripyatFolder = "AbandonedCity_PripyatLike";

    /// <summary>Folder of the HLOD proxy packages.</summary>
    public const string HlodFolder = "HLOD";

    private const string BuiltDataSuffix = "_BuiltData";

    /// <summary>
    /// Classifies a virtual file path below <paramref name="worldRoot"/>. Returns null for paths outside the root and for
    /// files that do not start a package (<c>.uexp</c>, <c>.ubulk</c>, ...).
    /// </summary>
    /// <param name="filePath">Virtual path such as <c>SCUM/Content/ConZ_Files/Maps/The_Island/A_0_Outpost.umap</c>.</param>
    /// <param name="worldRoot">World folder (virtual path), default <see cref="WorldIndex.DefaultRoot"/>.</param>
    /// <param name="projectName">Project root name used to build <c>/Game/...</c> package paths.</param>
    public static WorldPackage? Classify(string filePath, string worldRoot = WorldIndex.DefaultRoot, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var path = VirtualPath.Normalize(filePath);
        var root = VirtualPath.Normalize(worldRoot);
        if (!AssetPaths.IsPackageFile(path) || !VirtualPath.IsUnder(path, root) || path.Length <= root.Length)
        {
            return null;
        }

        var relative = root.Length == 0 ? path : path[(root.Length + 1)..];
        var folder = VirtualPath.GetDirectory(relative);
        var fileName = VirtualPath.GetFileName(relative);
        var extension = VirtualPath.GetExtension(fileName);
        var name = fileName[..^extension.Length];
        var isMap = extension.Equals(".umap", StringComparison.OrdinalIgnoreCase);
        var packagePath = AssetPaths.ToPackagePath(path, projectName);
        return ClassifyName(path, packagePath, name, folder, isMap);
    }

    /// <summary>
    /// Classifies a package by name, folder (relative to the world root) and whether it is a <c>.umap</c>.
    /// </summary>
    public static WorldPackage ClassifyName(string filePath, string packagePath, string name, string folder, bool isMap)
    {
        ArgumentNullException.ThrowIfNull(name);
        folder = VirtualPath.Normalize(folder ?? string.Empty);
        var topFolder = folder.Split('/', 2)[0];

        WorldPackage Make(WorldPackageKind kind, MapCell? cell) => new(filePath, packagePath, name, folder, kind, cell, isMap);

        if (name.EndsWith(BuiltDataSuffix, StringComparison.OrdinalIgnoreCase) && name.Length > BuiltDataSuffix.Length)
        {
            var owner = name[..^BuiltDataSuffix.Length];
            return Make(WorldPackageKind.BuiltData, ParseCell(owner)) with { Owner = owner };
        }

        var hlod = HlodRegex().Match(name);
        if (hlod.Success || topFolder.Equals(HlodFolder, StringComparison.OrdinalIgnoreCase))
        {
            var owner = hlod.Success ? hlod.Groups["owner"].Value : name;
            return Make(WorldPackageKind.Hlod, ParseCell(owner)) with { Owner = owner };
        }

        if (isMap && folder.Length == 0 && name.Equals(PersistentLevelName, StringComparison.OrdinalIgnoreCase))
        {
            return Make(WorldPackageKind.Persistent, null);
        }

        if (topFolder.StartsWith(PripyatFolder, StringComparison.OrdinalIgnoreCase))
        {
            return Make(isMap ? WorldPackageKind.Pripyat : WorldPackageKind.Misc, ParseCell(name));
        }

        var landscape = LandscapeRegex().Match(name);
        if (landscape.Success)
        {
            return Make(WorldPackageKind.Landscape, CellOf(landscape)) with
            {
                LandscapeQuadrant = landscape.Groups["quad"].Value[0] - '0',
                LandscapeVariant = landscape.Groups["var"].Success ? char.ToLowerInvariant(landscape.Groups["var"].Value[0]) : null,
            };
        }

        var tv = TvBaseRegex().Match(name);
        if (!tv.Success)
        {
            tv = CompactTvBaseRegex().Match(name);
        }

        if (tv.Success)
        {
            return Make(WorldPackageKind.TvBase, CellOf(tv));
        }

        var grid = GridRegex().Match(name);
        if (isMap && grid.Success)
        {
            return Make(WorldPackageKind.Poi, CellOf(grid));
        }

        return Make(WorldPackageKind.Misc, ParseCell(name));
    }

    /// <summary>
    /// Extracts the map cell from any The_Island package name: grid (<c>A_0_...</c>), landscape (<c>Landscape_A_0_1b</c>),
    /// TV base (<c>TV_Base_A_0</c>), compact (<c>B3_Castle_Ruins</c>) or trailing (<c>Safe_Zone_D_2</c>) forms.
    /// </summary>
    public static MapCell? ParseCell(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var regex in new[] { LandscapeRegex(), TvBaseRegex(), CompactTvBaseRegex(), GridRegex(), CompactRegex(), TrailingRegex() })
        {
            var m = regex.Match(name);
            if (m.Success)
            {
                return CellOf(m);
            }
        }

        return null;
    }

    private static MapCell CellOf(Match m) => new(m.Groups["col"].Value[0], m.Groups["row"].Value[0] - '0');

    [GeneratedRegex(@"^(?i:Landscape)_(?<col>[ABCDZ])_(?<row>[0-4])_(?<quad>[1-4])(?<var>[bcdBCD])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LandscapeRegex();

    [GeneratedRegex(@"^(?i:TV_Base)_(?<col>[ABCDZ])_(?<row>[0-4])(?:_.+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TvBaseRegex();

    [GeneratedRegex(@"^(?<col>[ABCDZ])(?<row>[0-4])_(?i:TV_Base)(?:_.+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTvBaseRegex();

    [GeneratedRegex(@"^(?<col>[ABCDZ])_(?<row>[0-4])_.+$", RegexOptions.CultureInvariant)]
    private static partial Regex GridRegex();

    [GeneratedRegex(@"^(?<col>[ABCDZ])(?<row>[0-4])_.+$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactRegex();

    [GeneratedRegex(@"_(?<col>[ABCDZ])_(?<row>[0-4])$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRegex();

    [GeneratedRegex(@"^(?<owner>.+)_\d+_(?i:HLOD)$", RegexOptions.CultureInvariant)]
    private static partial Regex HlodRegex();
}
