using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>The new packages of one craftable.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Recipe">The recipe package (<c>CR_SS_*</c>).</param>
/// <param name="Product">What the recipe makes: the base element (<c>BP_SS_*</c>) or, for power, the item.</param>
/// <param name="Item">The item a station or power object spawns, or null.</param>
/// <param name="Tag">A station's ingredient tag (<c>CI_SS_*</c>), or null.</param>
public sealed record CraftablePackages(string Name, string Recipe, string Product, string? Item, string? Tag);

/// <summary>What <see cref="CraftablesPlanner.Plan"/> asks the builder for.</summary>
/// <param name="Request">Clones, ingredient lists and values for <see cref="AssetModBuilder"/>.</param>
/// <param name="Entries">The packages of each planned craftable.</param>
/// <param name="Warnings">Craftables left out and settings the game's data cannot hold.</param>
public sealed record CraftablesPlan(AssetModRequest Request, IReadOnlyList<CraftablePackages> Entries, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns craftables into clones of the game's own crafting families (read from the paks, SCUM 1.3.3):
/// <list type="bullet">
/// <item>furniture: the recipe <c>CR_Chair_Improvised_Wood</c> (<c>PlaceableCraftingRecipe</c>, primary asset type
/// <c>PlaceableCraftingRecipe</c>) and its product, the data-only base element <c>BP_Chair_Improvised_Wood</c>
/// (<c>BaseBuildingComponent</c>, primary asset type <c>BaseBuilding</c>; its mesh is the CDO's <c>StaticMesh</c> and
/// <c>_staticMeshVariations</c>);</item>
/// <item>a station: <c>CR_Improvised_Workbench</c> → <c>BP_Base_ImprovisedWorkbench</c> (<c>BaseBuildingItemComponent</c>
/// whose <c>_itemClass</c> spawns the item <c>Improvised_Workbench</c>), whose <c>_ES</c> lists the ingredient tag
/// <c>CI_Group_Machinery</c> in <c>CraftingIngredientInstances</c>: recipes that need machinery as a tool are made next
/// to it. The clone carries a tag of its own (<c>CI_SS_*</c>, a copy of <c>CI_Group_Machinery</c>), and a craftable that
/// requires the station gets that tag as a tool slot;</item>
/// <item>power: the item <c>ElectricityGenerator</c> (+ <c>_ES</c>: <c>InfluenceAreaRadius</c> 1000 cm, <c>ResourceAmount</c>
/// 500, fuel <c>ResourceUsageRate</c> 0.13889) made by a copy of the item recipe <c>CR_Small_Battery_Charger</c>.</item>
/// </list>
/// Every static mesh the template packages import is pointed at the craftable's mesh (<see cref="ClonePlan.Redirects"/>),
/// names and captions become the craftable's, and the recipe gets the craftable's ingredients.
/// </summary>
public static class CraftablesPlanner
{
    private const string Placeables = ModdableAssets.ConZ + "Items/Crafting/Recipes/Placeables";
    private const string Elements = ModdableAssets.ConZ + "BaseBuilding/BaseElements";

    /// <summary>Furniture template: recipe and base element.</summary>
    public const string FurnitureRecipe = Placeables + "/CR_Chair_Improvised_Wood";

    /// <summary>Station template: recipe (its base element and item follow from it).</summary>
    public const string StationRecipe = Placeables + "/CR_Improvised_Workbench";

    /// <summary>Power template: the generator item.</summary>
    public const string PowerItem = ModdableAssets.ConZ + "Items/Equipment/ElectricityGenerator";

    /// <summary>Power template: the item recipe copied for it.</summary>
    public const string PowerRecipe = ModdableAssets.ConZ + "Items/Crafting/Recipes/Items/CR_Small_Battery_Charger";

    private const string FurnitureElement = Elements + "/BP_Chair_Improvised_Wood";
    private const string StationElement = Elements + "/BP_Base_ImprovisedWorkbench";
    private const string StationItem = ModdableAssets.ConZ + "Items/Equipment/Active_Items/Improvised_Workbench";
    private const string StationTag = RecipeIngredients.TagFolder + "/CI_Group_Machinery";
    private const string PowerSetup = "RangedResourceProviderEntityComponentContinousAmountSetup_0";
    private const string FuelSetup = "GameResourceContainerEntityComponentSetup_0";

    /// <summary>Plans every craftable of <paramref name="craftables"/> (bad or duplicate ones are left out with a warning).</summary>
    public static CraftablesPlan Plan(AssetCatalog catalog, IReadOnlyList<Craftable> craftables)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(craftables);
        var warnings = new List<string>();
        var clones = new List<ClonePlan>();
        var edits = new Dictionary<string, IReadOnlyList<TunableEdit>>(StringComparer.OrdinalIgnoreCase);
        var ingredients = new Dictionary<string, IReadOnlyList<CraftIngredient>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CraftablePackages>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stations = craftables.Where(c => c.Kind == CraftKind.Station).ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        foreach (var craftable in craftables)
        {
            var token = craftable.Token;
            if (!CloneFamilyPlanner.IsValidName(token) || !tokens.Add(token))
            {
                warnings.Add($"{craftable.Name}: left out (the name is empty or used twice).");
                continue;
            }

            if (!catalog.PackageExists(craftable.Mesh))
            {
                warnings.Add($"{craftable.Name}: left out ({craftable.Mesh} is not in the game files).");
                continue;
            }

            try
            {
                var (plan, packages) = PlanOne(catalog, craftable, token);
                clones.Add(plan);
                entries.Add(packages);
                var recipe = new List<CraftIngredient>(craftable.Ingredients.Count > 0 ? craftable.Ingredients : RecipeRules.Suggest(craftable.Material, craftable.SizeMeters));
                if (craftable.Station is { Length: > 0 } station)
                {
                    if (stations.TryGetValue(station, out var s))
                    {
                        recipe.Add(new CraftIngredient("CI_" + s.Token, 1, IsTool: true));
                    }
                    else
                    {
                        warnings.Add($"{craftable.Name}: the station '{station}' is not a station craftable of this project; no station needed.");
                    }
                }

                ingredients[packages.Recipe] = recipe;
                AddEdits(catalog, craftable, plan, packages, edits);
                if (craftable.NeedsPower)
                {
                    warnings.Add($"{craftable.Name}: \"needs electricity\" is kept but not exported: a station's power need is not a value of the game's data (native code).");
                }

                if (craftable.Kind == CraftKind.Power && craftable.Power.DayOnly)
                {
                    warnings.Add($"{craftable.Name}: \"works only by day\" is kept but not exported: the generator's data has no day/night switch (native code).");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
            {
                warnings.Add($"{craftable.Name}: left out ({ex.Message}).");
            }
        }

        var request = new AssetModRequest(clones, edits) { Ingredients = ingredients };
        return new CraftablesPlan(request, entries, warnings);
    }

    private static (ClonePlan Plan, CraftablePackages Packages) PlanOne(AssetCatalog catalog, Craftable craftable, string token)
    {
        var packages = new List<KeyValuePair<string, string>>();
        var redirects = new List<KeyValuePair<string, string>>();
        void Add(string template, string leaf) => packages.Add(new(PackageMap.Normalize(template), PackageMap.Folder(PackageMap.Normalize(template)) + "/" + leaf));

        CraftablePackages result;
        switch (craftable.Kind)
        {
            case CraftKind.Station:
                Add(StationRecipe, "CR_" + token);
                Add(StationElement, "BP_" + token);
                Add(StationItem, token);
                Add(StationItem + "_ES", token + "_ES");
                Add(StationTag, "CI_" + token);
                result = new CraftablePackages(craftable.Name, packages[0].Value, packages[1].Value, packages[2].Value, packages[4].Value);
                break;
            case CraftKind.Power:
                Add(PowerItem, token);
                Add(PowerItem + "_ES", token + "_ES");
                Add(PowerRecipe, "CR_" + token);
                if (ProductPackage(catalog, PowerRecipe) is { } product)
                {
                    redirects.Add(new(product, packages[0].Value)); // the copied recipe makes the new generator
                }

                result = new CraftablePackages(craftable.Name, packages[2].Value, packages[0].Value, packages[0].Value, null);
                break;
            default:
                Add(FurnitureRecipe, "CR_" + token);
                Add(FurnitureElement, "BP_" + token);
                result = new CraftablePackages(craftable.Name, packages[0].Value, packages[1].Value, null, null);
                break;
        }

        // Every mesh the templates show becomes the craftable's mesh.
        var mesh = PackageMap.Normalize(craftable.Mesh);
        foreach (var stock in packages.SelectMany(p => StaticMeshImports(catalog, p.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(stock, mesh, StringComparison.OrdinalIgnoreCase))
            {
                redirects.Add(new(stock, mesh));
            }
        }

        foreach (var (_, newPath) in packages)
        {
            if (catalog.PackageExists(newPath))
            {
                throw new ArgumentException($"{newPath} already exists in the game files; choose another name.");
            }
        }

        return (new ClonePlan(packages[0].Key, result.Product, packages) { Redirects = redirects }, result);
    }

    private static void AddEdits(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, Dictionary<string, IReadOnlyList<TunableEdit>> edits)
    {
        var map = plan.ToMap();
        void Edit(string newPackage, string templateExport, string property, string value)
        {
            var template = plan.Packages.First(p => string.Equals(p.Value, newPackage, StringComparison.OrdinalIgnoreCase)).Key;
            if (!HasValue(catalog, template, templateExport, property))
            {
                return;
            }

            var list = edits.TryGetValue(newPackage, out var existing) ? existing.ToList() : [];
            list.Add(new TunableEdit(map.RemapObject(templateExport), property, value));
            edits[newPackage] = list;
        }

        var name = craftable.Name.Trim();
        var recipeTemplate = PackageMap.Leaf(plan.Packages.First(p => p.Value == packages.Recipe).Key);
        Edit(packages.Recipe, recipeTemplate, "Caption", name);
        Edit(packages.Recipe, recipeTemplate, "Description", name);
        if (craftable.Kind != CraftKind.Power)
        {
            var element = PackageMap.Leaf(plan.Packages.First(p => p.Value == packages.Product).Key);
            Edit(packages.Product, $"Default__{element}_C", "_name", name);
        }

        if (packages.Item is { } item)
        {
            var es = PackageMap.Leaf(plan.Packages.First(p => p.Value == item + "_ES").Key);
            Edit(item + "_ES", $"Default__{es}_C", "Caption", name);
            if (craftable.Kind == CraftKind.Power)
            {
                Edit(item + "_ES", PowerSetup, "InfluenceAreaRadius", TunableValue.Format(craftable.Power.RadiusMeters * 100));
                Edit(item + "_ES", PowerSetup, "ResourceAmount", TunableValue.Format(craftable.Power.Output));
                Edit(item + "_ES", FuelSetup, "ResourceUsageRate", TunableValue.Format(craftable.Power.FuelPerMinute));
            }
        }

        if (packages.Tag is { } tag)
        {
            Edit(tag, PackageMap.Leaf(StationTag), "ClassRepresentativeCaption", name);
        }
    }

    /// <summary>True when export <paramref name="exportName"/> of <paramref name="package"/> stores <paramref name="property"/>.</summary>
    private static bool HasValue(AssetCatalog catalog, string package, string exportName, string property)
    {
        var cooked = ModdableAssets.ReadPackage(catalog, package);
        var index = Enumerable.Range(0, cooked.Exports.Count).FirstOrDefault(i => cooked.ResolveName(cooked.Exports[i].ObjectName) == exportName, -1);
        return index >= 0 && cooked.ReadProperties(index).Find(property) is not null;
    }

    /// <summary>The package of the recipe's <c>Product</c> class (soft path), or null.</summary>
    public static string? ProductPackage(AssetCatalog catalog, string recipe)
    {
        var package = ModdableAssets.ReadPackage(catalog, recipe);
        return package.ReadProperties(RecipeIngredients.RecipeExport(package)).Find("Product")?.Value is SoftObjectValue { AssetPath: { Length: > 0 } path }
            ? PackageMap.Normalize(path)
            : null;
    }

    /// <summary>The static mesh packages <paramref name="package"/> imports.</summary>
    public static IReadOnlyList<string> StaticMeshImports(AssetCatalog catalog, string package)
    {
        var cooked = ModdableAssets.ReadPackage(catalog, package);
        return cooked.Imports
            .Where(i => cooked.ResolveName(i.ClassName) == "StaticMesh" && i.OuterIndex < 0)
            .Select(i => cooked.ResolveName(cooked.Imports[-i.OuterIndex - 1].ObjectName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The static mesh a source shows: the mesh itself, or the first static mesh a Blueprint imports; null when none.</summary>
    public static string? MeshOf(AssetCatalog catalog, string source)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var path = PackageMap.Normalize(source);
        return catalog.GetExports(path).Any(e => e.ClassName == "StaticMesh") ? path : StaticMeshImports(catalog, path).FirstOrDefault();
    }
}
