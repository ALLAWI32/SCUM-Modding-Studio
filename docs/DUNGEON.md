# Making a dungeon: zombies, guards and loot

A step-by-step guide to filling a place you built on the map with enemies and loot, so that it works on a **dedicated server**.
It was worked out on a real server; what did not work is listed at the end so you do not repeat it.

| Part | What does it | Where it lives |
|---|---|---|
| The place itself | Map page: `place_objects`, `copy_place`, ... | the client/server pak (`export_mod`) |
| Zombies | zombie spawn points (Spawns page, or MCP `place_zombie_spawns`) | the pak |
| Armed guards | guard posts (Map page tool, or MCP `set_guard_spawns` + `write_guard_mod`) | a UE4SS Lua mod on the server |
| Loot | loot zones (Server page, **Loot** tab, or MCP `write_loot_zone`) | JSON files in the server's `Saved\Config` |

## 1. Build the place and export

Build or copy the place on the Map page, then **Export**. Copy the pak to the server's `SCUM\Content\Paks`.

## 2. Zombies

Add zombie spawn points inside the place (Spawns page). They are part of the pak, nothing else to install.

## 3. Guards (armed NPCs)

Armed NPC "encounter" posts did not spawn on the test server at all, not even in stock bases. Guard posts are the workaround:
the Studio writes a small Lua mod that spawns the guards while the server runs.

1. Map page, the guard-post tool: click where a guard should stand, pick the kind, the count and the dungeon name.
2. Press **Write mod**. It needs UE4SS on the server (Server page, **Install UE4SS**; the server must be stopped).
3. Start the server. The UE4SS log (Server page, **Log** tab) shows what spawned.

The Lua mod only uses functions from the checked catalogue (Server page, **Lua editor**). Hooks never touch players directly:
that crashed the server in tests.

## 4. Loot

SCUM has an official loot customization: JSON files in
`SCUM\Saved\Config\WindowsServer\Loot\Spawners\Presets\Override\<Zone>\`.

- `Zones.json` lists rectangles (`TopLeft` = max X / max Y, `BottomRight` = min X / min Y).
- One file per preset; its name is the preset's path with dashes, for example `Street-Military-Examine_Crate.json`.
- Inside, you set the groups (`Nodes` with a rarity and tags, or concrete `Items`), `Probability`, `QuantityMin/Max`, damage and stack.
  A missing `Probability` means the preset's default (for example 9), not 100.
- Rarities and their weights: Abundant 32, Common 16, Uncommon 8, Rare 4, VeryRare 2, ExtremelyRare 1.

The **Loot** tab writes these files for you: pick a preset, change the groups, chance and counts, choose the rectangle, save.
Then, on the server (as admin), run:

```
#ReloadLootCustomizationsAndResetSpawners
```

Useful admin commands: `#ExportDefaultItemSpawnerPresets` (writes every stock preset to `Loot\Spawners\Presets\Default`),
`#ExportItemSpawnerPresetsInZone X1 Y1 X2 Y2 Name` (what a rectangle currently gives).

**Containers:** use stock searchable meshes (for example `SM_MilitaryCrate_Pile_02`). A container you add as a plain mesh
with a loot preset written into its component loses the search interaction; the zone files above are the working way.

## What did not work

- Giving items to non-admin players from Lua (the spawn functions need an admin context).
- Editing a placed container's loot preset on the component (kills the "search" action).
- Armed encounter NPCs from the map: they do not spawn on a dedicated server.
- Lua hooks that read or write players directly: crashes.
- Writing a soft class reference to a quest trade deal from Lua: crashes the server.

## Safety

Everything here is server-side configuration or an ordinary pak. The Studio never starts or stops the server, never patches
`SCUMServer.exe`, and only writes into the folders you point it at.
