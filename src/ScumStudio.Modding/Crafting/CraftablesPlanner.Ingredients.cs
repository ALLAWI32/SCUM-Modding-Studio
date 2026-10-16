using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

public static partial class CraftablesPlanner
{
    /// <summary>
    /// The tag an item's new tag is copied from: <c>CI_Lead_Plate</c>, which one item satisfies and shows (its
    /// <c>UIClassRepresentative</c> is pointed at the item, its <c>ClassRepresentativeCaption</c> becomes the item's name).
    /// </summary>
    public const string ItemTagTemplate = RecipeIngredients.TagFolder + "/CI_Lead_Plate";

    /// <summary>True for a path under a folder where items never are (characters, vehicles …, <see cref="IsItem"/>).</summary>
    internal static bool IsNotItemFolder(string path) => NotItems.Any(n => path.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the copy of a package the studio reads (and would append to and ship) is the game's own: the winning entry
    /// comes from a pak whose name does not end in <c>_P</c>. A mod pak or an imported mod's loose folder that overrides it
    /// is not, even though the game also ships the path (<see cref="PackageOrigins.IsStock"/> would say yes). In a loose
    /// extract with no pak at all, only the extract's own folder is the game; an overlay folder is not.
    /// </summary>
    public static bool IsGamePackage(AssetCatalog catalog, string path)
    {
        if (!catalog.TryGetPackageFile(path, out var file))
        {
            return false;
        }

        if (catalog.Provider is IVfsFileProvider { MountedVfs.Count: > 0 })
        {
            return file is VfsEntry entry && !PackageOrigins.IsModContainer(entry.Vfs.Name);
        }

        return catalog.Provider is not ScumFileProvider { LooseRoots.Count: > 1 } loose
            || string.Equals(catalog.ContainerOf(path)?.TrimEnd('/', '\\'), loose.LooseRoots[0].TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A new tag for an item: its name and package, the item's stock entity setup that lists it, the clone and its caption.</summary>
    private sealed record NewItemTag(string Name, string Path, string Setup, ClonePlan Copy, TunableEdit? Caption);

    /// <summary>
    /// The items picked as ingredients in one <see cref="Plan"/> (owner: "what he pays for them also chosen in 3D, not
    /// typed"; CwHedgehog: "Can I add items as new crafting materials?"). An item with a tag of its own becomes that tag.
    /// One without needs a new tag, made only when the user allows it: a copy of <see cref="ItemTagTemplate"/>, and the tag
    /// appended to the item's entity setup at its stock path, since the game reads the stock setup of the item a player
    /// holds. Anything else is left out of the recipe with a warning that names the groups it counts as.
    /// </summary>
    private sealed class PickedItems(AssetCatalog catalog)
    {
        private readonly Dictionary<string, NewItemTag> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _users = new(StringComparer.Ordinal);
        private IngredientIndex? _index;

        /// <summary>True for a new tag this plan makes.</summary>
        public bool IsPlanned(string tag) => _tags.Values.Any(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary><paramref name="ingredients"/> with each picked item as the tag a recipe asks for (left out when it has none).</summary>
        public IEnumerable<CraftIngredient> Resolve(Craftable craftable, IEnumerable<CraftIngredient> ingredients, List<string> warnings)
        {
            foreach (var ingredient in ingredients)
            {
                if (ingredient.Item is not { Length: > 0 } item)
                {
                    yield return ingredient;
                }
                else if (TagOf(craftable, item, ingredient.AddTag, warnings) is { } tag)
                {
                    yield return ingredient with { Tag = tag };
                }
            }
        }

        /// <summary>
        /// The plan with the new tags of the planned craftables: their clones and captions, and the stock entity setups that
        /// list them (<see cref="AssetModRequest.IngredientInstances"/>, <see cref="CraftablesPlan.StockOverrides"/>). Two
        /// craftables that pick the same item share one tag and one setup write.
        /// </summary>
        public CraftablesPlan Finish(AssetModRequest request, IReadOnlyList<CraftablePackages> entries, IReadOnlyList<string> warnings)
        {
            var planned = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var used = _users.Where(u => planned.Contains(u.Key)).SelectMany(u => u.Value).Distinct(StringComparer.OrdinalIgnoreCase).Select(i => _tags[i]).ToList();
            if (used.Count == 0)
            {
                return new CraftablesPlan(request, entries, warnings);
            }

            var edits = request.Edits.ToDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in used.Where(t => t.Caption is not null))
            {
                edits[tag.Path] = [tag.Caption!];
            }

            var instances = used.GroupBy(t => t.Setup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => RecipeIngredients.TagPath(t.Name)).ToList(), StringComparer.OrdinalIgnoreCase);
            return new CraftablesPlan(request with { Clones = [.. request.Clones, .. used.Select(t => t.Copy)], Edits = edits, IngredientInstances = instances }, entries, warnings)
            {
                StockOverrides = [.. instances.Keys],
            };
        }

        private string? TagOf(Craftable craftable, string item, bool addTag, List<string> warnings)
        {
            var index = _index ??= IngredientIndex.Of(catalog);
            IngredientChoice choice;
            try
            {
                choice = index.Resolve(item);
            }
            catch (ArgumentException)
            {
                warnings.Add($"{craftable.Name}: the ingredient {item} is not a package path; left out of the recipe.");
                return null;
            }

            var leaf = PackageMap.Leaf(choice.Item);
            if (choice.Match == IngredientMatch.OwnTag)
            {
                return choice.Tag;
            }

            if (choice.Match == IngredientMatch.NotItem)
            {
                warnings.Add($"{craftable.Name}: the ingredient {choice.Item} is not an item of the game; left out of the recipe.");
                return null;
            }

            if (!addTag)
            {
                warnings.Add(choice.Match == IngredientMatch.GroupOnly
                    ? $"{craftable.Name}: {leaf} has no ingredient tag of its own (it counts only as {string.Join(", ", choice.Groups.Select(g => Describe(index, g)))}); left out of the recipe. Use that group, or allow a new tag for {leaf}."
                    : $"{craftable.Name}: no recipe of the game can ask for {leaf} (no ingredient tag lists it); left out of the recipe. Allow a new tag for {leaf} to use it.");
                return null;
            }

            if (!_tags.TryGetValue(choice.Item, out var tag))
            {
                if (NewTag(index, choice, out var reason) is not { } created)
                {
                    warnings.Add($"{craftable.Name}: {leaf} gets no tag of its own ({reason}); left out of the recipe.");
                    return null;
                }

                _tags[choice.Item] = tag = created;

                // What the copy cannot change is said, never skipped in silence (it would show the lead plate in the menu).
                if (created.Caption is null)
                {
                    warnings.Add($"{craftable.Name}: the new tag {created.Name} shows the caption of {PackageMap.Leaf(ItemTagTemplate)} (the template stores none to change).");
                }

                if (index.Tag(PackageMap.Leaf(ItemTagTemplate))?.Representative is null)
                {
                    warnings.Add($"{craftable.Name}: the new tag {created.Name} shows the picture of {PackageMap.Leaf(ItemTagTemplate)} (the template names no item to show).");
                }
            }

            if (!_users.TryGetValue(craftable.Name, out var items))
            {
                _users[craftable.Name] = items = [];
            }

            items.Add(choice.Item);
            return tag.Name;
        }

        private NewItemTag? NewTag(IngredientIndex index, IngredientChoice choice, out string reason)
        {
            var path = RecipeIngredients.TagFolder + "/" + choice.NewTag;
            reason = index.SetupOf(choice.Item) switch
            {
                null => "it has no entity setup",
                var s when s.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) => "a DLC item's entity setup is not changed",
                var s when !IsGamePackage(catalog, s) => $"its entity setup {s} is not the game's own, and another mod's package is never shipped",
                _ when _tags.Values.Any(t => string.Equals(t.Name, choice.NewTag, StringComparison.OrdinalIgnoreCase)) => $"another item already has the tag name {choice.NewTag}",
                _ when !catalog.PackageExists(ItemTagTemplate) => $"the tag template {PackageMap.Leaf(ItemTagTemplate)} is not in the game files",
                _ when catalog.PackageExists(path) => $"{path} already exists in the game files",
                _ => string.Empty,
            };
            if (reason.Length > 0)
            {
                return null;
            }

            var setup = index.SetupOf(choice.Item)!;
            var shows = index.Tag(PackageMap.Leaf(ItemTagTemplate))?.Representative;
            var clone = new ClonePlan(ItemTagTemplate, path, [new(ItemTagTemplate, path)])
            {
                Redirects = shows is not null && !string.Equals(shows, choice.Item, StringComparison.OrdinalIgnoreCase) ? [new(shows, GamePath(choice.Item))] : [],
            };
            var caption = HasValue(catalog, ItemTagTemplate, PackageMap.Leaf(ItemTagTemplate), "ClassRepresentativeCaption")
                ? new TunableEdit(choice.NewTag, "ClassRepresentativeCaption", CaptionOf(setup, choice.Item))
                : null;
            return new NewItemTag(choice.NewTag, path, setup, clone, caption);
        }

        // The item's own name as its setup stores it, else its asset name with spaces.
        private string CaptionOf(string setup, string item)
        {
            try
            {
                if (ItemSetups.ClassDefaults(ModdableAssets.ReadPackage(catalog, setup))?.Find("Caption")?.Value is TextValue { SourceString: { Length: > 0 } text })
                {
                    return text;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                // the asset name below
            }

            return PackageMap.Leaf(item).Replace('_', ' ');
        }

        private static string Describe(IngredientIndex index, string tag) =>
            index.Tag(tag)?.Caption is { } caption ? $"{tag} ('{caption}')" : tag;
    }
}
