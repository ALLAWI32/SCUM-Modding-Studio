using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

public static partial class CraftablesPlanner
{
    /// <summary>
    /// <see cref="Plan"/> plus the game's recipes whose amounts per skill level the
    /// user overrides (<see cref="CraftablesFile.RecipeOverrides"/>): each one the game ships as its own (not another mod's
    /// copy) and whose slots are still the ones it was set on becomes a stock override (<see cref="AssetModRequest.RecipeAmounts"/>,
    /// <see cref="CraftablesPlan.StockOverrides"/>); any other is left out with a warning.
    /// </summary>
    public static CraftablesPlan PlanWithStockRecipes(AssetCatalog catalog, IReadOnlyList<Craftable> craftables, IReadOnlyList<StockRecipeOverride> recipeOverrides, string? projectDirectory = null,
        IReadOnlyList<CraftGroup>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(recipeOverrides);
        var plan = Plan(catalog, craftables, projectDirectory, groups);
        if (recipeOverrides.Count == 0)
        {
            return plan;
        }

        var warnings = plan.Warnings.ToList();
        var amounts = StockRecipeAmounts(catalog, recipeOverrides, warnings);
        return plan with
        {
            Request = plan.Request with { RecipeAmounts = amounts },
            Warnings = warnings,
            StockOverrides = [.. plan.StockOverrides, .. amounts.Keys.Where(k => !plan.StockOverrides.Contains(k, StringComparer.OrdinalIgnoreCase))],
        };
    }

    /// <summary>
    /// The slots of the game's recipe <paramref name="recipe"/> (<see cref="RecipeIngredients.ReadSlots"/>) and its skill, or
    /// null with <paramref name="reason"/> set when it cannot be overridden: not a recipe of the game, not in the game files,
    /// or the copy the studio reads is another mod's (its package is never shipped over).
    /// </summary>
    public static (IReadOnlyList<RecipeSlot> Slots, string? Skill)? StockRecipe(AssetCatalog catalog, string recipe, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(recipe);
        var path = PackageMap.Normalize(recipe);
        reason = !CraftCategories.IsStockRecipe(path) ? "it is not a crafting recipe of the game"
            : !catalog.PackageExists(path) ? "it is not in the game files"
            : !IsGamePackage(catalog, path) ? $"the copy the studio reads comes from {catalog.ContainerOf(path) ?? "another mod"}, and another mod's package is never shipped"
            : string.Empty;
        if (reason.Length > 0)
        {
            return null;
        }

        try
        {
            var package = ModdableAssets.ReadPackage(catalog, path);
            var slots = RecipeIngredients.ReadSlots(package);
            if (slots.Count == 0)
            {
                reason = "it has no ingredient slot";
                return null;
            }

            return (slots, RecipeIngredients.Skill(package));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException)
        {
            reason = ex.Message;
            return null;
        }
    }

    // Each override still valid against the game's recipe -> its amounts, one per slot.
    private static Dictionary<string, IReadOnlyList<LevelAmounts>> StockRecipeAmounts(AssetCatalog catalog, IReadOnlyList<StockRecipeOverride> overrides, List<string> warnings)
    {
        var amounts = new Dictionary<string, IReadOnlyList<LevelAmounts>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides)
        {
            var path = PackageMap.Normalize(entry.Recipe);
            var leaf = PackageMap.Leaf(path);
            if (amounts.ContainsKey(path))
            {
                warnings.Add($"{leaf}: set twice in the recipe overrides; the first one is kept.");
                continue;
            }

            if (StockRecipe(catalog, path, out var reason) is not { } stock)
            {
                warnings.Add($"{leaf}: the amounts per skill level are not exported ({reason}).");
                continue;
            }

            // A game update that changed the recipe's slots would put the amounts on other ingredients.
            if (stock.Slots.Count != entry.Slots.Count || stock.Slots.Where((s, i) => !string.Equals(s.TagText, entry.Slots[i].Tags, StringComparison.OrdinalIgnoreCase)).Any())
            {
                warnings.Add($"{leaf}: the amounts per skill level are not exported (the game's recipe now asks for {string.Join(", ", stock.Slots.Select(s => s.TagText))}, not what they were set on); set them again on the Craftables page.");
                continue;
            }

            var wanted = entry.Slots.Select(s => s.Amount.Clamped()).ToList();
            if (wanted.SequenceEqual(stock.Slots.Select(s => s.Amount)))
            {
                warnings.Add($"{leaf}: the amounts per skill level are the game's own; the recipe is not shipped.");
                continue;
            }

            amounts[path] = wanted;
        }

        return amounts;
    }
}
