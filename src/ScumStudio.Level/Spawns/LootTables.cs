using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.Level.Spawns;

/// <summary>One loot group a preset draws from (<c>ItemLootTreeNodes.Airfield</c>) and how rare a pick of it is.</summary>
public sealed record LootNode(string Tag, string Rarity);

/// <summary>
/// A loot preset (<c>SpawnerPresets2/.../World_Shelf</c>): the chance a loot point spawns anything and the loot groups it
/// draws from.
/// </summary>
public sealed record LootPreset(string ClassPath, string Name, float Probability, bool AlwaysSpawn, IReadOnlyList<LootNode> Nodes);

/// <summary>One item a loot group can give, with its rarity and the branch it sits on (<c>Tools</c>, <c>Clothes.Head</c>).</summary>
public sealed record LootItem(string Name, string Branch, string Rarity);

/// <summary>
/// The game's loot tree (owner: "click a loot pin and see what can spawn there"). A loot point names a preset; the preset
/// names loot groups (<c>ItemLootTreeNodes.Airfield</c>) with a rarity; the tables under <see cref="NodesFolder"/>
/// (<c>ILTN_Airfield</c>, ...) list every node of the tree with its rarity, and the leaves are the items
/// (<c>ItemLootTreeNodes.Airfield.Tools.Car_Battery</c>). Read once per catalog; presets are read when first asked for.
/// </summary>
public sealed class LootTables
{
    /// <summary>Folder of the loot tree tables (one DataTable per group: <c>ILTN_Airfield</c>, <c>ILTN_Military</c>, ...).</summary>
    public const string NodesFolder = "/Game/ConZ_Files/Data/Tables/Items/Spawning/Nodes/";

    private const string RootTag = "ItemLootTreeNodes";

    private readonly AssetCatalog _catalog;
    private readonly Dictionary<string, string> _rarity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LootPreset?> _presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private LootTables(AssetCatalog catalog) => _catalog = catalog;

    /// <summary>Nodes of the tree (every row of every table).</summary>
    public int NodeCount => _rarity.Count;

    /// <summary>Reads every loot tree table of <paramref name="catalog"/>.</summary>
    public static LootTables Read(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var tables = new LootTables(catalog);
        var folder = AssetPaths.ToFilePathWithoutExtension(NodesFolder, catalog.ProjectName);
        foreach (var file in catalog.PackageFiles.Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
        {
            var path = AssetPaths.ToPackagePath(file, catalog.ProjectName);
            if (!catalog.TryLoadObject<UDataTable>(path, out var table))
            {
                continue;
            }

            foreach (var (name, row) in table.RowMap)
            {
                tables._rarity[name.Text] = RarityOf(row);
            }
        }

        foreach (var tag in tables._rarity.Keys)
        {
            var dot = tag.LastIndexOf('.');
            if (dot > 0)
            {
                var parent = tag[..dot];
                if (!tables._children.TryGetValue(parent, out var list))
                {
                    tables._children[parent] = list = [];
                }

                list.Add(tag);
            }
        }

        return tables;
    }

    /// <summary>The rarity the tree gives <paramref name="tag"/>, or null for a tag it does not have.</summary>
    public string? RarityOf(string tag) => _rarity.TryGetValue(tag, out var rarity) ? rarity : null;

    /// <summary>
    /// Every item under <paramref name="tag"/> (the leaves of its branch, or the tag itself when it is a leaf), in tree order.
    /// </summary>
    public IReadOnlyList<LootItem> ItemsUnder(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        var items = new List<LootItem>();
        var stack = new Stack<string>();
        stack.Push(tag);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (_children.TryGetValue(node, out var children))
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }

                continue;
            }

            if (_rarity.TryGetValue(node, out var rarity))
            {
                var dot = node.LastIndexOf('.');
                var branch = node.Length > tag.Length && dot > tag.Length ? node[(tag.Length + 1)..dot] : string.Empty;
                items.Add(new LootItem(node[(dot + 1)..], branch, rarity));
            }
        }

        return items;
    }

    /// <summary>The preset of a loot point (its class path, <c>/Game/.../World_Shelf.World_Shelf_C</c>), or null when unreadable.</summary>
    public LootPreset? Preset(string classPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(classPath);
        lock (_lock)
        {
            if (_presets.TryGetValue(classPath, out var known))
            {
                return known;
            }
        }

        var preset = ReadPreset(classPath);
        lock (_lock)
        {
            _presets[classPath] = preset;
        }

        return preset;
    }

    private LootPreset? ReadPreset(string classPath)
    {
        var (package, className) = AssetPaths.SplitObjectPath(classPath);
        if (!_catalog.TryLoadObject<UObject>(package + ".Default__" + className, out var cdo))
        {
            return null;
        }

        // A preset stores only what differs from its parent preset: values are looked up along the archetypes.
        var chain = new List<UObject>();
        for (var o = cdo; o is not null && chain.Count < 16; o = o.Template?.Load())
        {
            chain.Add(o);
        }

        var probability = Find(chain, "Probability", out float p) ? p : 100f;
        var always = Find(chain, "AlwaysSpawn", out bool a) && a;
        var nodes = new List<LootNode>();
        if (Find(chain, "Nodes", out FStructFallback[] entries))
        {
            foreach (var entry in entries)
            {
                var rarity = RarityOf(entry);
                if (entry.TryGetValue(out FGameplayTagContainer tags, "Nodes"))
                {
                    nodes.AddRange(tags.GameplayTags.Select(t => new LootNode(t.TagName.Text, rarity)));
                }
            }
        }

        var name = className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className;
        return new LootPreset(classPath, name, probability, always, nodes);

        static bool Find<T>(List<UObject> chain, string property, out T value)
        {
            foreach (var o in chain)
            {
                if (o.TryGetValue(out value, property))
                {
                    return true;
                }
            }

            value = default!;
            return false;
        }
    }

    /// <summary><c>EItemRarity::Uncommon</c> → <c>Uncommon</c>.</summary>
    private static string RarityOf(FStructFallback row) =>
        row.TryGetValue(out FName rarity, "Rarity") && rarity.Text is { Length: > 0 } text ? text[(text.LastIndexOf(':') + 1)..] : "Common";

    /// <summary>A readable name for an item leaf (<c>Car_Battery_Cables</c> → <c>Car Battery Cables</c>).</summary>
    public static string Readable(string name) => name.Replace('_', ' ');

    /// <summary>The group a tag names without the tree's root (<c>ItemLootTreeNodes.Airfield.Tools</c> → <c>Airfield.Tools</c>).</summary>
    public static string GroupName(string tag) => tag.StartsWith(RootTag + ".", StringComparison.Ordinal) ? tag[(RootTag.Length + 1)..] : tag;
}
