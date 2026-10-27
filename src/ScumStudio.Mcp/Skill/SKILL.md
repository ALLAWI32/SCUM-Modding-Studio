---
name: scumstudio
description: Drive ScumStudio, the SCUM (Unreal Engine 4.27) modding studio, through its MCP server "scumstudio" - edit the island map (delete, move, copy, place objects in any level or cell), place zombie spawn points, armed NPCs and server-side guard posts, tune or clone vehicles and weapons, and export ready mod paks. Use whenever the user asks to change SCUM's map, buildings, objects, zombie or NPC spawns, vehicles, weapons or to build/export a SCUM mod with ScumStudio.
---

# Working with ScumStudio over MCP

ScumStudio edits SCUM's **cooked** game files (no Unreal editor, no game source). Every edit goes into the open
**project's journal**: it shows up in the app's History at once and the user can undo it. Work like a careful level
designer sitting next to the owner: small steps, look at the result, explain what you did.

## Before anything

1. `get_status` - what is open (game files, project, page, levels in the viewport, camera).
2. No game files? `open_source` without a path (uses the folder the user configured). The AES key is the user's: it is
   never a tool argument, never ask for it, never print it.
3. No project? Edits need one: `create_project` (folder + name, e.g. the user's Documents\ScumStudio Projects) or
   `open_project`. Tell the user the project name.

## The map (The_Island)

- ~1,900 sublevels in 25 cells: `A_0`..`D_4` and `Z_0`..`Z_4`. Kinds: Poi (towns, bunkers, outposts...), Landscape
  (terrain tiles), TvBase, Pripyat (the abandoned city), Misc. Find levels with `list_levels` (filter by cell/name).
- Look before you touch: `show_levels` (a whole cell takes ~30 s to load in the app) then `screenshot`
  (`viewport`) - you can see what the user sees.
- Find objects: `list_actors` (filter words: name, class or mesh), `get_actor` for details, `search_assets` for meshes
  to place (className `StaticMesh`).
- Edit:
  - `delete_actors` / `restore_actors`
  - `delete_all_of_kind` (one undoable edit; name an example with `likeActor`; includes foliage/rock instances unless
    `includeInstances` is false - say so to the user before removing whole forests)
  - `move_actor` (absolute location/rotation/scale, or `offset`)
  - `duplicate_actor`, `add_static_mesh`, `copy_actor_to_level` (buildings keep their child actors)
- After each edit: `select_actor` + `screenshot` and check it. Wrong? `undo` immediately and say what happened.
- Units: **centimetres**. Axes: X forward, Y right, **Z up**. Rotation `[pitch, yaw, roll]` in degrees.
  1 m = 100. A door is ~220 tall, a building floor ~300-350.

## Building (a camp, a village, a forest)

- Find what to use: `list_object_categories` (pickup vs world, then e.g. `buildings.residential`, `furniture.tables`,
  `exterior.fences`, `nature.trees`, `nature.rocks`) and `list_objects` (with `withSize` to plan spacing and stacking).
- Look at the spot: `ground_height` (terrain height, top of what stands there, the sublevels there) and a `screenshot`.
- `place_objects` puts a whole list down in one undoable step. Every object rests on what is under it (`snap`
  `surface`, the default): the terrain, or the top of an object already there - including ones placed earlier in the
  same call, so build bottom-up: foundations/floors, then walls, then roofs, then props on tables. `ground` ignores
  objects; `none` keeps your exact `z` (use it inside buildings: from outside a building is one box).
  A Blueprint (a house with doors, a lamp, a loot crate) is copied from one instance the game already placed;
  `copyLevel` + `copyActor` copies any placed actor (a whole house, a watchtower) to your spot.
- `copy_place` copies a whole existing place (a village, farm, outpost, military camp: a Poi sublevel) to a new spot,
  turned as you like, following the new terrain - the fastest way to a complete, game-made village or base.
- `scatter_objects` plants trees, bushes, grass and rocks naturally: random spots at `minSpacing`, random turn and
  size, as deep in the ground as the game plants that mesh. It skips water and building footprints. A dense forest:
  several tree species mixed, `minSpacing` 400-700, then a second call with bushes at 150-300.
- Name each step with `title` ("Build the farm house", "Plant a pine forest"): it is what the user sees in History,
  and one `undo` removes the whole step.
- Think like a level designer: roads and paths between buildings, fences around yards, props where people would put
  them, vegetation thinning out near buildings. Check with `show_levels` + `screenshot` after each step.

## Zombies and armed NPCs

- `place_zombie_spawns`: zombie spawn points (`points` with x, y and optionally `z`; `type` Interior, the default, or
  Exterior). The zone around the points decides which zombies come, and only while a player is near.
- `list_npc_kinds` (optionally with `filter`, e.g. "military base"): the armed NPC kinds the game has, with place, threat
  level and characters. The first call reads the game's encounters and takes a while.
- `place_npcs`: one group of NPC posts (`posts` as a list of points, or `x`/`y` with `count` and `radius` for a ring) and
  an area over them that spawns only the chosen `kind`. The project gets its own copy of that kind's zone settings; the
  game's other NPCs and zones stay as they are. `ownCopyOf` (a character from `list_npc_kinds`) places an own copy of
  that one NPC's whole chain, so the stock NPCs are untouched too. Up to 24 posts per call.
- Give `z` (the floor, cm) for anything on a built floor or inside a building: without it the point stands on the terrain.
  Posts go 5 cm above the floor you name. Check the floor with `ground_height` (a roof over it reports the roof's top).
- Tell the user how the game works: NPCs come 1 to 3 at a time while a player walks into the area, from 30 to 200 m
  away and out of view, then the area rests 20 to 30 minutes; characters under new-player protection get none. It is
  not a fixed crew, and only the game shows whether they come at those posts. No setting is asked for chance or count.
- Each call is one journal step; `undo` removes it. The spawns reach the game with `export_mod`.
- **Guards that really spawn (use this for "bots in a dungeon")**: on the owner's server the game's own NPC encounters never
  spawned armed NPCs (not even at the island's stock bases), so `place_npcs` may stay silent in the game. What works:
  `set_guard_spawns` makes a named SET of guard posts (`posts` = x, y, floor `z`, `level` 3-5; `reach`, `idleSeconds`,
  `respawnSeconds`), `write_guard_mod` writes them as the server Lua mod `GuardSpawns` (`serverFolder` = any folder of the server,
  or `outputFolder` for a server you cannot reach), `list_guard_spawns` / `remove_guard_spawns` read and remove. The mod spawns
  `BP_Guard_Lvl_N` at the posts while a player is within the set's wake distance and removes them 90 s after everybody left; a
  killed guard's post refills after 5 min. No pak is involved. The same sets are edited on the Map page (Guard posts, click on
  the 3D map). A running server reloads a changed mod by itself, a new mod needs one restart.
- Floors: a post's `z` is the floor under the guard. For a built floor use its top (a tile placed at z=8300 has its top at about
  8301); `ground_height` over a roof reports the roof, so take the floor height from your own build.

## Server Lua mods (UE4SS inside SCUM Server)

A Lua script that runs in the dedicated server reacts to the game (chat, join, death, a searched object) and calls its functions.
The owner's server has UE4SS; scripts are files in `Mods/<Name>/Scripts/main.lua`, not part of the project journal.

- ALWAYS read `lua_catalog` before writing a script: each entry says whether it was **tested** on the owner's server, only **seen**
  (exists, effect unknown) or **unsafe** (crashes or does nothing). The rules group lists what froze or crashed the server.
- `lua_game_functions` searches the game's own function list (names only, from the server's class dump) for a hook or a call.
- `check_lua` checks a script (syntax with the line, game functions against the dump, known mistakes); `write_lua_mod` checks and writes it
  to the server (`serverFolder` = any folder of the server install) and switches it on. A running mod reloads by itself; a new one needs a restart.
- Never touch a player inside a hook (remember it, answer 1 s later through ExecuteWithDelay + ExecuteInGameThread), always check `:IsValid()`,
  never loop FindAllOf over many classes, never write soft class pointers. Tell the owner what the script does and which entries were only "seen".

## Loot in containers

Loot that works is the server's own JSON loot customization, not a pak: a searchable object of the game (a crate pile) uses a loot preset
(`SpawnerPresets2/.../Examine_X`) and the server can change that preset **inside a rectangle** of the map.

1. Put the container: `add_static_mesh` / `place_objects` a stock searchable model, for example `SM_MilitaryCrate_Pile_02` (the military crate piles use
   `Street-Military-Examine_Crate`). Do NOT call `set_loot` on it: on the owner's server that removed the search interaction.
2. `read_loot_zones` (are the game's presets exported? the owner types `#ExportDefaultItemSpawnerPresets` once), `list_loot_tags` (what groups and items exist),
   then `write_loot_zone`: the zone (corners, or `fromGuardSet` to cover a dungeon's posts), the preset file name, `probability` (ALWAYS give it; 100 = always),
   `quantityMin`/`quantityMax` (how many items one search gives), `groups` (rarity = weight: Abundant 32, Common 16, Uncommon 8, Rare 4, VeryRare 2, ExtremelyRare 1; entries =
   loot tree tags or item ids). Keep `filterByZone` and `applyLocationProbability` false for a place outside the game's own zones or nothing drops.
3. The owner types `#ReloadLootCustomizationsAndResetSpawners` in the game chat (no restart). `remove_loot_zone` undoes it.

`list_loot_presets` / `set_loot` (a pak edit of the preset, or a preset on an object) remain for completeness; editing a stock preset's chance changes every container that uses it.

## Vehicles and weapons

- `list_items` → `get_item_values` (stock and current value of every stored property) → `set_item_values`.
- Cooked Blueprints store **only values that differ from their parent class**: if a value is missing, it lives on the
  parent (e.g. RPK-74 inherits from AK47) or on the projectile/ammo (`BP_WeaponBullet_*`, `Cal_*`). Damage of a round
  lives on the projectile; which projectile a round fires lives on the ammo (`Cal_*.ProjectileClass`).
- `clone_item` makes a new item/vehicle under a new name with an in-game `caption`; the export registers it so
  `#SpawnItem <Name>` / `#SpawnVehicle BPC_<Name>` work in game. `remove_item_clone` undoes it. Any inventory item can
  be cloned (kind `Item`: `Copper_Coins`, `Painkillers_01`, `Nails` …); its stack size or uses is the `_ES`
  `MaxQuantity` (`StackableEntityComponentSetup_0` / `DiscreteUsageItemEntityComponentSetup_0`) when the item has one.
- `show_item` opens it on the Vehicles, Weapons or Items page so the user can see the values.

## Export

- `export_mod` builds `<Name>\Client\pakchunk<N>-<Name>_P.pak` (+ `.sig`, `export-report.md`) and, with `includeServer`,
  the server pak. Read the report back to the user: which levels/items changed, warnings.
- Tell the user where the files are; installing them is the user's step (their own launcher/server setup).

## Rules

- One logical change per call, then verify. Never batch dozens of destructive edits without showing the user first.
- Never ask for, print or store the AES key. Never patch `SCUM.exe`/`SCUMServer.exe`. No speed/movement hacks,
  no anti-cheat or signature workarounds - decline those.
- If a tool returns `isError`, read the message, fix the arguments and retry once; otherwise report it plainly.
- Cooked files are byte-exact formats: only use the tools, never ask the user to hand-edit `.uasset/.umap` files.

## When something does not respond

- "ScumStudio is not reachable": the app is closed or Settings → AI control is off.
- "Port … is not available": another ScumStudio instance holds the MCP port; close it.
- Nothing in `get_status` levels: call `show_levels` first; the viewport only shows what was loaded.
