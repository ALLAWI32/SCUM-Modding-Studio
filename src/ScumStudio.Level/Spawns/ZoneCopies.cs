using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Spawns;

/// <summary>
/// A zone's own settings (owner: "for the selected area, choose exactly which zombie or NPC types spawn there"): the zone
/// settings (<c>EncounterZoneData</c>) a threat zone shares with others are copied under a new name in the same folder, so
/// the copy's encounter rows can be re-pointed and weighed without changing the other zones. The copy is a one-package
/// <see cref="CloneAssetOp"/> of kind <see cref="Kind"/>: the exporter builds it like a vehicle or item clone.
/// </summary>
public static partial class ZoneCopies
{
    /// <summary>The <see cref="CloneAssetOp.Kind"/> of a zone settings copy.</summary>
    public const string Kind = "zone";

    /// <summary>
    /// The copy of the zone settings <paramref name="stockZonePackage"/> as <paramref name="newName"/> in its folder. Checks
    /// the name against every package of the game files: call it on a worker.
    /// </summary>
    /// <param name="catalog">The game files.</param>
    /// <param name="stockZonePackage">The zone settings package (<c>/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village</c>).</param>
    /// <param name="newName">The copy's name: letters, digits and '_', starting with a letter, not ending in <c>_&lt;number&gt;</c>.</param>
    /// <param name="planned">Packages the project already creates (its clones), which the name may not take either.</param>
    /// <exception cref="ArgumentException">The stock package is missing or the name is invalid or taken.</exception>
    public static CloneAssetOp Plan(AssetCatalog catalog, string stockZonePackage, string newName, IEnumerable<string>? planned = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stockZonePackage);
        if (!catalog.TryGetPackageFile(stockZonePackage, out _))
        {
            throw new ArgumentException($"{stockZonePackage} is not in the game files.", nameof(stockZonePackage));
        }

        if (newName is null || !ValidName().IsMatch(newName))
        {
            throw new ArgumentException($"'{newName}' is not a valid zone name (letters, digits and '_', starting with a letter).", nameof(newName));
        }

        // A trailing _<number> is an FName number: the game would look the package up as another name (audit F089).
        if (NumberSuffix().IsMatch(newName))
        {
            throw new ArgumentException($"'{newName}' ends in _<number>, which the game reads as another name; end it with a letter.", nameof(newName));
        }

        // The leaf must be unique in the whole game (audit F090), not only in the folder.
        if (Taken(catalog, planned).Contains(newName))
        {
            throw new ArgumentException($"'{newName}' is already the name of a package of the game or the project; choose another name.", nameof(newName));
        }

        var newPath = stockZonePackage[..(stockZonePackage.LastIndexOf('/') + 1)] + newName;
        return new CloneAssetOp(Kind, stockZonePackage, newPath, [new PackagePair(stockZonePackage, newPath)]);
    }

    /// <summary>A free name for <paramref name="zoneName"/>'s own settings: <c>MTZ_Village_Own</c>, else <c>MTZ_Village_OwnB</c>, <c>…C</c>, ….</summary>
    public static string SuggestName(string zoneName, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(zoneName);
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.Select(t => t[(t.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = zoneName + "_Own";
        if (!used.Contains(stem))
        {
            return stem;
        }

        for (var n = 1; ; n++)
        {
            var suffix = string.Empty;
            for (var k = n; k > 0; k = (k - 1) / 26)
            {
                suffix = (char)('A' + ((k - 1) % 26)) + suffix;
            }

            if (suffix != "A" && !used.Contains(stem + suffix))
            {
                return stem + suffix;
            }
        }
    }

    /// <summary>Every package name of the game files and of <paramref name="planned"/> (lists every package: call it on a worker).</summary>
    public static HashSet<string> Taken(AssetCatalog catalog, IEnumerable<string>? planned = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).Concat(planned ?? [])
            .Select(p => p[(p.LastIndexOf('/') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex NumberSuffix();
}
