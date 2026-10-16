using System.Globalization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// <see cref="CraftKind.Element"/> craftables (owner, 2026-10-12: "a bed must behave like a normal bed"): the game's own
/// element family is cloned (<see cref="ElementFamilies"/>), not the improvised chair. The recipe <c>CR_&lt;Token&gt;</c>
/// makes <c>BP_&lt;Token&gt;</c>; each upgrade level n is <c>CR_&lt;Token&gt;_Up&lt;n&gt;</c> → <c>BP_&lt;Token&gt;_L&lt;n&gt;</c>
/// (no new name ends in <c>_digits</c>, which the engine reads as an FName number). All of them are renamed by one map, so
/// each clone's <c>_upgradeRecipe</c> and each recipe's soft <c>Product</c> name the clones. A crafted bed keeps the bed's
/// <c>_isUsedForRespawn</c>, a chest the container item its <c>_itemClass</c> spawns (cloned as <c>&lt;Token&gt;</c> +
/// <c>_ES</c>), a lamp its lamp item. Respawn times are global (<c>RespawnSettings</c>, <c>ServerSettings.ini</c>) and are
/// never written here.
/// </summary>
public static partial class CraftablesPlanner
{
    static partial void PlanElement(AssetCatalog catalog, Craftable craftable, string token, List<KeyValuePair<string, string>> packages, List<KeyValuePair<string, string>> redirects, ref CraftablePackages? result)
    {
        var family = ElementFamilies.Of(catalog, craftable.Source) ?? throw new ArgumentException(ElementFamilies.WhyNot(catalog, craftable.Source));
        if (craftable.Element is { Template: { Length: > 0 } template } && !string.Equals(PackageMap.Normalize(template), family.Element, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{craftable.Source} is now the element {PackageMap.Leaf(family.Element)}, not {PackageMap.Leaf(template)}; add it again");
        }

        void Add(string stock, string leaf) => packages.Add(new(stock, PackageMap.Folder(stock) + "/" + leaf));
        for (var n = 0; n < family.Levels.Count; n++)
        {
            Add(family.Levels[n].Recipe, n == 0 ? "CR_" + token : $"CR_{token}_Up{n}");
            Add(family.Levels[n].Element, n == 0 ? "BP_" + token : $"BP_{token}_L{n}");
        }

        // A chest, lamp or fridge spawns its item: the clone spawns a renamed copy (its entity setup is its twin).
        string? item = null;
        if (family.ItemClass is { } stockItem && stockItem.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) && catalog.PackageExists(stockItem + "_ES"))
        {
            Add(stockItem, token);
            Add(stockItem + "_ES", token + "_ES");
            item = packages[^2].Value;
        }

        // The element's own mesh needs no redirect; another mesh replaces each level's StaticMesh. Its weather mask stays,
        // and variations cannot follow (two meshes renamed to one would share a name in the clone's name table).
        var mesh = GamePath(PackageMap.Normalize(craftable.Mesh)); // a DLC mesh is imported by the path the game knows it by
        if (!family.Levels.Any(l => StaticMeshImports(catalog, l.Element).Contains(mesh, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var level in family.Levels)
            {
                var (shown, variations) = ElementFamilies.MeshOf(catalog, level.Element);
                if (variations > 0 || shown is null)
                {
                    throw new ArgumentException($"{PackageMap.Leaf(level.Element)} {(shown is null ? "names no mesh of its own" : $"has {variations} mesh variation(s)")}; only its own look can be kept");
                }

                if (!redirects.Any(r => string.Equals(r.Key, shown, StringComparison.OrdinalIgnoreCase)))
                {
                    redirects.Add(new(shown, mesh));
                }
            }
        }

        // Both: the bag gets the spawned item too, by a copy of a stock item recipe of its kind made to make the clone.
        string? itemRecipe = null;
        if (craftable.Use == CraftUse.Both && item is not null)
        {
            var recipeTemplate = ItemTemplate(catalog, family.ItemClass!);
            Add(recipeTemplate, $"CR_{token}_Item");
            itemRecipe = packages[^1].Value;
            if (ProductPackage(catalog, recipeTemplate) is { } product && !packages.Any(p => string.Equals(p.Key, product, StringComparison.OrdinalIgnoreCase)))
            {
                redirects.Add(new(product, item));
            }
        }

        result = new CraftablePackages(craftable.Name, packages[0].Value, packages[1].Value, item, null, itemRecipe);
    }

    static partial void AddElementEdits(AssetCatalog catalog, Craftable craftable, ClonePlan plan, CraftablePackages packages, Dictionary<string, IReadOnlyList<TunableEdit>> edits, List<string> warnings)
    {
        var family = ElementFamilies.Of(catalog, craftable.Source);
        if (family is null)
        {
            return;
        }

        var map = plan.ToMap();
        void Edit(string newPackage, string export, string property, string value)
        {
            var list = edits.TryGetValue(newPackage, out var existing) ? existing.ToList() : [];
            list.Add(new TunableEdit(map.RemapObject(export), property, value));
            edits[newPackage] = list;
        }

        var name = craftable.Name.Trim();
        var settings = craftable.Element?.Levels ?? [];
        for (var n = 0; n < family.Levels.Count; n++)
        {
            var level = family.Levels[n];
            var newElement = map.Map(level.Element)!;
            var cdo = $"Default__{PackageMap.Leaf(level.Element)}_C";
            var package = ModdableAssets.ReadPackage(catalog, level.Element);
            var defaults = ElementFamilies.DefaultsOf(package, level.Element);
            if (defaults >= 0 && package.ReadProperties(defaults).Find("_name") is not null)
            {
                Edit(newElement, cdo, "_name", n == 0 ? name : $"{name} ({(n + 1).ToString(CultureInfo.InvariantCulture)})");
            }
            else
            {
                warnings.Add($"{craftable.Name}: level {n} of {PackageMap.Leaf(level.Element)} stores no name; it shows the game's.");
            }

            foreach (var (key, value) in n < settings.Count ? settings[n] : new Dictionary<string, string>())
            {
                if (level.Values.FirstOrDefault(v => v.Path == key) is not { } stock)
                {
                    warnings.Add($"{craftable.Name}: {key} of level {n} inherits the game's value; not written (the element does not store it).");
                }
                else if (!IsValid(stock.Kind, value))
                {
                    warnings.Add($"{craftable.Name}: {key} of level {n} is not a valid value ('{value}'); the game's {stock.Value} is kept.");
                }
                else
                {
                    Edit(newElement, cdo, key, value);
                }
            }
        }

        if (settings.Count > family.Levels.Count)
        {
            warnings.Add($"{craftable.Name}: {PackageMap.Leaf(family.Element)} has {family.Levels.Count} level(s) in the game; the values of the others are not written.");
        }

        if (family.ItemClass is { } stockItem && packages.Item is null)
        {
            warnings.Add($"{craftable.Name}: it spawns the game's own {PackageMap.Leaf(stockItem)} (no entity setup beside it to copy), which keeps the game's name.");
        }

        if (craftable.Use == CraftUse.Both && packages.ItemRecipe is null)
        {
            warnings.Add($"{craftable.Name}: {PackageMap.Leaf(family.Element)} spawns no item of its own to craft into the bag; it is only built.");
        }
    }

    private static bool IsValid(TunableKind kind, string value)
    {
        try
        {
            switch (kind)
            {
                case TunableKind.Float:
                    TunableValue.ParseFloat(value);
                    break;
                case TunableKind.Int:
                    long.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case TunableKind.UInt:
                    ulong.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case TunableKind.Bool:
                    TunableValue.ParseBool(value);
                    break;
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }
}
