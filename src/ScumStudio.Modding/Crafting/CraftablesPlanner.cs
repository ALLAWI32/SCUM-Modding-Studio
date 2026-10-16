using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>The new packages of one craftable.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Recipe">The recipe package (<c>CR_SS_*</c>).</param>
/// <param name="Product">What the recipe makes: the base element (<c>BP_SS_*</c>), for power the new item, for an item the game's item.</param>
/// <param name="Item">The item a station or power object spawns, or null.</param>
/// <param name="Tag">A station's ingredient tag (<c>CI_SS_*</c>), or null.</param>
/// <param name="ItemRecipe">A second recipe that crafts it into the bag (<see cref="CraftUse.Both"/>), or null.</param>
public sealed record CraftablePackages(string Name, string Recipe, string Product, string? Item, string? Tag, string? ItemRecipe = null);

/// <summary>
/// A craftable the export can neither ship nor leave out: a Craftables pak without it would remove what players built with
/// it. The export stops with the reason (an item craftable, which ships only a recipe, is left out with a warning instead).
/// </summary>
public class CraftableExportException(string craftable, string message) : InvalidOperationException(message)
{
    /// <summary>The craftable's name.</summary>
    public string Craftable { get; } = craftable;
}

/// <summary>
/// A craftable's new package path already exists in the export source (a mod pak or folder the studio reads). The export
/// stops: leaving the craftable out would drop what players built, and shipping it would replace a package that is not ours.
/// </summary>
public sealed class CraftableConflictException(string craftable, string packagePath, string? container)
    : CraftableExportException(craftable, $"{craftable}: {packagePath} already exists in {container ?? "the game files"}. The export stops so that it neither leaves this craftable out nor replaces that package: take {container ?? "that pak"} out of what the studio reads as the game (the Paks folder or the project's imported mods), then export again.")
{
    /// <summary>The new package path that is taken.</summary>
    public string PackagePath { get; } = packagePath;

    /// <summary>The pak or folder that has it (<see cref="PackageOrigins.ContainerOf"/>), or null.</summary>
    public string? Container { get; } = container;
}

/// <summary>What <see cref="CraftablesPlanner.Plan"/> asks the builder for.</summary>
/// <param name="Request">Clones, ingredient lists and values for <see cref="AssetModBuilder"/>.</param>
/// <param name="Entries">The packages of each planned craftable.</param>
/// <param name="Warnings">Craftables left out and settings the game's data cannot hold.</param>
public sealed record CraftablesPlan(AssetModRequest Request, IReadOnlyList<CraftablePackages> Entries, IReadOnlyList<string> Warnings)
{
    /// <summary>The stock packages the plan changes in place, each one the user opted in to (an item's entity setup that gets a new tag).</summary>
    public IReadOnlyList<string> StockOverrides { get; init; } = [];
}

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
/// 500, fuel <c>ResourceUsageRate</c> 0.13889) made by a copy of the item recipe <c>CR_Small_Battery_Charger</c>;</item>
/// <item>an item of the game (owner: "craft something in exchange for some things"): a copy of a stock
/// <c>ItemCraftingRecipe</c> of the same kind of item (<see cref="ItemTemplate"/>) whose soft <c>Product</c> is pointed at
/// that item. The copy keeps the template's <c>CraftingMetadata_RecipeCategory</c> (gameplay tag
/// <c>CraftingCategory.Items.*</c> + display priority: the crafting menu's section), <c>RelevantSkill</c>, <c>Duration</c>
/// and rewards; the menu shows the item's own name and icon (item recipes have no caption).</item>
/// </list>
/// A craftable's <see cref="Craftable.Category"/> (or its default, a building's or a sofa's) moves the copied recipe to
/// another section of the game's (<see cref="AssetModRequest.RecipeCategories"/>).
/// Every static mesh the template packages import is pointed at the craftable's mesh (<see cref="ClonePlan.Redirects"/>),
/// names and captions become the craftable's, and the recipe gets the craftable's ingredients.
/// </summary>
public static partial class CraftablesPlanner
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
    private const string ItemRecipes = ModdableAssets.ConZ + "Items/Crafting/Recipes/Items/";
    private const string PowerSetup = "RangedResourceProviderEntityComponentContinousAmountSetup_0";

    // Item recipe templates (SCUM 1.3.3, read from the paks) by the native class the item derives from, its own data (a
    // spear, the chainsaw or a DLC tomahawk is a melee WeaponItem whatever its folder, a charm a WeaponAttachment) ...
    private static readonly (Func<string, bool> Native, string Recipe)[] ClassTemplates =
    [
        (n => n == "AmmunitionArrow", "CR_Wooden_Arrow"), // Ammunition.ArrowsAndBolts
        (n => n.StartsWith("Ammunition", StringComparison.Ordinal), "CR_Ammo_Cal22"), // Ammunition.Bullets
        (n => n.StartsWith("WeaponAttachment", StringComparison.Ordinal) || n.StartsWith("BowAttachment", StringComparison.Ordinal), "CR_Improvised_Flashlight"), // RangedWeapons.Attachments
        (n => n is "WeaponBow" or "WeaponCrossbow" or "WeaponAutoCrossbow", "CR_Improvised_Bow_20"), // RangedWeapons.BowsAndCrossbows
        (n => n is "Weapon" or "WeaponRevolver", "CR_Improvised_Weapon_Handgun"), // RangedWeapons.Firearms
        (n => n is "WeaponItem" or "Chainsaw", "CR_Improvised_Knife"), // MeleeWeapons
        (n => n is "ClothesItem" or "Quiver" or "WeaponHolsterItem" or "NightVisionGogglesItem", "CR_Improvised_Boots"), // Clothing.ClothesAndAccessories
        (n => n == "FishingRod" || n.StartsWith("FishingAttachment", StringComparison.Ordinal), "CR_FishingBait_Bread"), // Fishing
        (n => n is "GrenadeItem" or "TrapItem", "CR_C4"), // Explosives
    ];

    // ... else the first rule matching the item's path; tools and misc (CR_Small_Battery_Charger,
    // CraftingCategory.Items.ToolsAndMisc.Misc) otherwise.
    private static readonly (Regex Path, string Recipe)[] ItemTemplates =
    [
        (new(@"/Ammunition/(Arrows|CrossbowBolts)/", RegexOptions.IgnoreCase), "CR_Wooden_Arrow"), // Ammunition.ArrowsAndBolts
        (new(@"/Ammunition/", RegexOptions.IgnoreCase), "CR_Ammo_Cal22"), // Ammunition.Bullets
        (new(@"/Weapons/Attachments/", RegexOptions.IgnoreCase), "CR_Improvised_Flashlight"), // RangedWeapons.Attachments
        (new(@"/[12]H_", RegexOptions.IgnoreCase), "CR_Improvised_Knife"), // MeleeWeapons
        (new(@"Crossbow|_Bow(_|\d|$)", RegexOptions.IgnoreCase), "CR_Improvised_Bow_20"), // RangedWeapons.BowsAndCrossbows
        (new(@"/Weapons/", RegexOptions.IgnoreCase), "CR_Improvised_Weapon_Handgun"), // RangedWeapons.Firearms
        (new(@"/Clothes/", RegexOptions.IgnoreCase), "CR_Improvised_Boots"), // Clothing.ClothesAndAccessories
        (new(@"/Fishing/", RegexOptions.IgnoreCase), "CR_FishingBait_Bread"), // Fishing
        (new(@"/(Traps|Explosives|Grenades)/", RegexOptions.IgnoreCase), "CR_C4"), // Explosives
    ];
    private const string FuelSetup = "GameResourceContainerEntityComponentSetup_0";

    // Where items never are: what moves on its own (RecipeRules.IsAllowedSource) but weapons and animal body parts (food).
    // BP_Prisoner has a BP_Prisoner_ES; the vehicles' item containers are parts of the vehicle.
    private static readonly string[] NotItems = ["/Characters/", "/NPCs/", "/NPC/", "/Zombies/", "/Puppets/", "/Vehicles/", "/Drones/"];

    // IsItem's answers per catalog (two package headers each; the Add box and the gallery ask for thousands).
    private static readonly ConditionalWeakTable<AssetCatalog, ConcurrentDictionary<string, string?>> ItemChecks = new();

    /// <summary>
    /// Plans every craftable of <paramref name="craftables"/> (bad or duplicate ones are left out with a warning). It reads
    /// <paramref name="catalog"/> and writes nothing, so an export can run it before it writes any pak. Throws
    /// <see cref="CraftableExportException"/> for a placed craftable it can neither ship nor leave out: a new package path
    /// already exists (<see cref="CraftableConflictException"/>), its mesh is not one every player has, or none of its
    /// ingredients is.
    /// </summary>
    public static CraftablesPlan Plan(AssetCatalog catalog, IReadOnlyList<Craftable> craftables)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(craftables);
        craftables = CraftablesFile.WithTokens(craftables);
        var warnings = new List<string>();
        var clones = new List<ClonePlan>();
        var edits = new Dictionary<string, IReadOnlyList<TunableEdit>>(StringComparer.OrdinalIgnoreCase);
        var ingredients = new Dictionary<string, IReadOnlyList<CraftIngredient>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CraftablePackages>();
        var sections = new Dictionary<string, CraftCategory>(StringComparer.OrdinalIgnoreCase);
        var skinless = new List<string>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pickedItems = new PickedItems(catalog);

        // Stations first, each after the station it needs: a recipe asks for a station's tag only when that station is
        // really built (name -> its tag).
        var stations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var craftable in BuildOrder(craftables))
        {
            // A stored token is never renamed, so one from before the FName number rule (SS_Chair_01_2) is still planned.
            var token = craftable.Token!;
            if (!CraftablesFile.IsValidToken(token) || !tokens.Add(token))
            {
                warnings.Add($"{craftable.Name}: left out (its asset name {token} is not valid or another craftable has it).");
                continue;
            }

            // What the new packages import must be in every player's game (an imported mesh ships in the pak, CraftablesExporter).
            if (craftable.Imported is null && Unshippable(catalog, craftable, craftable.Mesh, warnings) is { } meshReason)
            {
                LeaveOut(craftable, "its mesh " + meshReason, "give it a mesh of the game, or import the mod that has it into the project", warnings);
                continue;
            }

            if (craftable.Kind == CraftKind.Item && !IsItem(catalog, craftable.Source))
            {
                warnings.Add($"{craftable.Name}: left out ({craftable.Source} is not an item of the game).");
                continue;
            }

            if (craftable.Kind == CraftKind.Item && !string.Equals(craftable.Source, craftable.Mesh, StringComparison.OrdinalIgnoreCase)
                && Unshippable(catalog, craftable, craftable.Source, warnings) is { } itemReason)
            {
                warnings.Add($"{craftable.Name}: left out ({itemReason}).");
                continue;
            }

            // Only tags the game has (a typo would ship a recipe importing a missing package) and built stations' tags.
            var recipe = new List<CraftIngredient>();
            var dropped = new List<string>();
            foreach (var ingredient in pickedItems.Resolve(craftable, craftable.Ingredients.Count > 0 ? craftable.Ingredients : RecipeRules.Suggest(craftable.Material, craftable.SizeMeters), warnings))
            {
                if (stations.ContainsValue(ingredient.Tag) || pickedItems.IsPlanned(ingredient.Tag))
                {
                    recipe.Add(ingredient);
                }
                else if (Unshippable(catalog, craftable, PackageMap.Normalize(RecipeIngredients.TagPath(ingredient.Tag)), warnings) is { } reason)
                {
                    warnings.Add($"{craftable.Name}: the ingredient {ingredient.Tag} is left out of the recipe ({reason}).");
                    dropped.Add(reason);
                }
                else
                {
                    recipe.Add(ingredient);
                }
            }

            if (craftable.Station is { Length: > 0 } station)
            {
                if (stations.TryGetValue(station, out var stationTag))
                {
                    recipe.Add(new CraftIngredient(stationTag, 1, IsTool: true));
                }
                else
                {
                    warnings.Add($"{craftable.Name}: the station '{station}' is not a station craftable built with this project; no station needed.");
                }
            }

            if (recipe.Count == 0)
            {
                LeaveOut(craftable, "its recipe has no ingredient left: " + string.Join("; ", dropped), "give it ingredients of the game", warnings);
                continue;
            }

            try
            {
                var (plan, packages) = PlanOne(catalog, craftable, token, warnings);
                clones.Add(plan);
                entries.Add(packages);
                if (packages.Tag is { } tag)
                {
                    stations.TryAdd(craftable.Name, PackageMap.Leaf(tag));
                }

                if (packages.ItemRecipe is { } itemRecipe)
                {
                    ingredients[itemRecipe] = recipe;
                }

                if (craftable.Kind == CraftKind.Element)
                {
                    AddElementEdits(catalog, craftable, plan, packages, edits, warnings);
                    skinless.AddRange(plan.Packages.Select(p => p.Value).Where(p => p != packages.Recipe && p != packages.ItemRecipe && PackageMap.Leaf(p).StartsWith("CR_", StringComparison.OrdinalIgnoreCase)));
                }

                ingredients[packages.Recipe] = recipe;
                if (Section(catalog, craftable, plan, packages, warnings) is { } section)
                {
                    sections[packages.Recipe] = section;
                }

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
            catch (Exception ex) when (ex is (ArgumentException or InvalidOperationException or IOException or InvalidDataException) and not CraftableExportException)
            {
                warnings.Add($"{craftable.Name}: left out ({ex.Message}).");
            }
        }

        var request = new AssetModRequest(clones, edits) { Ingredients = ingredients, RecipeCategories = sections, SkinlessRecipes = skinless };
        return pickedItems.Finish(request, entries, warnings);
    }

    /// <summary>
    /// For each of <paramref name="craftables"/>, the name of the craftable before it in the build order that has its token
    /// (in any case), or null: <see cref="Plan"/> keeps that one and leaves this one out.
    /// </summary>
    public static IReadOnlyList<string?> TokenOwners(IReadOnlyList<Craftable> craftables)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<Craftable, string?>(ReferenceEqualityComparer.Instance);
        foreach (var craftable in BuildOrder(craftables))
        {
            taken[craftable] = craftable.Token is { } token ? owners.GetValueOrDefault(token) : null;
            if (CraftablesFile.IsValidToken(craftable.Token))
            {
                owners.TryAdd(craftable.Token!, craftable.Name);
            }
        }

        return craftables.Select(c => taken[c]).ToList();
    }

    /// <summary>
    /// Leaves an item craftable out with a warning (it ships only a recipe; what players crafted is the game's item). A placed
    /// one would vanish from every base that has it, so <see cref="CraftableExportException"/> stops the export instead.
    /// </summary>
    private static void LeaveOut(Craftable craftable, string reason, string remedy, List<string> warnings)
    {
        if (craftable.Kind != CraftKind.Item)
        {
            throw new CraftableExportException(craftable.Name, $"{craftable.Name}: {reason}. The export stops so that it does not leave this craftable out (what players built with it would vanish): {remedy}, then export again.");
        }

        warnings.Add($"{craftable.Name}: left out ({reason}).");
    }

    /// <summary>
    /// Why the craftable's packages cannot import <paramref name="path"/>, or null when they can: the game ships it
    /// (<see cref="PackageOrigins.IsStock"/>), or an imported mod's folder has it (with a warning: it ships only with that mod,
    /// in the project's main pak). A package only another mod's pak has would be missing for every player without that pak.
    /// </summary>
    private static string? Unshippable(AssetCatalog catalog, Craftable craftable, string path, List<string> warnings)
    {
        var package = PackageMap.Normalize(path);
        if (!catalog.PackageExists(package))
        {
            return $"{package} is not in the game files";
        }

        if (catalog.IsStock(package))
        {
            return null;
        }

        var container = catalog.ContainerOf(package);
        if (container is not null && Path.IsPathRooted(container)) // a loose folder: an imported mod's overlay (<mod>/SCUM)
        {
            var root = container.TrimEnd('/', '\\');
            var mod = Path.GetFileName(string.Equals(Path.GetFileName(root), catalog.ProjectName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(root) : root);
            warnings.Add($"{craftable.Name}: {package} comes from the imported mod {mod}; it ships only with that mod, in the project's main pak.");
            return null;
        }

        return $"{package} is only in the mod pak {container}, not in the game files";
    }

    /// <summary>
    /// Stations first, each one after the station it needs (a chain of any length; a loop is cut where it closes), then the
    /// others in list order.
    /// </summary>
    public static IReadOnlyList<Craftable> BuildOrder(IReadOnlyList<Craftable> craftables)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        var stations = craftables.Where(c => c.Kind == CraftKind.Station).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Craftable>(ReferenceEqualityComparer.Instance);
        var order = new List<Craftable>(craftables.Count);
        void Visit(Craftable station)
        {
            if (!seen.Add(station))
            {
                return;
            }

            if (station.Station is { Length: > 0 } needed && stations.TryGetValue(needed, out var before))
            {
                Visit(before);
            }

            order.Add(station);
        }

        foreach (var craftable in craftables.Where(c => c.Kind == CraftKind.Station))
        {
            Visit(craftable);
        }

        order.AddRange(craftables.Where(c => c.Kind != CraftKind.Station));
        return order;
    }

    private static (ClonePlan Plan, CraftablePackages Packages) PlanOne(AssetCatalog catalog, Craftable craftable, string token, List<string> warnings)
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
            case CraftKind.Item:
                var item = GamePath(PackageMap.Normalize(craftable.Source));
                var template = ItemTemplate(catalog, item);
                Add(template, "CR_" + token);
                if (ProductPackage(catalog, template) is { } stock && !string.Equals(stock, item, StringComparison.OrdinalIgnoreCase))
                {
                    redirects.Add(new(stock, item)); // the copied recipe makes the chosen item
                }

                result = new CraftablePackages(craftable.Name, packages[0].Value, item, null, null);
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
            case CraftKind.Element:
                CraftablePackages? element = null;
                PlanElement(catalog, craftable, token, packages, redirects, ref element);
                result = element ?? throw new InvalidOperationException($"{craftable.Element?.Template ?? craftable.Source} is not a base element this version can clone");
                break;
            default:
                Add(FurnitureRecipe, "CR_" + token);
                Add(FurnitureElement, "BP_" + token);
                result = new CraftablePackages(craftable.Name, packages[0].Value, packages[1].Value, null, null);
                break;
        }

        // Every mesh the templates show becomes the craftable's mesh, by the path the game knows it by (a DLC mesh imports
        // /<Pack>/..., not the catalog's /SCUM/Plugins/...); an item keeps its own: only its recipe is new.
        var mesh = GamePath(PackageMap.Normalize(craftable.Mesh));
        var shown = packages.SelectMany(p => StaticMeshImports(catalog, p.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (craftable.Kind != CraftKind.Item)
        {
            if (craftable.Kind != CraftKind.Element)
            {
                redirects.AddRange(shown.Where(s => !string.Equals(s, mesh, StringComparison.OrdinalIgnoreCase)).Select(s => KeyValuePair.Create(s, mesh)));
            }
        }

        // The recipe shows the craftable's picture, not its template's.
        AddIconRedirect(catalog, craftable, packages.First(p => string.Equals(p.Value, result.Recipe, StringComparison.OrdinalIgnoreCase)).Key, redirects, warnings);

        // A path the source already has is never shipped over, nor is the craftable silently left out (a pak without it
        // would remove what players built): the export stops and names the pak.
        foreach (var (_, newPath) in packages)
        {
            if (catalog.PackageExists(newPath))
            {
                throw new CraftableConflictException(craftable.Name, newPath, catalog.ContainerOf(newPath));
            }
        }

        if (craftable.Kind != CraftKind.Item && shown.Count == 0)
        {
            warnings.Add($"{craftable.Name}: keeps the stock look (the template shows no static mesh import).");
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
        if (craftable.Kind is CraftKind.Furniture or CraftKind.Station)
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

    /// <summary>
    /// The crafting menu section the copied recipe moves to, or null to keep its template's: the chosen one when it is one
    /// of the game's sections of the recipe's class (else a warning), otherwise the default for what it is
    /// (<see cref="CraftCategories.DefaultFor"/>: a building or a sofa may differ from the template's). The game's sections
    /// are read only for a craftable that may move.
    /// </summary>
    private static CraftCategory? Section(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, List<string> warnings)
    {
        if (craftable.Category is not { Length: > 0 } && !CraftCategories.HasOwnDefault(craftable))
        {
            return null;
        }

        var sections = CraftCategories.Read(catalog);
        var template = plan.Packages.First(p => p.Value == packages.Recipe).Key;
        if (CraftCategories.SectionOf(sections, template) is not { } own)
        {
            warnings.Add($"{craftable.Name}: the template {PackageMap.Leaf(template)} names no crafting section; it keeps the template's.");
            return null;
        }

        CraftCategory? chosen = null;
        if (craftable.Category is { Length: > 0 } wanted)
        {
            if (CraftCategories.Refusal(craftable, wanted, sections) is { } refusal)
            {
                warnings.Add(refusal);
            }
            else
            {
                chosen = sections.First(c => c.Tag == wanted);
            }
        }

        var section = chosen ?? CraftCategories.DefaultFor(craftable, sections, template);
        return section is null || section.Tag == own.Tag ? null : section;
    }

    /// <summary>True when export <paramref name="exportName"/> of <paramref name="package"/> stores <paramref name="property"/>.</summary>
    private static bool HasValue(AssetCatalog catalog, string package, string exportName, string property)
    {
        var cooked = ModdableAssets.ReadPackage(catalog, package);
        var index = Enumerable.Range(0, cooked.Exports.Count).FirstOrDefault(i => cooked.ResolveName(cooked.Exports[i].ObjectName) == exportName, -1);
        return index >= 0 && cooked.ReadProperties(index).Find(property) is not null;
    }

    /// <summary>
    /// True for an inventory item of the game (weapons, food, clothes, DLC items ...): a Blueprint class with its entity setup
    /// (<see cref="EntitySetupNames"/>), a Blueprint class too, beside it. Not a static mesh with an <c>SM_x_ES</c> mesh twin
    /// (132 buildings and rivers have one) and never a character (<c>BP_Prisoner</c> has a <c>BP_Prisoner_ES</c>) or a
    /// vehicle's part. An item whose setup is not named after it (<see cref="ItemSetups"/>: the setup's <c>Actor</c> names
    /// it) counts too. Reads the package headers once per catalog. With <paramref name="wait"/> false (the UI thread) it
    /// never waits for that list's first build: until it is read, such an item is not one (and the answer is not kept).
    /// </summary>
    public static bool IsItem(AssetCatalog catalog, string packagePath, bool wait = true) => EntitySetupOf(catalog, packagePath, wait) is not null;

    /// <summary>
    /// The entity setup of the item <paramref name="packagePath"/> (see <see cref="IsItem"/>), or null for a package that is
    /// no item. Reads the package headers once per catalog.
    /// </summary>
    public static string? EntitySetupOf(AssetCatalog catalog, string packagePath, bool wait = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var path = PackageMap.Normalize(packagePath);
        if (path.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) || NotItems.Any(n => path.Contains(n, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var checks = ItemChecks.GetValue(catalog, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        if (checks.TryGetValue(path, out var known))
        {
            return known;
        }

        if (EntitySetupNames(path).FirstOrDefault(es => IsBlueprintClass(catalog, es)) is { } twin && IsBlueprintClass(catalog, path))
        {
            return checks[path] = twin;
        }

        var setups = ItemSetups.OfAsync(catalog); // starts the worker build once
        if (!wait && !setups.IsCompletedSuccessfully)
        {
            return null;
        }

        return checks[path] = setups.GetAwaiter().GetResult().NotNamedAfterItem.GetValueOrDefault(path);
    }

    /// <summary>
    /// Where an item's entity setup can be: <c>&lt;Name&gt;_ES</c>, or the name without its "Item" ending
    /// (<c>AnimalBaitFeederItem</c> has <c>AnimalBaitFeeder_ES</c>, the DLC's <c>AnimalBaitFeederItem_Ridgeline</c> has
    /// <c>AnimalBaitFeeder_Ridgeline_ES</c>: seviscache found it in the Assets search but not in the Craftables gallery).
    /// </summary>
    public static IEnumerable<string> EntitySetupNames(string packagePath)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        yield return packagePath + "_ES";
        var slash = packagePath.LastIndexOf('/') + 1;
        var leaf = packagePath[slash..];
        var at = leaf.LastIndexOf("Item", StringComparison.OrdinalIgnoreCase);
        if (at > 0 && (at + 4 == leaf.Length || leaf[at + 4] == '_'))
        {
            yield return packagePath[..(slash + at)] + leaf[(at + 4)..] + "_ES";
        }
    }

    /// <summary>
    /// Every item of the catalog's package list (<see cref="IsItem"/> on each package with an entity setup beside it, and the
    /// items of setups not named after them): a few thousand package headers the first time, so it runs on a worker.
    /// </summary>
    public static IReadOnlySet<string> Items(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entries = (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries;
        var paths = entries.Select(e => e.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => EntitySetupNames(e.PackagePath).Any(paths.Contains)).AsParallel()
            .Where(e => IsItem(catalog, e.PackagePath)).Select(e => e.PackagePath).AsSequential().Concat(ItemSetups.Of(catalog).NotNamedAfterItem.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBlueprintClass(AssetCatalog catalog, string package)
    {
        try
        {
            return catalog.PackageExists(package) && catalog.GetExports(package).Any(e => e.ClassName == "BlueprintGeneratedClass");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // an unreadable header is no item
        }
    }

    /// <summary>The stock item recipe an item's recipe is copied from (its crafting menu category, skill and time).</summary>
    public static string ItemTemplate(AssetCatalog catalog, string item)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var leaf = NativeClassOf(catalog, item) is { } native && ClassTemplates.FirstOrDefault(t => t.Native(native)).Recipe is { } byClass
            ? byClass
            : ItemTemplates.FirstOrDefault(t => t.Path.IsMatch(item)).Recipe;
        var recipe = leaf is null ? PowerRecipe : ItemRecipes + leaf;
        return catalog.PackageExists(recipe) ? recipe : PowerRecipe;
    }

    /// <summary>
    /// The native class (<c>/Script/...</c>) the Blueprint class in <paramref name="package"/> derives from, through its parent
    /// Blueprints: <c>Weapon</c> (firearms), <c>WeaponItem</c> (melee), <c>WeaponBow</c>, <c>ClothesItem</c> ...; null when
    /// it cannot be read.
    /// </summary>
    public static string? NativeClassOf(AssetCatalog catalog, string package)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        for (var depth = 0; depth < 16; depth++)
        {
            CookedPackage cooked;
            try
            {
                cooked = ModdableAssets.ReadPackage(catalog, package);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                return null;
            }

            var index = Enumerable.Range(0, cooked.Exports.Count).FirstOrDefault(i => cooked.GetExportClassName(i) == "BlueprintGeneratedClass", -1);
            if (index < 0 || cooked.Exports[index].SuperIndex >= 0 || cooked.Imports[-cooked.Exports[index].SuperIndex - 1] is not { OuterIndex: < 0 } super)
            {
                return null;
            }

            var outer = cooked.ResolveName(cooked.Imports[-super.OuterIndex - 1].ObjectName);
            if (outer.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            {
                return cooked.ResolveName(super.ObjectName);
            }

            package = outer;
        }

        return null;
    }

    /// <summary>
    /// The path the game knows a package by: a DLC plugin's content (<c>/SCUM/Plugins/GameFeatures/ApexHunterPack/Content/X</c>,
    /// as the catalog lists it) is mounted at <c>/ApexHunterPack/X</c>, the path the game's own DLC recipes name.
    /// </summary>
    public static string GamePath(string packagePath) =>
        Regex.Replace(packagePath, "^/[^/]+/Plugins/(?:GameFeatures/)?([^/]+)/Content/", "/$1/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    /// <summary>
    /// Plans a <see cref="CraftKind.Element"/> craftable: adds the template family's packages (recipe first) and redirects,
    /// and sets <paramref name="result"/>; left null, the craftable is left out.
    /// </summary>
    static partial void PlanElement(AssetCatalog catalog, Craftable craftable, string token, List<KeyValuePair<string, string>> packages, List<KeyValuePair<string, string>> redirects, ref CraftablePackages? result);

    /// <summary>The element's own edits (its name and <see cref="ElementSettings.Levels"/>); a value the data cannot hold is a warning.</summary>
    static partial void AddElementEdits(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, Dictionary<string, IReadOnlyList<TunableEdit>> edits, List<string> warnings);
}
