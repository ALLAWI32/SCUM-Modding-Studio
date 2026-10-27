using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>
/// One loot group of a preset: what a search can give and how often relative to the other groups.
/// </summary>
/// <param name="Rarity">One of <see cref="LootPresetFile.Rarities"/>: the group's weight (Abundant is 32 times Extremely Rare).</param>
/// <param name="Entries">Loot tree tags (<c>ItemLootTreeNodes.Military.Weapons.Rifles</c>, a branch or one item) and item ids (<c>KeyCard</c>).</param>
public sealed record LootGroup(string Rarity, IReadOnlyList<string> Entries);

/// <summary>
/// A SCUM loot spawner preset as the server reads it from <c>Loot\Spawners\Presets\Override</c> (the file
/// <c>#ExportDefaultItemSpawnerPresets</c> writes): groups of loot tree tags (<c>Nodes</c>) and single items (<c>Items</c>) with a
/// rarity, the chance that the spawner gives anything, how many items one search gives, and the zone and damage rules. Fields the
/// editor does not know (<c>PostSpawnActions</c>, ...) are kept as they were read.
/// </summary>
public sealed partial class LootPresetFile
{
    /// <summary>The rarities, most common first. The game weighs them 32, 16, 8, 4, 2 and 1.</summary>
    public static readonly IReadOnlyList<string> Rarities = ["Abundant", "Common", "Uncommon", "Rare", "VeryRare", "ExtremelyRare"];

    /// <summary>The prefix of every loot tree tag.</summary>
    public const string TagRoot = "ItemLootTreeNodes";

    private readonly JsonObject _root;

    private LootPresetFile(JsonObject root) => _root = root;

    /// <summary>A preset that gives one pick of nothing yet (the editor fills it in).</summary>
    public static LootPresetFile Empty() => new(new JsonObject
    {
        ["Probability"] = 100,
        ["QuantityMin"] = 1,
        ["QuantityMax"] = 1,
        ["AllowDuplicates"] = false,
        ["ShouldFilterItemsByZone"] = false,
        ["ShouldApplyLocationSpecificProbabilityModifier"] = false,
        ["ShouldApplyLocationSpecificDamageModifier"] = false,
        ["InitialDamage"] = 0,
        ["RandomDamage"] = 0,
        ["InitialUsage"] = 0,
        ["RandomUsage"] = 0,
        ["InitialStack"] = 0,
        ["RandomStack"] = 0,
    });

    /// <summary>Reads a preset file's text.</summary>
    /// <exception cref="FormatException">The text is not a JSON object.</exception>
    public static LootPresetFile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject root
                ? new LootPresetFile(root)
                : throw new FormatException("A loot preset is a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>A copy with the same content.</summary>
    public LootPresetFile Clone() => new((JsonObject)_root.DeepClone());

    /// <summary>The chance (percent) that a search gives anything. Null: the file has no <c>Probability</c>, so the preset class's own applies (9 for a stock crate pile).</summary>
    public double? Probability
    {
        get => Number("Probability");
        set => SetNumber("Probability", value);
    }

    /// <summary>The fewest items one search gives.</summary>
    public int QuantityMin
    {
        get => (int)(Number("QuantityMin") ?? 1);
        set => _root["QuantityMin"] = value;
    }

    /// <summary>The most items one search gives.</summary>
    public int QuantityMax
    {
        get => (int)(Number("QuantityMax") ?? 1);
        set => _root["QuantityMax"] = value;
    }

    /// <summary>The same item may drop twice in one search.</summary>
    public bool AllowDuplicates
    {
        get => Flag("AllowDuplicates", false);
        set => _root["AllowDuplicates"] = value;
    }

    /// <summary>Items are filtered by the zone's rules (coastal, mountain ...). Off in places outside the game's own zones, or nothing drops.</summary>
    public bool FilterByZone
    {
        get => Flag("ShouldFilterItemsByZone", true);
        set => _root["ShouldFilterItemsByZone"] = value;
    }

    /// <summary>The place's own chance modifier multiplies the chance. Off outside the game's own zones.</summary>
    public bool ApplyLocationProbability
    {
        get => Flag("ShouldApplyLocationSpecificProbabilityModifier", true);
        set => _root["ShouldApplyLocationSpecificProbabilityModifier"] = value;
    }

    /// <summary>The place's own damage modifier applies.</summary>
    public bool ApplyLocationDamage
    {
        get => Flag("ShouldApplyLocationSpecificDamageModifier", true);
        set => _root["ShouldApplyLocationSpecificDamageModifier"] = value;
    }

    /// <summary>How worn the items come out at least (percent).</summary>
    public double InitialDamage
    {
        get => Number("InitialDamage") ?? 0;
        set => _root["InitialDamage"] = value;
    }

    /// <summary>How much more wear a random item gets at most (percent).</summary>
    public double RandomDamage
    {
        get => Number("RandomDamage") ?? 0;
        set => _root["RandomDamage"] = value;
    }

    /// <summary>The loot groups: the tag groups (<c>Nodes</c>) first, then the single items (<c>Items</c>) by rarity.</summary>
    public IReadOnlyList<LootGroup> Groups
    {
        get
        {
            var groups = new List<LootGroup>();
            if (_root["Nodes"] is JsonArray nodes)
            {
                foreach (var node in nodes.OfType<JsonObject>())
                {
                    var ids = node["Ids"] is JsonArray list ? list.Select(i => i?.GetValue<string>() ?? string.Empty).Where(i => i.Length > 0).ToList() : [];
                    groups.Add(new LootGroup(node["Rarity"]?.GetValue<string>() ?? "Uncommon", ids));
                }
            }

            if (_root["Items"] is JsonArray items)
            {
                foreach (var byRarity in items.OfType<JsonObject>()
                             .Select(i => (Rarity: i["Rarity"]?.GetValue<string>() ?? "Uncommon", Id: i["Id"]?.GetValue<string>() ?? string.Empty))
                             .Where(i => i.Id.Length > 0)
                             .GroupBy(i => i.Rarity))
                {
                    groups.Add(new LootGroup(byRarity.Key, byRarity.Select(i => i.Id).ToList()));
                }
            }

            return groups;
        }
    }

    /// <summary>Replaces the loot groups: entries with a dot are loot tree tags (<c>Nodes</c>), the others item ids (<c>Items</c>).</summary>
    public void SetGroups(IEnumerable<LootGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var nodes = new JsonArray();
        var items = new JsonArray();
        foreach (var group in groups)
        {
            var tags = group.Entries.Where(IsTag).ToList();
            if (tags.Count > 0)
            {
                nodes.Add(new JsonObject { ["Rarity"] = group.Rarity, ["Ids"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()) });
            }

            foreach (var id in group.Entries.Where(e => !IsTag(e)))
            {
                items.Add(new JsonObject { ["Rarity"] = group.Rarity, ["Id"] = id });
            }
        }

        Replace("Nodes", nodes);
        Replace("Items", items);
    }

    /// <summary>True for a loot tree tag (<c>ItemLootTreeNodes.Military.Weapons</c>), false for an item id.</summary>
    public static bool IsTag(string entry) => entry.Contains('.', StringComparison.Ordinal);

    /// <summary>What is wrong with the preset (empty when it can be written): the first problem as a key and its detail.</summary>
    public IReadOnlyList<(string Code, string Detail)> Problems()
    {
        var problems = new List<(string, string)>();
        var groups = Groups;
        if (groups.Count == 0 || groups.All(g => g.Entries.Count == 0))
        {
            problems.Add(("NoLoot", string.Empty));
        }

        foreach (var group in groups)
        {
            if (!Rarities.Contains(group.Rarity))
            {
                problems.Add(("Rarity", group.Rarity));
            }

            foreach (var entry in group.Entries)
            {
                if (IsTag(entry) ? !TagText().IsMatch(entry) || !entry.StartsWith(TagRoot + ".", StringComparison.Ordinal) : !ItemText().IsMatch(entry))
                {
                    problems.Add(("Entry", entry));
                }
            }
        }

        if (QuantityMin < 0 || QuantityMax < QuantityMin)
        {
            problems.Add(("Quantity", string.Create(CultureInfo.InvariantCulture, $"{QuantityMin}-{QuantityMax}")));
        }

        if (Probability is { } p && (p < 0 || p > 100))
        {
            problems.Add(("Probability", p.ToString(CultureInfo.InvariantCulture)));
        }

        return problems;
    }

    /// <summary>The file text (indented JSON).</summary>
    public string ToJson() =>
        _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private double? Number(string key) => _root[key] is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l : null
        : null;

    private bool Flag(string key, bool fallback) => _root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private void SetNumber(string key, double? value)
    {
        if (value is null)
        {
            _root.Remove(key);
        }
        else
        {
            _root[key] = value;
        }
    }

    private void Replace(string key, JsonArray list)
    {
        if (list.Count == 0)
        {
            _root.Remove(key);
        }
        else
        {
            _root[key] = list;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex TagText();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ItemText();
}
