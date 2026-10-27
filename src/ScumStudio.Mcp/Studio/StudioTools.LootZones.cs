using System.Text.Json;
using ScumStudio.Level.Spawns;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Loot zone tools for an AI: the server's own loot customization (<c>Loot\Spawners\Presets\Override\&lt;zone&gt;</c>), where a
/// preset's loot groups, chance and counts are changed only inside a rectangle of the map, such as a dungeon (the Loot editor
/// window's functions).
/// </summary>
public sealed partial class StudioTools
{
    private const int LootTagsMost = 200;

    private IEnumerable<McpTool> LootZoneTools()
    {
        yield return new McpTool("list_loot_tags",
            "Searches the game's loot tree (needs the game files): the tags a loot group may hold, a branch (ItemLootTreeNodes.Military.Weapons.Rifles, " +
            "many items) or one item. At most 200 are returned.",
            ToolSchema.Object().String("filter", "Words that must all appear in the tag (e.g. 'rifles' or 'flares').").Build(), ListLootTags)
        { Title = "Loot tree tags", ReadOnly = true, Idempotent = true };

        yield return new McpTool("read_loot_zones",
            "The server's loot customization: the zones with changed loot (their rectangles and changed presets), whether the game's own presets " +
            "were exported, and with 'preset' the loot groups of one changed preset. Read before writing.",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server (its Paks folder, SCUM, Win64 or the install folder).", required: true)
                .String("zone", "A zone name, to read one zone's presets.")
                .String("preset", "With 'zone': a changed preset's name (e.g. Street-Military-Examine_Crate) to read in full.")
                .String("defaultPreset", "Instead: one of the game's own presets to read in full (its file name without .json).")
                .Build(), ReadLootZones)
        { Title = "Loot zones", ReadOnly = true, Idempotent = true, OpenWorld = true };

        var group = ToolSchema.Object()
            .String("rarity", "Abundant (weight 32), Common (16), Uncommon (8), Rare (4), VeryRare (2) or ExtremelyRare (1).", required: true, choices: LootPresetFile.Rarities)
            .Strings("entries", "Loot tree tags (ItemLootTreeNodes....: a branch or one item, see list_loot_tags) and item ids (KeyCard).", required: true);
        yield return new McpTool("write_loot_zone",
            "Writes one changed loot preset for a zone into the server's loot customization (the zone's Zones.json rectangle and the preset file). " +
            "Inside the rectangle the preset gives what the groups say; everywhere else the game's own loot stays. A searchable object (a crate pile placed " +
            "with place_objects/add_static_mesh, no set_loot) uses the preset of its model: Street-Military-Examine_Crate for the military crate piles. " +
            "The server reads it after the admin types #ReloadLootCustomizationsAndResetSpawners in the game chat. Always give 'probability': a missing one " +
            "means the preset's own (9), not 100.",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server (its Paks folder, SCUM, Win64 or the install folder).", required: true)
                .String("zone", "The zone's folder name: letters, digits, _ and - (at most 40).", required: true)
                .Vector("cornerA", "One corner of the zone [x, y, z] (cm); z is ignored (give 0).")
                .Vector("cornerB", "The opposite corner [x, y, z] (cm); z is ignored (give 0).")
                .String("fromGuardSet", "Instead of corners: the project's guard set whose posts the zone covers.")
                .Number("margin", "With fromGuardSet: cm around the posts (default 1500).")
                .String("preset", "The preset's file name: its path with dashes, without .json (list the game's with read_loot_zones, e.g. Street-Military-Examine_Crate).", required: true)
                .Number("probability", "Chance in percent that one search gives anything (100 = always).", required: true)
                .Integer("quantityMin", "The fewest items one search gives (default 1).", minimum: 0, maximum: 50)
                .Integer("quantityMax", "The most items one search gives (default 1).", minimum: 0, maximum: 50)
                .Boolean("filterByZone", "Filter items by the game's zone rules (default false: in a dungeon outside the game's zones nothing would drop).")
                .Boolean("applyLocationProbability", "Let the place's own chance modifier multiply the chance (default false, for the same reason).")
                .Boolean("allowDuplicates", "The same item may drop twice in one search (default false).")
                .Objects("groups", "The loot groups: what a search can give, weighed by rarity.", group, required: true)
                .Build(), WriteLootZone)
        { Title = "Write loot zone", OpenWorld = true };

        yield return new McpTool("remove_loot_zone",
            "Deletes a zone's changed loot from the server (the whole zone folder, or with 'preset' one changed preset).",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server.", required: true)
                .String("zone", "The zone's name.", required: true)
                .String("preset", "Only this changed preset.")
                .Build(), RemoveLootZone)
        { Title = "Remove loot zone", OpenWorld = true };
    }

    private Task<McpToolResult> ListLootTags(ToolCall call, CancellationToken cancellationToken)
    {
        var tables = LootTables.Read(RequireCatalog());
        var tokens = Tokens(call.GetString("filter")).ToList();
        var found = tables.Tags.Where(t => MatchesAll(tokens, t)).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(McpToolResult.Json(new
        {
            total = found.Count,
            shown = Math.Min(found.Count, LootTagsMost),
            tags = found.Take(LootTagsMost).Select(t => new { tag = t, kind = tables.IsBranch(t) ? "group" : "item", rarity = tables.RarityOf(t) }),
        }));
    }

    private Task<McpToolResult> ReadLootZones(ToolCall call, CancellationToken cancellationToken)
    {
        var store = new LootOverrideStore(RequireServer(call));
        if (call.GetString("defaultPreset") is { Length: > 0 } defaultName)
        {
            var preset = store.ReadDefault(defaultName) ?? throw new ToolArgumentException($"No exported game preset '{defaultName}' (the Default folder is written by #ExportDefaultItemSpawnerPresets).");
            return Task.FromResult(McpToolResult.Json(PresetView(defaultName, preset)));
        }

        if (call.GetString("zone") is { Length: > 0 } zone)
        {
            if (call.GetString("preset") is { Length: > 0 } presetName)
            {
                var preset = store.ReadOverride(zone, presetName) ?? throw new ToolArgumentException($"Zone '{zone}' has no changed preset '{presetName}'.");
                return Task.FromResult(McpToolResult.Json(PresetView(presetName, preset)));
            }

            return Task.FromResult(McpToolResult.Json(new { zone, rectangle = ZoneView(store.ReadZone(zone)), presets = store.PresetsOf(zone) }));
        }

        return Task.FromResult(McpToolResult.Json(new
        {
            gameDefaultsExported = store.HasDefaults,
            gamePresets = store.DefaultPresets().Count,
            exportCommand = LootOverrideStore.ExportCommand,
            reloadCommand = LootOverrideStore.ReloadCommand,
            zones = store.Zones().Select(z => new { zone = z, rectangle = ZoneView(store.ReadZone(z)), presets = store.PresetsOf(z) }),
        }));
    }

    private static object? ZoneView(LootZone? zone) =>
        zone is { } z ? new { topLeft = new { x = z.MaxX, y = z.MaxY }, bottomRight = new { x = z.MinX, y = z.MinY } } : null;

    private static object PresetView(string name, LootPresetFile preset) => new
    {
        preset = name,
        probability = preset.Probability,
        quantityMin = preset.QuantityMin,
        quantityMax = preset.QuantityMax,
        filterByZone = preset.FilterByZone,
        applyLocationProbability = preset.ApplyLocationProbability,
        allowDuplicates = preset.AllowDuplicates,
        groups = preset.Groups.Select(g => new { rarity = g.Rarity, entries = g.Entries }),
    };

    private Task<McpToolResult> WriteLootZone(ToolCall call, CancellationToken cancellationToken)
    {
        var server = RequireServer(call);
        var store = new LootOverrideStore(server);
        var zone = call.RequireString("zone");
        var presetName = call.RequireString("preset");
        if (!LootOverrideStore.IsValidZoneName(zone))
        {
            throw new ToolArgumentException("'zone' is letters, digits, _ and - (at most 40).");
        }

        if (!LootOverrideStore.IsValidPresetName(presetName))
        {
            throw new ToolArgumentException("'preset' is a preset file name without .json: letters, digits, _, - and . only.");
        }

        LootZone area;
        if (call.GetString("fromGuardSet") is { Length: > 0 } setName)
        {
            var set = RequireProject().State.GetGuardSet(setName) ?? throw new ToolArgumentException($"The project has no guard set '{setName}' (list_guard_spawns).");
            area = LootZone.Around(set.Posts.Select(p => ((double)p.Position.X, (double)p.Position.Y)), call.GetNumber("margin") ?? 1500);
        }
        else if (call.GetVector3("cornerA") is { } a && call.GetVector3("cornerB") is { } b)
        {
            area = LootZone.Of(a.X, a.Y, b.X, b.Y);
        }
        else
        {
            area = store.ReadZone(zone) ?? throw new ToolArgumentException("Give 'cornerA' and 'cornerB', or 'fromGuardSet' (the zone has no rectangle yet).");
        }

        if (!area.IsValid)
        {
            throw new ToolArgumentException("The zone's corners must make a rectangle with a size.");
        }

        // Start from the game's own file when there is one: its other fields (post-spawn actions ...) stay.
        var preset = (store.ReadOverride(zone, presetName) ?? store.ReadDefault(presetName))?.Clone() ?? LootPresetFile.Empty();
        preset.Probability = call.GetNumber("probability");
        preset.QuantityMin = call.GetInt("quantityMin", 1);
        preset.QuantityMax = call.GetInt("quantityMax", 1);
        preset.FilterByZone = call.GetBool("filterByZone", false);
        preset.ApplyLocationProbability = call.GetBool("applyLocationProbability", false);
        preset.AllowDuplicates = call.GetBool("allowDuplicates", false);
        var groups = new List<LootGroup>();
        if (call.GetElement("groups") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var element in list.EnumerateArray())
            {
                var rarity = element.TryGetProperty("rarity", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                var entries = element.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(i => i.GetString() ?? string.Empty).Where(i => i.Length > 0).ToList()
                    : [];
                groups.Add(new LootGroup(rarity, entries));
            }
        }

        preset.SetGroups(groups);
        if (preset.Problems() is { Count: > 0 } problems)
        {
            throw new ToolArgumentException("The preset has problems: " + string.Join("; ", problems.Select(p => p.Code + (p.Detail.Length > 0 ? " '" + p.Detail + "'" : string.Empty))));
        }

        store.WriteZone(zone, area);
        store.WritePreset(zone, presetName, preset);
        return Task.FromResult(McpToolResult.Json(new
        {
            written = Path.Combine(store.OverrideFolder, zone, presetName + ".json"),
            rectangle = ZoneView(area),
            gameDefaultsExported = store.HasDefaults,
            next = "Ask the admin to type " + LootOverrideStore.ReloadCommand + " in the game chat (no server restart needed).",
        }));
    }

    private Task<McpToolResult> RemoveLootZone(ToolCall call, CancellationToken cancellationToken)
    {
        var store = new LootOverrideStore(RequireServer(call));
        var zone = call.RequireString("zone");
        if (!store.Zones().Contains(zone, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolArgumentException($"No loot zone '{zone}' on the server (read_loot_zones).");
        }

        if (call.GetString("preset") is { Length: > 0 } preset)
        {
            store.RemovePreset(zone, preset);
            return Task.FromResult(McpToolResult.Text($"Removed the changed preset {preset} from zone {zone}."));
        }

        store.RemoveZone(zone);
        return Task.FromResult(McpToolResult.Text($"Removed zone {zone}."));
    }
}
