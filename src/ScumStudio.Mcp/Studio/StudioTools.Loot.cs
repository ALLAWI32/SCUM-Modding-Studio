using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Loot of a container an AI has placed: which of the game's loot presets (<c>SpawnerPresets2/.../Examine_X</c>) a search of it gives,
/// as the Map page's loot panel sets it. One journal step; undo restores the loot it had.
/// </summary>
public sealed partial class StudioTools
{
    private const string LootPresetsFolder = "/Game/ConZ_Files/Items/SpawnerPresets2/";

    private IEnumerable<McpTool> LootTools()
    {
        yield return new McpTool("list_loot_presets",
            "The game's loot presets a container can give when searched (SpawnerPresets2/.../Examine_X): the name to pass to set_loot, " +
            "its folder (which kind of place it belongs to) and, when 'details' is true, its chance (Probability, percent) and loot groups.",
            ToolSchema.Object()
                .String("filter", "Words that must all appear in the path (e.g. 'military weapons').")
                .Boolean("details", "Read each preset's chance and loot groups (slower; at most 30 presets).")
                .Build(), ListLootPresets)
        { Title = "Loot presets", ReadOnly = true, Idempotent = true };

        yield return new McpTool("set_loot",
            "Sets what searching a container gives: one or more of the game's loot presets (list_loot_presets). Works on an actor the project " +
            "placed (add_static_mesh, place_objects). Each preset is one pick with its own chance, so several presets make several groups. " +
            "An empty list makes the container decoration only. One journal step. WARNING: on the owner's server a preset set this way on an added mesh " +
            "removed the search interaction altogether; for loot that works use write_loot_zone with a stock searchable model instead.",
            ToolSchema.Object()
                .String("level", "Level name or package path of the actor.", required: true)
                .String("actor", "Actor name (the project's added actor).", required: true)
                .Strings("presets", "Preset names (e.g. 'Examine_Military_Weapons_Crate') or class paths. Empty: not lootable.", required: true)
                .Build(), SetLoot)
        { Title = "Set loot" };
    }

    private IEnumerable<string> LootPresetPackages() =>
        RequireCatalog() is { } catalog
            ? catalog.PackageFiles
                .Where(f => f.StartsWith(AssetPaths.ToFilePathWithoutExtension(LootPresetsFolder, catalog.ProjectName), StringComparison.OrdinalIgnoreCase))
                .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
                .Where(p => PackageLeaf(p).StartsWith("Examine_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];

    private static string PackageLeaf(string package) => package[(package.LastIndexOf('/') + 1)..];

    private static string PresetClassPath(string package) => package + "." + PackageLeaf(package) + "_C";

    private Task<McpToolResult> ListLootPresets(ToolCall call, CancellationToken cancellationToken)
    {
        var tokens = Tokens(call.GetString("filter")).ToList();
        var found = LootPresetPackages().Where(p => MatchesAll(tokens, p)).ToList();
        var details = call.GetBool("details", false);
        var tables = details ? ScumStudio.Level.Spawns.LootTables.Read(RequireCatalog()) : null;
        var presets = found.Take(details ? 30 : 400).Select(p =>
        {
            var read = tables?.Preset(PresetClassPath(p));
            return new
            {
                name = PackageLeaf(p),
                folder = p[LootPresetsFolder.Length..p.LastIndexOf('/')],
                chance = read?.Probability,
                alwaysSpawn = read?.AlwaysSpawn,
                groups = read?.Nodes.Select(n => new { tag = n.Tag, rarity = n.Rarity }),
            };
        }).ToList();
        return Task.FromResult(McpToolResult.Json(new { total = found.Count, shown = presets.Count, presets }));
    }

    private Task<McpToolResult> SetLoot(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var path = ResolveLevel(call.RequireString("level"));
        var name = call.RequireString("actor");
        var reference = new ActorRef(path, name);
        if (project.State.AddedActors.GetValueOrDefault(reference) is not AddStaticMeshActorOp added)
        {
            throw new ToolArgumentException($"'{name}' is not a static mesh the project placed in {path}. set_loot works on actors added with add_static_mesh or place_objects.");
        }

        if (project.State.IsNoLoot(reference))
        {
            throw new ToolArgumentException($"{name} has 'No loot inside' on; switch it off first.");
        }

        var wanted = (call.GetStrings("presets") ?? []).Select(p => ResolveLootPreset(catalog, p)).ToList();
        var old = project.State.GetLoot(reference)
            ?? (added.Loot is { } carried
                ? new LootSetting(carried.Count > 0, carried)
                : MeshLoot.FromCatalog(catalog).Of(added.StaticMesh) is { Presets.Count: > 0 } own ? new LootSetting(true, own.Presets) : new LootSetting(false, []));
        var next = new LootSetting(wanted.Count > 0, wanted);
        if (old.Equals(next))
        {
            return Task.FromResult(McpToolResult.Text($"{name} already has that loot; nothing changed."));
        }

        var op = new SetLootOp(reference, null, old, next);
        var entry = _host.Apply(op);
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {op.Describe()}. Undo restores the old loot."));
    }

    /// <summary>A preset name ('Examine_Crate'), or a package or class path, as a class path that exists.</summary>
    private string ResolveLootPreset(AssetCatalog catalog, string text)
    {
        var all = LootPresetPackages().ToList();
        var package = text.Contains('/', StringComparison.Ordinal)
            ? AssetPaths.NormalizeObjectPath(text, catalog.ProjectName)
            : null;
        if (package is not null)
        {
            package = package[..(package.IndexOf('.', package.LastIndexOf('/') + 1) is var dot and > 0 ? dot : package.Length)];
            if (!all.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                throw new ToolArgumentException($"'{text}' is not a loot preset (list_loot_presets).");
            }

            return PresetClassPath(all.First(p => string.Equals(p, package, StringComparison.OrdinalIgnoreCase)));
        }

        var byName = all.Where(p => string.Equals(PackageLeaf(p), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
        {
            return PresetClassPath(byName[0]);
        }

        throw new ToolArgumentException(byName.Count == 0
            ? $"No loot preset named '{text}' (list_loot_presets)."
            : $"'{text}' names {byName.Count} presets in different folders; give one class path:\n" + string.Join("\n", byName.Select(PresetClassPath)));
    }
}
