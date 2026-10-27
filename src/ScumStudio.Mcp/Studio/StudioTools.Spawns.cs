using System.Globalization;
using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Spawn tools for an AI: the armed NPCs and zombies the game spawns around a place, set the way the Spawns page does it
/// ("Place NPCs" wizard, New place tool). Every call is one journal step: one undo removes all of it.
/// </summary>
public sealed partial class StudioTools
{
    private AssetCatalog? _spawnsCatalog;
    private IReadOnlyList<NpcGuardType>? _npcKinds;
    private IReadOnlyList<SpawnPlace>? _stockPlaces;

    private IEnumerable<McpTool> SpawnTools()
    {
        yield return new McpTool("list_npc_kinds",
            "The kinds of armed NPC the game spawns (guards, soldiers, drifters ...): the name to pass to place_npcs, the place it " +
            "belongs to, its threat level and the characters it spawns (the names for place_npcs' ownCopyOf). The first call reads " +
            "the game's encounters and takes a while.",
            ToolSchema.Object().String("filter", "Words that must all appear in the name (e.g. 'military base').").Build(), ListNpcKinds)
        { Title = "NPC kinds", ReadOnly = true, Idempotent = true };

        var point = ToolSchema.Object()
            .Number("x", "X (cm).", required: true)
            .Number("y", "Y (cm).", required: true)
            .Number("z", "Z of the floor the NPC or zombie stands on (cm). Omit: the terrain height there. Inside a building or on a built floor give it: the spawn point is put 5 cm above it.");
        yield return new McpTool("place_npcs",
            "Places armed NPCs: one group of spawn posts and an area over them that spawns only the chosen kind (the project's own " +
            "copy of the zone settings; the game's other NPCs and zones stay as they are). Give the posts as a list of points, or " +
            "x/y with count and radius for a ring. The game spawns 1 to 3 of the kind while a player walks into the area, then the " +
            "area rests 20 to 30 minutes: that is how the game's NPC encounters work, it cannot be made a fixed crew. One journal step.",
            ToolSchema.Object()
                .String("kind", "NPC kind from list_npc_kinds (the name, or words of it when only one matches).", required: true)
                .Objects("posts", "Where the NPCs may appear (1 to 24 points, one group).", point)
                .Number("x", "Without 'posts': centre X (cm).")
                .Number("y", "Without 'posts': centre Y (cm).")
                .Integer("count", "Without 'posts': how many posts on the ring (default 3).", minimum: 1, maximum: SpawnPlaces.MaxGroupPosts)
                .Number("radius", "Without 'posts': ring radius (cm, default 1500).")
                .Number("z", "Without 'posts': floor Z (cm); omit for the terrain.")
                .String("ownCopyOf", "A character of the kind (from list_npc_kinds) to place as an own copy of its whole chain, so the stock NPCs are untouched; omit for the kind as the game has it.")
                .String("title", "History title of this step.")
                .Build(), PlaceNpcs)
        { Title = "Place NPCs" };

        var guardPost = ToolSchema.Object()
            .Number("x", "X (cm).", required: true)
            .Number("y", "Y (cm).", required: true)
            .Number("z", "Z of the floor the guard stands on (cm).", required: true)
            .Integer("level", $"The guard: {GuardSpawns.MinLevel} (weakest) to {GuardSpawns.MaxLevel} (strongest). Default 4.", minimum: GuardSpawns.MinLevel, maximum: GuardSpawns.MaxLevel);
        yield return new McpTool("set_guard_spawns",
            "Creates or replaces a SET of guard posts: armed guards (BP_Guard_Lvl_3..5) that a small server Lua mod spawns at the posts while a " +
            "player is near and removes when everybody has left. This is the way that works on the owner's server (the game's own NPC " +
            "encounters never spawned armed NPCs there); no pak is involved. The same sets are edited on the Map page's Guard posts tool. " +
            "Write the mod with write_guard_mod. One journal step.",
            ToolSchema.Object()
                .String("name", "The set's name: letters, digits, _ and - (at most 24), e.g. 'Dungeon'.", required: true)
                .Objects("posts", $"The posts, one guard each (1 to {GuardSpawns.MaxPosts}).", guardPost, required: true)
                .Number("reach", $"How far outside the posts' area a player wakes the guards (cm, default {GuardSpawns.DefaultReach:0}).")
                .Integer("idleSeconds", $"The guards leave when nobody has been near for this long (default {GuardSpawns.DefaultIdleSeconds}).", minimum: 10, maximum: 86400)
                .Integer("respawnSeconds", $"A killed guard's post refills after this long (default {GuardSpawns.DefaultRespawnSeconds}).", minimum: 10, maximum: 86400)
                .String("title", "History title of this step.")
                .Build(), SetGuardSpawns)
        { Title = "Set guard posts" };

        yield return new McpTool("list_guard_spawns",
            "The project's guard sets: name, timings and every post (x, y, z, guard level).",
            ToolSchema.Object().Build(), ListGuardSpawns)
        { Title = "Guard sets", ReadOnly = true, Idempotent = true };

        yield return new McpTool("remove_guard_spawns",
            "Removes a guard set from the project (one journal step; undo brings it back).",
            ToolSchema.Object().String("name", "The set's name (list_guard_spawns).", required: true).Build(), RemoveGuardSpawns)
        { Title = "Remove guard posts" };

        yield return new McpTool("write_guard_mod",
            "Writes the project's guard sets as the server Lua mod 'GuardSpawns' (Mods/GuardSpawns/Scripts/main.lua + mods.txt/mods.json) into " +
            "the server's UE4SS folder ('serverFolder': any folder of the server install, e.g. its SCUM\\Content\\Paks), or, with 'outputFolder', " +
            "only the main.lua into <outputFolder>/GuardSpawns/Scripts. A running server reloads a changed mod by itself; a new mod needs one restart.",
            ToolSchema.Object()
                .String("serverFolder", "A folder of the dedicated server (its Paks folder, SCUM, Win64 or the install folder).")
                .String("outputFolder", "Instead: a folder that receives GuardSpawns/Scripts/main.lua (for a server the app cannot reach).")
                .Build(), WriteGuardMod)
        { Title = "Write guard mod", OpenWorld = true };

        yield return new McpTool("place_zombie_spawns",
            "Adds zombie spawn points (the island's own kind of point, outdoor or indoor): zombies of the threat zone around come out " +
            "of them while a player is near. One journal step.",
            ToolSchema.Object()
                .Objects("points", "Where zombies may appear (at most 2000).", point, required: true)
                .String("type", "Interior (default: inside buildings) or Exterior (outdoors).", choices: ["Interior", "Exterior"])
                .String("title", "History title of this step.")
                .Build(), PlaceZombieSpawns)
        { Title = "Place zombie spawns" };
    }

    private Task<McpToolResult> ListNpcKinds(ToolCall call, CancellationToken cancellationToken)
    {
        var tokens = Tokens(call.GetString("filter")).ToList();
        var kinds = NpcKinds()
            .Where(k => MatchesAll(tokens, k.Name, NpcKindNames.PlaceOf(k.Name)))
            .Select(k => new
            {
                name = k.Name,
                place = NpcKindNames.PlaceOf(k.Name),
                threat = NpcKindNames.ThreatOf(k.Name).ToString().ToLowerInvariant(),
                characters = k.Characters,
            }).ToList();
        return Task.FromResult(McpToolResult.Json(new { total = kinds.Count, kinds }));
    }

    private Task<McpToolResult> PlaceNpcs(ToolCall call, CancellationToken cancellationToken)
    {
        var project = RequireProject();
        var catalog = RequireCatalog();
        var state = project.State;
        var type = FindNpcKind(call.RequireString("kind"));
        IReadOnlyList<FVector> posts = Posts(call, "posts", NpcPlacement.MaxPosts);
        if (posts.Count == 0)
        {
            var x = (float)Required(call, "x");
            var y = (float)Required(call, "y");
            var center = new FVector(x, y, Floor(call.GetNumber("z") is { } z ? (float)z : null, x, y));
            posts = NpcPlacement.PostsAround(center, call.GetInt("count", 3), (float)(call.GetNumber("radius") ?? NpcPlacement.DefaultRadius));
        }

        var planned = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToList();
        var taken = StockPlaces().Select(p => p.ActorName).Concat(state.AddedActors.Keys.Select(a => a.Actor)).ToList();
        var zones = SpawnPlaces.WithEdits(StockPlaces().Where(p => p.Kind == SpawnPlaceKind.Zone).ToList(), state);
        var package = type.ZonePackage;
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        var rows = SpawnChoices.ForZone(catalog, package).FirstOrDefault(l => l.Level == 0)
                   ?? throw new ToolArgumentException($"{leaf}: its encounters cannot be read.");
        var stored = NpcGuards.StoredNpcOnlyValues(catalog, package);

        IReadOnlyList<EditOp> ops;
        string who;
        if (call.GetString("ownCopyOf") is { Length: > 0 } character)
        {
            var name = type.Characters.FirstOrDefault(c => c.Equals(character, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ToolArgumentException($"{type.Name} spawns {string.Join(", ", type.Characters)}; '{character}' is none of them.");
            var npc = NpcCopies.Plan(catalog, type, name, planned);
            ops = NpcPlacement.Plan([new NpcPostGroup(posts)], npc.Type, npc.Chain, NpcCopies.Rows(rows, npc.Chain), taken, zones, stored, npc.Tunes);
            who = $"{NpcKindNames.CharacterOf(name)} (own copy)";
        }
        else
        {
            var copy = ZoneCopies.Plan(catalog, package, ZoneCopies.SuggestName(leaf, ZoneCopies.Taken(catalog, planned)), planned);
            ops = NpcPlacement.Plan([new NpcPostGroup(posts)], type, copy, rows, taken, zones, stored);
            who = NpcKindNames.PlaceOf(type.Name);
        }

        var title = call.GetString("title") is { Length: > 0 } t ? t : $"Place NPCs: {who}";
        var entry = _host.Apply(new BatchOp(title, ops));
        return Task.FromResult(McpToolResult.Json(new
        {
            step = entry.Seq,
            title,
            kind = type.Name,
            posts = posts.Select(p => Vec(p)).ToList(),
            area = ops.OfType<AddSubZoneOp>().Select(a => a.Zone.Actor + ": " + a.SubZone).Concat(ops.OfType<AddSpawnPlaceOp>().Where(o => o.Kind == SpawnPlaceKind.Zone).Select(o => o.NewName + " (new zone)")).FirstOrDefault(),
        }).WithSummary(string.Create(CultureInfo.InvariantCulture, $"#{entry.Seq} {title}: {posts.Count} post(s). Undo removes them all.")));
    }

    private Task<McpToolResult> PlaceZombieSpawns(ToolCall call, CancellationToken cancellationToken)
    {
        var state = RequireProject().State;
        var points = Posts(call, "points", MaxPerCall);
        if (points.Count == 0)
        {
            throw new ToolArgumentException("Give 1 to " + MaxPerCall + " points.");
        }

        var type = call.GetString("type") ?? SpawnGroundCheck.Interior;
        var names = new HashSet<string>(StockPlaces().Select(p => p.ActorName).Concat(state.AddedActors.Keys.Select(a => a.Actor)), StringComparer.OrdinalIgnoreCase);
        var ops = new List<EditOp>();
        foreach (var p in points)
        {
            var name = SpawnPlaces.NewName(SpawnPlaceKind.Character, names);
            names.Add(name);
            ops.Add(new AddSpawnPlaceOp(name, SpawnPlaceKind.Character, type, new TransformValue(p, FRotator.Zero, FVector.One)));
        }

        var title = call.GetString("title") is { Length: > 0 } t ? t : string.Create(CultureInfo.InvariantCulture, $"Place {ops.Count} zombie spawn(s)");
        var entry = _host.Apply(new BatchOp(title, ops));
        return Task.FromResult(McpToolResult.Text(string.Create(CultureInfo.InvariantCulture,
            $"#{entry.Seq} {title}: {ops.Count} {type.ToLowerInvariant()} zombie spawn point(s). Undo removes them all.")));
    }

    private Task<McpToolResult> SetGuardSpawns(ToolCall call, CancellationToken cancellationToken)
    {
        var state = RequireProject().State;
        var name = call.RequireString("name");
        var items = call.GetElement("posts") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : throw new ToolArgumentException("'posts' must be a list.");
        var posts = items.Select((e, i) => new GuardPost(
            new FVector(Num(e, "x") ?? throw new ToolArgumentException($"posts[{i}]: 'x' is required."), Num(e, "y") ?? throw new ToolArgumentException($"posts[{i}]: 'y' is required."),
                Num(e, "z") ?? throw new ToolArgumentException($"posts[{i}]: 'z' is required.")),
            e.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 4)).ToList();
        var set = new GuardSpawnSet(name, posts, (float)(call.GetNumber("reach") ?? GuardSpawns.DefaultReach),
            call.GetInt("idleSeconds", GuardSpawns.DefaultIdleSeconds), call.GetInt("respawnSeconds", GuardSpawns.DefaultRespawnSeconds));
        if (set.Validate() is { } problem)
        {
            throw new ToolArgumentException(problem);
        }

        var entry = _host.Apply(new SetGuardSpawnsOp(name, state.GetGuardSet(name), set));
        var title = call.GetString("title") is { Length: > 0 } t ? t : entry.Op.Describe();
        return Task.FromResult(McpToolResult.Text(string.Create(CultureInfo.InvariantCulture,
            $"#{entry.Seq} {title}: {posts.Count} post(s); wakes within {set.WakeRadius / 100f:0} m of ({set.Center.X:0}, {set.Center.Y:0}). Write the mod with write_guard_mod.")));
    }

    private Task<McpToolResult> ListGuardSpawns(ToolCall call, CancellationToken cancellationToken) =>
        Task.FromResult(McpToolResult.Json(new
        {
            sets = RequireProject().State.GuardSets.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new
            {
                name = s.Name,
                reach = s.Reach,
                idleSeconds = s.IdleSeconds,
                respawnSeconds = s.RespawnSeconds,
                wakeRadius = Round(s.WakeRadius),
                posts = s.Posts.Select(p => new { x = Round(p.Position.X), y = Round(p.Position.Y), z = Round(p.Position.Z), level = p.Level }).ToList(),
            }).ToList(),
        }));

    private Task<McpToolResult> RemoveGuardSpawns(ToolCall call, CancellationToken cancellationToken)
    {
        var state = RequireProject().State;
        var name = call.RequireString("name");
        var old = state.GetGuardSet(name) ?? throw new ToolArgumentException($"No guard set called '{name}'. Call list_guard_spawns.");
        var entry = _host.Apply(new SetGuardSpawnsOp(old.Name, old, null));
        return Task.FromResult(McpToolResult.Text($"#{entry.Seq} {entry.Op.Describe()}. Undo brings it back."));
    }

    private Task<McpToolResult> WriteGuardMod(ToolCall call, CancellationToken cancellationToken)
    {
        var sets = RequireProject().State.GuardSets.Values.ToList();
        if (sets.Count == 0)
        {
            throw new ToolArgumentException("The project has no guard sets: call set_guard_spawns first.");
        }

        var lua = GuardSpawns.Generate(sets);
        if (call.GetString("outputFolder") is { Length: > 0 } output)
        {
            var path = Path.Combine(output, GuardSpawns.ModName, "Scripts", "main.lua");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, lua, new System.Text.UTF8Encoding(false));
            return Task.FromResult(McpToolResult.Text($"Wrote {sets.Count} guard set(s) to {path}. Copy it to Mods/GuardSpawns/Scripts on the server."));
        }

        var folder = call.GetString("serverFolder") ?? throw new ToolArgumentException("Give 'serverFolder' (a folder of the dedicated server) or 'outputFolder'.");
        var server = ServerInstall.Find(folder) ?? throw new ToolArgumentException($"No SCUMServer.exe found at or above {folder}.");
        var mod = new ServerModStore(server).Save(GuardSpawns.ModName, lua);
        return Task.FromResult(McpToolResult.Text($"Wrote {sets.Count} guard set(s) to {mod.ScriptPath} and switched the mod on. A running server reloads a changed mod by itself; a new mod needs one restart."));
    }

    /// <summary>The points of a list argument as world positions (the floor plus the character lift); empty when the argument is missing.</summary>
    private List<FVector> Posts(ToolCall call, string name, int most)
    {
        var result = new List<FVector>();
        if (call.GetElement(name) is not { ValueKind: JsonValueKind.Array } list)
        {
            return result;
        }

        foreach (var (element, index) in list.EnumerateArray().Select((e, i) => (e, i)))
        {
            var at = $"{name}[{index}]";
            var x = Num(element, "x") ?? throw new ToolArgumentException($"{at}: 'x' is required.");
            var y = Num(element, "y") ?? throw new ToolArgumentException($"{at}: 'y' is required.");
            result.Add(new FVector(x, y, Floor(Num(element, "z"), x, y)));
        }

        if (result.Count > most)
        {
            throw new ToolArgumentException($"'{name}' takes at most {most} points ({result.Count} given).");
        }

        return result;
    }

    // The standing height of a spawn point: the given floor (else the terrain) and the lift the game's own points have.
    private float Floor(float? z, float x, float y) =>
        (z ?? Probe().TerrainHeight(x, y) ?? throw new ToolArgumentException($"({x:0}, {y:0}) is not on the island (no ground): give 'z'.")) + SpawnGroundCheck.CharacterLift;

    private NpcGuardType FindNpcKind(string text)
    {
        var kinds = NpcKinds();
        var word = text.Trim();
        var exact = kinds.FirstOrDefault(k => k.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var tokens = Tokens(word).ToList();
        var found = kinds.Where(k => MatchesAll(tokens, k.Name)).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new ToolArgumentException($"No NPC kind matches '{text}'. Call list_npc_kinds."),
            _ => throw new ToolArgumentException($"'{text}' matches {found.Count} NPC kinds: {string.Join(", ", found.Take(8).Select(k => k.Name))}. Give more of the name."),
        };
    }

    private IReadOnlyList<NpcGuardType> NpcKinds()
    {
        var catalog = RequireCatalog();
        if (!ReferenceEquals(catalog, _spawnsCatalog))
        {
            _spawnsCatalog = catalog;
            _npcKinds = null;
            _stockPlaces = null;
        }

        return _npcKinds ??= NpcGuards.Types(catalog);
    }

    private IReadOnlyList<SpawnPlace> StockPlaces()
    {
        var catalog = RequireCatalog();
        if (!ReferenceEquals(catalog, _spawnsCatalog))
        {
            _spawnsCatalog = catalog;
            _npcKinds = null;
            _stockPlaces = null;
        }

        return _stockPlaces ??= SpawnPlaces.ReadFrom(catalog);
    }
}
