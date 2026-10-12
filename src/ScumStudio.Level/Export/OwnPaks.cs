using System.Text.RegularExpressions;

namespace ScumStudio.Level.Export;

/// <summary>
/// The studio's own exported paks are not game files. Copied into <c>SCUM\Content\Paks</c> as the Build page says, an
/// earlier export would otherwise be read as stock: every craftable it shipped is then left out of the next export as
/// "already exists in the game files", and its registry records come back as the game's.
/// </summary>
public static partial class OwnPaks
{
    /// <summary>
    /// False for a pak this studio exports: <c>pakchunk&lt;n&gt;-&lt;mod&gt;_P.pak</c> and its Craftables pak for each of
    /// <paramref name="projectModNames"/> (sanitized as <see cref="ProjectExporter.SanitizeModName"/> does), and every
    /// <c>pakchunk9&lt;2 digits&gt;-*_P.pak</c> (the chunks the studio exports to). Game paks and other mods' paks
    /// (<c>pakchunk99-BMW_P.pak</c>) are true.
    /// </summary>
    public static bool IsGameSource(string pakFileName, IReadOnlyCollection<string> projectModNames)
    {
        ArgumentNullException.ThrowIfNull(pakFileName);
        ArgumentNullException.ThrowIfNull(projectModNames);
        var match = ModPak().Match(Path.GetFileName(pakFileName));
        if (!match.Success)
        {
            return true;
        }

        if (match.Groups["chunk"].Value is { Length: 3 } chunk && chunk[0] == '9')
        {
            return false;
        }

        var name = match.Groups["name"].Value;
        return !projectModNames.Select(ProjectExporter.SanitizeModName).Any(m =>
            string.Equals(name, m, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, m + CraftablesExporter.Suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The mod names whose exported paks are a project's own: the export's mod name (when it has one) and the project
    /// name. Every export source, client and server, uses this one list, so both cooks leave out the same paks.
    /// </summary>
    public static IReadOnlyList<string> ProjectNames(string projectName, string? modName = null)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        return new[] { modName, projectName }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// True when <paramref name="newNames"/> leaves out other paks of <paramref name="source"/> (a Paks folder or one
    /// <c>.pak</c>) than <paramref name="oldNames"/> did: the source must be opened again.
    /// </summary>
    public static bool ChangesSkips(string source, IReadOnlyCollection<string> oldNames, IReadOnlyCollection<string> newNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        IEnumerable<string> files = Directory.Exists(source) ? Directory.EnumerateFiles(source) : File.Exists(source) ? [source] : [];
        return files.Any(f => IsGameSource(f, oldNames) != IsGameSource(f, newNames));
    }

    /// <summary>
    /// A <c>PakFileFilter</c> (<see cref="Assets.Catalog.AssetCatalogOptions.PakFileFilter"/>) that keeps only
    /// <see cref="IsGameSource"/> paks and adds every one it leaves out to <paramref name="skipped"/>.
    /// </summary>
    public static Func<string, bool> Filter(IReadOnlyCollection<string> projectModNames, ICollection<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(projectModNames);
        var names = projectModNames.ToList();
        return file =>
        {
            if (IsGameSource(file, names))
            {
                return true;
            }

            skipped?.Add(Path.GetFileName(file));
            return false;
        };
    }

    [GeneratedRegex(@"^pakchunk(?<chunk>\d+)-(?<name>.+)_P\.(?:pak|utoc)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModPak();
}
