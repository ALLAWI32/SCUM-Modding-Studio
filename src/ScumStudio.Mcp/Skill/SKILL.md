---
name: scumstudio
description: Drive ScumStudio, the SCUM (Unreal Engine 4.27) modding studio, through its MCP server "scumstudio" - edit the island map (delete, move, copy, place objects in any level or cell), tune or clone vehicles and weapons, and export ready mod paks. Use whenever the user asks to change SCUM's map, buildings, objects, vehicles, weapons or to build/export a SCUM mod with ScumStudio.
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

## Vehicles and weapons

- `list_items` → `get_item_values` (stock and current value of every stored property) → `set_item_values`.
- Cooked Blueprints store **only values that differ from their parent class**: if a value is missing, it lives on the
  parent (e.g. RPK-74 inherits from AK47) or on the projectile/ammo (`BP_WeaponBullet_*`, `Cal_*`). Damage of a round
  lives on the projectile; which projectile a round fires lives on the ammo (`Cal_*.ProjectileClass`).
- `clone_item` makes a new item/vehicle under a new name with an in-game `caption`; the export registers it so
  `#SpawnItem <Name>` / `#SpawnVehicle BPC_<Name>` work in game. `remove_item_clone` undoes it.
- `show_item` opens it on the Vehicles/Weapons page so the user can see the values.

## Export

- `export_mod` builds `Client\pakchunk<N>-<Name>_P.pak` (+ `.sig`, `export-report.md`) and, with `includeServer`,
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
