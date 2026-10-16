using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Modding;

public sealed partial record AssetModRequest
{
    /// <summary>
    /// Stock entity setup → the ingredient tags (object paths) appended to its <c>CraftingIngredientInstances</c>: an item
    /// the user allowed a new tag (<see cref="CraftIngredient.AddTag"/>). Written at the stock path, as an override.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> IngredientInstances { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// The package an override starts from instead of the catalog's (the project pak's staged copy of the same package, so
    /// its edits are kept under the higher pak), or null to read the catalog.
    /// </summary>
    public Func<string, CookedPackage?>? BasePackage { get; init; }
}

public static partial class AssetModBuilder
{
    /// <summary>Appends <see cref="AssetModRequest.IngredientInstances"/> to their entity setups (<see cref="Crafting.IngredientInstances"/>).</summary>
    private static void AddIngredientInstances(AssetCatalog catalog, AssetModRequest request,
        Dictionary<string, (CookedPackage Package, PackageBytes Bytes, bool IsClone)> built, List<string> warnings)
    {
        foreach (var (setup, tags) in request.IngredientInstances)
        {
            var path = PackageMap.Normalize(setup);
            try
            {
                var package = built.TryGetValue(path, out var kept) ? kept.Package : request.BasePackage?.Invoke(path) ?? ModdableAssets.ReadPackage(catalog, path);
                var inherited = ItemSetups.ParentBlueprint(package) is { } parent ? IngredientIndex.Read(catalog, parent).Instances : [];
                var bytes = Crafting.IngredientInstances.Append(package, tags, inherited);
                built[path] = (CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, path), bytes, false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
            {
                warnings.Add($"{path}: the new ingredient tags are not added ({ex.Message}).");
            }
        }
    }
}
