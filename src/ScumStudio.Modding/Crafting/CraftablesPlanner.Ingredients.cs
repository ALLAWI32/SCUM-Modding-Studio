using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>Why an item cannot carry a new ingredient tag (<see cref="CraftablesPlanner.NewTagRefusalOf"/>).</summary>
public enum NewTagRefusal
{
    /// <summary>It can.</summary>
    None,

    /// <summary>Not an inventory item of the game.</summary>
    NotItem,

    /// <summary>The item has no entity setup.</summary>
    NoSetup,

    /// <summary>A DLC item: its setup is not changed.</summary>
    Dlc,

    /// <summary>The setup the studio reads is another mod's copy (never shipped).</summary>
    NotGameOwn,
}

public static partial class CraftablesPlanner
{
    /// <summary>
    /// The tag a new tag is copied from when its items are used up whole (<see cref="TagTemplateFor"/>): <c>CI_Group_Glass</c>,
    /// a tag of the game's default consumption method (no <c>ConsumptionMethod</c> stored, like 196 of its 238 tags) that
    /// shows an item (<c>UIClassRepresentative</c>, pointed at the new tag's first item) under a caption of its own
    /// (<c>OverrideClassRepresentativeCaption</c> on, <c>ClassRepresentativeCaption</c> becomes the item's or the group's name).
    /// Until 3.6 every new tag was a copy of <c>CI_Lead_Plate</c>, whose <c>CICM_Uses</c> counts uses: an item without uses
    /// (plastic scissors, igor8802's tool) was most likely never accepted.
    /// </summary>
    public const string ItemTagTemplate = RecipeIngredients.TagFolder + "/CI_Group_Glass";

    /// <summary>
    /// The tag a new tag is copied from when every item of it is used up by uses or pieces (<see cref="IngredientIndex.CountsUses"/>:
    /// glue, a repair kit, arrows): <c>CI_Group_Rope</c>, <c>CICM_Uses</c> as the game gives its own tags of such items, with an
    /// item shown under a caption of its own like <see cref="ItemTagTemplate"/>.
    /// </summary>
    public const string UsesTagTemplate = RecipeIngredients.TagFolder + "/CI_Group_Rope";

    /// <summary>
    /// The template of a new tag satisfied by <paramref name="items"/>: <see cref="UsesTagTemplate"/> when every one counts
    /// uses or pieces, else <see cref="ItemTagTemplate"/> (an item without uses would never count under <c>CICM_Uses</c>;
    /// one with uses is then used up whole).
    /// </summary>
    public static string TagTemplateFor(IngredientIndex index, IReadOnlyCollection<string> items)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(items);
        return items.Count > 0 && items.All(index.CountsUses) ? UsesTagTemplate : ItemTagTemplate;
    }

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

    /// <summary>
    /// Why <paramref name="item"/> cannot carry a new tag (its own, or a project group's), or <see cref="NewTagRefusal.None"/>
    /// when it can: it must be an item with an entity setup of the game's own (an imported mod's or an installed mod pak's copy
    /// is never shipped), and a DLC item's setup is not changed.
    /// </summary>
    public static NewTagRefusal NewTagRefusalOf(AssetCatalog catalog, IngredientIndex index, string item)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(item);
        if (!IsItem(catalog, PackageMap.Normalize(item)))
        {
            return NewTagRefusal.NotItem;
        }

        return index.SetupOf(item) switch
        {
            null => NewTagRefusal.NoSetup,
            var s when s.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) => NewTagRefusal.Dlc,
            var s when !IsGamePackage(catalog, s) => NewTagRefusal.NotGameOwn,
            _ => NewTagRefusal.None,
        };
    }

    /// <summary><see cref="NewTagRefusalOf"/> as the export's warnings say it, or null when the item can carry a new tag.</summary>
    public static string? WhyNoNewTag(AssetCatalog catalog, IngredientIndex index, string item) => NewTagRefusalOf(catalog, index, item) switch
    {
        NewTagRefusal.NotItem => "it is not an item of the game",
        NewTagRefusal.NoSetup => "it has no entity setup",
        NewTagRefusal.Dlc => "a DLC item's entity setup is not changed",
        NewTagRefusal.NotGameOwn => $"its entity setup {index.SetupOf(item)} is not the game's own, and another mod's package is never shipped",
        _ => null,
    };

    /// <summary>
    /// A new tag: its name and package, the stock entity setups that will list it (one item's, or each member's of a group),
    /// the clone and its caption.
    /// </summary>
    private sealed record NewItemTag(string Name, string Path, IReadOnlyList<string> Setups, ClonePlan Copy, TunableEdit? Caption);

    /// <summary>
    /// The items picked as ingredients in one <see cref="Plan"/> (owner: "what he pays for them also chosen in 3D, not
    /// typed"; CwHedgehog: "Can I add items as new crafting materials?") and the project's groups its recipes ask for. An
    /// item with a tag of its own becomes that tag. One without needs a new tag, made only when the user allows it: a copy of
    /// <see cref="TagTemplateFor"/>, and the tag appended to the item's entity setup at its stock path, since the game reads
    /// the stock setup of the item a player holds. A project group (<see cref="CraftGroup"/>) is a new tag the same way,
    /// appended to every member's setup. Anything else is left out of the recipe with a warning.
    /// </summary>
    private sealed class PickedItems(AssetCatalog catalog, IReadOnlyList<CraftGroup> groups)
    {
        private readonly Dictionary<string, NewItemTag> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (NewItemTag? Tag, string Reason)> _groupTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _groupUsers = new(StringComparer.Ordinal);
        private IngredientIndex? _index;

        private IngredientIndex Index => _index ??= IngredientIndex.Of(catalog);

        /// <summary>True for a new tag this plan makes (an item's or a group's).</summary>
        public bool IsPlanned(string tag) =>
            _tags.Values.Any(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase))
            || _groupTags.Values.Any(t => t.Tag is { } made && string.Equals(made.Name, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// <paramref name="ingredients"/> with each picked item as the tag a recipe asks for (left out when it has none) and each
        /// project group checked (left out when it cannot be made).
        /// </summary>
        public IEnumerable<CraftIngredient> Resolve(Craftable craftable, IEnumerable<CraftIngredient> ingredients, List<string> warnings)
        {
            foreach (var ingredient in ingredients)
            {
                if (ingredient.Item is { Length: > 0 } item)
                {
                    if (TagOf(craftable, item, ingredient.AddTag, warnings) is { } tag)
                    {
                        yield return ingredient with { Tag = tag };
                    }
                }
                else if (groups.FirstOrDefault(g => string.Equals(g.Tag, ingredient.Tag, StringComparison.OrdinalIgnoreCase)) is { } group)
                {
                    if (GroupTag(craftable, group, warnings) is { } tag)
                    {
                        yield return ingredient with { Tag = tag };
                    }
                }
                else
                {
                    yield return ingredient;
                }
            }
        }

        /// <summary>
        /// The plan with the new tags of the planned craftables: their clones and captions, and the stock entity setups that
        /// list them (<see cref="AssetModRequest.IngredientInstances"/>, <see cref="CraftablesPlan.StockOverrides"/>). Two
        /// craftables that pick the same item or group share one tag, and a setup that gets several tags is written once.
        /// </summary>
        public CraftablesPlan Finish(AssetModRequest request, IReadOnlyList<CraftablePackages> entries, IReadOnlyList<string> warnings)
        {
            var planned = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var used = _users.Where(u => planned.Contains(u.Key)).SelectMany(u => u.Value).Distinct(StringComparer.OrdinalIgnoreCase).Select(i => _tags[i])
                .Concat(_groupUsers.Where(u => planned.Contains(u.Key)).SelectMany(u => u.Value).Distinct(StringComparer.OrdinalIgnoreCase).Select(g => _groupTags[g].Tag!))
                .ToList();
            if (used.Count == 0)
            {
                return new CraftablesPlan(request, entries, warnings);
            }

            var edits = request.Edits.ToDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in used.Where(t => t.Caption is not null))
            {
                edits[tag.Path] = [tag.Caption!];
            }

            var instances = used.SelectMany(t => t.Setups.Select(s => (Setup: s, Tag: RecipeIngredients.TagPath(t.Name))))
                .GroupBy(t => t.Setup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            return new CraftablesPlan(request with { Clones = [.. request.Clones, .. used.Select(t => t.Copy)], Edits = edits, IngredientInstances = instances }, entries, warnings)
            {
                StockOverrides = [.. instances.Keys],
            };
        }

        private string? TagOf(Craftable craftable, string item, bool addTag, List<string> warnings)
        {
            var index = Index;
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
                WarnAboutTemplate(craftable.Name, created, warnings);
            }

            if (!_users.TryGetValue(craftable.Name, out var items))
            {
                _users[craftable.Name] = items = [];
            }

            items.Add(choice.Item);
            return tag.Name;
        }

        private string? GroupTag(Craftable craftable, CraftGroup group, List<string> warnings)
        {
            if (!_groupTags.TryGetValue(group.Tag, out var made))
            {
                var created = NewGroupTag(group, warnings, out var reason);
                _groupTags[group.Tag] = made = (created, reason);
                if (created is not null)
                {
                    WarnAboutTemplate(group.Name, created, warnings);
                }
            }

            if (made.Tag is null)
            {
                warnings.Add($"{craftable.Name}: the group '{group.Name}' ({group.Tag}) is left out of the recipe ({made.Reason}).");
                return null;
            }

            if (!_groupUsers.TryGetValue(craftable.Name, out var tags))
            {
                _groupUsers[craftable.Name] = tags = [];
            }

            tags.Add(group.Tag);
            return made.Tag.Name;
        }

        // What the copy cannot change is said, never skipped in silence (it would show the template's caption or picture in the menu).
        private void WarnAboutTemplate(string owner, NewItemTag created, List<string> warnings)
        {
            var template = PackageMap.Leaf(created.Copy.Template);
            if (created.Caption is null)
            {
                warnings.Add($"{owner}: the new tag {created.Name} shows the caption of {template} (the template stores none to change).");
            }

            if (Index.Tag(template)?.Representative is null)
            {
                warnings.Add($"{owner}: the new tag {created.Name} shows the picture of {template} (the template names no item to show).");
            }
        }

        private NewItemTag? NewTag(IngredientIndex index, IngredientChoice choice, out string reason)
        {
            var path = RecipeIngredients.TagFolder + "/" + choice.NewTag;
            var template = TagTemplateFor(index, [choice.Item]);
            reason = WhyNoNewTag(catalog, index, choice.Item) ?? choice.NewTag switch
            {
                _ when _tags.Values.Any(t => string.Equals(t.Name, choice.NewTag, StringComparison.OrdinalIgnoreCase)) => $"another item already has the tag name {choice.NewTag}",
                _ when !catalog.PackageExists(template) => $"the tag template {PackageMap.Leaf(template)} is not in the game files",
                _ when catalog.PackageExists(path) => $"{path} already exists in the game files",
                _ => string.Empty,
            };
            if (reason.Length > 0)
            {
                return null;
            }

            var setup = index.SetupOf(choice.Item)!;
            return Copy(index, template, choice.NewTag, path, [setup], choice.Item, CaptionOf(setup, choice.Item));
        }

        // A project group: every member whose setup may carry the tag (the others are named and left out of the group).
        private NewItemTag? NewGroupTag(CraftGroup group, List<string> warnings, out string reason)
        {
            var path = RecipeIngredients.TagFolder + "/" + group.Tag;
            if (!CraftGroups.IsValidTag(group.Tag))
            {
                reason = $"its tag {group.Tag} is not a valid asset name";
                return null;
            }

            if (catalog.PackageExists(path))
            {
                reason = $"{path} already exists in the game files; make the group again under another name";
                return null;
            }

            var index = Index;
            var members = new List<(string Item, string Setup)>();
            foreach (var member in group.Members.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string item;
                try
                {
                    item = index.Resolve(member) is { Match: not IngredientMatch.NotItem } choice ? choice.Item : string.Empty;
                }
                catch (ArgumentException)
                {
                    item = string.Empty;
                }

                if (item.Length == 0)
                {
                    warnings.Add($"Group '{group.Name}': {member} is not an item of the game; left out of the group.");
                }
                else if (WhyNoNewTag(catalog, index, item) is { } why)
                {
                    warnings.Add($"Group '{group.Name}': {PackageMap.Leaf(item)} is left out of the group ({why}).");
                }
                else
                {
                    members.Add((item, index.SetupOf(item)!));
                }
            }

            var template = TagTemplateFor(index, members.Select(m => m.Item).ToList());
            reason = members.Count == 0 ? "none of its items can carry its tag"
                : !catalog.PackageExists(template) ? $"the tag template {PackageMap.Leaf(template)} is not in the game files"
                : string.Empty;
            if (reason.Length > 0)
            {
                return null;
            }

            var shown = members[0].Item;
            return Copy(index, template, group.Tag, path, [.. members.Select(m => m.Setup).Distinct(StringComparer.OrdinalIgnoreCase)], shown, group.Name.Trim());
        }

        // The copy of the template: it shows the item (the template's own picture item is redirected to it) under the caption.
        private NewItemTag Copy(IngredientIndex index, string template, string name, string path, IReadOnlyList<string> setups, string shows, string caption)
        {
            var own = index.Tag(PackageMap.Leaf(template))?.Representative;
            var clone = new ClonePlan(template, path, [new(template, path)])
            {
                Redirects = own is not null && !string.Equals(own, shows, StringComparison.OrdinalIgnoreCase) ? [new(own, GamePath(shows))] : [],
            };
            var edit = HasValue(catalog, template, PackageMap.Leaf(template), "ClassRepresentativeCaption")
                ? new TunableEdit(name, "ClassRepresentativeCaption", caption)
                : null;
            return new NewItemTag(name, path, setups, clone, edit);
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
