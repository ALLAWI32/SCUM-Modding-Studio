using ScumStudio.Formats.Packages;

namespace ScumStudio.Modding.Cloning;

/// <summary>A cloned package: header + .uexp bytes, the copied .ubulk and what changed.</summary>
/// <param name="OldPackagePath">Package that was cloned.</param>
/// <param name="NewPackagePath">Package path of the clone.</param>
/// <param name="Bytes">The rebuilt header and .uexp.</param>
/// <param name="UBulk">The source .ubulk, copied verbatim (null when the source has none).</param>
/// <param name="RenamedNames">Name-table entries whose text changed.</param>
public sealed record ClonedPackage(string OldPackagePath, string NewPackagePath, PackageBytes Bytes, byte[]? UBulk, int RenamedNames)
{
    /// <summary>Header extension of the source (<c>.uasset</c>; <c>.umap</c> is never cloned here).</summary>
    public string HeaderExtension { get; init; } = ".uasset";
}

/// <summary>
/// Rename-clone of one cooked package (port of <c>clone()</c> in the owner's <c>clone_vehicle.py</c>): every name-table
/// entry is remapped through a <see cref="PackageMap"/>, imports and exports keep their name indices, every export payload
/// is copied byte-for-byte, and the result is re-parsed to prove the payloads are unchanged.
/// </summary>
public static class PackageCloner
{
    /// <summary>Clones <paramref name="source"/> under <paramref name="map"/>.</summary>
    /// <param name="source">Pristine package (header + .uexp, optional .ubulk).</param>
    /// <param name="oldPackagePath">The source's package path (key in <paramref name="map"/>).</param>
    /// <param name="map">Rename map; must map <paramref name="oldPackagePath"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="oldPackagePath"/> is not in the map.</exception>
    /// <exception cref="InvalidOperationException">The remapped name table has duplicates, or verification failed.</exception>
    public static ClonedPackage Clone(CookedPackage source, string oldPackagePath, PackageMap map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        var newPackagePath = map.Map(oldPackagePath) ?? throw new ArgumentException($"{oldPackagePath} is not in the package map.", nameof(oldPackagePath));

        var input = PackageWriter.ToBuildInput(source);
        var names = new string[input.Names.Count];
        var wide = new bool[names.Length];
        var renamed = 0;
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = map.RemapName(input.Names[i]);
            wide[i] = input.NameIsWide is { } flags && i < flags.Count && flags[i] && !IsAscii(names[i]);
            if (!string.Equals(names[i], input.Names[i], StringComparison.Ordinal))
            {
                renamed++;
            }
        }

        var duplicate = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Cloning {oldPackagePath} as {newPackagePath} would give the name '{duplicate.Key}' twice in the name table; choose another name.");
        }

        var bytes = PackageWriter.Build(input with { Names = names, NameIsWide = wide, NameHashOverride = null });
        Verify(source, bytes, newPackagePath);
        return new ClonedPackage(PackageMap.Normalize(oldPackagePath), newPackagePath, bytes, source.UBulk, renamed);
    }

    /// <summary>Re-parses <paramref name="bytes"/> and checks every export payload equals the source's.</summary>
    internal static void Verify(CookedPackage source, PackageBytes bytes, string packagePath)
    {
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, packagePath);
        if (check.Exports.Count != source.Exports.Count || check.Imports.Count != source.Imports.Count)
        {
            throw new InvalidOperationException($"{packagePath}: export/import count changed while cloning.");
        }

        for (var i = 0; i < source.Exports.Count; i++)
        {
            if (!check.GetExportData(i).Span.SequenceEqual(source.GetExportData(i).Span))
            {
                throw new InvalidOperationException($"{packagePath}: export {i + 1} payload changed while cloning.");
            }
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }
}
