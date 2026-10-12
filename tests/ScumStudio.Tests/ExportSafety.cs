using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;

namespace ScumStudio.Tests;

/// <summary>
/// The export law (Desktop handoff §12.1 #4, P0 #1): a mod only adds. Its pak ships no package the game has (unless it is
/// on a reviewed allow-list), and its <c>AssetRegistry.bin</c> keeps every stock record as it is and adds exactly the
/// records the export made.
/// </summary>
internal static class ExportSafety
{
    /// <summary>Fails when a shipped package is one the game itself has (<see cref="PackageOrigins.IsStock"/>) and is not allowed.</summary>
    public static void AssertOnlyAdds(AssetCatalog stock, IEnumerable<string> shippedPackagePaths, IReadOnlySet<string>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(shippedPackagePaths);
        var changed = shippedPackagePaths
            .Where(p => !(allowed?.Contains(p) ?? false) && stock.IsStock(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(changed.Count == 0, "The export ships packages the game already has: " + string.Join(", ", changed));
    }

    /// <summary>
    /// Fails unless every record of <paramref name="stock"/> is in <paramref name="shipped"/> with the same names and tags,
    /// and the records <paramref name="shipped"/> has beyond those are exactly <paramref name="expectedAdded"/> (object paths).
    /// </summary>
    public static void AssertRegistrySuperset(AssetRegistryFile stock, AssetRegistryFile shipped, IEnumerable<string> expectedAdded)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(expectedAdded);
        var byPath = new Dictionary<string, AssetData>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in shipped.Assets)
        {
            Assert.True(byPath.TryAdd(asset.ObjectPath, asset), $"{asset.ObjectPath} is in the shipped registry twice.");
        }

        var problems = new List<string>();
        var stockPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in stock.Assets)
        {
            stockPaths.Add(record.ObjectPath);
            if (!byPath.TryGetValue(record.ObjectPath, out var copy))
            {
                problems.Add($"{record.ObjectPath} is missing");
                continue;
            }

            if ((copy.PackagePath, copy.AssetClass, copy.PackageName, copy.AssetName) != (record.PackagePath, record.AssetClass, record.PackageName, record.AssetName))
            {
                problems.Add($"{record.ObjectPath} has other names");
            }
            else if (!Describe(shipped, copy).SequenceEqual(Describe(stock, record)))
            {
                problems.Add($"{record.ObjectPath} has other tags");
            }
        }

        Assert.True(problems.Count == 0, "Stock records changed: " + string.Join("; ", problems.Take(10)) + (problems.Count > 10 ? $" (and {problems.Count - 10} more)" : string.Empty));
        Assert.Equal(
            expectedAdded.Order(StringComparer.OrdinalIgnoreCase),
            byPath.Keys.Where(p => !stockPaths.Contains(p)).Order(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Describe(AssetRegistryFile registry, AssetData asset) =>
        registry.GetTags(asset).Select(t => $"{t.Key}={t.Value.Kind}:{t.Value.Text}#{t.Value.NameNumber}");
}
