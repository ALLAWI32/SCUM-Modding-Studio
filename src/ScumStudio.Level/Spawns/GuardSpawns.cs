using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Spawns;

/// <summary>One post of a guard set: where an armed guard stands (world space, centimetres; the floor it stands on) and which guard.</summary>
/// <param name="Position">The floor under the guard's feet.</param>
/// <param name="Level">The guard's level, <see cref="GuardSpawns.MinLevel"/> (weakest) to <see cref="GuardSpawns.MaxLevel"/> (strongest): <c>BP_Guard_Lvl_3</c> to <c>BP_Guard_Lvl_5</c>.</param>
public sealed record GuardPost(FVector Position, int Level);

/// <summary>
/// A named group of guard posts and how they behave (owner: "a constructor for spawns like the dungeon's guards, placed on the 3D
/// map"). The game's own encounter system did not spawn armed NPCs on the owner's server, so a set becomes a small server Lua
/// mod (<see cref="GuardSpawns.Generate"/>) that spawns the guard Blueprint at each post while a player is near and removes the
/// guards when everybody has left.
/// </summary>
/// <param name="Name">Letters, digits, <c>_</c> and <c>-</c> (see <see cref="GuardSpawns.IsValidName"/>).</param>
/// <param name="Posts">The posts, one guard each.</param>
/// <param name="Reach">How far outside the posts' area a player wakes the guards (cm).</param>
/// <param name="IdleSeconds">The guards are removed when nobody has been near for this long.</param>
/// <param name="RespawnSeconds">A killed guard's post is filled again after this long.</param>
public sealed record GuardSpawnSet(string Name, IReadOnlyList<GuardPost> Posts, float Reach = GuardSpawns.DefaultReach,
    int IdleSeconds = GuardSpawns.DefaultIdleSeconds, int RespawnSeconds = GuardSpawns.DefaultRespawnSeconds)
{
    /// <inheritdoc />
    public bool Equals(GuardSpawnSet? other) =>
        other is not null && Name == other.Name && Reach.Equals(other.Reach) && IdleSeconds == other.IdleSeconds && RespawnSeconds == other.RespawnSeconds
        && Posts.SequenceEqual(other.Posts);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Posts.Count, Reach, IdleSeconds, RespawnSeconds);

    /// <summary>The middle of the posts' area (X, Y), where the guards' wake-up distance is measured from.</summary>
    public (float X, float Y) Center => Posts.Count == 0 ? (0f, 0f)
        : ((Posts.Min(p => p.Position.X) + Posts.Max(p => p.Position.X)) / 2f, (Posts.Min(p => p.Position.Y) + Posts.Max(p => p.Position.Y)) / 2f);

    /// <summary>How close to <see cref="Center"/> a player has to be to wake the guards: the farthest post plus <see cref="Reach"/>.</summary>
    public float WakeRadius
    {
        get
        {
            var (cx, cy) = Center;
            return Posts.Count == 0 ? Reach : Posts.Max(p => MathF.Sqrt(((p.Position.X - cx) * (p.Position.X - cx)) + ((p.Position.Y - cy) * (p.Position.Y - cy)))) + Reach;
        }
    }

    /// <summary>Why the set cannot be used, or null when it can.</summary>
    public string? Validate()
    {
        static bool Sane(float v) => float.IsFinite(v) && MathF.Abs(v) <= 10_000_000f;
        return !GuardSpawns.IsValidName(Name) ? "A guard set's name is letters, digits, _ and - (at most 24)."
            : Posts.Count is < 1 or > GuardSpawns.MaxPosts ? $"A guard set holds 1 to {GuardSpawns.MaxPosts} posts."
            : Posts.Any(p => p.Level is < GuardSpawns.MinLevel or > GuardSpawns.MaxLevel) ? $"A guard is level {GuardSpawns.MinLevel} to {GuardSpawns.MaxLevel}."
            : Posts.Any(p => !Sane(p.Position.X) || !Sane(p.Position.Y) || !Sane(p.Position.Z)) ? "A post is not a place on the island."
            : !float.IsFinite(Reach) || Reach is < 0f or > 100_000f ? "The wake-up reach is 0 to 1000 m."
            : IdleSeconds is < 10 or > 86_400 ? "The time before the guards leave is 10 s to a day."
            : RespawnSeconds is < 10 or > 86_400 ? "The respawn time is 10 s to a day."
            : null;
    }
}

/// <summary>
/// Which height a guard post gets when the owner clicks the 3D map (owner: "can we find the height ourselves, so nobody types it?
/// the floors differ everywhere"). A click on a roof hits the roof; the floor is lower, under it. From every surface under the
/// clicked point (top down: roof, upper floors, floor, terrain) the floors a guard can stand on are those with room for a person
/// above them; the default is the topmost floor that has a ceiling over it (the top floor inside a building), else the first
/// surface (open ground).
/// </summary>
public static class GuardFloors
{
    /// <summary>Room a guard needs above his feet (cm): a floor with less above it (a slab on the terrain, a crawl space) is no floor.</summary>
    public const float Headroom = 200f;

    /// <summary>Surfaces this close (cm) are one: a slab's top and its underside.</summary>
    public const float MergeWithin = 30f;

    /// <summary>
    /// The floors among <paramref name="surfaces"/> (heights, any order), highest first, and which of them is the default.
    /// With no surface the list is empty and the default is -1.
    /// </summary>
    public static (IReadOnlyList<float> Floors, int Default) Choose(IEnumerable<float> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var stack = new List<float>();
        foreach (var z in surfaces.Where(float.IsFinite).OrderByDescending(z => z))
        {
            if (stack.Count == 0 || stack[^1] - z > MergeWithin)
            {
                stack.Add(z);
            }
        }

        var floors = new List<float>();
        var covered = new List<bool>();
        for (var i = 0; i < stack.Count; i++)
        {
            if (i == 0 || stack[i - 1] - stack[i] >= Headroom)
            {
                floors.Add(stack[i]);
                covered.Add(i > 0);
            }
        }

        if (floors.Count == 0)
        {
            return ([], -1);
        }

        var chosen = covered.IndexOf(true);
        return (floors, chosen < 0 ? 0 : chosen);
    }
}

/// <summary>Guard sets and the server Lua mod made from them.</summary>
public static partial class GuardSpawns
{
    /// <summary>The weakest guard (<c>BP_Guard_Lvl_3</c>).</summary>
    public const int MinLevel = 3;

    /// <summary>The strongest guard (<c>BP_Guard_Lvl_5</c>).</summary>
    public const int MaxLevel = 5;

    /// <summary>Most posts in one set.</summary>
    public const int MaxPosts = 64;

    /// <summary>A player this far (cm) outside the posts' area still wakes the guards: 40 m.</summary>
    public const float DefaultReach = 4000f;

    /// <summary>The guards leave when nobody has been near for this long (seconds).</summary>
    public const int DefaultIdleSeconds = 90;

    /// <summary>A killed guard's post is filled again after this long (seconds).</summary>
    public const int DefaultRespawnSeconds = 300;

    /// <summary>The server mod's folder name.</summary>
    public const string ModName = "GuardSpawns";

    /// <summary>True for a set name the Lua file can carry (letters, digits, <c>_</c> and <c>-</c>, at most 24).</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 24 } && ValidName().IsMatch(name);

    /// <summary>A name not in <paramref name="taken"/>: <c>Guards</c>, <c>Guards2</c>, ...</summary>
    public static string NewName(IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = "Guards";
        for (var i = 2; used.Contains(name); i++)
        {
            name = "Guards" + i.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>The Lua of the server mod <see cref="ModName"/> for <paramref name="sets"/>.</summary>
    public static string Generate(IEnumerable<GuardSpawnSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var text = new StringBuilder();
        foreach (var set in sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (cx, cy) = set.Center;
            text.Append("  { name = \"").Append(set.Name).Append("\", cx = ").Append(Num(cx)).Append(", cy = ").Append(Num(cy))
                .Append(", radius = ").Append(Num(set.WakeRadius)).Append(", idle = ").Append(set.IdleSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(", respawn = ").Append(set.RespawnSeconds.ToString(CultureInfo.InvariantCulture)).AppendLine(", posts = {");
            foreach (var post in set.Posts)
            {
                text.Append("    { ").Append(Num(post.Position.X)).Append(", ").Append(Num(post.Position.Y)).Append(", ").Append(Num(post.Position.Z)).Append(", ")
                    .Append(post.Level.ToString(CultureInfo.InvariantCulture)).AppendLine(" },");
            }

            text.AppendLine("  } },");
        }

        return LuaTemplate.Replace("--[[SETS]]", text.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    // As a double: a float's custom format keeps seven digits, which cuts a coordinate like 456350.25 to 456350.2.
    private static string Num(float value) => ((double)value).ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidName();

    // The mod the owner tried by hand (DungeonGuards): SpawnActor of the guard Blueprint gives it its own AI controller; the class
    // has to be loaded first with its full object path. The tick only reads the player list and spawns a couple of guards.
    private const string LuaTemplate = """
        -- GUARD SPAWNS / ОХРАНА: made by ScumStudio (Map page > Guard posts). Do not edit: write it again from the app.
        -- Armed guards stand at the posts while a player is near, and are removed when everybody has left. The game's encounter
        -- system is not used: the guard Blueprint is spawned directly, and the game gives it its own AI.
        local CHECK_SECONDS = 10
        local LOG_FILE = "ue4ss/guard_spawns.log"
        local GUARD_FOLDER = "/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/Blueprint/Guard/"
        local SETS = {
        --[[SETS]]
        }

        local classes = {}
        local state = {}     -- per set: slots = { { actor, readyAt } ... }, lastNear
        for i, set in ipairs(SETS) do
          state[i] = { slots = {}, lastNear = 0 }
          for p = 1, #set.posts do state[i].slots[p] = { actor = nil, readyAt = 0 } end
        end

        local function log(...)
          local parts = {}
          for i = 1, select("#", ...) do parts[#parts + 1] = tostring((select(i, ...))) end
          local f = io.open(LOG_FILE, "a")
          if f then f:write(os.date("%Y-%m-%d %H:%M:%S "), table.concat(parts, " "), "\n"); f:close() end
        end

        local function guardClass(level)
          local c = classes[level]
          if c and c:IsValid() then return c end
          local name = "BP_Guard_Lvl_" .. level
          local full = GUARD_FOLDER .. name .. "." .. name .. "_C"
          c = StaticFindObject(full)
          if not (c and c:IsValid()) then
            pcall(function() LoadAsset(GUARD_FOLDER .. name .. "." .. name) end)
            c = StaticFindObject(full)
          end
          if c and c:IsValid() then classes[level] = c; return c end
          return nil
        end

        local function alive(actor)
          if not (actor and actor:IsValid()) then return false end
          local ok, c = pcall(function() return actor:GetController() end)
          return ok and c ~= nil and c:IsValid()   -- a dead guard has no controller any more
        end

        local function players()
          local ok, list = pcall(FindAllOf, "BP_Prisoner_C")
          local result, world = {}, nil
          if ok and type(list) == "table" then
            for _, p in ipairs(list) do
              if p:IsValid() then
                local okl, l = pcall(function() return p:K2_GetActorLocation() end)
                if okl and l then
                  result[#result + 1] = { x = l.X, y = l.Y }
                  world = world or p:GetWorld()
                end
              end
            end
          end
          return result, world
        end

        local function removeAll(index, why)
          local n = 0
          for _, s in ipairs(state[index].slots) do
            if s.actor and s.actor:IsValid() then pcall(function() s.actor:K2_DestroyActor() end); n = n + 1 end
            s.actor = nil
            s.readyAt = 0
          end
          if n > 0 then log(SETS[index].name, "removed", n, "guard(s):", why) end
        end

        local function tickSet(index, list, world, now)
          local set, st = SETS[index], state[index]
          local near = false
          for _, p in ipairs(list) do
            local dx, dy = p.x - set.cx, p.y - set.cy
            if dx * dx + dy * dy <= set.radius * set.radius then near = true; break end
          end
          if near then st.lastNear = now end
          if not near then
            if st.lastNear > 0 and now - st.lastNear >= set.idle then removeAll(index, "nobody near"); st.lastNear = 0 end
            return
          end
          if not world then return end
          local spawned = 0
          for i, s in ipairs(st.slots) do
            if s.actor and not alive(s.actor) then
              if s.actor:IsValid() then pcall(function() s.actor:K2_DestroyActor() end) end   -- killed: the body goes, the post refills later
              s.actor = nil
              s.readyAt = now + set.respawn
              log(set.name, "guard", i, "was killed")
            end
            if not s.actor and now >= s.readyAt and spawned < 2 then
              local post = set.posts[i]
              local cls = guardClass(post[4])
              if cls then
                local ok, a = pcall(function() return world:SpawnActor(cls, { X = post[1], Y = post[2], Z = post[3] + 100 }, { Pitch = 0, Yaw = (i * 45) % 360, Roll = 0 }) end)
                if ok and a and a:IsValid() then
                  s.actor = a
                  spawned = spawned + 1
                  log(set.name, "spawned guard", i, "level", post[4])
                else
                  s.readyAt = now + 30
                  log(set.name, "spawn failed at post", i, ok and "(no actor)" or tostring(a):sub(1, 100))
                end
              end
            end
          end
        end

        local function tick()
          local list, world = players()
          local now = os.time()
          for index = 1, #SETS do
            local ok, err = pcall(tickSet, index, list, world, now)
            if not ok then log("tick error", SETS[index].name, tostring(err):sub(1, 160)) end
          end
        end

        ExecuteWithDelay(30000, function()
          LoopAsync(CHECK_SECONDS * 1000, function() ExecuteInGameThread(tick); return false end)
        end)

        -- "!guards" in the chat writes how many guards stand now into the log
        RegisterHook("/Script/SCUM.PlayerRpcChannel:Chat_Server_BroadcastChatMessage", function(self, Message, Channel)
          local ok, text = pcall(function() return Message:get():ToString() end)
          if not ok or string.lower(text or "") ~= "!guards" then return end
          ExecuteWithDelay(800, function()
            ExecuteInGameThread(function()
              for index, set in ipairs(SETS) do
                local n, a = 0, 0
                for _, s in ipairs(state[index].slots) do if s.actor and s.actor:IsValid() then n = n + 1; if alive(s.actor) then a = a + 1 end end end
                log(set.name, "status:", n, "guard actors,", a, "alive, posts", #set.posts)
              end
            end)
          end)
        end)
        print("[GuardSpawns] on: " .. #SETS .. " set(s)\n")
        """;
}
